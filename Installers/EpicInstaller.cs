using System;
using System.Diagnostics;
using Playnite.SDK;
using Playnite.SDK.Models;
using SilentInstall.Settings;

namespace SilentInstall.Installers
{
    /// <summary>
    /// Handles Epic Games installation.
    /// Currently opens the Epic Launcher on the game page.
    /// Full silent install via Legendary CLI will be added in a future version.
    /// </summary>
    public static class EpicInstaller
    {
        public static readonly Guid EpicPluginId = Guid.Parse("00000002-dbd1-46c6-b5d0-b1ba559d10e4");

        public static bool CanHandle(Game game) => game.PluginId == EpicPluginId;

        public static bool IsConfigured(PluginSettings settings) => false; // Epic not yet supported

        public static void Install(Game game, PluginSettings settings, IPlayniteAPI api)
        {
            try
            {
                Process.Start($"com.epicgames.launcher://apps/{game.GameId}?action=install");
            }
            catch (Exception ex)
            {
                api.Notifications.Add(new NotificationMessage(
                    $"si-epic-err-{game.GameId}",
                    $"Epic install error: {ex.Message}",
                    NotificationType.Error));
            }
        }
    }
}
