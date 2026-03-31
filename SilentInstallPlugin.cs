using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using SilentInstall.Installers;
using SilentInstall.Settings;

namespace SilentInstall
{
    public class SilentInstallPlugin : GenericPlugin
    {
        private readonly PluginSettings _settings;

        public override Guid Id { get; } = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

        public SilentInstallPlugin(IPlayniteAPI api) : base(api)
        {
            _settings  = new PluginSettings(this);
            Properties = new GenericPluginProperties { HasSettings = true };
            SilentLogger.Initialize(GetPluginUserDataPath());
            SilentLogger.Info($"Plugin initialized — Playnite mode: {api.ApplicationInfo.Mode}");
            SilentLogger.Info($"Detected {_settings.DetectedSteamLibraries.Count} Steam library(ies):");
            foreach (var lib in _settings.DetectedSteamLibraries)
                SilentLogger.Info($"  • {lib.Label}  [{lib.Path}]");
            SilentLogger.Info($"Active steamapps path: {_settings.SteamAppsPath}");
            SilentLogger.Info($"UseCustomPath: {_settings.UseCustomPath}");
        }

        public override IEnumerable<InstallController> GetInstallActions(GetInstallActionsArgs args)
        {
            if (SteamInstaller.CanHandle(args.Game))
            {
                SilentLogger.Info($"GetInstallActions → returning SilentSteamInstallController for [{args.Game.Name}] (AppID={args.Game.GameId})");
                yield return new SilentSteamInstallController(args.Game, _settings, PlayniteApi);
            }
        }

        public override IEnumerable<UninstallController> GetUninstallActions(GetUninstallActionsArgs args)
        {
            if (SteamInstaller.CanHandle(args.Game))
            {
                SilentLogger.Info($"GetUninstallActions → returning SilentSteamUninstallController for [{args.Game.Name}] (AppID={args.Game.GameId})");
                yield return new SilentSteamUninstallController(args.Game, _settings, PlayniteApi);
            }
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            if (args.Games.Count != 1) yield break;

            var game    = args.Games[0];
            var isSteam = SteamInstaller.CanHandle(game);
            var isEpic  = EpicInstaller.CanHandle(game);

            if (!isSteam && !isEpic) yield break;

            yield return new GameMenuItem
            {
                Description = isSteam ? "Install silently (Steam)" : "Install silently (Epic)",
                MenuSection  = "Silent Install",
                Action       = _ =>
                {
                    SilentLogger.Info($"GameMenuItem triggered for [{game.Name}] (isSteam={isSteam})");
                    if (isSteam) SteamInstaller.Install(game, _settings, PlayniteApi);
                    else         EpicInstaller.Install(game, _settings, PlayniteApi);
                }
            };
        }

        public override ISettings GetSettings(bool firstRunSettings) => _settings;
        public override UserControl GetSettingsView(bool firstRunSettings) => new SettingsView(_settings);
    }

    // ── Install controller ────────────────────────────────────────────────────

    public class SilentSteamInstallController : InstallController
    {
        private readonly PluginSettings _settings;
        private readonly IPlayniteAPI   _api;
        private System.Threading.CancellationTokenSource _cts;
        private string _targetLibraryPath;

        public SilentSteamInstallController(Game game, PluginSettings settings, IPlayniteAPI api)
            : base(game)
        {
            Name      = "Silent Install (Steam)";
            _settings = settings;
            _api      = api;
            SilentLogger.Info($"[{game.Name}] InstallController created (AppID={game.GameId}, PluginId={game.PluginId})");
        }

        public override void Install(InstallActionArgs args)
        {
            SilentLogger.Info($"[{Game.Name}] Install() called — selecting library…");
            _targetLibraryPath = SelectLibrary();
            if (_targetLibraryPath == null)
            {
                SilentLogger.Info($"[{Game.Name}] Install cancelled by user at library selection.");
                return;
            }

            SilentLogger.Info($"[{Game.Name}] Install started → {_targetLibraryPath}");
            SteamInstaller.Install(Game, _settings, _api, _targetLibraryPath);

            SilentLogger.Info($"[{Game.Name}] Starting monitoring thread…");
            _cts = new System.Threading.CancellationTokenSource();
            StartMonitoring(_cts.Token);
        }

