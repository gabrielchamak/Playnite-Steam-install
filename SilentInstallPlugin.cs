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
        }

        public override IEnumerable<InstallController> GetInstallActions(GetInstallActionsArgs args)
        {
            if (SteamInstaller.CanHandle(args.Game))
                yield return new SilentSteamInstallController(args.Game, _settings, PlayniteApi);
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

        /// <summary>Library path chosen by the user at install time (may differ from settings default).</summary>
        private string _targetLibraryPath;

        public SilentSteamInstallController(Game game, PluginSettings settings, IPlayniteAPI api)
            : base(game)
        {
            Name      = "Silent Install (Steam)";
            _settings = settings;
            _api      = api;
        }

        public override void Install(InstallActionArgs args)
        {
            // Let the user pick a library if multiple are available
            _targetLibraryPath = SelectLibrary();
            if (_targetLibraryPath == null)
            {
                // User cancelled the dialog — abort silently
                SilentLogger.Info($"[{Game.Name}] Install cancelled by user at library selection.");
                return;
            }

            SilentLogger.Info($"[{Game.Name}] Install started → {_targetLibraryPath}");
            SteamInstaller.Install(Game, _settings, _api, _targetLibraryPath);

            _cts = new System.Threading.CancellationTokenSource();
            StartMonitoring(_cts.Token);
        }

        /// <summary>
        /// Shows a library picker if multiple Steam libraries are available
        /// and the application is in Desktop mode.
        /// In Fullscreen mode, ChooseItemWithSearch is not implemented by Playnite —
        /// we fall back to the default library from settings silently.
        /// Returns null only if the user explicitly cancels in Desktop mode.
        /// </summary>
        private string SelectLibrary()
        {
            var libs = _settings.DetectedSteamLibraries;

            // Single library — no dialog needed regardless of mode
            if (libs.Count <= 1)
                return _settings.SteamAppsPath;

            // In Fullscreen mode, dialogs are not fully supported — use default library
            if (_api.ApplicationInfo.Mode == ApplicationMode.Fullscreen)
            {
                SilentLogger.Info($"Fullscreen mode — skipping library picker, using default: {_settings.SteamAppsPath}");
                return _settings.SteamAppsPath;
            }

            var options = libs
                .Select(l => new GenericItemOption(l.Label, l.Path))
                .ToList();

            var chosen = _api.Dialogs.ChooseItemWithSearch(
                options,
                search => string.IsNullOrEmpty(search)
                    ? options
                    : options.Where(o =>
                        o.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).ToList(),
                string.Empty,
                "Silent Install — Choose a Steam library");

            // Description holds the path (set via GenericItemOption ctor)
            return chosen?.Description;
        }

        private string InProgressNotifId => $"si-steam-{Game.GameId}";

        private void StartMonitoring(System.Threading.CancellationToken token)
        {
            var acfPath   = System.IO.Path.Combine(_targetLibraryPath, $"appmanifest_{Game.GameId}.acf");
            var steamApps = _targetLibraryPath;
            var gameName  = Game.Name;

            System.Threading.Tasks.Task.Run(() =>
            {
                var deadline        = DateTime.Now.AddHours(24);
                int errorStreak     = 0;
                int lastReportedPct = -1;
                const int maxStreak = 12; // ~1 min of consecutive errors

                while (!token.IsCancellationRequested && DateTime.Now < deadline)
                {
                    System.Threading.Thread.Sleep(5000);

                    try
                    {
                        if (!System.IO.File.Exists(acfPath)) { errorStreak = 0; continue; }

                        var content   = System.IO.File.ReadAllText(acfPath);
                        var flagMatch = System.Text.RegularExpressions.Regex.Match(
                            content, "\"StateFlags\"\\s*\"(\\d+)\"");

                        if (!flagMatch.Success) { errorStreak = 0; continue; }

                        var stateFlags = int.Parse(flagMatch.Groups[1].Value);

                        // ── Check for Steam error states ──────────────────────
                        // StateFlags is a bitmask; some bits indicate failure conditions.
                        // FilesCorrupt=128, FilesMissing=32, StateInvalid=0
                        // Also check UpdateResult field (non-zero = Steam reported an error)
                        var errorState = DetectSteamError(content, stateFlags, gameName);
                        if (errorState != null)
                        {
                            SilentLogger.Error($"[{gameName}] Steam error: {errorState}");
                            // Clean up the ACF and stop monitoring
                            try { System.IO.File.Delete(acfPath); } catch { }
                            _api.Notifications.Remove(InProgressNotifId);
                            _api.Notifications.Add(new NotificationMessage(
                                $"si-steam-err-{Game.GameId}",
                                $"❌ {gameName} — {errorState}",
                                NotificationType.Error));
                            return;
                        }

                        // ── Download in progress — show real percentage ───────
                        if (stateFlags != 4)
                        {
                            errorStreak = 0;
                            TryUpdateProgress(content, gameName, ref lastReportedPct);
                            continue;
                        }

                        // ── StateFlags=4 — fully installed ───────────────────
                        var dirMatch   = System.Text.RegularExpressions.Regex.Match(
                            content, "\"installdir\"\\s*\"([^\"]+)\"");
                        var installDir = dirMatch.Success
                            ? System.IO.Path.Combine(steamApps, "common", dirMatch.Groups[1].Value)
                            : System.IO.Path.Combine(steamApps, "common", gameName);

                        // Delete ACF — Steam won't auto-reinstall if the game is later uninstalled
                        try
                        {
                            System.IO.File.Delete(acfPath);
                            SilentLogger.Info($"[{gameName}] ACF deleted — Steam has no trace of this game.");
                        }
                        catch (Exception delEx) { SilentLogger.Warn($"[{gameName}] Could not delete ACF: {delEx.Message}"); }

                        SilentLogger.Info($"[{gameName}] Installation complete → {installDir}");

                        _api.Notifications.Remove(InProgressNotifId);
                        _api.Notifications.Add(new NotificationMessage(
                            $"si-steam-done-{Game.GameId}",
                            $"✅ {gameName} — installation complete. Ready to play!",
                            NotificationType.Info));

                        InvokeOnInstalled(new GameInstalledEventArgs
                        {
                            InstalledInfo = new GameInstallationData { InstallDirectory = installDir }
                        });
                        return;
                    }
                    catch (Exception ex)
                    {
                        errorStreak++;
                        SilentLogger.Error($"[{gameName}] Monitoring error (streak {errorStreak})", ex);

                        if (errorStreak == maxStreak)
                            _api.Notifications.Add(new NotificationMessage(
                                $"si-steam-warn-{Game.GameId}",
                                $"⚠ {gameName} — trouble reading the appmanifest. Still monitoring…",
                                NotificationType.Error));
                    }
                }

                // ── Timeout ───────────────────────────────────────────────────
                if (!token.IsCancellationRequested)
                {
                    SilentLogger.Warn($"[{gameName}] Monitoring timed out after 24 h.");
                    _api.Notifications.Remove(InProgressNotifId);
                    _api.Notifications.Add(new NotificationMessage(
                        $"si-steam-timeout-{Game.GameId}",
                        $"⚠ {gameName} — monitoring stopped after 24 h. Check Steam's download queue.",
                        NotificationType.Error));
                }

            }, token);
        }

        /// <summary>
        /// Analyses the ACF content and StateFlags for known Steam error conditions.
        /// Returns a human-readable error message, or null if no error detected.
        /// </summary>
        private static string DetectSteamError(string acfContent, int stateFlags, string gameName)
        {
            if (stateFlags == 0)
                return "Steam set the manifest to an invalid state (StateFlags=0). Check Steam's download queue.";

            if ((stateFlags & 128) != 0 && stateFlags != 4)
                return "Steam reported corrupt files (StateFlags has FilesCorrupt bit). Try verifying integrity in Steam.";

            if ((stateFlags & 32) != 0 && stateFlags != 4)
                return "Steam reported missing files (StateFlags has FilesMissing bit). Check disk space and permissions.";

            // UpdateResult field: non-zero = Steam reported an error during the last operation
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
                    return key?.GetValue("InstallPath") as string;
            }
            catch { return null; }
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

            SilentLogger.Info(string.Format("[{0}] Progress: {1:F2} GB written (lib={2} def={3})", gameName, writtenGb, folderBytesLib, folderBytesDef));
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
            _cts?.Cancel();

            // Clean up ACF if the install was cancelled before completing
            var lib     = _targetLibraryPath ?? _settings.SteamAppsPath;
            var acfPath = System.IO.Path.Combine(lib, $"appmanifest_{Game.GameId}.acf");
            try
            {
                if (System.IO.File.Exists(acfPath))
                {
                    var content   = System.IO.File.ReadAllText(acfPath);
                    var flagMatch = System.Text.RegularExpressions.Regex.Match(
                        content, "\"StateFlags\"\\s*\"(\\d+)\"");
                    if (flagMatch.Success && flagMatch.Groups[1].Value == "1026")
                    {
                        System.IO.File.Delete(acfPath);
                        SilentLogger.Info($"[{Game.Name}] Cancelled — ACF cleaned up.");
                    }
                }
            }
            catch { /* non-critical */ }

            base.Dispose();
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
