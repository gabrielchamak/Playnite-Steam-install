using System;
using System.IO;

namespace SilentInstall
{
    /// <summary>
    /// Lightweight file logger for Silent Install.
    /// Writes timestamped entries to SilentInstall.log in the plugin data directory.
    /// Automatically rotates at 1 MB (keeps one .old backup).
    /// </summary>
    public static class SilentLogger
    {
        private static string _logPath;
        private const long MaxBytes = 1_048_576; // 1 MB

        public static void Initialize(string pluginDataDir)
        {
            try
            {
                Directory.CreateDirectory(pluginDataDir);
                _logPath = Path.Combine(pluginDataDir, "SilentInstall.log");

                // Rotate if log exceeds 1 MB
                if (File.Exists(_logPath) && new FileInfo(_logPath).Length > MaxBytes)
                {
                    var backup = _logPath + ".old";
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Move(_logPath, backup);
                }

                Info("════════════════════════════════════════");
                Info("Silent Install — session started");
                Info("════════════════════════════════════════");
            }
            catch { /* logging must never crash the plugin */ }
        }

        public static void Info(string msg)                        => Write("INFO ", msg);
        public static void Warn(string msg)                        => Write("WARN ", msg);
        public static void Error(string msg, Exception ex = null)  => Write("ERROR",
            ex != null ? $"{msg} — {ex.GetType().Name}: {ex.Message}" : msg);

        private static void Write(string level, string msg)
        {
            if (_logPath == null) return;
            try
            {
                File.AppendAllText(_logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {msg}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