        private string SelectLibrary()
        {
            var libs = _settings.DetectedSteamLibraries;
            SilentLogger.Info($"[{Game.Name}] SelectLibrary — {libs.Count} library(ies) available, mode={_api.ApplicationInfo.Mode}");

            if (libs.Count <= 1)
            {
                SilentLogger.Info($"[{Game.Name}] Single library — no picker needed: {_settings.SteamAppsPath}");
                return _settings.SteamAppsPath;
            }

            if (_api.ApplicationInfo.Mode == ApplicationMode.Fullscreen)
            {
                SilentLogger.Info($"[{Game.Name}] Fullscreen mode — skipping library picker, using default: {_settings.SteamAppsPath}");
                return _settings.SteamAppsPath;
            }

            SilentLogger.Info($"[{Game.Name}] Showing library picker dialog ({libs.Count} choices)…");
            var options = libs.Select(l => new GenericItemOption(l.Label, l.Path)).ToList();

            var chosen = _api.Dialogs.ChooseItemWithSearch(
                options,
                search => string.IsNullOrEmpty(search)
                    ? options
                    : options.Where(o => o.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).ToList(),
                string.Empty,
                "Silent Install — Choose a Steam library");

            if (chosen == null)
            {
                SilentLogger.Info($"[{Game.Name}] Library picker: user cancelled.");
                return null;
            }

            SilentLogger.Info($"[{Game.Name}] Library picker: user chose '{chosen.Name}' → {chosen.Description}");
            return chosen.Description;
        }

        private string InProgressNotifId => $"si-steam-{Game.GameId}";

