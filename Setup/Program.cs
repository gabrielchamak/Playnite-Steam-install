using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace SilentInstallSetup
{
    static class Program
    {
        // Playnite Extensions folder — %APPDATA% already resolves to AppData\Roaming
        private static readonly string ExtensionFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Playnite", "Extensions", "SilentInstall");

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // ── Locate source files (next to Install.exe) ──────────────────
            var exeDir     = AppDomain.CurrentDomain.BaseDirectory;
            var payloadDir = Path.Combine(exeDir, "payload");
            var sourceDir  = Directory.Exists(payloadDir) ? payloadDir : exeDir;
            var sourceDll  = Path.Combine(sourceDir, "SilentInstall.dll");
            var sourceYaml = Path.Combine(sourceDir, "extension.yaml");

            if (!File.Exists(sourceDll) || !File.Exists(sourceYaml))
            {
                MessageBox.Show(
                    "Could not find the plugin files.\n\n" +
                    "Make sure you extracted the full zip without changing its structure before running the installer.",
                    "Silent Install Setup — Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // ── Read versions ───────────────────────────────────────────────
            var newVersion = ReadVersion(sourceYaml);
            var destYaml   = Path.Combine(ExtensionFolder, "extension.yaml");
            var isUpdate   = File.Exists(destYaml);
            var oldVersion = isUpdate ? ReadVersion(destYaml) : null;

            // ── Confirm dialog ──────────────────────────────────────────────
            string action = isUpdate
                ? $"Update  v{oldVersion}  →  v{newVersion}"
                : $"Install v{newVersion}";

            var confirm = MessageBox.Show(
                $"Silent Install — {action}\n\n" +
                $"Destination:\n{ExtensionFolder}\n\n" +
                "Continue?",
                "Silent Install Setup",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1);

            if (confirm != DialogResult.Yes) return;

            // ── Check if Playnite is running ────────────────────────────────
            if (IsPlayniteRunning())
            {
                var close = MessageBox.Show(
                    "Playnite is currently running.\n\n" +
                    "It must be closed before installing.\n\n" +
                    "Close Playnite now and continue?",
                    "Silent Install Setup",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (close != DialogResult.Yes) return;

                KillPlaynite();
                Thread.Sleep(1500); // let it fully exit
            }

            // ── Copy files ──────────────────────────────────────────────────
            try
            {
                Directory.CreateDirectory(ExtensionFolder);
                File.Copy(sourceDll,  Path.Combine(ExtensionFolder, "SilentInstall.dll"), overwrite: true);
                File.Copy(sourceYaml, Path.Combine(ExtensionFolder, "extension.yaml"),    overwrite: true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to copy files:\n\n{ex.Message}\n\n" +
                    "Try running Install.exe as Administrator.",
                    "Silent Install Setup — Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // ── Success ─────────────────────────────────────────────────────
            string verb = isUpdate ? "updated" : "installed";
            var restart = MessageBox.Show(
                $"✅ Silent Install v{newVersion} {verb} successfully!\n\n" +
                "Launch Playnite now?",
                "Silent Install Setup",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);

            if (restart == DialogResult.Yes)
                LaunchPlaynite();
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>Reads the Version field from extension.yaml (no YAML parser needed).</summary>
        private static string ReadVersion(string yamlPath)
        {
            try
            {
                foreach (var line in File.ReadAllLines(yamlPath))
                {
                    if (line.TrimStart().StartsWith("Version:", StringComparison.OrdinalIgnoreCase))
                        return line.Split(new[] { ':' }, 2)[1].Trim();
                }
            }
            catch { }
            return "?";
        }

        private static bool IsPlayniteRunning()
            => Process.GetProcessesByName("Playnite.DesktopApp").Length > 0
            || Process.GetProcessesByName("Playnite.FullscreenApp").Length > 0;

        private static void KillPlaynite()
        {
            foreach (var name in new[] { "Playnite.DesktopApp", "Playnite.FullscreenApp" })
                foreach (var p in Process.GetProcessesByName(name))
                    try { p.Kill(); } catch { }
        }

        private static void LaunchPlaynite()
        {
            // Try the default install location; Playnite also registers a protocol handler
            var local    = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var exe      = Path.Combine(local, "Playnite", "Playnite.DesktopApp.exe");
            if (File.Exists(exe))
                Process.Start(exe);
            else
                // Fallback: open via Start Menu shortcut / shell
                Process.Start(new ProcessStartInfo("playnite://") { UseShellExecute = true });
        }
    }
}
