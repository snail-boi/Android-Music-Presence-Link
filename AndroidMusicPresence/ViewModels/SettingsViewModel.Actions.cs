using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// The settings window's action buttons: check for update, redo onboarding, toggle the
    /// media-player view, clear/open the media cache, and open the log folder. (Theme cycling
    /// lives in the Theming partial.)
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

        private RelayCommand? _exportConfigCommand;
        public RelayCommand ExportConfigCommand => _exportConfigCommand ??= new RelayCommand(ExportConfig);

        private RelayCommand? _importConfigCommand;
        public RelayCommand ImportConfigCommand => _importConfigCommand ??= new RelayCommand(ImportConfig);

        // The header theme button now cycles through all themes; see CycleThemeCommand in
        // the Theming partial.

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
                _config.MediaPlayer.ShowWindow = false;
                MusicConfigManager.Save(_config);
                _savedConfig.MediaPlayer.ShowWindow = false;
                app.GoBackToSettingsWindow();
            }
            else
            {
                _config.MediaPlayer.ShowWindow = true;
                MusicConfigManager.Save(_config);
                _savedConfig.MediaPlayer.ShowWindow = true;
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
                    _config.AppSettings.CachClearInMB,
                    _config.Library.CoverArtFileNamePatterns);
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

        // ── Forced (custom) covers management (Library & Caching section) ─────

        public sealed class ForcedCoverItem
        {
            public string Token { get; init; } = string.Empty;
            public string Title { get; init; } = string.Empty;
            public string Artist { get; init; } = string.Empty;
            public ImageSource? Thumbnail { get; init; }
        }

        public ObservableCollection<ForcedCoverItem> ForcedCovers { get; } = new();

        public bool HasForcedCovers => ForcedCovers.Count > 0;

        private RelayCommand<ForcedCoverItem>? _removeForcedCoverCommand;
        public RelayCommand<ForcedCoverItem> RemoveForcedCoverCommand => _removeForcedCoverCommand ??= new RelayCommand<ForcedCoverItem>(RemoveForcedCover);

        private RelayCommand? _removeAllForcedCoversCommand;
        public RelayCommand RemoveAllForcedCoversCommand => _removeAllForcedCoversCommand ??= new RelayCommand(RemoveAllForcedCovers);

        // Called by the window whenever the Library & Caching expander opens, so covers
        // forced from the media player while settings were already open still show up.
        internal void RefreshForcedCovers()
        {
            ForcedCovers.Clear();
            foreach (var cover in ForcedCoverStore.All())
            {
                ForcedCovers.Add(new ForcedCoverItem
                {
                    Token = cover.Token,
                    Title = cover.Title,
                    Artist = cover.Artist,
                    Thumbnail = LoadCoverThumbnail(cover.ImagePath)
                });
            }

            RaisePropertyChanged(nameof(HasForcedCovers));
        }

        private void RemoveForcedCover(ForcedCoverItem? item)
        {
            if (item == null) return;

            ForcedCoverStore.RemoveByToken(item.Token);
            ForcedCovers.Remove(item);
            RaisePropertyChanged(nameof(HasForcedCovers));
            (Application.Current as App)?.NotifyForcedCoversChanged();
        }

        private void RemoveAllForcedCovers()
        {
            if (ForcedCovers.Count == 0) return;

            ForcedCoverStore.RemoveAll();
            ForcedCovers.Clear();
            RaisePropertyChanged(nameof(HasForcedCovers));
            (Application.Current as App)?.NotifyForcedCoversChanged();
        }

        private static ImageSource? LoadCoverThumbnail(string path)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                // A re-forced cover reuses the same file name; skip WPF's URI cache so the
                // list shows the new image instead of the stale one.
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.DecodePixelWidth = 96;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
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

        // Exports the settings currently shown in the window (including unsaved edits, since
        // BuildConfig reflects the live UI) to a user-chosen JSON file.
        private void ExportConfig()
        {
            try
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Export configuration",
                    Filter = "Config file|*.json|All files|*.*",
                    FileName = "musicconfig-export.json",
                    AddExtension = true,
                    DefaultExt = ".json"
                };
                if (dlg.ShowDialog() != true)
                    return;

                MusicConfigManager.ExportTo(dlg.FileName, BuildConfig());
                Interaction?.ShowInfo("Configuration exported.", "Export");
            }
            catch (Exception ex)
            {
                Interaction?.ShowWarning($"Failed to export configuration: {ex.Message}", "Export");
            }
        }

        // Imports a previously exported config, replacing all current settings. UpdateConfig
        // pushes the imported config back into this window (reseeding the UI and password box).
        private void ImportConfig()
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Import configuration",
                    Filter = "Config file|*.json|All files|*.*",
                    CheckFileExists = true
                };
                if (dlg.ShowDialog() != true)
                    return;

                MusicConfig imported;
                try
                {
                    imported = MusicConfigManager.ImportFrom(dlg.FileName);
                }
                catch (Exception ex)
                {
                    Interaction?.ShowWarning($"That file could not be read as a configuration: {ex.Message}", "Import");
                    return;
                }

                if (Interaction?.ConfirmYesNo(
                        "This will replace all current settings with the imported configuration. Continue?",
                        "Import configuration") != true)
                    return;

                MusicConfigManager.Save(imported);
                (Application.Current as App)?.UpdateConfig(imported);
                Interaction?.ShowInfo("Configuration imported.", "Import");
            }
            catch (Exception ex)
            {
                Interaction?.ShowWarning($"Failed to import configuration: {ex.Message}", "Import");
            }
        }
    }
}