using System;
using System.IO;
using System.Windows;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// ViewModel for the media player's right-side settings pane. Every change writes App.Config,
    /// persists, and fires the host callback (re-raised as the pane's SettingChanged event) so the
    /// player updates live. There is no dirty-tracking; this is an immediate-apply form.
    ///
    /// Checkboxes are two-way bools. The cycle buttons (pills, gradient, next-song mode/sort,
    /// time format, seek unit) are commands with derived label/opacity/visibility properties.
    /// </summary>
    internal sealed class MediaPlayerSettingsPaneViewModel : ViewModelBase
    {
        private bool _loading;

        // Forwarded to the pane's public SettingChanged event, and to the player for time format.
        public Action? SettingChangedCallback { get; set; }
        public Action? OnTimeFormatToggled { get; set; }

        public MediaPlayerSettingsPaneViewModel()
        {
            CycleConnectionPillCommand = new RelayCommand(CycleConnectionPill);
            CycleAudioLinkPillCommand = new RelayCommand(CycleAudioLinkPill);
            CycleQualityPillCommand = new RelayCommand(CycleQualityPill);
            CycleAlwaysOnTopPillCommand = new RelayCommand(CycleAlwaysOnTopPill);
            SelectGradientCommand = new RelayCommand<string>(SelectGradient);
            ToggleSeekUnitCommand = new RelayCommand(ToggleSeekUnit);
            ToggleTimeFormatCommand = new RelayCommand(ToggleTimeFormat);
            CycleNextSongModeCommand = new RelayCommand(CycleNextSongMode);
            CycleNextSongSortCommand = new RelayCommand(CycleNextSongSort);
            RescanLibraryCommand = new RelayCommand(RescanLibrary);
            CyclePredictiveUiCommand = new RelayCommand(CyclePredictiveUi);
            CyclePredictiveCoverCommand = new RelayCommand(CyclePredictiveCover);

            CycleBatteryStyleCommand = new RelayCommand(CycleBatteryStyle);
            CycleBatteryPercentPlacementCommand = new RelayCommand(CycleBatteryPercentPlacement);
            CycleBatteryBoltPlacementCommand = new RelayCommand(CycleBatteryBoltPlacement);
            CycleBatteryColorModeCommand = new RelayCommand(CycleBatteryColorMode);
            ToggleBatteryPollUnitCommand = new RelayCommand(ToggleBatteryPollUnit);
        }

        // ── Checkboxes ────────────────────────────────────────────────────────

        private bool _playerShowTitle;
        public bool PlayerShowTitle { get => _playerShowTitle; set { if (!Set(ref _playerShowTitle, value)) return; App.Config.MediaPlayer.ShowTitle = value; if (!_loading) SaveAndNotify(); } }

        private bool _playerShowArtist;
        public bool PlayerShowArtist { get => _playerShowArtist; set { if (!Set(ref _playerShowArtist, value)) return; App.Config.MediaPlayer.ShowArtist = value; if (!_loading) SaveAndNotify(); } }

        private bool _playerShowAlbum;
        public bool PlayerShowAlbum { get => _playerShowAlbum; set { if (!Set(ref _playerShowAlbum, value)) return; App.Config.MediaPlayer.ShowAlbum = value; if (!_loading) SaveAndNotify(); } }

        private bool _playerSwapArtistAlbum;
        public bool PlayerSwapArtistAlbum { get => _playerSwapArtistAlbum; set { if (!Set(ref _playerSwapArtistAlbum, value)) return; App.Config.MediaPlayer.SwapArtistAlbum = value; if (!_loading) SaveAndNotify(); } }

        private bool _playerShowCover;
        public bool PlayerShowCover { get => _playerShowCover; set { if (!Set(ref _playerShowCover, value)) return; App.Config.MediaPlayer.ShowCover = value; if (!_loading) SaveAndNotify(); } }

        private bool _playerCoverRoundedCorners;
        public bool PlayerCoverRoundedCorners { get => _playerCoverRoundedCorners; set { if (!Set(ref _playerCoverRoundedCorners, value)) return; App.Config.MediaPlayer.CoverRoundedCorners = value; if (!_loading) SaveAndNotify(); } }

        private bool _playerCoverShadow;
        public bool PlayerCoverShadow { get => _playerCoverShadow; set { if (!Set(ref _playerCoverShadow, value)) return; App.Config.MediaPlayer.CoverShadow = value; if (!_loading) SaveAndNotify(); } }

        private bool _playerTextShadow;
        public bool PlayerTextShadow { get => _playerTextShadow; set { if (!Set(ref _playerTextShadow, value)) return; App.Config.MediaPlayer.TextShadow = value; if (!_loading) SaveAndNotify(); } }

        private bool _playerShowVolumeButton;
        public bool PlayerShowVolumeButton { get => _playerShowVolumeButton; set { if (!Set(ref _playerShowVolumeButton, value)) return; App.Config.MediaPlayer.ShowVolumeButton = value; if (!_loading) SaveAndNotify(); } }

        private bool _playerShowLyricsButton;
        public bool PlayerShowLyricsButton { get => _playerShowLyricsButton; set { if (!Set(ref _playerShowLyricsButton, value)) return; App.Config.MediaPlayer.ShowLyricsButton = value; if (!_loading) SaveAndNotify(); } }

        private bool _playerShowBattery;
        public bool PlayerShowBattery { get => _playerShowBattery; set { if (!Set(ref _playerShowBattery, value)) return; App.Config.MediaPlayer.ShowBattery = value; if (!_loading) SaveAndNotify(); } }

        // ── Battery customization ─────────────────────────────────────────────

        private bool _batteryShowPercent;
        public bool BatteryShowPercent { get => _batteryShowPercent; set { if (!Set(ref _batteryShowPercent, value)) return; App.Config.MediaPlayer.BatteryShowPercent = value; RaisePropertyChanged(nameof(BatteryPercentPlacementVisible)); if (!_loading) SaveAndNotify(); } }

        private bool _batteryShowBolt;
        public bool BatteryShowBolt { get => _batteryShowBolt; set { if (!Set(ref _batteryShowBolt, value)) return; App.Config.MediaPlayer.BatteryShowBolt = value; RaisePropertyChanged(nameof(BatteryBoltPlacementVisible)); if (!_loading) SaveAndNotify(); } }

        public RelayCommand CycleBatteryStyleCommand { get; }
        public RelayCommand CycleBatteryPercentPlacementCommand { get; }
        public RelayCommand CycleBatteryBoltPlacementCommand { get; }
        public RelayCommand CycleBatteryColorModeCommand { get; }

        private static readonly string[] BatteryStyleLabels = { "Classic", "Pill", "Vertical" };
        private static readonly string[] BatteryColorModeLabels = { "Enabled", "Text color", "Disabled" };

        public string BatteryStyleLabel => BatteryStyleLabels[Math.Clamp((int)App.Config.MediaPlayer.BatteryVisualStyle, 0, 2)];
        public string BatteryColorModeLabel => BatteryColorModeLabels[Math.Clamp((int)App.Config.MediaPlayer.BatteryColorMode, 0, 2)];

        // Inside/outside placement labels for the cycle buttons.
        public string BatteryPercentPlacementLabel => App.Config.MediaPlayer.BatteryPercentInside ? "Inside" : "Outside";
        public string BatteryBoltPlacementLabel => App.Config.MediaPlayer.BatteryBoltInside ? "Inside" : "Outside";

        // The percentage placement control is hidden when the percentage is off, or when the
        // Vertical style forces it outside (no choice to make).
        public bool BatteryPercentPlacementVisible
            => App.Config.MediaPlayer.BatteryShowPercent && App.Config.MediaPlayer.BatteryVisualStyle != BatteryVisualStyle.Vertical;

        // The bolt placement control is only meaningful when the bolt is shown.
        public bool BatteryBoltPlacementVisible => App.Config.MediaPlayer.BatteryShowBolt;

        private void CycleBatteryStyle()
        {
            int next = ((int)App.Config.MediaPlayer.BatteryVisualStyle + 1) % 3;
            App.Config.MediaPlayer.BatteryVisualStyle = (BatteryVisualStyle)next;
            RaisePropertyChanged(nameof(BatteryStyleLabel));
            RaisePropertyChanged(nameof(BatteryPercentPlacementVisible));
            RaisePropertyChanged(nameof(BatteryPercentPlacementLabel));
            SaveAndNotify();
        }

        private void CycleBatteryPercentPlacement()
        {
            App.Config.MediaPlayer.BatteryPercentInside = !App.Config.MediaPlayer.BatteryPercentInside;
            RaisePropertyChanged(nameof(BatteryPercentPlacementLabel));
            SaveAndNotify();
        }

        private void CycleBatteryBoltPlacement()
        {
            App.Config.MediaPlayer.BatteryBoltInside = !App.Config.MediaPlayer.BatteryBoltInside;
            RaisePropertyChanged(nameof(BatteryBoltPlacementLabel));
            SaveAndNotify();
        }

        private void CycleBatteryColorMode()
        {
            int next = ((int)App.Config.MediaPlayer.BatteryColorMode + 1) % 3;
            App.Config.MediaPlayer.BatteryColorMode = (BatteryColorMode)next;
            RaisePropertyChanged(nameof(BatteryColorModeLabel));
            SaveAndNotify();
        }

        // ── Battery poll rate (same textbox + unit-toggle pattern as seek) ──────

        public RelayCommand ToggleBatteryPollUnitCommand { get; }

        private string _batteryPollText = "150";
        public string BatteryPollText
        {
            get => _batteryPollText;
            set
            {
                if (!Set(ref _batteryPollText, value)) return;
                if (_loading) return;
                if (int.TryParse(value.Trim(), out int v) && v > 0)
                {
                    bool isMin = BatteryPollUnitLabel == "min";
                    App.Config.MediaPlayer.BatteryPollIntervalSeconds = Math.Max(5, isMin ? v * 60 : v);
                    SaveAndNotify();
                }
            }
        }

        private string _batteryPollUnitLabel = "sec";
        public string BatteryPollUnitLabel { get => _batteryPollUnitLabel; private set => Set(ref _batteryPollUnitLabel, value); }

        private void ToggleBatteryPollUnit()
        {
            if (_loading) return;

            bool currentlyMin = BatteryPollUnitLabel == "min";
            _loading = true;
            if (currentlyMin)
            {
                if (int.TryParse(BatteryPollText.Trim(), out int min))
                    BatteryPollText = (min * 60).ToString();
                BatteryPollUnitLabel = "sec";
            }
            else
            {
                if (int.TryParse(BatteryPollText.Trim(), out int sec) && sec % 60 == 0)
                {
                    BatteryPollText = (sec / 60).ToString();
                }
                else if (int.TryParse(BatteryPollText.Trim(), out int secR))
                {
                    int rounded = Math.Max(1, (int)Math.Round(secR / 60.0));
                    BatteryPollText = rounded.ToString();
                }
                BatteryPollUnitLabel = "min";
            }
            _loading = false;

            if (int.TryParse(BatteryPollText.Trim(), out int v) && v > 0)
            {
                bool nowMin = BatteryPollUnitLabel == "min";
                App.Config.MediaPlayer.BatteryPollIntervalSeconds = Math.Max(5, nowMin ? v * 60 : v);
                SaveAndNotify();
            }
        }

        private bool _playerShowHelpButton;
        public bool PlayerShowHelpButton { get => _playerShowHelpButton; set { if (!Set(ref _playerShowHelpButton, value)) return; App.Config.MediaPlayer.ShowHelpButton = value; if (!_loading) SaveAndNotify(); } }

        private bool _playerShowFullscreenButton;
        public bool PlayerShowFullscreenButton { get => _playerShowFullscreenButton; set { if (!Set(ref _playerShowFullscreenButton, value)) return; App.Config.MediaPlayer.ShowFullscreenButton = value; if (!_loading) SaveAndNotify(); } }

        private bool _playerShowSeekButtons;
        public bool PlayerShowSeekButtons { get => _playerShowSeekButtons; set { if (!Set(ref _playerShowSeekButtons, value)) return; App.Config.MediaPlayer.ShowSeekButtons = value; if (!_loading) SaveAndNotify(); } }

        private bool _swapSettingsLocation;
        public bool SwapSettingsLocation { get => _swapSettingsLocation; set { if (!Set(ref _swapSettingsLocation, value)) return; App.Config.MediaPlayer.SwapSettingsLocation = value; if (!_loading) SaveAndNotify(); } }

        // ── Pills ─────────────────────────────────────────────────────────────

        private static readonly string[] ConnectionPillModeLabels = { "Full", "Mini", "Off", "Top" };
        private static readonly string[] PillModeLabels = { "Full", "Mini", "Off" };

        public RelayCommand CycleConnectionPillCommand { get; }
        public RelayCommand CycleAudioLinkPillCommand { get; }
        public RelayCommand CycleQualityPillCommand { get; }
        public RelayCommand CycleAlwaysOnTopPillCommand { get; }

        public string ConnectionPillLabel => ConnectionPillModeLabels[Math.Clamp(App.Config.MediaPlayer.PillModeConnection, 0, 3)];
        public double ConnectionPillOpacity => App.Config.MediaPlayer.PillModeConnection == 2 ? 0.45 : 1.0;
        public string AudioLinkPillLabel => PillModeLabels[Math.Clamp(App.Config.MediaPlayer.PillModeAudioLink, 0, 2)];
        public double AudioLinkPillOpacity => App.Config.MediaPlayer.PillModeAudioLink == 2 ? 0.45 : 1.0;
        public string QualityPillLabel => PillModeLabels[Math.Clamp(App.Config.MediaPlayer.PillModeQuality, 0, 2)];
        public double QualityPillOpacity => App.Config.MediaPlayer.PillModeQuality == 2 ? 0.45 : 1.0;
        public string AlwaysOnTopPillLabel => PillModeLabels[Math.Clamp(App.Config.MediaPlayer.PillModeAlwaysOnTop, 0, 2)];
        public double AlwaysOnTopPillOpacity => App.Config.MediaPlayer.PillModeAlwaysOnTop == 2 ? 0.45 : 1.0;

        private void CycleConnectionPill()
        {
            App.Config.MediaPlayer.PillModeConnection = (App.Config.MediaPlayer.PillModeConnection + 1) % 4;
            RaisePropertyChanged(nameof(ConnectionPillLabel));
            RaisePropertyChanged(nameof(ConnectionPillOpacity));
            SaveAndNotify();
        }

        private void CycleAudioLinkPill()
        {
            App.Config.MediaPlayer.PillModeAudioLink = (App.Config.MediaPlayer.PillModeAudioLink + 1) % 3;
            RaisePropertyChanged(nameof(AudioLinkPillLabel));
            RaisePropertyChanged(nameof(AudioLinkPillOpacity));
            SaveAndNotify();
        }

        private void CycleQualityPill()
        {
            App.Config.MediaPlayer.PillModeQuality = (App.Config.MediaPlayer.PillModeQuality + 1) % 3;
            RaisePropertyChanged(nameof(QualityPillLabel));
            RaisePropertyChanged(nameof(QualityPillOpacity));
            SaveAndNotify();
        }

        private void CycleAlwaysOnTopPill()
        {
            App.Config.MediaPlayer.PillModeAlwaysOnTop = (App.Config.MediaPlayer.PillModeAlwaysOnTop + 1) % 3;
            RaisePropertyChanged(nameof(AlwaysOnTopPillLabel));
            RaisePropertyChanged(nameof(AlwaysOnTopPillOpacity));
            SaveAndNotify();
        }

        // ── Gradient ──────────────────────────────────────────────────────────

        public RelayCommand<string> SelectGradientCommand { get; }

        public int GradientSamplePoints => App.Config.MediaPlayer.GradientSamplePoints;

        private void SelectGradient(string? tag)
        {
            if (!int.TryParse(tag, out int val)) return;
            App.Config.MediaPlayer.GradientSamplePoints = val;
            RaisePropertyChanged(nameof(GradientSamplePoints));
            SaveAndNotify();
        }

        // ── Seek threshold ────────────────────────────────────────────────────

        public RelayCommand ToggleSeekUnitCommand { get; }

        private string _seekThresholdText = "10";
        public string SeekThresholdText
        {
            get => _seekThresholdText;
            set
            {
                if (!Set(ref _seekThresholdText, value)) return;
                if (_loading) return;
                if (int.TryParse(value.Trim(), out int v) && v > 0)
                {
                    bool isMin = SeekUnitLabel == "min";
                    App.Config.MediaPlayer.SeekButtonThresholdSeconds = isMin ? v * 60 : v;
                    SaveAndNotify();
                }
            }
        }

        private string _seekUnitLabel = "sec";
        public string SeekUnitLabel { get => _seekUnitLabel; private set => Set(ref _seekUnitLabel, value); }

        private void ToggleSeekUnit()
        {
            if (_loading) return;

            bool currentlyMin = SeekUnitLabel == "min";
            _loading = true;
            if (currentlyMin)
            {
                if (int.TryParse(SeekThresholdText.Trim(), out int min))
                    SeekThresholdText = (min * 60).ToString();
                SeekUnitLabel = "sec";
            }
            else
            {
                if (int.TryParse(SeekThresholdText.Trim(), out int sec) && sec % 60 == 0)
                {
                    SeekThresholdText = (sec / 60).ToString();
                }
                else if (int.TryParse(SeekThresholdText.Trim(), out int secR))
                {
                    int rounded = Math.Max(1, (int)Math.Round(secR / 60.0));
                    SeekThresholdText = rounded.ToString();
                }
                SeekUnitLabel = "min";
            }
            _loading = false;

            if (int.TryParse(SeekThresholdText.Trim(), out int v) && v > 0)
            {
                bool nowMin = SeekUnitLabel == "min";
                App.Config.MediaPlayer.SeekButtonThresholdSeconds = nowMin ? v * 60 : v;
                SaveAndNotify();
            }
        }

        // ── Time format ───────────────────────────────────────────────────────

        public RelayCommand ToggleTimeFormatCommand { get; }

        private string _timeFormatLabel = "Elapsed";
        public string TimeFormatLabel { get => _timeFormatLabel; private set => Set(ref _timeFormatLabel, value); }

        private void ToggleTimeFormat()
        {
            if (_loading) return;
            App.Config.MediaPlayer.ShowTimeLeft = !App.Config.MediaPlayer.ShowTimeLeft;
            TimeFormatLabel = App.Config.MediaPlayer.ShowTimeLeft ? "Remaining" : "Elapsed";
            SaveAndNotify();
            OnTimeFormatToggled?.Invoke();
        }

        // Called by the player window when the user clicks the time label directly.
        public void SyncTimeFormat(bool showTimeLeft)
        {
            TimeFormatLabel = showTimeLeft ? "Remaining" : "Elapsed";
        }

        // ── Next / previous song ──────────────────────────────────────────────

        private static readonly string[] NextSongModeLabels = { "Off", "Text only", "Full art", "Kirsten" };
        private static readonly string[] NextSongSortLabels = { "A-Z", "Z-A", "Newest", "Oldest" };

        public RelayCommand CycleNextSongModeCommand { get; }
        public RelayCommand CycleNextSongSortCommand { get; }
        public RelayCommand RescanLibraryCommand { get; }

        public string NextSongModeLabel
        {
            get
            {
                int idx = (int)App.Config.NextSong.Mode;
                return idx >= 0 && idx < NextSongModeLabels.Length ? NextSongModeLabels[idx] : "Off";
            }
        }
        public double NextSongModeOpacity => App.Config.NextSong.Mode == NextSongMode.Off ? 0.45 : 1.0;
        public bool NextSongOptionsVisible => App.Config.NextSong.Mode != NextSongMode.Off;
        public string NextSongSortLabel => NextSongSortLabels[(int)App.Config.NextSong.SortMode];

        private string _nextSongListStatus = "No list yet";
        public string NextSongListStatus { get => _nextSongListStatus; private set => Set(ref _nextSongListStatus, value); }

        private bool _rescanEnabled = true;
        public bool RescanEnabled { get => _rescanEnabled; private set => Set(ref _rescanEnabled, value); }

        private string _rescanButtonText = "Rescan";
        public string RescanButtonText { get => _rescanButtonText; private set => Set(ref _rescanButtonText, value); }

        private void CycleNextSongMode()
        {
            // FullArt -> TextOnly -> Off -> Kirsten -> FullArt
            var next = App.Config.NextSong.Mode switch
            {
                NextSongMode.FullArt => NextSongMode.TextOnly,
                NextSongMode.TextOnly => NextSongMode.Off,
                NextSongMode.Off => NextSongMode.Kirsten,
                NextSongMode.Kirsten => NextSongMode.FullArt,
                _ => NextSongMode.FullArt
            };
            App.Config.NextSong.Mode = next;
            RaisePropertyChanged(nameof(NextSongModeLabel));
            RaisePropertyChanged(nameof(NextSongModeOpacity));
            RaisePropertyChanged(nameof(NextSongOptionsVisible));
            RefreshNextSongListStatus();
            SaveAndNotify();
            _ = (Application.Current as App)?.RefreshNextSongNeighboursAsync();
        }

        private void CycleNextSongSort()
        {
            var prev = App.Config.NextSong.SortMode;
            var next = (NextSongSortMode)(((int)prev + 1) % 4);
            App.Config.NextSong.SortMode = next;
            RaisePropertyChanged(nameof(NextSongSortLabel));
            SaveAndNotify();
            if (prev != next)
                (Application.Current as App)?.ResortNextSongListAsync();
        }

        private void RescanLibrary()
        {
            RescanEnabled = false;
            RescanButtonText = "Scanning...";
            (Application.Current as App)?.RescanNextSongLibraryAsync(() =>
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    RescanEnabled = true;
                    RescanButtonText = "Rescan";
                    RefreshNextSongListStatus();
                });
            });
        }

        public void RefreshNextSongListStatus()
        {
            var path = AppPaths.GetDataPath("library_list.txt");
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                NextSongListStatus = $"Last scan: {info.LastWriteTime:g}";
            }
            else
            {
                NextSongListStatus = "No list yet";
            }
        }

        // ── Predictive UI / covers ────────────────────────────────────────────

        private static readonly string[] PredictiveUiLabels = { "Off", "Safe", "Full" };
        private static readonly string[] PredictiveCoverLabels = { "Off", "1x", "2x" };

        public RelayCommand CyclePredictiveUiCommand { get; }
        public RelayCommand CyclePredictiveCoverCommand { get; }

        public string PredictiveUiLabel => PredictiveUiLabels[Math.Clamp((int)App.Config.MediaPlayer.PredictiveUi, 0, 2)];
        public double PredictiveUiOpacity => App.Config.MediaPlayer.PredictiveUi == PredictiveUiMode.Off ? 0.45 : 1.0;

        public string PredictiveCoverLabel => PredictiveCoverLabels[Math.Clamp(App.Config.MediaPlayer.PredictiveCoverMode, 0, 2)];
        public double PredictiveCoverOpacity => App.Config.MediaPlayer.PredictiveCoverMode == 0 ? 0.45 : 1.0;

        // Sort order (shared with next/prev song) only matters when something is
        // actually reading the library list: Full predictions or predictive covers.
        public bool PredictiveListOptionsVisible
            => App.Config.MediaPlayer.PredictiveUi == PredictiveUiMode.Full
            || App.Config.MediaPlayer.PredictiveCoverMode > 0;

        private void CyclePredictiveUi()
        {
            int next = ((int)App.Config.MediaPlayer.PredictiveUi + 1) % 3;
            App.Config.MediaPlayer.PredictiveUi = (PredictiveUiMode)next;
            RaisePropertyChanged(nameof(PredictiveUiLabel));
            RaisePropertyChanged(nameof(PredictiveUiOpacity));
            RaisePropertyChanged(nameof(PredictiveListOptionsVisible));
            SaveAndNotify();
            // Full predictions come from the library list; make sure one exists.
            if (App.Config.MediaPlayer.PredictiveUi == PredictiveUiMode.Full)
                (Application.Current as App)?.EnsurePredictiveLibraryAsync();
        }

        private void CyclePredictiveCover()
        {
            App.Config.MediaPlayer.PredictiveCoverMode = (App.Config.MediaPlayer.PredictiveCoverMode + 1) % 3;
            RaisePropertyChanged(nameof(PredictiveCoverLabel));
            RaisePropertyChanged(nameof(PredictiveCoverOpacity));
            RaisePropertyChanged(nameof(PredictiveListOptionsVisible));
            SaveAndNotify();
            if (App.Config.MediaPlayer.PredictiveCoverMode > 0)
                (Application.Current as App)?.EnsurePredictiveLibraryAsync();
        }

        // ── Load / save ───────────────────────────────────────────────────────

        public void LoadFromConfig()
        {
            _loading = true;
            try
            {
                var c = App.Config;

                _playerShowTitle = c.MediaPlayer.ShowTitle;
                _playerShowArtist = c.MediaPlayer.ShowArtist;
                _playerShowAlbum = c.MediaPlayer.ShowAlbum;
                _playerSwapArtistAlbum = c.MediaPlayer.SwapArtistAlbum;
                _playerShowCover = c.MediaPlayer.ShowCover;
                _playerCoverRoundedCorners = c.MediaPlayer.CoverRoundedCorners;
                _playerCoverShadow = c.MediaPlayer.CoverShadow;
                _playerTextShadow = c.MediaPlayer.TextShadow;
                _playerShowVolumeButton = c.MediaPlayer.ShowVolumeButton;
                _playerShowLyricsButton = c.MediaPlayer.ShowLyricsButton;
                _playerShowBattery = c.MediaPlayer.ShowBattery;
                _batteryShowPercent = c.MediaPlayer.BatteryShowPercent;
                _batteryShowBolt = c.MediaPlayer.BatteryShowBolt;
                _playerShowHelpButton = c.MediaPlayer.ShowHelpButton;
                _playerShowFullscreenButton = c.MediaPlayer.ShowFullscreenButton;
                _playerShowSeekButtons = c.MediaPlayer.ShowSeekButtons;
                _swapSettingsLocation = c.MediaPlayer.SwapSettingsLocation;

                int threshSec = c.MediaPlayer.SeekButtonThresholdSeconds;
                bool useMin = threshSec % 60 == 0;
                _seekUnitLabel = useMin ? "min" : "sec";
                _seekThresholdText = useMin ? (threshSec / 60).ToString() : threshSec.ToString();

                int pollSec = c.MediaPlayer.BatteryPollIntervalSeconds;
                bool pollUseMin = pollSec % 60 == 0;
                _batteryPollUnitLabel = pollUseMin ? "min" : "sec";
                _batteryPollText = pollUseMin ? (pollSec / 60).ToString() : pollSec.ToString();

                _timeFormatLabel = c.MediaPlayer.ShowTimeLeft ? "Remaining" : "Elapsed";

                RefreshNextSongListStatus();
            }
            finally
            {
                _loading = false;
            }

            // Refresh every bound property at once (scalars and the config-derived getters).
            RaisePropertyChanged(string.Empty);
        }

        private void SaveAndNotify()
        {
            MusicConfigManager.Save(App.Config);
            (Application.Current as App)?.UpdateConfig(App.Config);
            SettingChangedCallback?.Invoke();
        }
    }
}