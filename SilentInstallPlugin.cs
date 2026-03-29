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

        public override IEnumerable<UninstallController> GetUninstallActions(GetUninstallActionsArgs args)
        {
            if (SteamInstaller.CanHandle(args.Game))
                yield return new SilentSteamUninstallController(args.Game, _settings, PlayniteApi);
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
        /// Reads BytesDownloaded / BytesToDownload from the ACF and updates
        /// the in-progress notification with a percentage + GB progress.
        /// Only fires when the percentage actually changes to avoid notification spam.
        /// </summary>
        private void TryUpdateProgress(string acfContent, string gameName, ref int lastPct)
        {
            var dlMatch    = System.Text.RegularExpressions.Regex.Match(acfContent, "\"BytesDownloaded\"\\s*\"(\\d+)\"");
            var totalMatch = System.Text.RegularExpressions.Regex.Match(acfContent, "\"BytesToDownload\"\\s*\"(\\d+)\"");

            if (!dlMatch.Success || !totalMatch.Success) return;

            var downloaded = long.Parse(dlMatch.Groups[1].Value);
            var total      = long.Parse(totalMatch.Groups[1].Value);

            if (total <= 0) return;

            var pct = (int)(downloaded * 100L / total);
            if (pct == lastPct) return; // no change — skip update

            lastPct = pct;
            var dlGb    = downloaded / 1_073_741_824.0;
            var totalGb = total      / 1_073_741_824.0;

            SilentLogger.Info($"[{gameName}] Progress: {pct}% ({dlGb:F2} / {totalGb:F2} GB)");

            _api.Notifications.Remove(InProgressNotifId);
            _api.Notifications.Add(new NotificationMessage(
                InProgressNotifId,
                $"⬇ {gameName} — {pct}%  ({dlGb:F1} / {totalGb:F1} GB)",
                NotificationType.Info));
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
                SilentLogger.Info($"[{Game.Name}] Uninstall started.");

                // ACF is already deleted post-install — use Playnite's stored install directory
                var gameDir = Game.InstallDirectory;
                if (!string.IsNullOrEmpty(gameDir) && System.IO.Directory.Exists(gameDir))
                {
                    System.IO.Directory.Delete(gameDir, recursive: true);
                    SilentLogger.Info($"[{Game.Name}] Game directory deleted: {gameDir}");
                }

                // Safety net — delete ACF in case of a partial/failed install
                var acfPath = System.IO.Path.Combine(_settings.SteamAppsPath, $"appmanifest_{Game.GameId}.acf");
                try { System.IO.File.Delete(acfPath); } catch { }

                SilentLogger.Info($"[{Game.Name}] Uninstall complete.");
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
