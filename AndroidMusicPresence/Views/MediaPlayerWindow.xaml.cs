using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AndroidMusicPresenceLink
{
    public partial class MediaPlayerWindow : Window
    {
        // Duration for the settings pane slide-in / slide-out animation.
        private static readonly Duration SettingsPaneAnimDuration = new Duration(TimeSpan.FromMilliseconds(220));

        // Crossfade duration for cover-art gradient transitions.
        private static readonly Duration GradientFadeDuration = new Duration(TimeSpan.FromMilliseconds(450));

        // ── Settings pane layout ──────────────────────────────────────────────
        // Default width of the settings pane when opened.
        private const double DefaultSettingsWidth = 500;
        // Maximum fraction of the window width the settings pane may occupy.
        private const double SettingsMaxWidthFraction = 0.75;
        // Window width below which the settings pane (500px) auto-collapses.
        private const double SettingsAutoCollapseThreshold = 1030;
        // Window width the window expands to when opening the settings pane.
        private const double SettingsAutoExpandWidth = 1030;
        // Window width below which the player settings pane (280px) auto-collapses.
        private const double PlayerSettingsAutoCollapseThreshold = 810;
        // Window width the window expands to when opening the player settings pane.
        private const double PlayerSettingsAutoExpandWidth = 810;
        // ─────────────────────────────────────────────────────────────────────

        private readonly Func<Task> _pauseAction;
        private readonly Func<Task> _nextAction;
        private readonly Func<Task> _previousAction;
        private readonly Action? _lyricsToggleAction;
        private readonly Func<bool>? _isScrcpyAudioAvailable;
        private readonly Func<float?>? _getVolume;
        private readonly Action<float>? _setVolume;
        private readonly Action<bool>? _stepVolume;
        private readonly Func<Task<(int current, int max)>>? _getPhoneVolume;
        private readonly Func<int, int, int, Task>? _setPhoneVolume;
        private int _lastSentPhoneVolumeIndex = -1;
        private int _lastPhoneVolumeMax = 15;
        private bool _suppressVolumeSliderEcho;
        // Cached phone volume (0..1, -1 = unknown) so the volume glyph can track the
        // device's real volume when no scrcpy audio session exists. Refreshed via a
        // throttled background ADB read; only touched on the UI thread.
        private float _lastKnownPhoneVolumeRatio = -1f;
        private DateTime _lastPhoneVolumeFetchUtc = DateTime.MinValue;
        private bool _phoneVolumeFetchInFlight;
        private static readonly TimeSpan PhoneVolumeRefreshInterval = TimeSpan.FromSeconds(10);
        private string? _currentCoverPath;
        private string? _lastGradientSourcePath;
        private bool? _lastIdleIsDark;
        private bool _useLayerA = true;
        private static readonly Color DefaultTopLeft = Color.FromRgb(52, 52, 52);
        private static readonly Color DefaultTopRight = Color.FromRgb(43, 43, 43);
        private static readonly Color DefaultBottomLeft = Color.FromRgb(36, 36, 36);
        private static readonly Color DefaultBottomRight = Color.FromRgb(28, 28, 28);

        private static readonly Duration CoverFadeDuration = new Duration(TimeSpan.FromMilliseconds(350));
        private bool _coverUseLayerA = true;

        private bool _showTimeLeft = false;
        private long _lastPositionMs;
        private long _lastDurationMs;

        private long _positionAnchorMs;
        private DateTime _positionAnchorTime;
        private DispatcherTimer? _smoothTimer;

        private bool _settingsPaneOpen = false;

        private bool _playerSettingsPaneOpen = false;
        private MediaPlayerSettingsPane? _playerSettingsPane;

        private bool _panesSwapped = false;

        private bool _audioLinkActive = false;
        private readonly Action<bool>? _setAudioLink;

        private readonly Func<MusicConfig>? _getConfig;
        private readonly Func<Task>? _editMetadataAction;
        private readonly Func<Task>? _saveTrackAction;
        // Returns true when the current track was resolved from Subsonic (a cloud track with no
        // local file). Used to disable tag editing in the cover context menu.
        private readonly Func<bool>? _isCloudTrack;
        // Forced (user-chosen) cover overrides; orchestration lives in App so the token is
        // built from the raw track metadata rather than the window's display text.
        private readonly Func<Task>? _setCustomCoverAction;
        private readonly Func<Task>? _removeCustomCoverAction;
        private readonly Func<bool>? _hasForcedCover;
        private readonly Action<AudioQualityPresets.Preset>? _applyAudioQualityPreset;
        private readonly Action? _openCustomQualityWindow;

        private bool _isFullscreen = false;
        private WindowStyle _prevWindowStyle = WindowStyle.SingleBorderWindow;
        private ResizeMode _prevResizeMode = ResizeMode.CanResizeWithGrip;

        private bool _alwaysOnTop = false;

        private string _connectionStatusText = "Not connected";
        // Whether a device is currently reachable; maintained by SetConnectionStatus.
        // Gates the phone-volume popup, which is useless without a device.
        private bool _isDeviceConnected;
        private string _connectionDetailText = "";

        private readonly MediaPlayerViewModel _vm = new MediaPlayerViewModel();
        private Color _connectionColor = Color.FromRgb(0xFF, 0x3B, 0x30);

        private readonly Func<int, Task>? _seekRelativeSeconds;

        private readonly LyricsOverlayManager? _lyricsManager;
        private bool _lyricsViewActive;
        private IReadOnlyList<LyricsOverlayManager.LyricsLineDto> _lyricsLines = Array.Empty<LyricsOverlayManager.LyricsLineDto>();
        private bool _lyricsAreTimed;
        private int _lyricsHighlightedIndex = -1;
        private DispatcherTimer? _lyricsTimer;
        private readonly List<TextBlock> _lyricsLineBlocks = new();
        private readonly List<Border> _lyricsLineHosts = new();
        private Brush _lyricsInactiveBrush = Brushes.White;
        private Brush _lyricsActiveBrush = Brushes.White;
        private Brush _lyricsActiveLineBgBrush = Brushes.Transparent;

        private double _lyricsTargetScrollOffset;
        private bool _lyricsScrollLoopActive;

        internal MediaPlayerWindow(
            Func<Task> pauseAction,
            Func<Task> nextAction,
            Func<Task> previousAction,
            Action? lyricsToggleAction = null,
            Func<bool>? isScrcpyAudioAvailable = null,
            Func<float?>? getVolume = null,
            Action<float>? setVolume = null,
            Action<bool>? stepVolume = null,
            Action<bool>? setAudioLink = null,
            Func<int, Task>? seekRelativeSeconds = null,
            LyricsOverlayManager? lyricsManager = null,
            Func<MusicConfig>? getConfig = null,
            Action<AudioQualityPresets.Preset>? applyAudioQualityPreset = null,
            Action? openCustomQualityWindow = null,
            Func<Task<(int current, int max)>>? getPhoneVolume = null,
            Func<int, int, int, Task>? setPhoneVolume = null,
            Func<Task>? editMetadataAction = null,
            Func<Task>? saveTrackAction = null,
            Func<bool>? isCloudTrack = null,
            Func<Task>? setCustomCoverAction = null,
            Func<Task>? removeCustomCoverAction = null,
            Func<bool>? hasForcedCover = null)
        {
            InitializeComponent();
            PlayerPaneBorder.DataContext = _vm;
            _pauseAction = pauseAction;
            _nextAction = nextAction;
            _previousAction = previousAction;
            _lyricsToggleAction = lyricsToggleAction;
            _isScrcpyAudioAvailable = isScrcpyAudioAvailable;
            _getVolume = getVolume;
            _setVolume = setVolume;
            _stepVolume = stepVolume;
            _setAudioLink = setAudioLink;
            _seekRelativeSeconds = seekRelativeSeconds;
            _lyricsManager = lyricsManager;
            _getConfig = getConfig;
            _applyAudioQualityPreset = applyAudioQualityPreset;
            _openCustomQualityWindow = openCustomQualityWindow;
            _getPhoneVolume = getPhoneVolume;
            _setPhoneVolume = setPhoneVolume;
            _editMetadataAction = editMetadataAction;
            _saveTrackAction = saveTrackAction;
            _isCloudTrack = isCloudTrack;
            _setCustomCoverAction = setCustomCoverAction;
            _removeCustomCoverAction = removeCustomCoverAction;
            _hasForcedCover = hasForcedCover;

            if (_lyricsManager != null)
            {
                _lyricsManager.LinesChanged += OnLyricsLinesChanged;
            }

            RenderTransportIcons(isPlaying: false);
            RenderAuxiliaryIcons();
            RenderSettingsPaneArrowIcons();
            RenderFastSeekIcons();
            RenderAudioLinkButton();
            RenderHelpButtonIcon();
            RenderFullscreenButtonIcon();
            RefreshAlwaysOnTopButton();
            RefreshAudioQualityButton();
            ApplyCoverGradientBackground(null);

            _playerSettingsPane = new MediaPlayerSettingsPane();
            _playerSettingsPane.SettingChanged += OnPlayerSettingChanged;
            PlayerSettingsHost.Content = _playerSettingsPane;

            SizeChanged += MediaPlayerWindow_SizeChanged;
            PlayerPaneBorder.SizeChanged += (_, _) => UpdateGradientClip();
            Loaded += (_, _) =>
            {
                ClampSettingsColumnWidth();
                RefreshVolumeIcon();
                UpdateGradientClip();
                RefreshConnectionButton();
                RefreshAudioQualityButton();
                RenderFullscreenButtonIcon();
                RefreshAlwaysOnTopButton();
                RenderBatteryButtonIcon();
                StartBatteryPolling();
                ApplyPaneLayout();
                ApplySavedRuntimeState();
                ApplyPlayerSettings();
            };

            _smoothTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _smoothTimer.Tick += SmoothTimer_Tick;
            _smoothTimer.Start();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            var config = App.Config;
            Width = config.PlayerWindow.Width;
            Height = config.PlayerWindow.Height;
            Top = config.PlayerWindow.Top;
            Left = config.PlayerWindow.Left;
            WindowState = config.PlayerWindow.State;
        }

        private void MediaPlayerWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!e.WidthChanged) return;

            double mainThreshold = _panesSwapped ? PlayerSettingsAutoCollapseThreshold : SettingsAutoCollapseThreshold;
            double playerThreshold = _panesSwapped ? SettingsAutoCollapseThreshold : PlayerSettingsAutoCollapseThreshold;

            if (_settingsPaneOpen)
            {
                double threshold = mainThreshold + (_playerSettingsPaneOpen ? (_panesSwapped ? DefaultSettingsWidth : DefaultPlayerSettingsWidth) : 0);
                if (ActualWidth < threshold)
                    CollapseSettingsPane();
                else
                    ClampSettingsColumnWidth();
            }

            if (_playerSettingsPaneOpen)
            {
                double threshold = playerThreshold + (_settingsPaneOpen ? (_panesSwapped ? DefaultPlayerSettingsWidth : DefaultSettingsWidth) : 0);
                if (ActualWidth < threshold)
                    CollapsePlayerSettingsPane();
                else
                    ClampPlayerSettingsColumnWidth();
            }
        }

        private void ClampSettingsColumnWidth()
        {
            if (!_settingsPaneOpen) return;
            double available = ActualWidth - 28;
            if (available <= 0) return;
            double defaultWidth = _panesSwapped ? DefaultPlayerSettingsWidth : DefaultSettingsWidth;
            double max = Math.Min(defaultWidth, available * SettingsMaxWidthFraction);
            if (max <= 0) return;
            SettingsColumn.MaxWidth = max;
            SettingsColumn.Width = new GridLength(max, GridUnitType.Pixel);
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);

            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;

            var config = App.Config;
            config.MediaPlayer.SettingsPaneOpen = _panesSwapped ? _playerSettingsPaneOpen : _settingsPaneOpen;
            config.MediaPlayer.InlineLyricsViewActive = _lyricsViewActive;
            config.MediaPlayer.FullscreenActive = _isFullscreen;
            config.PlayerWindow.State = WindowState;
            config.PlayerWindow.Width = SanitizeBound(RestoreBounds.Width, 1080);
            config.PlayerWindow.Height = SanitizeBound(RestoreBounds.Height, 760);
            config.PlayerWindow.Top = SanitizeBound(RestoreBounds.Top, 100);
            config.PlayerWindow.Left = SanitizeBound(RestoreBounds.Left, 100);

            MusicConfigManager.Save(config);
        }

        private static double SanitizeBound(double value, double fallback)
            => double.IsFinite(value) ? value : fallback;

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                if (_lyricsManager != null)
                {
                    _lyricsManager.LinesChanged -= OnLyricsLinesChanged;
                    _lyricsManager.PositionChanged -= OnLyricsPositionChanged;
                }
                StopLyricsTimer();
                StopLyricsScrollLoop();
                _lyricsTimer = null;
                StopBatteryPolling();
            }
            catch { }
            base.OnClosed(e);
        }

        private void UpdateGradientClip()
        {
            double w = PlayerPaneBorder.ActualWidth;
            double h = PlayerPaneBorder.ActualHeight;
            if (w <= 0 || h <= 0) return;

            double radius = PlayerPaneBorder.CornerRadius.TopLeft;
            GradientGrid.Clip = new RectangleGeometry(new Rect(0, 0, w, h), radius, radius);
        }

        // The host that currently holds the main settings content depends on swap state.
        private ContentControl MainSettingsHost => _panesSwapped ? PlayerSettingsHost : SettingsHost;

        public void SetSettingsContent(object? content)
        {
            MainSettingsHost.Content = content;
        }

        public object? TakeSettingsContent()
        {
            var host = MainSettingsHost;
            var content = host.Content;
            host.Content = null;
            return content;
        }

        public void ClearSettingsContent()
        {
            MainSettingsHost.Content = null;
        }

        public void ApplySavedRuntimeState()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(ApplySavedRuntimeState);
                return;
            }

            var config = App.Config;

            // Main settings pane: restore saved state, mapped to the correct physical column.
            // When swapped, main settings is in the right column.
            bool openMainSettings = config.MediaPlayer.SettingsPaneOpen;
            if (_panesSwapped)
            {
                CollapseSettingsPane();
                if (openMainSettings) ShowPlayerSettingsPane(); else CollapsePlayerSettingsPane();
            }
            else
            {
                if (openMainSettings) ShowSettingsPane(); else CollapseSettingsPane();
                CollapsePlayerSettingsPane();
            }

            if (_lyricsViewActive != config.MediaPlayer.InlineLyricsViewActive)
                ToggleInlineLyricsView();

            _showTimeLeft = config.MediaPlayer.ShowTimeLeft;
            RefreshPositionLabel();

            SetFullscreenActive(config.MediaPlayer.FullscreenActive);
        }

        private void BtnCollapseSettingsPane_Click(object sender, RoutedEventArgs e)
        {
            CollapseSettingsPane();
        }

        private void BtnShowSettingsPane_Click(object sender, RoutedEventArgs e)
        {
            ShowSettingsPane();
        }

        private void CollapseSettingsPane()
        {
            _settingsPaneOpen = false;
            PersistRuntimeState();
            double fromWidth = SettingsColumn.Width.IsAbsolute ? SettingsColumn.Width.Value : DefaultSettingsWidth;

            BtnCollapseSettingsPane.IsEnabled = false;
            BtnShowSettingsPane.Visibility = Visibility.Collapsed;

            var anim = new GridLengthAnimation
            {
                From = new GridLength(fromWidth, GridUnitType.Pixel),
                To = new GridLength(0, GridUnitType.Pixel),
                Duration = SettingsPaneAnimDuration,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };

            anim.Completed += (_, _) =>
            {
                SettingsColumn.BeginAnimation(ColumnDefinition.WidthProperty, null);
                SettingsColumn.Width = new GridLength(0, GridUnitType.Pixel);
                SettingsPaneBorder.Visibility = Visibility.Collapsed;
                SplitterColumn.Width = new GridLength(0, GridUnitType.Pixel);
                if (!_playerSettingsPaneOpen)
                    Grid.SetColumnSpan(PlayerPaneBorder, 5);
                UpdatePlayerCornerRadius();
                UpdateGradientClip();

                BtnCollapseSettingsPane.Visibility = Visibility.Collapsed;
                BtnShowSettingsPane.Visibility = Visibility.Visible;
                BtnShowSettingsPane.IsEnabled = true;
            };

            SettingsColumn.BeginAnimation(ColumnDefinition.WidthProperty, anim);
        }

        private void ShowSettingsPane()
        {
            if (SettingsHost.Content == null)
            {
                _settingsPaneOpen = false;
                PersistRuntimeState();
                SettingsPaneBorder.Visibility = Visibility.Collapsed;
                SettingsColumn.Width = new GridLength(0, GridUnitType.Pixel);
                SplitterColumn.Width = new GridLength(0, GridUnitType.Pixel);
                BtnShowSettingsPane.Visibility = Visibility.Collapsed;
                BtnCollapseSettingsPane.Visibility = Visibility.Collapsed;
                UpdatePlayerCornerRadius();
                UpdateGradientClip();
                return;
            }

            double collapseThreshold = _panesSwapped ? PlayerSettingsAutoCollapseThreshold : SettingsAutoCollapseThreshold;
            double expandWidth = _panesSwapped ? PlayerSettingsAutoExpandWidth : SettingsAutoExpandWidth;
            // If the other pane is also open, we need room for both.
            if (_playerSettingsPaneOpen) expandWidth += _panesSwapped ? DefaultSettingsWidth : DefaultPlayerSettingsWidth;
            if (ActualWidth < collapseThreshold || (_playerSettingsPaneOpen && ActualWidth < expandWidth))
                Width = expandWidth;

            _settingsPaneOpen = true;
            PersistRuntimeState();
            Grid.SetColumnSpan(PlayerPaneBorder, 1);
            PlayerPaneBorder.BorderThickness = new Thickness(1);
            SplitterColumn.Width = new GridLength(8, GridUnitType.Pixel);
            SettingsPaneBorder.Visibility = Visibility.Visible;
            BtnShowSettingsPane.Visibility = Visibility.Collapsed;
            BtnCollapseSettingsPane.Visibility = Visibility.Visible;
            BtnCollapseSettingsPane.IsEnabled = false;
            UpdatePlayerCornerRadius();

            ClampSettingsColumnWidth();
            double targetWidth = Math.Min(
                _panesSwapped ? DefaultPlayerSettingsWidth : DefaultSettingsWidth,
                SettingsColumn.MaxWidth > 0 ? SettingsColumn.MaxWidth : (_panesSwapped ? DefaultPlayerSettingsWidth : DefaultSettingsWidth));
            SettingsColumn.Width = new GridLength(0, GridUnitType.Pixel);
            double fromWidth = 0;

            var anim = new GridLengthAnimation
            {
                From = new GridLength(fromWidth, GridUnitType.Pixel),
                To = new GridLength(targetWidth, GridUnitType.Pixel),
                Duration = SettingsPaneAnimDuration,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };

            anim.Completed += (_, _) =>
            {
                SettingsColumn.BeginAnimation(ColumnDefinition.WidthProperty, null);
                SettingsColumn.Width = new GridLength(targetWidth, GridUnitType.Pixel);
                BtnCollapseSettingsPane.IsEnabled = true;
                UpdateGradientClip();
            };

            SettingsColumn.BeginAnimation(ColumnDefinition.WidthProperty, anim);
        }

        private string? _lastCoverImagePath;
        private bool _hasSong = false;
        private bool _isPlaying = false;

        public void UpdateTrack(string? title, string? artist, string? album, string? coverPath, bool isPlaying)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => UpdateTrack(title, artist, album, coverPath, isPlaying));
                return;
            }

            // Predictive pause: keep the flipped icon until the phone confirms the
            // new state or the hold window expires (failed command -> corrected here).
            if (_pausePredictedIsPlaying.HasValue)
            {
                if (isPlaying == _pausePredictedIsPlaying.Value || DateTime.UtcNow >= _pausePredictHoldUntil)
                    _pausePredictedIsPlaying = null;
                else
                    isPlaying = _pausePredictedIsPlaying.Value;
            }

            // Predictive track switch: ignore updates still describing the pre-click
            // track for a short window so an in-flight poll doesn't stomp the
            // prediction before the phone has reacted. Anything else (the new track,
            // or the old track after the hold expires) applies normally.
            if (DateTime.UtcNow < _predictionHoldUntil
                && string.Equals(title, _predictionPreClickTitle, StringComparison.Ordinal)
                && string.Equals(artist, _predictionPreClickArtist, StringComparison.Ordinal))
            {
                return;
            }
            _predictionHoldUntil = DateTime.MinValue;

            _lastRawTitle = title;
            _lastRawArtist = artist;

            string normalizedTitle = NormalizeTrackText(title);
            string normalizedArtist = NormalizeTrackText(artist, replaceNullWithPlaceholder: true);
            string normalizedAlbum = NormalizeTrackText(album, replaceNullWithPlaceholder: true);

            bool hasSong = !string.IsNullOrWhiteSpace(normalizedTitle) && normalizedTitle != "-";
            bool hasSongChanged = hasSong != _hasSong;
            _hasSong = hasSong;
            _isPlaying = isPlaying;

            _vm.Title = normalizedTitle;
            _vm.Artist = normalizedArtist;
            _vm.Album = normalizedAlbum;
            RenderTransportIcons(isPlaying);

            RenderAuxiliaryIcons();
            RefreshVolumeIcon();

            _currentCoverPath = string.IsNullOrWhiteSpace(coverPath) ? null : coverPath;

            if (!string.Equals(_lastCoverImagePath, _currentCoverPath, StringComparison.OrdinalIgnoreCase))
            {
                _lastCoverImagePath = _currentCoverPath;
                FadeCoverImage(_currentCoverPath);
            }

            ApplyCoverGradientBackground(hasSong ? _currentCoverPath : null);
            ApplyPlayerTextColor(hasSong);

            if (_lyricsViewActive && _lyricsLines.Count > 0 && hasSongChanged)
                AdoptLyricsData(new LyricsOverlayManager.LyricsTrackData(_lyricsLines, _lyricsAreTimed));
        }

        private void ApplyPlayerTextColor(bool hasSong)
        {
            if (hasSong)
            {
                PlayerPaneBorder.SetValue(TextBlock.ForegroundProperty, Brushes.White);
                TxtTitle.Foreground = Brushes.White;
                TxtArtist.Foreground = Brushes.White;
                TxtAlbum.Foreground = Brushes.White;
                TxtPositionLabel.Foreground = Brushes.White;
                TxtDurationLabel.Foreground = Brushes.White;
                TxtConnectionLabel.Foreground = Brushes.White;

                ProgressSlider.Foreground = Brushes.White;
                ProgressSlider.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            }
            else
            {
                PlayerPaneBorder.ClearValue(TextBlock.ForegroundProperty);
                TxtTitle.ClearValue(TextBlock.ForegroundProperty);
                TxtArtist.ClearValue(TextBlock.ForegroundProperty);
                TxtAlbum.ClearValue(TextBlock.ForegroundProperty);
                TxtPositionLabel.ClearValue(TextBlock.ForegroundProperty);
                TxtDurationLabel.ClearValue(TextBlock.ForegroundProperty);

                bool isDark = IsDarkThemeActive();
                TxtConnectionLabel.Foreground = isDark ? Brushes.White : Brushes.Black;
                ProgressSlider.Foreground = isDark ? Brushes.White : Brushes.Black;
                ProgressSlider.Background = new SolidColorBrush(isDark
                    ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)
                    : Color.FromArgb(0x33, 0x00, 0x00, 0x00));
            }
        }

        public void NotifyThemeChanged()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(NotifyThemeChanged);
                return;
            }

            _lastGradientSourcePath = null;
            ApplyCoverGradientBackground(_hasSong ? _currentCoverPath : null);

            ApplyPlayerTextColor(_hasSong);

            RenderTransportIcons(isPlaying: _isPlaying);
            RenderAuxiliaryIcons();
            RefreshVolumeIcon();
            RenderSettingsPaneArrowIcons();
            RenderHelpButtonIcon();
            RenderFullscreenButtonIcon();
            RefreshAudioQualityButton();
            RefreshAlwaysOnTopButton();

            if (_lyricsViewActive && _lyricsLines.Count > 0)
                AdoptLyricsData(new LyricsOverlayManager.LyricsTrackData(_lyricsLines, _lyricsAreTimed));
        }

        private void PersistRuntimeState()
        {
            try
            {
                App.Config.MediaPlayer.SettingsPaneOpen = _panesSwapped ? _playerSettingsPaneOpen : _settingsPaneOpen;
                App.Config.MediaPlayer.InlineLyricsViewActive = _lyricsViewActive;
                App.Config.MediaPlayer.FullscreenActive = _isFullscreen;
                MusicConfigManager.Save(App.Config);
                (Application.Current as App)?.UpdateConfig(App.Config);
            }
            catch
            {
            }
        }

        private static string NormalizeTrackText(string? value, bool replaceNullWithPlaceholder = false)
        {
            if (string.IsNullOrWhiteSpace(value))
                return replaceNullWithPlaceholder ? " " : string.Empty;

            var trimmed = value.Trim();
            return trimmed.Equals("null", StringComparison.OrdinalIgnoreCase)
                ? (replaceNullWithPlaceholder ? " " : string.Empty)
                : trimmed;
        }

        public void UpdateProgress(long positionMs, long durationMs)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => UpdateProgress(positionMs, durationMs));
                return;
            }

            _lastPositionMs = positionMs;
            _lastDurationMs = durationMs;
            _positionAnchorMs = positionMs;
            _positionAnchorTime = DateTime.UtcNow;

            if (durationMs <= 0)
            {
                ProgressSlider.Maximum = 1;
                ProgressSlider.Value = 0;
                _vm.PositionLabel = "0:00";
                _vm.DurationLabel = "0:00";
                BtnSeekBack.Visibility = Visibility.Collapsed;
                BtnSeekFwd.Visibility = Visibility.Collapsed;
                return;
            }

            ProgressSlider.Maximum = durationMs;

            // Paint the position immediately. The smooth timer only advances the
            // slider while playing, so a freshly resolved track (duration arrives
            // after the pull) or a paused track would otherwise leave the bar and
            // elapsed label stuck at their previous value until playback resumes.
            long paintedMs = Math.Max(0, Math.Min(positionMs, durationMs));
            ProgressSlider.Value = paintedMs;

            RefreshSeekButtonVisibility();

            _vm.DurationLabel = FormatMs(durationMs);
            RefreshPositionLabel();
        }

        private void SmoothTimer_Tick(object? sender, EventArgs e)
        {
            if (_lastDurationMs <= 0 || !_isPlaying)
                return;

            long elapsed = (long)(DateTime.UtcNow - _positionAnchorTime).TotalMilliseconds;
            long raw = _positionAnchorMs + elapsed;

            // If we've passed the end of the track, wrap the anchor forward so the bar
            // runs smoothly from 0 without waiting for the next UpdateProgress(0,...) poll.
            if (raw >= _lastDurationMs)
            {
                long overflow = raw - _lastDurationMs;
                _positionAnchorMs = 0;
                _positionAnchorTime = DateTime.UtcNow - TimeSpan.FromMilliseconds(overflow);
                raw = overflow;
            }

            long interpolated = Math.Max(0, Math.Min(raw, _lastDurationMs));

            _lastPositionMs = interpolated;
            ProgressSlider.Value = interpolated;
            RefreshPositionLabel();
        }

        // ── Inline toast support ─────────────────────────────────────────────

        public void AddToast(UIElement element)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => AddToast(element)); return; }
            // Center the fixed-width toast card inside the panel.
            element.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            var wrapper = new System.Windows.Controls.Border { Margin = new Thickness(0, 8, 0, 0), Child = (FrameworkElement)element };
            ToastOverlayPanel.Children.Insert(0, wrapper);
        }

        public void RemoveToast(UIElement element)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => RemoveToast(element)); return; }
            // element is the Border we wrapped it in, or the panel itself -- find the wrapper.
            var wrapper = ToastOverlayPanel.Children
                .OfType<System.Windows.Controls.Border>()
                .FirstOrDefault(b => b.Child == element || b == element);
            if (wrapper != null)
                ToastOverlayPanel.Children.Remove(wrapper);
        }

        public void SetConnectionStatus(string status, string detail, Color statusColor)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetConnectionStatus(status, detail, statusColor));
                return;
            }

            bool wasDisconnected = _connectionStatusText.StartsWith("Not connected")
                                   || _connectionStatusText.StartsWith("Wi-Fi port");
            bool isConnected = !status.StartsWith("Not connected")
                               && !status.StartsWith("Wi-Fi port");

            _connectionStatusText = status;
            _connectionDetailText = detail;
            _connectionColor = statusColor;
            _isDeviceConnected = isConnected;
            RefreshConnectionButton();

            // Fire an immediate battery poll the first time a device becomes reachable
            // so the icon isn't blank until the 2.5-minute timer ticks.
            if (isConnected && wasDisconnected)
                _ = PollBatteryAsync();
        }

        // ── Player settings pane (right side) ────────────────────────────────

        private const double DefaultPlayerSettingsWidth = 280;

        private void ClampPlayerSettingsColumnWidth()
        {
            if (!_playerSettingsPaneOpen) return;
            double available = ActualWidth - 28;
            if (available <= 0) return;
            double defaultWidth = _panesSwapped ? DefaultSettingsWidth : DefaultPlayerSettingsWidth;
            double max = Math.Min(defaultWidth, available * SettingsMaxWidthFraction);
            if (max <= 0) return;
            PlayerSettingsColumn.MaxWidth = max;
            PlayerSettingsColumn.Width = new GridLength(max, GridUnitType.Pixel);
        }

        private void CollapsePlayerSettingsPane()
        {
            _playerSettingsPaneOpen = false;
            PersistRuntimeState();
            double fromWidth = PlayerSettingsColumn.Width.IsAbsolute
                ? PlayerSettingsColumn.Width.Value
                : DefaultPlayerSettingsWidth;

            BtnCollapsePlayerSettingsPane.IsEnabled = false;
            BtnShowPlayerSettingsPane.Visibility = Visibility.Collapsed;

            var anim = new GridLengthAnimation
            {
                From = new GridLength(fromWidth, GridUnitType.Pixel),
                To = new GridLength(0, GridUnitType.Pixel),
                Duration = SettingsPaneAnimDuration,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };

            anim.Completed += (_, _) =>
            {
                PlayerSettingsColumn.BeginAnimation(ColumnDefinition.WidthProperty, null);
                PlayerSettingsColumn.Width = new GridLength(0, GridUnitType.Pixel);
                PlayerSettingsPaneBorder.Visibility = Visibility.Collapsed;
                PlayerSettingsSplitterColumn.Width = new GridLength(0, GridUnitType.Pixel);
                UpdatePlayerCornerRadius();
                UpdateGradientClip();

                BtnCollapsePlayerSettingsPane.Visibility = Visibility.Collapsed;
                BtnShowPlayerSettingsPane.Visibility = Visibility.Visible;
                BtnShowPlayerSettingsPane.IsEnabled = true;
            };

            PlayerSettingsColumn.BeginAnimation(ColumnDefinition.WidthProperty, anim);
        }

        private void ShowPlayerSettingsPane()
        {
            if (PlayerSettingsHost.Content == null)
            {
                CollapsePlayerSettingsPane();
                return;
            }

            double collapseThreshold = _panesSwapped ? SettingsAutoCollapseThreshold : PlayerSettingsAutoCollapseThreshold;
            double expandWidth = _panesSwapped ? SettingsAutoExpandWidth : PlayerSettingsAutoExpandWidth;
            // If the other pane is also open, we need room for both.
            if (_settingsPaneOpen) expandWidth += _panesSwapped ? DefaultPlayerSettingsWidth : DefaultSettingsWidth;
            if (ActualWidth < collapseThreshold || (_settingsPaneOpen && ActualWidth < expandWidth))
                Width = expandWidth;

            _playerSettingsPaneOpen = true;
            PersistRuntimeState();
            Grid.SetColumnSpan(PlayerPaneBorder, 1);
            PlayerPaneBorder.BorderThickness = new Thickness(1);
            PlayerSettingsSplitterColumn.Width = new GridLength(8, GridUnitType.Pixel);
            PlayerSettingsPaneBorder.Visibility = Visibility.Visible;
            BtnShowPlayerSettingsPane.Visibility = Visibility.Collapsed;
            BtnCollapsePlayerSettingsPane.Visibility = Visibility.Visible;
            BtnCollapsePlayerSettingsPane.IsEnabled = false;
            UpdatePlayerCornerRadius();

            ClampPlayerSettingsColumnWidth();
            double defaultWidth = _panesSwapped ? DefaultSettingsWidth : DefaultPlayerSettingsWidth;
            double targetWidth = Math.Min(
                defaultWidth,
                PlayerSettingsColumn.MaxWidth > 0 ? PlayerSettingsColumn.MaxWidth : defaultWidth);
            PlayerSettingsColumn.Width = new GridLength(0, GridUnitType.Pixel);

            var anim = new GridLengthAnimation
            {
                From = new GridLength(0, GridUnitType.Pixel),
                To = new GridLength(targetWidth, GridUnitType.Pixel),
                Duration = SettingsPaneAnimDuration,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };

            anim.Completed += (_, _) =>
            {
                PlayerSettingsColumn.BeginAnimation(ColumnDefinition.WidthProperty, null);
                PlayerSettingsColumn.Width = new GridLength(targetWidth, GridUnitType.Pixel);
                BtnCollapsePlayerSettingsPane.IsEnabled = true;
                UpdateGradientClip();
            };

            PlayerSettingsColumn.BeginAnimation(ColumnDefinition.WidthProperty, anim);
        }

        private void BtnCollapsePlayerSettingsPane_Click(object sender, RoutedEventArgs e)
            => CollapsePlayerSettingsPane();

        private void BtnShowPlayerSettingsPane_Click(object sender, RoutedEventArgs e)
            => ShowPlayerSettingsPane();

        private void UpdatePlayerCornerRadius()
        {
            bool left = _settingsPaneOpen;
            bool right = _playerSettingsPaneOpen;

            if (!left && !right)
            {
                Grid.SetColumnSpan(PlayerPaneBorder, 5);
                PlayerPaneBorder.CornerRadius = new CornerRadius(12);
                PlayerPaneBorder.BorderThickness = new Thickness(0);
            }
            else if (left && !right)
            {
                Grid.SetColumnSpan(PlayerPaneBorder, 1);
                PlayerPaneBorder.CornerRadius = new CornerRadius(6, 12, 12, 6);
                PlayerPaneBorder.BorderThickness = new Thickness(1);
            }
            else if (!left && right)
            {
                Grid.SetColumnSpan(PlayerPaneBorder, 1);
                PlayerPaneBorder.CornerRadius = new CornerRadius(12, 6, 6, 12);
                PlayerPaneBorder.BorderThickness = new Thickness(1);
            }
            else
            {
                Grid.SetColumnSpan(PlayerPaneBorder, 1);
                PlayerPaneBorder.CornerRadius = new CornerRadius(6);
                PlayerPaneBorder.BorderThickness = new Thickness(1);
            }
        }

        // ── Player settings: apply to live UI ────────────────────────────────

        private void OnPlayerSettingChanged()
        {
            ApplyPlayerSettings();
            RefreshNextSongPanelSettings();
            RefreshAudioQualityButton();
            ApplyPaneLayout();
        }

        public void ApplyPlayerSettings()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(ApplyPlayerSettings); return; }

            var c = App.Config;

            TxtTitle.Visibility = c.MediaPlayer.ShowTitle ? Visibility.Visible : Visibility.Collapsed;
            TxtArtist.Visibility = c.MediaPlayer.ShowArtist ? Visibility.Visible : Visibility.Collapsed;
            TxtAlbum.Visibility = c.MediaPlayer.ShowAlbum ? Visibility.Visible : Visibility.Collapsed;

            Grid.SetRow(TxtArtist, c.MediaPlayer.SwapArtistAlbum ? 3 : 2);
            Grid.SetRow(TxtAlbum, c.MediaPlayer.SwapArtistAlbum ? 2 : 3);
            TxtArtist.Margin = c.MediaPlayer.SwapArtistAlbum ? new Thickness(0, 0, 0, 10) : new Thickness(0, 0, 0, 2);
            TxtAlbum.Margin = c.MediaPlayer.SwapArtistAlbum ? new Thickness(0, 0, 0, 2) : new Thickness(0, 0, 0, 10);

            if (CoverViewbox != null)
            {
                CoverViewbox.Visibility = c.MediaPlayer.ShowCover ? Visibility.Visible : Visibility.Collapsed;
                CoverViewbox.Effect = c.MediaPlayer.CoverShadow
                    ? new DropShadowEffect { Color = Colors.Black, BlurRadius = 32, ShadowDepth = 8, Opacity = 0.55, Direction = 315 }
                    : null;
            }
            double coverRadius = c.MediaPlayer.CoverRoundedCorners ? 10 : 0;
            CoverBorder.CornerRadius = new CornerRadius(coverRadius);
            CoverImageGrid.Clip = coverRadius > 0
                ? new RectangleGeometry(new Rect(0, 0, 420, 420), coverRadius, coverRadius)
                : null;

            var textShadow = c.MediaPlayer.TextShadow
                ? new DropShadowEffect { Color = Colors.Black, BlurRadius = 12, ShadowDepth = 1, Opacity = 0.65, Direction = 270 }
                : null;
            TxtTitle.Effect = textShadow;
            TxtArtist.Effect = textShadow;
            TxtAlbum.Effect = textShadow;

            ApplyPillMode(BtnConnectionInfo, c.MediaPlayer.PillModeConnection);
            ApplyPillMode(BtnAudioLink, c.MediaPlayer.PillModeAudioLink);
            ApplyPillMode(BtnAudioQuality, c.MediaPlayer.PillModeQuality);
            ApplyPillMode(BtnAlwaysOnTop, c.MediaPlayer.PillModeAlwaysOnTop);
            BtnConnectionTop.Visibility = c.MediaPlayer.PillModeConnection == 3 ? Visibility.Visible : Visibility.Collapsed;

            BtnVolume.Visibility = c.MediaPlayer.ShowVolumeButton ? Visibility.Visible : Visibility.Collapsed;
            BtnLyrics.Visibility = c.MediaPlayer.ShowLyricsButton ? Visibility.Visible : Visibility.Collapsed;
            BtnBattery.Visibility = c.MediaPlayer.ShowBattery ? Visibility.Visible : Visibility.Collapsed;
            BtnHelp.Visibility = c.MediaPlayer.ShowHelpButton ? Visibility.Visible : Visibility.Collapsed;
            BtnFullscreen.Visibility = c.MediaPlayer.ShowFullscreenButton ? Visibility.Visible : Visibility.Collapsed;

            RefreshSeekButtonVisibility();
            RefreshBatteryPollInterval();

            _lastGradientSourcePath = null;
            ApplyCoverGradientBackground(_hasSong ? _currentCoverPath : null);
        }

        private static void ApplyPillMode(Button pill, int mode)
        {
            // Mode 3 = Top (connection only): collapse pill, show top icon instead.
            // The top icon visibility is handled separately in ApplyPlayerSettings.
            if (mode == 2 || mode == 3) { pill.Visibility = Visibility.Collapsed; return; }
            pill.Visibility = Visibility.Visible;
            if (pill.Content is StackPanel sp)
                foreach (UIElement child in sp.Children)
                    if (child is TextBlock tb)
                        tb.Visibility = mode == 1 ? Visibility.Collapsed : Visibility.Visible;
            // Connection pill mini: only the dot remains — clear text margin and use equal padding so it's circular.
            if (pill.Name == "BtnConnectionInfo")
            {
                pill.Padding = mode == 1 ? new Thickness(8) : new Thickness(10, 4, 10, 4);
                if (pill.Content is StackPanel csp)
                    foreach (UIElement child in csp.Children)
                        if (child is System.Windows.Shapes.Ellipse dot)
                            dot.Margin = mode == 1 ? new Thickness(0) : new Thickness(0, 0, 5, 0);
            }
        }

        // ── Pane side swap ────────────────────────────────────────────────────

        private void ApplyPaneLayout()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(ApplyPaneLayout); return; }

            bool swap = App.Config.MediaPlayer.SwapSettingsLocation;
            if (swap == _panesSwapped) return;

            // Remember which content types were open before the swap.
            bool mainWasOpen = _panesSwapped ? _playerSettingsPaneOpen : _settingsPaneOpen;
            bool playerWasOpen = _panesSwapped ? _settingsPaneOpen : _playerSettingsPaneOpen;

            // Collapse both panes instantly (no animation) to avoid void states.
            _settingsPaneOpen = false;
            _playerSettingsPaneOpen = false;
            SettingsColumn.Width = new GridLength(0, GridUnitType.Pixel);
            PlayerSettingsColumn.Width = new GridLength(0, GridUnitType.Pixel);
            SplitterColumn.Width = new GridLength(0, GridUnitType.Pixel);
            PlayerSettingsSplitterColumn.Width = new GridLength(0, GridUnitType.Pixel);
            SettingsPaneBorder.Visibility = Visibility.Collapsed;
            PlayerSettingsPaneBorder.Visibility = Visibility.Collapsed;
            BtnCollapseSettingsPane.Visibility = Visibility.Collapsed;
            BtnCollapsePlayerSettingsPane.Visibility = Visibility.Collapsed;
            BtnShowSettingsPane.Visibility = Visibility.Visible;
            BtnShowPlayerSettingsPane.Visibility = Visibility.Visible;
            Grid.SetColumnSpan(PlayerPaneBorder, 5);
            UpdatePlayerCornerRadius();

            // Move content to new hosts.
            var mainContent = MainSettingsHost.Content;
            MainSettingsHost.Content = null;
            _panesSwapped = swap;
            MainSettingsHost.Content = mainContent;
            (_panesSwapped ? (ContentControl)SettingsHost : PlayerSettingsHost).Content = _playerSettingsPane;

            // Reopen whatever was open before, using the correct column for the new positions.
            if (mainWasOpen)
            {
                if (_panesSwapped) ShowPlayerSettingsPane(); else ShowSettingsPane();
            }
            if (playerWasOpen)
            {
                if (_panesSwapped) ShowSettingsPane(); else ShowPlayerSettingsPane();
            }

            RenderSettingsPaneArrowIcons();
        }
    }
}