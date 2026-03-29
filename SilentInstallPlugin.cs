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

        private void StartMonitoring(System.Threading.CancellationToken token)
        {
            var acfPath   = System.IO.Path.Combine(_settings.SteamAppsPath, $"appmanifest_{Game.GameId}.acf");
            var steamApps = _settings.SteamAppsPath;

            System.Threading.Tasks.Task.Run(() =>
            {
                // Poll every 5 seconds for up to 24 hours
                var deadline = DateTime.Now.AddHours(24);

                while (!token.IsCancellationRequested && DateTime.Now < deadline)
                {
                    System.Threading.Thread.Sleep(5000);

                    try
                    {
                        if (!System.IO.File.Exists(acfPath)) continue;

                        var content   = System.IO.File.ReadAllText(acfPath);
                        var flagMatch = System.Text.RegularExpressions.Regex.Match(content, "\"StateFlags\"\\s*\"(\\d+)\"");

                        if (!flagMatch.Success) continue;
                        if (int.Parse(flagMatch.Groups[1].Value) != 4) continue;

                        // StateFlags=4 means fully installed
                        var dirMatch   = System.Text.RegularExpressions.Regex.Match(content, "\"installdir\"\\s*\"([^\"]+)\"");
                        var installDir = dirMatch.Success
                            ? System.IO.Path.Combine(steamApps, "common", dirMatch.Groups[1].Value)
                            : System.IO.Path.Combine(steamApps, "common", Game.Name);

                        // Notify Playnite — let the Steam library plugin handle sync
                        InvokeOnInstalled(new GameInstalledEventArgs
                        {
                            InstalledInfo = new GameInstallationData { InstallDirectory = installDir }
                        });
                        return;
                    }
                    catch { /* retry on next poll */ }
                }
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