        private void StartMonitoring(System.Threading.CancellationToken token)
        {
            var acfPath   = System.IO.Path.Combine(_targetLibraryPath, $"appmanifest_{Game.GameId}.acf");
            var steamApps = _targetLibraryPath;
            var gameName  = Game.Name;
            var gameId    = Game.GameId;

            SilentLogger.Info($"[{gameName}] Monitoring thread started — polling {acfPath} every 5 s (deadline: 24 h)");

            System.Threading.Tasks.Task.Run(() =>
            {
                var deadline        = DateTime.Now.AddHours(24);
                int errorStreak     = 0;
                int lastReportedPct = -1;
                int pollCount       = 0;
                const int maxStreak = 12;

                while (!token.IsCancellationRequested && DateTime.Now < deadline)
                {
                    System.Threading.Thread.Sleep(5000);
                    pollCount++;

                    try
                    {
                        if (!System.IO.File.Exists(acfPath))
                        {
                            if (pollCount % 6 == 0) // log every 30s to avoid spam
                                SilentLogger.Info($"[{gameName}] Poll #{pollCount} — ACF not found yet at {acfPath}");
                            errorStreak = 0;
                            continue;
                        }

                        var content   = System.IO.File.ReadAllText(acfPath);
                        var flagMatch = System.Text.RegularExpressions.Regex.Match(
                            content, "\"StateFlags\"\\s*\"(\\d+)\"");

                        if (!flagMatch.Success)
                        {
                            SilentLogger.Warn($"[{gameName}] Poll #{pollCount} — ACF exists but StateFlags not found. ACF size={content.Length} bytes");
                            errorStreak = 0;
                            continue;
                        }

                        var stateFlags = int.Parse(flagMatch.Groups[1].Value);

                        if (pollCount % 3 == 0) // log every 15s during active download
                            SilentLogger.Info($"[{gameName}] Poll #{pollCount} — StateFlags={stateFlags} (0x{stateFlags:X})");

                        var errorState = DetectSteamError(content, stateFlags, gameName);
                        if (errorState != null)
                        {
                            SilentLogger.Error($"[{gameName}] Fatal error detected at poll #{pollCount}: {errorState}");
                            SilentLogger.Info($"[{gameName}] Cleaning up ACF: {acfPath}");
                            try { System.IO.File.Delete(acfPath); SilentLogger.Info($"[{gameName}] ACF deleted."); }
                            catch (Exception delEx) { SilentLogger.Warn($"[{gameName}] Could not delete ACF: {delEx.Message}"); }
                            _api.Notifications.Remove(InProgressNotifId);
                            _api.Notifications.Add(new NotificationMessage(
                                $"si-steam-err-{gameId}",
                                $"❌ {gameName} — {errorState}",
                                NotificationType.Error));
                            return;
                        }

                        if (stateFlags != 4)
                        {
                            errorStreak = 0;
                            TryUpdateProgress(content, gameName, ref lastReportedPct);
                            continue;
                        }

                        // StateFlags=4 — fully installed
                        var dirMatch   = System.Text.RegularExpressions.Regex.Match(
                            content, "\"installdir\"\\s*\"([^\"]+)\"");
                        var installDir = dirMatch.Success
                            ? System.IO.Path.Combine(steamApps, "common", dirMatch.Groups[1].Value)
                            : System.IO.Path.Combine(steamApps, "common", gameName);

                        SilentLogger.Info($"[{gameName}] StateFlags=4 detected at poll #{pollCount} — installation complete!");
                        SilentLogger.Info($"[{gameName}] installdir from ACF: {(dirMatch.Success ? dirMatch.Groups[1].Value : "(not found, using game name)")}");
                        SilentLogger.Info($"[{gameName}] Full install path: {installDir}");
                        SilentLogger.Info($"[{gameName}] ACF kept for Steam tracking (updates/overlay/achievements).");

                        _api.Notifications.Remove(InProgressNotifId);
                        _api.Notifications.Add(new NotificationMessage(
                            $"si-steam-done-{gameId}",
                            $"✅ {gameName} — installation complete. Ready to play!",
                            NotificationType.Info));

                        SilentLogger.Info($"[{gameName}] Calling InvokeOnInstalled → InstallDirectory={installDir}");
                        InvokeOnInstalled(new GameInstalledEventArgs
                        {
                            InstalledInfo = new GameInstallationData { InstallDirectory = installDir }
                        });
                        SilentLogger.Info($"[{gameName}] Monitoring thread exiting normally after {pollCount} polls.");
                        return;
                    }
                    catch (Exception ex)
                    {
                        errorStreak++;
                        SilentLogger.Error($"[{gameName}] Monitoring exception at poll #{pollCount} (streak={errorStreak}/{maxStreak})", ex);

                        if (errorStreak == maxStreak)
                        {
                            SilentLogger.Warn($"[{gameName}] {maxStreak} consecutive errors — notifying user but continuing.");
                            _api.Notifications.Add(new NotificationMessage(
                                $"si-steam-warn-{gameId}",
                                $"⚠ {gameName} — trouble reading the appmanifest. Still monitoring…",
                                NotificationType.Error));
                        }
                    }
                }

                if (!token.IsCancellationRequested)
                {
                    SilentLogger.Warn($"[{gameName}] 24h timeout reached after {pollCount} polls — giving up.");
                    _api.Notifications.Remove(InProgressNotifId);
                    _api.Notifications.Add(new NotificationMessage(
                        $"si-steam-timeout-{gameId}",
                        $"⚠ {gameName} — monitoring stopped after 24 h. Check Steam's download queue.",
                        NotificationType.Error));
                }
                else
                {
                    SilentLogger.Info($"[{gameName}] Monitoring cancelled after {pollCount} polls (token was cancelled).");
                }

            }, token);
        }

        private static string DetectSteamError(string acfContent, int stateFlags, string gameName)
        {
            if (stateFlags == 0)
            {
                SilentLogger.Warn($"[{gameName}] StateFlags=0 (StateInvalid) — manifest is broken.");
                return "Steam set the manifest to an invalid state (StateFlags=0). Check Steam's download queue.";
            }

            if ((stateFlags & 128) != 0 && stateFlags != 4)
            {
                SilentLogger.Warn($"[{gameName}] StateFlags={stateFlags} has FilesCorrupt bit (128) set.");
                return "Steam reported corrupt files (StateFlags has FilesCorrupt bit). Try verifying integrity in Steam.";
            }

            if ((stateFlags & 32) != 0 && stateFlags != 4)
            {
                SilentLogger.Warn($"[{gameName}] StateFlags={stateFlags} has FilesMissing bit (32) set.");
                return "Steam reported missing files (StateFlags has FilesMissing bit). Check disk space and permissions.";
            }

            var resultMatch = System.Text.RegularExpressions.Regex.Match(
                acfContent, @"""UpdateResult""\s*""(\d+)""");
            if (resultMatch.Success)
            {
                var result = int.Parse(resultMatch.Groups[1].Value);
                if (result != 0)
                {
                    string reason;
                    switch (result)
                    {
                        case 2:  reason = "No connection to Steam content servers"; break;
                        case 6:  reason = "Disk write error — check permissions and free space"; break;
                        case 7:  reason = "Content still encrypted (game not released or region locked)"; break;
                        case 11: reason = "Disk write error (disk full or permissions issue)"; break;
                        default: reason = string.Format("Steam reported error code {0}", result); break;
                    }
                    SilentLogger.Warn(string.Format("[{0}] UpdateResult={1}: {2}", gameName, result, reason));
                    if (result == 6 || result == 11)
                        return string.Format("Disk error: {0}. Free up space and retry.", reason);
                }
            }

            return null;
        }

