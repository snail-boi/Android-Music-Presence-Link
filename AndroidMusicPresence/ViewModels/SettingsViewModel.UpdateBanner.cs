namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// Update banner display state. The window subscribes to Updater.UpdateStatusChanged and
    /// pushes results in via SetUpdateStatus; the subscription itself stays in the window to
    /// avoid the VM holding a handler on a static event.
    /// </summary>
    internal sealed partial class SettingsViewModel
    {
        private string _versionInfoText = string.Empty;
        public string VersionInfoText { get => _versionInfoText; private set => Set(ref _versionInfoText, value); }

        private string _updateStatusText = string.Empty;
        public string UpdateStatusText { get => _updateStatusText; private set => Set(ref _updateStatusText, value); }

        private bool _isUpdateAvailable;
        public bool IsUpdateAvailable { get => _isUpdateAvailable; private set => Set(ref _isUpdateAvailable, value); }

        internal void SetUpdateStatus(UpdateStatus status, string? latestVersion, string? patchNotes)
        {
            VersionInfoText = $"v{App.CurrentVersion}";
            UpdateStatusText = status switch
            {
                UpdateStatus.UpdateAvailable => "· Update available",
                UpdateStatus.DebugBuild => "· Debug build",
                _ => "· Up to date"
            };
            IsUpdateAvailable = status == UpdateStatus.UpdateAvailable;
        }
    }
}
