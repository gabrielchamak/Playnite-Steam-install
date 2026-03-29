using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
using Playnite.SDK;
using Playnite.SDK.Models;
using SilentInstall.Settings;

namespace SilentInstall.Installers
{
    /// <summary>
    /// Handles silent Steam game installation using the appmanifest trick.
    ///
    /// How it works:
    ///   Steam monitors its steamapps folders at startup. When it finds an
    ///   appmanifest file with StateFlags=1026 ("update started + required"),
    ///   it automatically begins downloading the game — no dialog, no interaction.
    ///
    /// References:
    ///   https://github.com/pinkwah/steam-appmanifest
    /// </summary>
    public static class SteamInstaller
    {
        public static readonly Guid SteamPluginId = Guid.Parse("cb91dfc9-b977-43bf-8e70-55f46e410fab");

        public static bool CanHandle(Game game) => game.PluginId == SteamPluginId;

        /// <summary>
        /// Installs a Steam game silently:
        /// 1. Ensures the steamapps folder is registered in Steam's library config
        /// 2. Creates an appmanifest file with StateFlags=1026
        /// 3. Restarts Steam silently so it picks up the new manifest
        /// </summary>
        public static void Install(Game game, PluginSettings settings, IPlayniteAPI api)
        {
            try
            {
                ValidateSettings(settings);

                // Make sure this library is registered in Steam's libraryfolders.vdf
                EnsureSteamLibraryRegistered(settings.SteamAppsPath);

                var acfPath    = Path.Combine(settings.SteamAppsPath, $"appmanifest_{game.GameId}.acf");
                var installDir = FetchInstallDirFromSteamApi(game.GameId, game.Name);

                if (File.Exists(acfPath)) return; // already in progress

                WriteAppManifest(acfPath, game.GameId, installDir);
                RestartSteamSilently();

                api.Notifications.Add(new NotificationMessage(
                    $"si-steam-{game.GameId}",
                    $"⬇ {game.Name} — queued in Steam. Monitoring until complete…",
                    NotificationType.Info));
            }
            catch (Exception ex)
            {
                api.Notifications.Add(new NotificationMessage(
                    $"si-steam-err-{game.GameId}",
                    $"Silent Install error: {ex.Message}",
                    NotificationType.Error));
            }
        }

        // ── Private helpers ───────────────────────────────────────────────

        private static void ValidateSettings(PluginSettings settings)
        {
            if (string.IsNullOrWhiteSpace(settings.SteamAppsPath))
                throw new InvalidOperationException(
                    "Steam library path is not configured. Open plugin settings and select a steamapps folder.");

            if (!Directory.Exists(settings.SteamAppsPath))
                throw new InvalidOperationException(
                    $"steamapps folder not found: {settings.SteamAppsPath}");
        }

        /// <summary>
        /// Creates the appmanifest ACF file.
        /// StateFlags=1026 = "update started + update required" → Steam auto-downloads.
        /// </summary>
        private static void WriteAppManifest(string acfPath, string appId, string installDir)
        {
            var content =
                "\"AppState\"\n" +
                "{\n" +
                $"\t\"appid\"\t\t\"{appId}\"\n" +
                $"\t\"Universe\"\t\"1\"\n" +
                $"\t\"installdir\"\t\"{installDir}\"\n" +
                $"\t\"StateFlags\"\t\"1026\"\n" +
                "}\n";

            File.WriteAllText(acfPath, content);
        }

        /// <summary>
        /// Shuts down Steam completely then restarts it silently (system tray only).
        /// This is required because Steam only reads appmanifest files at startup.
        /// </summary>
        private static void RestartSteamSilently()
        {
            var steamExe = GetSteamExePath();
            if (steamExe == null || !File.Exists(steamExe)) return;

            // Send shutdown signal
            Process.Start(new ProcessStartInfo
            {
                FileName        = steamExe,
                Arguments       = "-shutdown",
                UseShellExecute = false,
                CreateNoWindow  = true
            });

            // Wait for all Steam processes to fully exit (up to 15 seconds)
            var deadline = DateTime.Now.AddSeconds(15);
            while (DateTime.Now < deadline)
            {
                Thread.Sleep(1000);
                if (Process.GetProcessesByName("steam").Length == 0) break;
            }

            // Force-kill any remaining Steam processes
            foreach (var proc in Process.GetProcessesByName("steam"))
            {
                try { proc.Kill(); } catch { }
            }

            Thread.Sleep(1500);

            // Restart with -silent (system tray only, no main window)
            Process.Start(new ProcessStartInfo
            {
                FileName        = steamExe,
                Arguments       = "-silent",
                UseShellExecute = false,
                CreateNoWindow  = true
            });

            // Open the downloads page to trigger Steam's download queue processing
            Thread.Sleep(4000);
            Process.Start("steam://open/downloads");
        }

        /// <summary>
        /// Queries the Steam Store API to get the official installation directory name.
        /// Falls back to the game name from Playnite if the API is unreachable.
        /// </summary>
        private static string FetchInstallDirFromSteamApi(string appId, string fallbackName)
        {
            try
            {
                var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic";
                using (var client = new WebClient())
                {
                    client.Headers.Add("User-Agent", "Mozilla/5.0");
                    var json  = client.DownloadString(url);
                    var match = Regex.Match(json, "\"name\"\\s*:\\s*\"([^\"]+)\"");
                    if (match.Success)
                        return SanitizeDirectoryName(match.Groups[1].Value);
                }
            }
            catch { /* fall through to fallback */ }

            return SanitizeDirectoryName(fallbackName);
        }

        /// <summary>
        /// Ensures the given steamapps folder is registered in Steam's libraryfolders.vdf.
        /// This allows Playnite's Steam plugin to scan the folder during library updates.
        /// </summary>
        private static void EnsureSteamLibraryRegistered(string steamAppsDir)
        {
            try
            {
                var steamRoot = GetSteamRootPath();
                if (steamRoot == null) return;

                var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf)) return;

                // libraryfolders.vdf stores the parent of steamapps (e.g. E:\SteamLibrary)
                var libraryRoot = Path.GetDirectoryName(steamAppsDir);
                if (string.IsNullOrEmpty(libraryRoot)) return;

                var content = File.ReadAllText(vdf);
                var escaped = libraryRoot.Replace("\\", "\\\\");
                if (content.Contains(escaped)) return; // already registered

                // Find the next available index
                var maxIdx = 0;
                foreach (Match m in Regex.Matches(content, "\"(\\d+)\""))
                    if (int.TryParse(m.Groups[1].Value, out int idx) && idx > maxIdx)
                        maxIdx = idx;

                var entry = $"\t\"{maxIdx + 1}\"\n\t{{\n\t\t\"path\"\t\t\"{escaped}\"\n\t}}\n";
                var insertAt = content.LastIndexOf('}');
                if (insertAt > 0)
                    File.WriteAllText(vdf, content.Insert(insertAt, entry));
            }
            catch { /* non-critical, skip silently */ }
        }

        private static string GetSteamExePath()
        {
            var root = GetSteamRootPath();
            return root != null ? Path.Combine(root, "steam.exe") : null;
        }

        private static string GetSteamRootPath()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")
                    ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam"))
                    return key?.GetValue("InstallPath") as string;
            }
            catch { return null; }
        }

        private static string SanitizeDirectoryName(string name)
            => Regex.Replace(name, @"[<>:""/\\|?*]", string.Empty).Trim();
    }
}
