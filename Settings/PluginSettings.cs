using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace SilentInstall.Settings
{
    public class SteamLibraryOption
    {
        public string Path  { get; set; }
        public string Label { get; set; }
    }

    public class PluginSettings : ObservableObject, ISettings
    {
        private readonly SilentInstallPlugin _plugin;

        [DontSerialize]
        public List<SteamLibraryOption> DetectedSteamLibraries { get; private set; } = new List<SteamLibraryOption>();

        // Whether the user wants to override the auto-detected path
        private bool _useCustomPath = false;
        public bool UseCustomPath
        {
            get => _useCustomPath;
            set { _useCustomPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(SteamAppsPath)); }
        }

        // Auto-detected path (from dropdown)
        private string _selectedLibraryPath = string.Empty;
        public string SelectedLibraryPath
        {
            get => _selectedLibraryPath;
            set { _selectedLibraryPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(SteamAppsPath)); }
        }

        // Manual override path
        private string _customPath = string.Empty;
        public string CustomPath
        {
            get => _customPath;
            set { _customPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(SteamAppsPath)); }
        }

        /// <summary>The active steamapps path — either auto-detected or custom.</summary>
        [DontSerialize]
        public string SteamAppsPath => UseCustomPath && !string.IsNullOrWhiteSpace(_customPath)
            ? _customPath
            : _selectedLibraryPath;

        public PluginSettings() { }

        public PluginSettings(SilentInstallPlugin plugin)
        {
            _plugin = plugin;
            DetectSteamLibraries();

            var saved = plugin.LoadPluginSettings<PluginSettings>();
            if (saved != null)
            {
                UseCustomPath        = saved.UseCustomPath;
                SelectedLibraryPath  = saved.SelectedLibraryPath;
                CustomPath           = saved.CustomPath;
            }

            if (string.IsNullOrEmpty(SelectedLibraryPath) && DetectedSteamLibraries.Count > 0)
                SelectedLibraryPath = DetectedSteamLibraries[0].Path;
        }

        private void DetectSteamLibraries()
        {
            DetectedSteamLibraries.Clear();
            try
            {
                var steamRoot = GetSteamRootPath();
                if (steamRoot == null) return;

                AddLibrary(Path.Combine(steamRoot, "steamapps"), "Steam (default)");

                var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf)) return;

                var content = File.ReadAllText(vdf);
                foreach (Match m in Regex.Matches(content, "\"path\"\\s+\"([^\"]+)\""))
                {
                    var libPath   = m.Groups[1].Value.Replace("\\\\", "\\");
                    var drive     = Path.GetPathRoot(libPath)?.TrimEnd('\\');
                    AddLibrary(Path.Combine(libPath, "steamapps"), $"Steam Library ({drive})");
                }
            }
            catch { }
        }

        private void AddLibrary(string path, string label)
        {
            if (!Directory.Exists(path)) return;
            if (DetectedSteamLibraries.Any(l => l.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
            try
            {
                var drive   = new DriveInfo(Path.GetPathRoot(path));
                var freeGb  = drive.AvailableFreeSpace / 1_073_741_824.0;
                var totalGb = drive.TotalSize / 1_073_741_824.0;
                var name    = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? Path.GetPathRoot(path)?.TrimEnd('\\') : drive.VolumeLabel;
                DetectedSteamLibraries.Add(new SteamLibraryOption
                {
                    Path  = path,
                    Label = $"{name} — {path}  ({freeGb:F0} GB free / {totalGb:F0} GB)"
                });
            }
            catch { DetectedSteamLibraries.Add(new SteamLibraryOption { Path = path, Label = label }); }
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

        public void BeginEdit() { }
        public void CancelEdit() { }
        public void EndEdit() => _plugin?.SavePluginSettings(this);
        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            if (string.IsNullOrWhiteSpace(SteamAppsPath))
                errors.Add("Steam steamapps path is required.");
            else if (!Directory.Exists(SteamAppsPath))
                errors.Add($"steamapps folder not found: {SteamAppsPath}");
            return errors.Count == 0;
        }
    }
}
