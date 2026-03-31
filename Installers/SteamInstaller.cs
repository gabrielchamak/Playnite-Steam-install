using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        public static bool CanHandle(Game game) => game.PluginId == SteamPluginId;

        /// <summary>
        /// Installs a Steam game silently:
        /// 1. Ensures the steamapps folder is registered in Steam's library config
        /// 2. Creates an appmanifest file with StateFlags=1026
        /// 3. Restarts Steam silently so it picks up the new manifest
        /// </summary>
        /// <param name="targetLibraryPath">
        /// Steamapps folder to install into. Falls back to settings.SteamAppsPath if null.
        /// </param>
        public static void Install(Game game, PluginSettings settings, IPlayniteAPI api,
                                   string targetLibraryPath = null)
        {
            try
            {
                var libraryPath = !string.IsNullOrEmpty(targetLibraryPath)
                    ? targetLibraryPath
                    : settings.SteamAppsPath;

                SilentLogger.Info($"[{game.Name}] Preparing install in: {libraryPath}");
                ValidateLibraryPath(libraryPath);
                CheckDiskSpace(libraryPath, game.Name);

                // Make sure this library is registered in Steam's libraryfolders.vdf
                EnsureSteamLibraryRegistered(libraryPath);

                var acfPath    = Path.Combine(libraryPath, $"appmanifest_{game.GameId}.acf");
                var installDir = FetchInstallDirFromSteamApi(game.GameId, game.Name);

                SilentLogger.Info($"[{game.Name}] installdir resolved to: {installDir}");

                if (File.Exists(acfPath))
                {
                    SilentLogger.Warn($"[{game.Name}] ACF already exists — install already in progress.");
                    return;
                }

                WriteAppManifest(acfPath, game.GameId, installDir);
                SilentLogger.Info($"[{game.Name}] ACF written: {acfPath}");

                TriggerDownloadOrRestart(libraryPath, game.GameId);
                SilentLogger.Info($"[{game.Name}] Download trigger complete.");

                // Warn if low disk space (not a hard block, but likely to fail for large games)
                try
                {
                    var drive   = new DriveInfo(Path.GetPathRoot(libraryPath));
                    var freeGb  = drive.AvailableFreeSpace / 1_073_741_824.0;
                    var driveLbl = Path.GetPathRoot(libraryPath) ?? libraryPath;
                    if (freeGb < 5.0)
                        api.Notifications.Add(new NotificationMessage(
                            $"si-steam-diskwarn-{game.GameId}",
                            $"⚠ {game.Name} — only {freeGb:F1} GB free on {driveLbl}. Download may fail if the game is large.",
                            NotificationType.Error));
                }
                catch { }

                api.Notifications.Add(new NotificationMessage(
                    $"si-steam-{game.GameId}",
                    $"⬇ {game.Name} — queued in Steam. Monitoring until complete…",
                    NotificationType.Info));
            }
            catch (Exception ex)
            {
                SilentLogger.Error($"[{game.Name}] Install failed", ex);
                api.Notifications.Add(new NotificationMessage(
                    $"si-steam-err-{game.GameId}",
                    $"Silent Install error: {ex.Message}",
                    NotificationType.Error));
            }
        }

        // ── Private helpers ───────────────────────────────────────────────

        private static void ValidateLibraryPath(string libraryPath)
        {
            if (string.IsNullOrWhiteSpace(libraryPath))
                throw new InvalidOperationException(
                    "Steam library path is not configured. Open plugin settings and select a steamapps folder.");

            if (!Directory.Exists(libraryPath))
                throw new InvalidOperationException(
                    $"steamapps folder not found: {libraryPath}");

            SilentLogger.Info($"ValidateLibraryPath OK: {libraryPath}");
        }

        /// <summary>
        /// Checks that the target library drive has enough free space.
        /// Throws if less than 1 GB (hard block) — Steam can't even stage a download.
        /// Warns in the log if less than 5 GB (low but not blocking).
        /// </summary>
        private static void CheckDiskSpace(string libraryPath, string gameName)
        {
            try
            {
                var drive    = new DriveInfo(Path.GetPathRoot(libraryPath));
                var freeGb   = drive.AvailableFreeSpace / 1_073_741_824.0;
                var driveName = Path.GetPathRoot(libraryPath) ?? libraryPath;

                SilentLogger.Info($"[{gameName}] Disk space on {driveName}: {freeGb:F1} GB free");

                if (freeGb < 1.0)
                    throw new InvalidOperationException(
                        $"Not enough disk space on {driveName} ({freeGb:F1} GB free). " +
                        "At least 1 GB is required to start a download.");

                if (freeGb < 5.0)
                    SilentLogger.Warn($"[{gameName}] Low disk space on {driveName}: only {freeGb:F1} GB free. " +
                        "The download may fail if the game is large.");
            }
            catch (InvalidOperationException) { throw; } // re-throw our own
            catch (Exception ex)
            {
                SilentLogger.Warn($"[{gameName}] Could not check disk space: {ex.Message}");
                // Non-critical — don't block install if we can't check
            }
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
            SilentLogger.Info($"ACF written to {acfPath}:");
            SilentLogger.Info($"  appid={appId}  installdir={installDir}  StateFlags=1026");
        }

        /// <summary>
        /// Tries to trigger the download without restarting Steam first.
        /// Modern Steam (post ~2022) watches steamapps/ for new ACF files and
        /// may pick them up while running when the downloads page is opened.
        ///
        /// Strategy:
        ///   1. Open steam://open/downloads (may trigger a rescan)
        ///   2. Wait up to 20 seconds watching the downloading/ folder for activity
        ///   3. If activity detected → no restart needed ✅
        ///   4. If no activity → fall back to full restart
        /// </summary>
        private static void TriggerDownloadOrRestart(string steamAppsPath, string appId)
        {
            SilentLogger.Info($"[AppID {appId}] Attempting no-restart trigger via steam://open/downloads…");

            // Step 1: open the downloads page — may cause Steam to rescan steamapps/
            Process.Start("steam://open/downloads");

            // Step 2: watch for activity in steamapps/downloading/<appid>/
            var downloadingDir = Path.Combine(steamAppsPath, "downloading", appId);
            var steamRoot      = GetSteamRootPath();
            var downloadingDef = steamRoot != null
                ? Path.Combine(steamRoot, "steamapps", "downloading", appId)
                : null;

            SilentLogger.Info($"[AppID {appId}] Watching for activity:");
            SilentLogger.Info($"  lib path: {downloadingDir}");
            SilentLogger.Info($"  def path: {downloadingDef ?? "(no default Steam root)"}");

            var deadline = DateTime.Now.AddSeconds(20);
            int checkNum = 0;
            while (DateTime.Now < deadline)
            {
                Thread.Sleep(2000);
                checkNum++;

                bool activityLib = Directory.Exists(downloadingDir) &&
                    new DirectoryInfo(downloadingDir).EnumerateFiles("*", SearchOption.AllDirectories).Any();
                bool activityDef = downloadingDef != null && Directory.Exists(downloadingDef) &&
                    new DirectoryInfo(downloadingDef).EnumerateFiles("*", SearchOption.AllDirectories).Any();

                SilentLogger.Info($"[AppID {appId}] Check #{checkNum}: libExists={Directory.Exists(downloadingDir)} libActive={activityLib} defActive={activityDef}");

                if (activityLib || activityDef)
                {
                    SilentLogger.Info($"[AppID {appId}] Download started without restart ✅ (lib={activityLib} def={activityDef})");
                    return; // no restart needed
                }
            }

            // Step 3: no activity detected — Steam didn't pick up the ACF; restart required
            SilentLogger.Info($"[AppID {appId}] No activity detected — falling back to Steam restart…");
            RestartSteamSilently();
        }

        /// <summary>
        /// Shuts down Steam completely then restarts it silently (system tray only).
        /// Fallback used when the no-restart trigger doesn't work.
        /// </summary>
        private static void RestartSteamSilently()
        {
            var steamExe = GetSteamExePath();
            if (steamExe == null || !File.Exists(steamExe)) return;

            SilentLogger.Info($"Sending -shutdown to: {steamExe}");
            Process.Start(new ProcessStartInfo
            {
                FileName        = steamExe,
                Arguments       = "-shutdown",
                UseShellExecute = false,
                CreateNoWindow  = true
            });

            SilentLogger.Info("Waiting for all steam.exe processes to exit (max 15 s)…");
            var deadline = DateTime.Now.AddSeconds(15);
            while (DateTime.Now < deadline)
            {
                Thread.Sleep(1000);
                var remaining = Process.GetProcessesByName("steam");
                if (remaining.Length == 0) { SilentLogger.Info("All steam.exe processes exited."); break; }
                SilentLogger.Info($"  Still running: {remaining.Length} steam.exe process(es)…");
            }

            var stragglers = Process.GetProcessesByName("steam");
            if (stragglers.Length > 0)
            {
                SilentLogger.Warn($"Force-killing {stragglers.Length} remaining steam.exe process(es).");
                foreach (var proc in stragglers)
                    try { proc.Kill(); SilentLogger.Info($"  Killed PID {proc.Id}"); } catch { }
            }

            Thread.Sleep(1500);

            // Restart Steam silently.
            // steam.exe is a bootstrapper — it spawns a NEW steam.exe process and exits.
            // This means our Process handle is useless for window manipulation.
            // Instead, we poll for the Steam window by its known class name
            // and minimize it directly via FindWindow + ShowWindow.
            SilentLogger.Info($"Launching Steam: {steamExe} -silent -minimized");
            Process.Start(new ProcessStartInfo
            {
                FileName        = steamExe,
                Arguments       = "-silent -minimized",
                UseShellExecute = false,
                CreateNoWindow  = true
            });
            SilentLogger.Info("Steam bootstrapper launched. Starting window suppressor…");

            // Background thread: wait for Steam's window to appear, then minimize it.
            // Steam window class name is "vguiPopupWindow" (main) or we search by process name.
            System.Threading.Tasks.Task.Run(() =>
            {
                var deadline = DateTime.Now.AddSeconds(15);
                while (DateTime.Now < deadline)
                {
                    Thread.Sleep(1000);
                    try
                    {
                        // Find the Steam main window by searching all steam.exe processes
                        foreach (var proc in Process.GetProcessesByName("steam"))
                        {
                            try
                            {
                                proc.Refresh();
                                var hwnd = proc.MainWindowHandle;
                                if (hwnd != IntPtr.Zero)
                                {
                                    // SW_MINIMIZE = 6, SW_HIDE = 0, SW_SHOWMINNOACTIVE = 7
                                    ShowWindow(hwnd, 7); // minimize without activating
                                    SilentLogger.Info($"Steam window minimized (PID {proc.Id}, hWnd {hwnd}).");
                                    return;
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
                SilentLogger.Info("Steam started with no visible window (good — -silent flag worked).");
            });

            // Open downloads page to flush the queue
            Thread.Sleep(4000);
            Process.Start("steam://open/downloads");
        }

        /// <summary>
        /// Queries the Steam Store API to get the official installation directory name.
        ///
        /// The Steam Store API returns the *localized* game name which can be in any
        /// language (Russian, Chinese, etc.) — unsuitable as a directory name.
        /// We sanitize it and fall back to the Playnite name if it looks garbled.
        /// </summary>
        private static string FetchInstallDirFromSteamApi(string appId, string fallbackName)
        {
            var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic&cc=us&l=english";
            SilentLogger.Info($"Fetching installdir from Steam API: {url}");
            try
            {
                using (var client = new WebClient())
                {
                    client.Headers.Add("User-Agent", "Mozilla/5.0");
                    client.Encoding = System.Text.Encoding.UTF8;
                    var json  = client.DownloadString(url);
                    SilentLogger.Info($"Steam API response length: {json.Length} chars");
                    var match = Regex.Match(json, "\"name\"\\s*:\\s*\"([^\"]+)\"");
                    if (match.Success)
                    {
                        var rawName  = match.Groups[1].Value;
                        var apiName  = SanitizeDirectoryName(rawName);
                        var isAscii  = IsAsciiSafe(apiName);
                        SilentLogger.Info($"Steam API name: '{rawName}' → sanitized: '{apiName}' → ASCII safe: {isAscii}");
                        if (!string.IsNullOrWhiteSpace(apiName) && isAscii)
                        {
                            SilentLogger.Info($"Using API installdir: '{apiName}'");
                            return apiName;
                        }
                        SilentLogger.Warn($"API name rejected (non-ASCII or empty) — falling back to Playnite name.");
                    }
                    else
                    {
                        SilentLogger.Warn("Steam API response did not contain a 'name' field.");
                    }
                }
            }
            catch (Exception ex)
            {
                SilentLogger.Warn($"Steam API call failed: {ex.Message} — using fallback name.");
            }

            var fallback = SanitizeDirectoryName(fallbackName);
            SilentLogger.Info($"Using fallback installdir: '{fallback}' (from Playnite game name: '{fallbackName}')");
            return fallback;
        }

        /// <summary>Returns true if the string contains only ASCII printable characters.</summary>
        private static bool IsAsciiSafe(string s)
        {
            foreach (char c in s)
                if (c > 127) return false;
            return true;
        }

        /// <summary>
        /// Ensures the given steamapps folder is registered in Steam's libraryfolders.vdf.
        /// This allows Playnite's Steam plugin to scan the folder during library updates.
        /// </summary>
        private static void EnsureSteamLibraryRegistered(string steamAppsDir)
        {
            try
            {
                SilentLogger.Info($"EnsureSteamLibraryRegistered: checking {steamAppsDir}");
                var steamRoot = GetSteamRootPath();
                if (steamRoot == null) { SilentLogger.Warn("EnsureSteamLibraryRegistered: Steam root not found."); return; }

                var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
                SilentLogger.Info($"libraryfolders.vdf path: {vdf} (exists={File.Exists(vdf)})");
                if (!File.Exists(vdf)) return;

                var libraryRoot = Path.GetDirectoryName(steamAppsDir);
                if (string.IsNullOrEmpty(libraryRoot)) return;

                var content = File.ReadAllText(vdf);
                var escaped = libraryRoot.Replace("\\", "\\\\");

                if (content.Contains(escaped))
                {
                    SilentLogger.Info($"Library already registered in libraryfolders.vdf: {libraryRoot}");
                    return;
                }

                var maxIdx = 0;
                foreach (Match m in Regex.Matches(content, "\"(\\d+)\""))
                    if (int.TryParse(m.Groups[1].Value, out int idx) && idx > maxIdx)
                        maxIdx = idx;

                var entry = $"\t\"{maxIdx + 1}\"\n\t{{\n\t\t\"path\"\t\t\"{escaped}\"\n\t}}\n";
                var insertAt = content.LastIndexOf('}');
                if (insertAt > 0)
                {
                    File.WriteAllText(vdf, content.Insert(insertAt, entry));
                    SilentLogger.Info($"Library registered in libraryfolders.vdf at index {maxIdx + 1}: {libraryRoot}");
                }
            }
            catch (Exception ex)
            {
                SilentLogger.Warn($"EnsureSteamLibraryRegistered failed (non-critical): {ex.Message}");
            }
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
                {
                    var path = key?.GetValue("InstallPath") as string;
                    SilentLogger.Info($"GetSteamRootPath → {path ?? "(not found in registry)"}");
                    return path;
                }
            }
            catch (Exception ex)
            {
                SilentLogger.Warn($"GetSteamRootPath registry error: {ex.Message}");
                return null;
            }
        }

        private static string SanitizeDirectoryName(string name)
            => Regex.Replace(name, @"[<>:""/\\|?*]", string.Empty).Trim();
    }
}
