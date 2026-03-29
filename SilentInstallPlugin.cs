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
    public class SilentInstallPlugin : GenericPlugin
    {
        private readonly PluginSettings _settings;

        public override Guid Id { get; } = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

        public SilentInstallPlugin(IPlayniteAPI api) : base(api)
        {
            _settings  = new PluginSettings(this);
            Properties = new GenericPluginProperties { HasSettings = true };
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
                Description  = isSteam ? "Install silently (Steam)" : "Install silently (Epic)",
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
        private static readonly ILogger Log = LogManager.GetLogger();
        private readonly PluginSettings _settings;
        private readonly IPlayniteAPI   _api;
        private System.Threading.CancellationTokenSource _cts;
        private bool _installCompleted = false;

        public SilentSteamInstallController(Game game, PluginSettings settings, IPlayniteAPI api)
            : base(game)
        {
            Name      = "Silent Install (Steam)";
            _settings = settings;
            _api      = api;
        }

        public override void Install(InstallActionArgs args)
        {
            SteamInstaller.Install(Game, _settings, _api);
            _cts = new System.Threading.CancellationTokenSource();
            StartMonitoring(_cts.Token);
        }

        private string InProgressNotifId => $"si-steam-{Game.GameId}";

        private void StartMonitoring(System.Threading.CancellationToken token)
        {
            var acfPath   = System.IO.Path.Combine(_settings.SteamAppsPath, $"appmanifest_{Game.GameId}.acf");
            var steamApps = _settings.SteamAppsPath;
            var gameName  = Game.Name;

            System.Threading.Tasks.Task.Run(() =>
            {
                var deadline    = DateTime.Now.AddHours(24);
                int errorStreak = 0;
                const int maxStreak = 12; // ~1 min of consecutive errors

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

                        // StateFlags=4 — fully installed
                        var dirMatch   = System.Text.RegularExpressions.Regex.Match(content, "\"installdir\"\\s*\"([^\"]+)\"");
                        var installDir = dirMatch.Success
                            ? System.IO.Path.Combine(steamApps, "common", dirMatch.Groups[1].Value)
                            : System.IO.Path.Combine(steamApps, "common", gameName);

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
                    catch
                    {
                        errorStreak++;
                        if (errorStreak == maxStreak)
                            _api.Notifications.Add(new NotificationMessage(
                                $"si-steam-warn-{Game.GameId}",
                                $"⚠ {gameName} — trouble reading the appmanifest. Still monitoring…",
                                NotificationType.Error));
                    }
                }

                if (!token.IsCancellationRequested)
                {
                    _api.Notifications.Remove(InProgressNotifId);
                    _api.Notifications.Add(new NotificationMessage(
                        $"si-steam-timeout-{Game.GameId}",
                        $"⚠ {gameName} — monitoring stopped after 24 h. Check Steam's download queue.",
                        NotificationType.Error));
                }

            }, token);
        }

        public override void Dispose()
        {
            _cts?.Cancel();

            // If the install was cancelled before Steam picked it up (StateFlags still 1026),
            // delete the partial ACF so Steam doesn't re-queue the download on next startup.
            try
            {
                var acfPath = System.IO.Path.Combine(_settings.SteamAppsPath, $"appmanifest_{Game.GameId}.acf");
                if (System.IO.File.Exists(acfPath))
                {
                    var content   = System.IO.File.ReadAllText(acfPath);
                    var flagMatch = System.Text.RegularExpressions.Regex.Match(content, "\"StateFlags\"\\s*\"(\\d+)\"");
                    if (flagMatch.Success && flagMatch.Groups[1].Value == "1026")
                        System.IO.File.Delete(acfPath);
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
                var acfPath = System.IO.Path.Combine(_settings.SteamAppsPath, $"appmanifest_{Game.GameId}.acf");

                // Read install directory from ACF before deleting it
                string gameDir = null;
                if (System.IO.File.Exists(acfPath))
                {
                    var content  = System.IO.File.ReadAllText(acfPath);
                    var dirMatch = System.Text.RegularExpressions.Regex.Match(content, "\"installdir\"\\s*\"([^\"]+)\"");
                    if (dirMatch.Success)
                        gameDir = System.IO.Path.Combine(_settings.SteamAppsPath, "common", dirMatch.Groups[1].Value);

                    // Remove the ACF — Steam will no longer track the game as installed
                    System.IO.File.Delete(acfPath);
                }

                // Delete game files if the folder exists
                if (gameDir != null && System.IO.Directory.Exists(gameDir))
                    System.IO.Directory.Delete(gameDir, recursive: true);

                InvokeOnUninstalled(new GameUninstalledEventArgs());
            }
            catch (Exception ex)
            {
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
