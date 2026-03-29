using System.Windows;
using System.Windows.Controls;
using SWF = System.Windows.Forms;

namespace SilentInstall.Settings
{
    public partial class SettingsView : UserControl
    {
        private readonly PluginSettings _settings;

        public SettingsView(PluginSettings settings)
        {
            InitializeComponent();
            _settings = settings;
            DataContext = settings;
        }

        private void BrowseCustomPath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SWF.FolderBrowserDialog
            {
                Description  = "Select your steamapps folder",
                SelectedPath = _settings.CustomPath
            };
            if (dialog.ShowDialog() == SWF.DialogResult.OK)
                _settings.CustomPath = dialog.SelectedPath;
        }
    }
}
