using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// The settings window's action buttons: check for update, redo onboarding, toggle the
    /// media-player view, clear/open the media cache, open the log folder, and toggle theme.
    ///
    /// Commands are created lazily on first access, so this partial needs no constructor hook
    /// and the Core partial is unchanged. Dialogs and message boxes go through the interaction
    /// seam; folder opening and update checks are plain side effects.
    /// </summary>
    internal sealed partial class SettingsViewModel
    {
        private RelayCommand? _checkUpdateCommand;
        public RelayCommand CheckUpdateCommand => _checkUpdateCommand ??= new RelayCommand(CheckForUpdate);

        private RelayCommand? _redoOnboardingCommand;
        public RelayCommand RedoOnboardingCommand => _redoOnboardingCommand ??= new RelayCommand(RedoOnboarding);

        private RelayCommand? _toggleMediaPlayerViewCommand;
        public RelayCommand ToggleMediaPlayerViewCommand => _toggleMediaPlayerViewCommand ??= new RelayCommand(ToggleMediaPlayerView);

        private RelayCommand? _clearCoverCacheCommand;
        public RelayCommand ClearCoverCacheCommand => _clearCoverCacheCommand ??= new RelayCommand(ClearCoverCache);

        private RelayCommand? _openCoverCacheCommand;
        public RelayCommand OpenCoverCacheCommand => _openCoverCacheCommand ??= new RelayCommand(OpenCoverCache);

        private RelayCommand? _openLyricsCacheCommand;
        public RelayCommand OpenLyricsCacheCommand => _openLyricsCacheCommand ??= new RelayCommand(OpenLyricsCache);

        private RelayCommand? _openLogFolderCommand;
        public RelayCommand OpenLogFolderCommand => _openLogFolderCommand ??= new RelayCommand(OpenLogFolder);

        private RelayCommand? _toggleThemeCommand;
        public RelayCommand ToggleThemeCommand => _toggleThemeCommand ??= new RelayCommand(() => UseDarkMode = !UseDarkMode);

        // Media-player-view button label. Depends on the app's current mode, which the window
        // pushes in via SetMediaPlayerModeActive (called from UpdateMediaPlayerModeButton).
        private bool _isMediaPlayerModeActive;
        public string MediaPlayerViewButtonText => _isMediaPlayerModeActive
            ? "Switch to settings view"
            : "Switch to media player view";

        internal void SetMediaPlayerModeActive(bool active)
        {
            _isMediaPlayerModeActive = active;
            RaisePropertyChanged(nameof(MediaPlayerViewButtonText));
        }

        private void CheckForUpdate()
        {
            _ = Updater.CheckForUpdateAsync(App.CurrentVersion, showPrompt: true, allowRemindLater: false);
        }

        private void RedoOnboarding()
        {
            Save(false);
            (Application.Current as App)?.ShowOnboarding(true);
        }

        private void ToggleMediaPlayerView()
        {
            var app = Application.Current as App;
            if (app == null)
                return;

            if (app.IsMediaPlayerModeActive())
            {
                _config.ShowMediaPlayerWindow = false;
                MusicConfigManager.Save(_config);
                _savedConfig.ShowMediaPlayerWindow = false;
                app.GoBackToSettingsWindow();
            }
            else
            {
                _config.ShowMediaPlayerWindow = true;
                MusicConfigManager.Save(_config);
                _savedConfig.ShowMediaPlayerWindow = true;
                app.ShowMediaPlayerWindowNow();
            }

            SetMediaPlayerModeActive(app.IsMediaPlayerModeActive());
        }

        private void ClearCoverCache()
        {
            try
            {
                var manager = new CoverCacheManager(
                    _config.Paths.FfmpegPath,
                    _config.Paths.CoverCachePath,
                    _config.CachClearInMB,
                    _config.CoverArtFileNamePatterns);
                manager.ClearCache();
                LyricsCache.ClearAll();
                Interaction?.ShowInfo("Media cache cleared.", "Cache");
            }
            catch (Exception ex)
            {
                Interaction?.ShowWarning($"Failed to clear media cache: {ex.Message}", "Error");
            }
        }

        private void OpenCoverCache()
        {
            try
            {
                var cachePath = _config.Paths?.CoverCachePath ?? string.Empty;
                if (string.IsNullOrWhiteSpace(cachePath))
                {
                    Interaction?.ShowWarning("Cover cache path is not configured.", "Cover Cache");
                    return;
                }

                Directory.CreateDirectory(cachePath);
                Process.Start(new ProcessStartInfo
                {
                    FileName = cachePath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Interaction?.ShowWarning($"Failed to open cover cache folder: {ex.Message}", "Error");
            }
        }

        private void OpenLyricsCache()
        {
            try
            {
                var cachePath = AppPaths.GetDataPath("LyricsCache");
                Directory.CreateDirectory(cachePath);
                Process.Start(new ProcessStartInfo
                {
                    FileName = cachePath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Interaction?.ShowWarning($"Failed to open lyrics cache folder: {ex.Message}", "Error");
            }
        }

        private void OpenLogFolder()
        {
            try
            {
                var logPath = Debugger.LogDirectory;
                Directory.CreateDirectory(logPath);
                Process.Start(new ProcessStartInfo
                {
                    FileName = logPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Interaction?.ShowWarning($"Failed to open log folder: {ex.Message}", "Error");
            }
        }
    }
}