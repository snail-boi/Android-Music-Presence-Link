using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace musicpresense
{
    public partial class MainWindow
    {
        private async void BtnBrowseLyricsFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var device = await GetCurrentDeviceForAppsAsync();
                if (string.IsNullOrWhiteSpace(device))
                {
                    MessageBox.Show("No device connected.", "Device Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var picker = new RemoteFolderPicker(device)
                {
                    Owner = this
                };

                if (picker.ShowDialog() == true)
                {
                    var selectedFolder = picker.SelectedFolder?.Trim() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(selectedFolder))
                    {
                        TxtLyricsFolderOverride.Text = selectedFolder;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to pick lyrics folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static List<string> GetNormalizedRemoteRoots(MusicConfig config)
        {
            var roots = (config.MusicRemoteRoots ?? new List<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (roots.Count == 0 && !string.IsNullOrWhiteSpace(config.MusicRemoteRoot))
            {
                roots.Add(config.MusicRemoteRoot.Trim());
            }

            return roots;
        }

        private async void BtnPickRemoteRoot_Click(object sender, RoutedEventArgs e)
        {
            var device = await GetCurrentDeviceForAppsAsync();
            if (string.IsNullOrWhiteSpace(device))
            {
                MessageBox.Show("No device connected.", "Device Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var picker = new RemoteFolderPicker(device)
            {
                Owner = this
            };

            if (picker.ShowDialog() == true)
            {
                var selectedFolder = picker.SelectedFolder?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(selectedFolder))
                    return;

                if (_remoteRoots.Any(p => string.Equals(p, selectedFolder, StringComparison.OrdinalIgnoreCase)))
                    return;

                _remoteRoots.Add(selectedFolder);
            }
        }

        private void BtnRemoveRemoteRoot_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button)
                return;

            if (button.Tag is not string root || string.IsNullOrWhiteSpace(root))
                return;

            var existing = _remoteRoots.FirstOrDefault(p => string.Equals(p, root, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                _remoteRoots.Remove(existing);
            }
        }
    }
}
