using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace musicpresense
{
    /// <summary>
    /// Folders group: the remote music roots and the optional lyrics folder override. Picking a
    /// folder opens the RemoteFolderPicker, which the view supplies through the PickRemoteFolder
    /// delegate; the VM owns the device resolution, the duplicate check, and the collection.
    /// </summary>
    internal sealed partial class SettingsViewModel
    {
        // Set by the window. Given a device serial, shows the picker and returns the chosen
        // folder, or null if cancelled.
        public Func<string, string?>? PickRemoteFolder { get; set; }

        public ObservableCollection<string> RemoteRoots { get; } = new();

        private string _lyricsFolder = string.Empty;
        public string LyricsFolder { get => _lyricsFolder; set => Set(ref _lyricsFolder, value); }

        public RelayCommand PickRemoteRootCommand { get; private set; } = null!;
        public RelayCommand<string> RemoveRemoteRootCommand { get; private set; } = null!;
        public RelayCommand BrowseLyricsCommand { get; private set; } = null!;

        partial void InitFolders()
        {
            PickRemoteRootCommand = new RelayCommand(async () => await PickRemoteRootAsync());
            RemoveRemoteRootCommand = new RelayCommand<string>(RemoveRemoteRoot);
            BrowseLyricsCommand = new RelayCommand(async () => await BrowseLyricsAsync());

            LoadFoldersFromConfig();
        }

        partial void LoadFoldersFromConfig()
        {
            RemoteRoots.Clear();
            foreach (var root in GetNormalizedRemoteRoots(_config))
                RemoteRoots.Add(root);

            _lyricsFolder = _config.LyricsSearchFolderOverride ?? string.Empty;
        }

        partial void ApplyFoldersToConfig(MusicConfig config)
        {
            config.MusicRemoteRoots = RemoteRoots
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            config.MusicRemoteRoot = config.MusicRemoteRoots.FirstOrDefault() ?? string.Empty;

            config.LyricsSearchFolderOverride = LyricsFolder.Trim();
        }

        private async Task PickRemoteRootAsync()
        {
            var device = await DeviceQuery.ResolveActiveDeviceAsync(_config);
            if (string.IsNullOrWhiteSpace(device))
            {
                Interaction?.ShowWarning("No device connected.", "Device Required");
                return;
            }

            var folder = PickRemoteFolder?.Invoke(device)?.Trim();
            if (string.IsNullOrWhiteSpace(folder))
                return;

            if (RemoteRoots.Any(p => string.Equals(p, folder, StringComparison.OrdinalIgnoreCase)))
                return;

            RemoteRoots.Add(folder);
        }

        private void RemoveRemoteRoot(string? root)
        {
            if (string.IsNullOrWhiteSpace(root))
                return;

            var existing = RemoteRoots.FirstOrDefault(p => string.Equals(p, root, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                RemoteRoots.Remove(existing);
        }

        private async Task BrowseLyricsAsync()
        {
            try
            {
                var device = await DeviceQuery.ResolveActiveDeviceAsync(_config);
                if (string.IsNullOrWhiteSpace(device))
                {
                    Interaction?.ShowWarning("No device connected.", "Device Required");
                    return;
                }

                var folder = PickRemoteFolder?.Invoke(device)?.Trim();
                if (!string.IsNullOrWhiteSpace(folder))
                    LyricsFolder = folder;
            }
            catch (Exception ex)
            {
                Interaction?.ShowWarning($"Failed to pick lyrics folder: {ex.Message}", "Error");
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
                roots.Add(config.MusicRemoteRoot.Trim());

            return roots;
        }
    }
}