        private static string GetSteamRootFromRegistry()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")
                    ?? Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam"))
                {
                    var path = key?.GetValue("InstallPath") as string;
                    SilentLogger.Info($"GetSteamRootFromRegistry → {path ?? "(not found)"}");
                    return path;
                }
            }
            catch (Exception ex)
            {
                SilentLogger.Warn($"GetSteamRootFromRegistry failed: {ex.Message}");
                return null;
            }
        }

        private void TryUpdateProgress(string acfContent, string gameName, ref int lastPct)
        {
            var totalMatch = System.Text.RegularExpressions.Regex.Match(acfContent, @"""BytesToDownload""\s*""(\d+)""");
            if (!totalMatch.Success) return;
            var total = long.Parse(totalMatch.Groups[1].Value);
            if (total <= 0) return;
            var totalGb = total / 1_073_741_824.0;

            var downloadingInLib = System.IO.Path.Combine(_targetLibraryPath, "downloading", Game.GameId);
            var steamRoot        = GetSteamRootFromRegistry();
            var downloadingInDef = steamRoot != null
                ? System.IO.Path.Combine(steamRoot, "steamapps", "downloading", Game.GameId)
                : null;
            long folderBytesLib = GetDirectorySize(downloadingInLib);
            long folderBytesDef = downloadingInDef != null ? GetDirectorySize(downloadingInDef) : 0L;
            long folderBytes    = Math.Max(folderBytesLib, folderBytesDef);

            var dlMatch = System.Text.RegularExpressions.Regex.Match(acfContent, @"""BytesDownloaded""\s*""(\d+)""");
            long acfDownloaded = dlMatch.Success ? long.Parse(dlMatch.Groups[1].Value) : 0L;
            long downloaded = Math.Max(folderBytes, acfDownloaded);

            var writtenGb = downloaded / 1_073_741_824.0;
            int bucket = (int)(writtenGb * 10);
            if (bucket == lastPct) return;
            lastPct = bucket;

            SilentLogger.Info(string.Format(
                "[{0}] Progress: {1:F2} GB written / {2:F2} GB total | lib={3} bytes, def={4} bytes, acf={5} bytes",
                gameName, writtenGb, totalGb, folderBytesLib, folderBytesDef, acfDownloaded));

            _api.Notifications.Remove(InProgressNotifId);
            _api.Notifications.Add(new NotificationMessage(
                InProgressNotifId,
                downloaded > 0
                    ? string.Format("⬇ {0} — {1:F1} GB written / ~{2:F1} GB", gameName, writtenGb, totalGb)
                    : string.Format("⬇ {0} — downloading…  (~{1:F1} GB total)", gameName, totalGb),
                NotificationType.Info));
        }

        private static long GetDirectorySize(string path)
        {
            if (!System.IO.Directory.Exists(path)) return 0L;
            try
            {
                return new System.IO.DirectoryInfo(path)
                    .EnumerateFiles("*", System.IO.SearchOption.AllDirectories)
                    .Sum(f => { try { return f.Length; } catch { return 0L; } });
            }
            catch { return 0L; }
        }

        public override void Dispose()
        {
            SilentLogger.Info($"[{Game.Name}] Dispose() called — cancelling monitoring token.");
            _cts?.Cancel();

            var lib     = _targetLibraryPath ?? _settings.SteamAppsPath;
            var acfPath = System.IO.Path.Combine(lib, $"appmanifest_{Game.GameId}.acf");
            SilentLogger.Info($"[{Game.Name}] Checking ACF for cleanup: {acfPath}");
            try
            {
                if (System.IO.File.Exists(acfPath))
                {
                    var content   = System.IO.File.ReadAllText(acfPath);
                    var flagMatch = System.Text.RegularExpressions.Regex.Match(
                        content, "\"StateFlags\"\\s*\"(\\d+)\"");
                    var flags = flagMatch.Success ? flagMatch.Groups[1].Value : "unknown";
                    SilentLogger.Info($"[{Game.Name}] ACF exists, StateFlags={flags}");

                    if (flagMatch.Success && flagMatch.Groups[1].Value == "1026")
                    {
                        System.IO.File.Delete(acfPath);
                        SilentLogger.Info($"[{Game.Name}] ACF with StateFlags=1026 deleted (install was cancelled/incomplete).");
                    }
                    else
                    {
                        SilentLogger.Info($"[{Game.Name}] ACF kept (StateFlags={flags} — install completed or in progress by Steam).");
                    }
                }
                else
                {
                    SilentLogger.Info($"[{Game.Name}] ACF not found — nothing to clean up.");
                }
            }
            catch (Exception ex)
            {
                SilentLogger.Warn($"[{Game.Name}] Dispose cleanup error: {ex.Message}");
            }

            base.Dispose();
        }
    }

    // ── Uninstall controller ──────────────────────────────────────────────────

    public class SilentSteamUninstallController : UninstallController
    {
        private readonly PluginSettings _settings;
        private readonly IPlayniteAPI   _api;

        public SilentSteamUninstallController(Game game, PluginSettings settings, IPlayniteAPI api)
            : base(game)
        {
            Name      = "Silent Uninstall (Steam)";
            _settings = settings;
            _api      = api;
        }

        public override void Uninstall(UninstallActionArgs args)
        {
            try
            {
                SilentLogger.Info($"[{Game.Name}] Uninstall started (AppID={Game.GameId}).");
                SilentLogger.Info($"[{Game.Name}] InstallDirectory from Playnite: {Game.InstallDirectory ?? "(null)"}");

                var gameDir = Game.InstallDirectory;
                if (!string.IsNullOrEmpty(gameDir) && System.IO.Directory.Exists(gameDir))
                {
                    SilentLogger.Info($"[{Game.Name}] Deleting game directory: {gameDir}");
                    System.IO.Directory.Delete(gameDir, recursive: true);
                    SilentLogger.Info($"[{Game.Name}] Game directory deleted.");
                }
                else
                {
                    SilentLogger.Warn($"[{Game.Name}] Game directory not found or empty: '{gameDir}' — skipping file deletion.");
                }

                var acfPath = System.IO.Path.Combine(_settings.SteamAppsPath, $"appmanifest_{Game.GameId}.acf");
                SilentLogger.Info($"[{Game.Name}] Looking for ACF at: {acfPath}");
                try
                {
                    if (System.IO.File.Exists(acfPath))
                    {
                        System.IO.File.Delete(acfPath);
                        SilentLogger.Info($"[{Game.Name}] ACF deleted — Steam no longer tracks this game.");
                    }
                    else
                    {
                        SilentLogger.Info($"[{Game.Name}] ACF not found at expected path — already removed or different library.");
                    }
                }
                catch (Exception ex) { SilentLogger.Warn($"[{Game.Name}] Could not delete ACF: {ex.Message}"); }

                SilentLogger.Info($"[{Game.Name}] Uninstall complete. Calling InvokeOnUninstalled.");
                InvokeOnUninstalled(new GameUninstalledEventArgs());
            }
            catch (Exception ex)
            {
                SilentLogger.Error($"[{Game.Name}] Uninstall failed", ex);
                _api.Notifications.Add(new NotificationMessage(
                    $"si-steam-uninstall-err-{Game.GameId}",
                    $"⚠ Failed to uninstall {Game.Name}: {ex.Message}",
                    NotificationType.Error));
            }
        }
    }

    // ── Epic stub ─────────────────────────────────────────────────────────────

    public class SilentEpicInstallController : InstallController
    {
        private readonly PluginSettings _settings;
        private readonly IPlayniteAPI   _api;

        public SilentEpicInstallController(Game game, PluginSettings settings, IPlayniteAPI api)
            : base(game)
        {
            Name      = "Silent Install (Epic / Legendary)";
            _settings = settings;
            _api      = api;
        }

        public override void Install(InstallActionArgs args)
            => EpicInstaller.Install(Game, _settings, _api);
    }
}
