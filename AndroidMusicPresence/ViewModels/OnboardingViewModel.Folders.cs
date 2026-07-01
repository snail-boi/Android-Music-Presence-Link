using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// Folders step: the list of remote music roots and the optional lyrics folder override.
    ///
    /// Picking a folder opens the existing RemoteFolderPicker, which is a view concern, so the
    /// view supplies it through the injected PickRemoteFolder delegate. The VM still owns the
    /// device resolution, the duplicate check, and the collection itself.
    /// </summary>
    internal sealed partial class OnboardingViewModel
    {
        // Set by the view after construction. Given a device serial, shows the remote folder
        // picker and returns the chosen folder, or null if cancelled.
        public Func<string, string?>? PickRemoteFolder { get; set; }

        public ObservableCollection<string> RemoteRoots { get; } = new();

        private string _lyricsFolder = string.Empty;
        public string LyricsFolder
        {
            get => _lyricsFolder;
            set => Set(ref _lyricsFolder, value);
        }

        public RelayCommand PickRemoteRootCommand { get; private set; } = null!;
        public RelayCommand<string> RemoveRemoteRootCommand { get; private set; } = null!;
        public RelayCommand BrowseLyricsCommand { get; private set; } = null!;

        private void InitFolders()
        {
            PickRemoteRootCommand = new RelayCommand(async () => await PickRemoteRootAsync());
            RemoveRemoteRootCommand = new RelayCommand<string>(RemoveRemoteRoot);
            BrowseLyricsCommand = new RelayCommand(async () => await BrowseLyricsAsync());

            foreach (var root in GetNormalizedRemoteRoots(_workingConfig))
                RemoteRoots.Add(root);

            _lyricsFolder = _workingConfig.Library.LyricsSearchFolderOverride ?? string.Empty;
        }

        private void CommitFoldersToConfig()
        {
            _workingConfig.Library.MusicRemoteRoots = RemoteRoots
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            _workingConfig.Library.MusicRemoteRoot = _workingConfig.Library.MusicRemoteRoots.FirstOrDefault() ?? string.Empty;

            _workingConfig.Library.LyricsSearchFolderOverride = LyricsFolder.Trim();
        }

        private async Task PickRemoteRootAsync()
        {
            var device = await DeviceQuery.ResolveActiveDeviceAsync(_workingConfig);
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
            var device = await DeviceQuery.ResolveActiveDeviceAsync(_workingConfig);
            if (string.IsNullOrWhiteSpace(device))
            {
                Interaction?.ShowWarning("No device connected.", "Device Required");
                return;
            }

            var folder = PickRemoteFolder?.Invoke(device)?.Trim();
            if (!string.IsNullOrWhiteSpace(folder))
                LyricsFolder = folder;
        }

        private static List<string> GetNormalizedRemoteRoots(MusicConfig config)
        {
            var roots = (config.Library.MusicRemoteRoots ?? new List<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (roots.Count == 0 && !string.IsNullOrWhiteSpace(config.Library.MusicRemoteRoot))
                roots.Add(config.Library.MusicRemoteRoot.Trim());

            return roots;
        }
    }
}
