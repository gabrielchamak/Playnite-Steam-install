using System;
using System.Collections.Generic;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using SilentInstall.Installers;
using SilentInstall.Settings;

namespace SilentInstall
{
    /// <summary>
    /// Silent Install — Playnite Plugin
    ///
    /// Installs Steam and Epic games silently without leaving Playnite Fullscreen mode.
    /// Intercepts Playnite's native Install button and handles installation in the background.
    ///
    /// Supports:
    ///   - Steam: appmanifest trick (StateFlags=1026), works with any Steam library folder
    ///   - Epic:  Legendary CLI (optional), falls back to Epic Launcher
    /// </summary>
    public class SilentInstallPlugin : GenericPlugin
    {
        private readonly PluginSettings _settings;

        public override Guid Id { get; } = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

        public SilentInstallPlugin(IPlayniteAPI api) : base(api)
        {
            _settings  = new PluginSettings(this);
            Properties = new GenericPluginProperties { HasSettings = true };
        }

        /// <summary>
        /// Provides custom install controllers that intercept Playnite's native
        /// Install button — works in both Desktop and Fullscreen (Ubiquity) modes.
        /// </summary>
        public override IEnumerable<InstallController> GetInstallActions(GetInstallActionsArgs args)
        {
            if (SteamInstaller.CanHandle(args.Game))
                yield return new SilentSteamInstallController(args.Game, _settings, PlayniteApi);
        }

        /// <summary>
        /// Adds a right-click menu option as an alternative trigger point.
        /// Useful when the default Install button is unavailable.
        /// </summary>
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

    /// <summary>
    /// Install controller for Steam games.
    /// Triggers silent installation and monitors the appmanifest file until
    /// the game is fully installed (StateFlags=4), then notifies Playnite.
    /// </summary>
    public class SilentSteamInstallController : InstallController
    {
        private static readonly ILogger Log = LogManager.GetLogger();
        private readonly PluginSettings _settings;
        private readonly IPlayniteAPI   _api;
        private System.Threading.CancellationTokenSource _cts;

        public SilentSteamInstallController(Game game, PluginSettings settings, IPlayniteAPI api)
            : base(game)
        {
            Name      = "Silent Install (Steam)";
            _settings = settings;
            _api      = api;
        }

        public override void Install(InstallActionArgs args)
        {
            // Trigger the download
            SteamInstaller.Install(Game, _settings, _api);

            // Monitor the appmanifest until installation is complete
            _cts = new System.Threading.CancellationTokenSource();
            StartMonitoring(_cts.Token);
        }

        // Shared notification ID for the "in-progress" banner — kept consistent so
        // we can remove it precisely when install finishes or fails.
        private string InProgressNotifId => $"si-steam-{Game.GameId}";

        private void StartMonitoring(System.Threading.CancellationToken token)
        {
            var acfPath   = System.IO.Path.Combine(_settings.SteamAppsPath, $"appmanifest_{Game.GameId}.acf");
            var steamApps = _settings.SteamAppsPath;
            var gameName  = Game.Name;

            System.Threading.Tasks.Task.Run(() =>
            {
                // Poll every 5 seconds for up to 24 hours
                var deadline      = DateTime.Now.AddHours(24);
                int errorStreak   = 0;
                const int maxStreak = 12; // ~1 minute of consecutive errors → report

                while (!token.IsCancellationRequested && DateTime.Now < deadline)
                {
                    System.Threading.Thread.Sleep(5000);

                    try
                    {
                        if (!System.IO.File.Exists(acfPath)) { errorStreak = 0; continue; }

                        var content   = System.IO.File.ReadAllText(acfPath);
                        var flagMatch = System.Text.RegularExpressions.Regex.Match(content, "\"StateFlags\"\\s*\"(\\d+)\"");

                        if (!flagMatch.Success) { errorStreak = 0; continue; }
                        if (int.Parse(flagMatch.Groups[1].Value) != 4) { errorStreak = 0; continue; }

                        // ── StateFlags=4 → fully installed ───────────────────────
                        var dirMatch   = System.Text.RegularExpressions.Regex.Match(content, "\"installdir\"\\s*\"([^\"]+)\"");
                        var installDir = dirMatch.Success
                            ? System.IO.Path.Combine(steamApps, "common", dirMatch.Groups[1].Value)
                            : System.IO.Path.Combine(steamApps, "common", gameName);

                        // Replace the "in progress" banner with a "done" banner
                        _api.Notifications.Remove(InProgressNotifId);
                        _api.Notifications.Add(new NotificationMessage(
                            $"si-steam-done-{Game.GameId}",
                            $"✅ {gameName} — installation complete. Ready to play!",
                            NotificationType.Info));

                        // Notify Playnite — let the Steam library plugin handle sync
                        InvokeOnInstalled(new GameInstalledEventArgs
                        {
                            InstalledInfo = new GameInstallationData { InstallDirectory = installDir }
                        });
                        return;
                    }
                    catch
                    {
                        errorStreak++;
                        if (errorStreak == maxStreak)
                        {
                            // Persistent read errors — warn the user but keep trying
                            _api.Notifications.Add(new NotificationMessage(
                                $"si-steam-warn-{Game.GameId}",
                                $"⚠ {gameName} — trouble reading the appmanifest. Still monitoring…",
                                NotificationType.Error));
                        }
                    }
                }

                // ── Loop exited without completing ───────────────────────────────
                if (!token.IsCancellationRequested)
                {
                    // 24-hour timeout reached
                    _api.Notifications.Remove(InProgressNotifId);
                    _api.Notifications.Add(new NotificationMessage(
                        $"si-steam-timeout-{Game.GameId}",
                        $"⚠ {gameName} — monitoring stopped after 24 h. Check Steam's download queue.",
                        NotificationType.Error));
                }
                // If cancelled, the user explicitly cancelled — no notification needed.

            }, token);
        }

        public override void Dispose()
        {
            _cts?.Cancel();
            base.Dispose();
        }
    }

    /// <summary>
    /// Install controller for Epic games via Legendary CLI.
    /// </summary>
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
