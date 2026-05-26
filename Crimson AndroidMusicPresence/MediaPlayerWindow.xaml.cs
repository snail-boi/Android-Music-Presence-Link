using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace musicpresense
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
        // Window width below which the settings pane auto-collapses.
        private const double SettingsAutoCollapseThreshold = 950;
        // Window width the window expands to when opening the pane on a narrow window.
        private const double SettingsAutoExpandWidth = 950;
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
        // Set true while we initialize the slider value from the actual scrcpy
        // volume on popup open, so the resulting ValueChanged event doesn't
        // bounce back as a SetVolume call.
        private bool _suppressVolumeSliderEcho;
        private string? _currentCoverPath;
        private string? _lastGradientSourcePath;
        // Tracks the theme used for the last idle (no-image) gradient paint, so we can
        // skip the crossfade when the next idle paint would produce an identical brush
        // (otherwise the transition pulses from a color back to the same color).
        // Nullable so the very first idle paint always renders.
        private bool? _lastIdleIsDark;
        private bool _useLayerA = true;
        private static readonly Color DefaultTopLeft = Color.FromRgb(52, 52, 52);
        private static readonly Color DefaultTopRight = Color.FromRgb(43, 43, 43);
        private static readonly Color DefaultBottomLeft = Color.FromRgb(36, 36, 36);
        private static readonly Color DefaultBottomRight = Color.FromRgb(28, 28, 28);

        // Cover art crossfade
        private static readonly Duration CoverFadeDuration = new Duration(TimeSpan.FromMilliseconds(350));
        private bool _coverUseLayerA = true;

        // Whether TxtPositionLabel shows elapsed (false) or time-left (true)
        private bool _showTimeLeft = false;
        private long _lastPositionMs;
        private long _lastDurationMs;

        // Smooth progress interpolation — updated every poll, ticked every 100 ms.
        private long _positionAnchorMs;
        private DateTime _positionAnchorTime;
        private DispatcherTimer? _smoothTimer;

        // Settings pane open/closed state — used by SizeChanged to decide whether to clamp or auto-collapse.
        private bool _settingsPaneOpen = false;

        // Audio link toggle state
        private bool _audioLinkActive = false;
        private readonly Action<bool>? _setAudioLink;

        // Audio quality preset wiring. _getConfig returns the latest MusicConfig
        // (so the button label reflects current saved values without needing to be
        // re-pushed on every config change). _applyAudioQualityPreset writes the
        // preset to the live config, persists it, and restarts scrcpy if needed.
        private readonly Func<MusicConfig>? _getConfig;
        private readonly Action<AudioQualityPresets.Preset>? _applyAudioQualityPreset;
        private readonly Action? _openCustomQualityWindow;

        // Hide-decorations toggle state. We snapshot the chrome before hiding
        // so the toggle restores the user's exact pre-toggle layout. Despite the
        // "fullscreen" naming in code, this only hides title bar / borders;
        // the window stays the same size and remains movable & resizable.
        private bool _isFullscreen = false;
        private WindowStyle _prevWindowStyle = WindowStyle.SingleBorderWindow;
        private ResizeMode _prevResizeMode = ResizeMode.CanResizeWithGrip;

        // Always-on-top state.
        private bool _alwaysOnTop = false;

        // Connection status info (set from outside)
        private string _connectionStatusText = "Not connected";
        private string _connectionDetailText = "";
        private Color _connectionColor = Color.FromRgb(0xFF, 0x3B, 0x30);

        // Fast-seek ADB action
        private readonly Func<int, Task>? _seekRelativeSeconds;

        // Inline lyrics view state
        private readonly LyricsOverlayManager? _lyricsManager;
        private bool _lyricsViewActive;
        private IReadOnlyList<LyricsOverlayManager.LyricsLineDto> _lyricsLines = Array.Empty<LyricsOverlayManager.LyricsLineDto>();
        private bool _lyricsAreTimed;
        private int _lyricsHighlightedIndex = -1;
        private DispatcherTimer? _lyricsTimer;
        private readonly List<TextBlock> _lyricsLineBlocks = new();
        // Wrapper Border for each line, parallel to _lyricsLineBlocks, used to render
        // the darkened background pill behind the active line. null entries match the
        // empty separator slots in _lyricsLineBlocks.
        private readonly List<Border> _lyricsLineHosts = new();
        // Brushes for lyrics lines. Recomputed once per track/theme change in
        // RebuildLyricsPanel; reused per highlight update to avoid allocating new
        // brushes (which would cause a render-pass flash on each line transition).
        private Brush _lyricsInactiveBrush = Brushes.White;
        private Brush _lyricsActiveBrush = Brushes.White;
        private Brush _lyricsActiveLineBgBrush = Brushes.Transparent;

        // Continuous-lerp scroll: instead of starting a new tween on each line change
        // (which produces a stuttery restart when lines come fast), we keep a target
        // offset and ease toward it once per frame via CompositionTarget.Rendering.
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
            Func<int, int, int, Task>? setPhoneVolume = null)
        {
            InitializeComponent();
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

            if (_lyricsManager != null)
            {
                _lyricsManager.LinesChanged += OnLyricsLinesChanged;
            }

            RenderTransportIcons(isPlaying: false);
            RenderAuxiliaryIcons();
            RenderSettingsPaneArrowIcon();
            RenderFastSeekIcons();
            RenderAudioLinkButton();
            RenderHelpButtonIcon();
            RenderFullscreenButtonIcon();
            RefreshAlwaysOnTopButton();
            RefreshAudioQualityButton();
            ApplyCoverGradientBackground(null);

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
                ApplySavedRuntimeState();
            };

            _smoothTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _smoothTimer.Tick += SmoothTimer_Tick;
            _smoothTimer.Start();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            var config = App.Config;
            Width = config.MediaPlayerWindowWidth;
            Height = config.MediaPlayerWindowHeight;
            Top = config.MediaPlayerWindowTop;
            Left = config.MediaPlayerWindowLeft;
            WindowState = config.MediaPlayerWindowState;
        }

        private void MediaPlayerWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!e.WidthChanged) return;

            if (_settingsPaneOpen)
            {
                // Auto-collapse if the window becomes too narrow to show both panes.
                if (ActualWidth < SettingsAutoCollapseThreshold)
                    CollapseSettingsPane();
                else
                    ClampSettingsColumnWidth();
            }
        }

        private void ClampSettingsColumnWidth()
        {
            if (!_settingsPaneOpen) return;

            double available = ActualWidth - 28;
            if (available <= 0) return;

            double max = Math.Min(DefaultSettingsWidth, available * SettingsMaxWidthFraction);
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
            config.MediaPlayerSettingsPaneOpen = _settingsPaneOpen;
            config.MediaPlayerInlineLyricsViewActive = _lyricsViewActive;
            config.MediaPlayerFullscreenActive = _isFullscreen;
            config.MediaPlayerWindowState = WindowState;
            config.MediaPlayerWindowWidth = RestoreBounds.Width;
            config.MediaPlayerWindowHeight = RestoreBounds.Height;
            config.MediaPlayerWindowTop = RestoreBounds.Top;
            config.MediaPlayerWindowLeft = RestoreBounds.Left;

            MusicConfigManager.Save(config);
        }

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

        public void SetSettingsContent(object? content)
        {
            SettingsHost.Content = content;
        }

        public object? TakeSettingsContent()
        {
            var content = SettingsHost.Content;
            SettingsHost.Content = null;
            return content;
        }

        public void ClearSettingsContent()
        {
            SettingsHost.Content = null;
        }

        public void ApplySavedRuntimeState()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(ApplySavedRuntimeState);
                return;
            }

            var config = App.Config;

            if (config.MediaPlayerSettingsPaneOpen)
                ShowSettingsPane();
            else
                CollapseSettingsPane();

            if (_lyricsViewActive != config.MediaPlayerInlineLyricsViewActive)
                ToggleInlineLyricsView();

            SetFullscreenActive(config.MediaPlayerFullscreenActive);
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
                // Release the animation hold so width can be set directly again.
                SettingsColumn.BeginAnimation(ColumnDefinition.WidthProperty, null);
                SettingsColumn.Width = new GridLength(0, GridUnitType.Pixel);
                SettingsPaneBorder.Visibility = Visibility.Collapsed;
                SplitterColumn.Width = new GridLength(0, GridUnitType.Pixel);
                Grid.SetColumnSpan(PlayerPaneBorder, 3);
                PlayerPaneBorder.CornerRadius = new CornerRadius(12);
                PlayerPaneBorder.BorderThickness = new Thickness(0);
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
                Grid.SetColumnSpan(PlayerPaneBorder, 3);
                PlayerPaneBorder.CornerRadius = new CornerRadius(12);
                PlayerPaneBorder.BorderThickness = new Thickness(0);
                UpdateGradientClip();
                return;
            }

            // If the window is too narrow to show both panes, grow it first.
            if (ActualWidth < SettingsAutoCollapseThreshold)
                Width = SettingsAutoExpandWidth;

            // Restore player column layout before animating.
            _settingsPaneOpen = true;
            PersistRuntimeState();
            Grid.SetColumnSpan(PlayerPaneBorder, 1);
            PlayerPaneBorder.CornerRadius = new CornerRadius(6);
            PlayerPaneBorder.BorderThickness = new Thickness(1);
            SplitterColumn.Width = new GridLength(8, GridUnitType.Pixel);
            SettingsPaneBorder.Visibility = Visibility.Visible;
            BtnShowSettingsPane.Visibility = Visibility.Collapsed;
            BtnCollapseSettingsPane.Visibility = Visibility.Visible;
            BtnCollapseSettingsPane.IsEnabled = false;

            ClampSettingsColumnWidth();
            double targetWidth = Math.Min(DefaultSettingsWidth, SettingsColumn.MaxWidth > 0 ? SettingsColumn.MaxWidth : DefaultSettingsWidth);
            // Reset to 0 so the animation always slides in from nothing.
            // ClampSettingsColumnWidth sets Width to the target which would
            // cause the animation to start at the end and appear instant.
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

        // Track the last cover path shown so we never re-fade the same image on each update tick
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

            string normalizedTitle = NormalizeTrackText(title);
            string normalizedArtist = NormalizeTrackText(artist, replaceNullWithPlaceholder: true);
            string normalizedAlbum = NormalizeTrackText(album, replaceNullWithPlaceholder: true);

            bool hasSong = !string.IsNullOrWhiteSpace(normalizedTitle) && normalizedTitle != "-";
            bool hasSongChanged = hasSong != _hasSong;
            _hasSong = hasSong;
            _isPlaying = isPlaying;

            TxtTitle.Text = normalizedTitle;
            TxtArtist.Text = normalizedArtist;
            TxtAlbum.Text = normalizedAlbum;
            RenderTransportIcons(isPlaying);

            // Auxiliary icons (seek, volume, lyrics, audio link) resolve their brush
            // from _hasSong, so they need to re-render whenever song state changes.
            RenderAuxiliaryIcons();
            RefreshVolumeIcon();

            _currentCoverPath = string.IsNullOrWhiteSpace(coverPath) ? null : coverPath;

            // Only crossfade the cover image when it actually changes
            if (!string.Equals(_lastCoverImagePath, _currentCoverPath, StringComparison.OrdinalIgnoreCase))
            {
                _lastCoverImagePath = _currentCoverPath;
                FadeCoverImage(_currentCoverPath);
            }

            // Gradient background: null path → solid dark. ApplyCoverGradientBackground
            // already has its own _lastGradientSourcePath guard so repeated calls are cheap.
            ApplyCoverGradientBackground(hasSong ? _currentCoverPath : null);
            ApplyPlayerTextColor(hasSong);

            // Lyrics brush colors depend on _hasSong. Only rebuild when that flips,
            // not on every poll tick.
            if (_lyricsViewActive && _lyricsLines.Count > 0 && hasSongChanged)
                AdoptLyricsData(new LyricsOverlayManager.LyricsTrackData(_lyricsLines, _lyricsAreTimed));
        }

        /// <summary>
        /// White text on the player pane whenever a song is active (background is always dark).
        /// When idle in light mode, clear the override so theme black shows on white background.
        /// We set the property on each TextBlock directly because the implicit TextBlock style
        /// merged from MainWindow.xaml (and the parent Button's Foreground for the position
        /// label) both beat values inherited from the parent Border. The progress slider's
        /// fill and thumb are template-bound to its Foreground, so setting that recolors them.
        /// </summary>
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

        /// <summary>
        /// Call this from the host application whenever the color theme changes.
        /// Re-extracts the gradient and re-renders all icons with the new brush.
        /// </summary>
        public void NotifyThemeChanged()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(NotifyThemeChanged);
                return;
            }

            // Bust the gradient cache so it re-extracts and crossfades.
            _lastGradientSourcePath = null;
            ApplyCoverGradientBackground(_hasSong ? _currentCoverPath : null);

            // Re-apply text color in case idle + theme flipped.
            ApplyPlayerTextColor(_hasSong);

            // Re-render all icons with the freshly resolved brush.
            RenderTransportIcons(isPlaying: _isPlaying);
            RenderAuxiliaryIcons();
            RefreshVolumeIcon();
            RenderSettingsPaneArrowIcon();
            RenderHelpButtonIcon();
            RenderFullscreenButtonIcon();
            RefreshAudioQualityButton();
            RefreshAlwaysOnTopButton();

            // Lyrics panel uses theme-aware brushes; rebuild colors.
            if (_lyricsViewActive && _lyricsLines.Count > 0)
                AdoptLyricsData(new LyricsOverlayManager.LyricsTrackData(_lyricsLines, _lyricsAreTimed));
        }

        private void PersistRuntimeState()
        {
            try
            {
                App.Config.MediaPlayerSettingsPaneOpen = _settingsPaneOpen;
                App.Config.MediaPlayerInlineLyricsViewActive = _lyricsViewActive;
                App.Config.MediaPlayerFullscreenActive = _isFullscreen;
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

        /// <summary>
        /// Called every poll cycle. Sets the wall-clock anchor so the smooth timer
        /// can interpolate position between polls regardless of update cycle length.
        /// </summary>
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
                TxtPositionLabel.Text = "0:00";
                TxtDurationLabel.Text = "0:00";
                BtnSeekBack.Visibility = Visibility.Collapsed;
                BtnSeekFwd.Visibility = Visibility.Collapsed;
                return;
            }

            ProgressSlider.Maximum = durationMs;

            // Show skip buttons inline in transport row for songs > 10 minutes
            var seekVisibility = durationMs > 10 * 60 * 1000L ? Visibility.Visible : Visibility.Collapsed;
            BtnSeekBack.Visibility = seekVisibility;
            BtnSeekFwd.Visibility = seekVisibility;

            TxtDurationLabel.Text = FormatMs(durationMs);
        }

        private void SmoothTimer_Tick(object? sender, EventArgs e)
        {
            if (_lastDurationMs <= 0 || !_isPlaying)
                return;

            long elapsed = (long)(DateTime.UtcNow - _positionAnchorTime).TotalMilliseconds;
            long interpolated = Math.Max(0, Math.Min(_positionAnchorMs + elapsed, _lastDurationMs));

            _lastPositionMs = interpolated;
            ProgressSlider.Value = interpolated;
            RefreshPositionLabel();
        }

        // ── Connection Info ───────────────────────────────────────────────────

        /// <summary>
        /// Call this from the host to update connection status.
        /// statusColor should be the same Color used elsewhere in the app for that state.
        /// </summary>
        public void SetConnectionStatus(string status, string detail, Color statusColor)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetConnectionStatus(status, detail, statusColor));
                return;
            }

            _connectionStatusText = status;
            _connectionDetailText = detail;
            _connectionColor = statusColor;
            RefreshConnectionButton();
        }
    }
}