using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        // Settings pane snaps to collapsed when dragged below this width.
        // Set well above 0 so the embedded UserControl never visibly squashes
        // before disappearing.
        private const double CollapsedThreshold = 500;

        // Settings pane MaxWidth is computed dynamically as this fraction of
        // the window's client width on each SizeChanged.
        private const double SettingsMaxWidthFraction = 0.55;

        // Floor for MaxWidth, prevents the cap collapsing on very small windows
        // and locking the user out of their last drag width.
        private const double SettingsMaxWidthFloor = 320;

        // Crossfade duration for cover-art gradient transitions.
        private static readonly Duration GradientFadeDuration = new Duration(TimeSpan.FromMilliseconds(450));

        private const double DefaultSettingsWidth = 500;
        private readonly Func<Task> _pauseAction;
        private readonly Func<Task> _nextAction;
        private readonly Func<Task> _previousAction;
        private readonly Action? _lyricsToggleAction;
        private readonly Func<bool>? _isScrcpyAudioAvailable;
        private readonly Func<float?>? _getVolume;
        private readonly Action<float>? _setVolume;
        private readonly Action<bool>? _stepVolume;
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

        // Audio link toggle state
        private bool _audioLinkActive = false;
        private readonly Action<bool>? _setAudioLink;

        // Audio quality preset wiring. _getConfig returns the latest MusicConfig
        // (so the button label reflects current saved values without needing to be
        // re-pushed on every config change). _applyAudioQualityPreset writes the
        // preset to the live config, persists it, and restarts scrcpy if needed.
        private readonly Func<MusicConfig>? _getConfig;
        private readonly Action<AudioQualityPresets.Preset>? _applyAudioQualityPreset;

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
            Action<AudioQualityPresets.Preset>? applyAudioQualityPreset = null)
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
                UpdateSettingsColumnMaxWidth();
                RefreshVolumeIcon();
                UpdateGradientClip();
                RefreshConnectionButton();
                RefreshAudioQualityButton();
                RenderFullscreenButtonIcon();
                RefreshAlwaysOnTopButton();
            };
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
            UpdateSettingsColumnMaxWidth();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);

            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;

            var config = App.Config;
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

        private void UpdateSettingsColumnMaxWidth()
        {
            // Compute as a fraction of the window's available width so the
            // settings pane scales with the window and never pushes the
            // player pane off screen.
            double available = ActualWidth;
            if (available <= 0) return;

            double max = Math.Max(SettingsMaxWidthFloor, available * SettingsMaxWidthFraction);
            SettingsColumn.MaxWidth = max;

            // If the column is already wider than the new max (e.g. user shrank
            // the window), clamp it down.
            if (SettingsColumn.Width.IsAbsolute && SettingsColumn.Width.Value > max)
            {
                SettingsColumn.Width = new GridLength(max, GridUnitType.Pixel);
            }
        }

        public void SetSettingsContent(object? content)
        {
            SettingsHost.Content = content;
            ShowSettingsPane(restoreDefaultWidth: false);
        }

        public object? TakeSettingsContent()
        {
            var content = SettingsHost.Content;
            SettingsHost.Content = null;
            ShowSettingsPane(restoreDefaultWidth: false);
            return content;
        }

        public void ClearSettingsContent()
        {
            SettingsHost.Content = null;
        }

        private void SettingsSplitter_DragDelta(object sender, DragDeltaEventArgs e)
        {
            // Fade the pane out as it shrinks toward the collapse threshold so it
            // visually disappears before the column actually hits 0. Gives a
            // smoother "snap to closed" feel without the content squashing.
            double w = SettingsColumn.ActualWidth;
            if (w >= CollapsedThreshold)
            {
                SettingsPaneBorder.Opacity = 1;
            }
            else
            {
                // Linear fade from 1 at threshold to 0 at width 0.
                SettingsPaneBorder.Opacity = Math.Clamp(w / CollapsedThreshold, 0, 1);
            }
        }

        private void SettingsSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            SettingsPaneBorder.Opacity = 1;

            if (SettingsColumn.ActualWidth <= CollapsedThreshold)
            {
                CollapseSettingsPane();
            }
            else
            {
                ShowSettingsPane(restoreDefaultWidth: false);
            }
        }

        private void BtnShowSettingsPane_Click(object sender, RoutedEventArgs e)
        {
            ShowSettingsPane(restoreDefaultWidth: true);
        }

        private void CollapseSettingsPane()
        {
            SettingsPaneBorder.Visibility = Visibility.Collapsed;
            SettingsColumn.Width = new GridLength(0, GridUnitType.Pixel);
            SplitterColumn.Width = new GridLength(0, GridUnitType.Pixel);
            BtnShowSettingsPane.Visibility = Visibility.Visible;
            BtnShowSettingsPane.IsEnabled = true;

            // Expand player to fill the full grid area with rounded corners.
            Grid.SetColumnSpan(PlayerPaneBorder, 3);
            PlayerPaneBorder.CornerRadius = new CornerRadius(12);
            PlayerPaneBorder.BorderThickness = new Thickness(0);
            UpdateGradientClip();
        }

        private void ShowSettingsPane(bool restoreDefaultWidth)
        {
            var hasSettingsContent = SettingsHost.Content != null;

            if (!hasSettingsContent)
            {
                SettingsPaneBorder.Visibility = Visibility.Collapsed;
                SettingsColumn.Width = new GridLength(0, GridUnitType.Pixel);
                SplitterColumn.Width = new GridLength(0, GridUnitType.Pixel);
                BtnShowSettingsPane.Visibility = Visibility.Collapsed;
                Grid.SetColumnSpan(PlayerPaneBorder, 3);
                PlayerPaneBorder.CornerRadius = new CornerRadius(12);
                PlayerPaneBorder.BorderThickness = new Thickness(0);
                UpdateGradientClip();
                return;
            }

            SettingsPaneBorder.Visibility = Visibility.Visible;
            SplitterColumn.Width = new GridLength(8, GridUnitType.Pixel);
            PlayerPaneBorder.BorderThickness = new Thickness(1);

            // Restore player to its own column with original corner radius.
            Grid.SetColumnSpan(PlayerPaneBorder, 1);
            PlayerPaneBorder.CornerRadius = new CornerRadius(6);
            UpdateGradientClip();

            if (restoreDefaultWidth || SettingsColumn.Width.Value <= CollapsedThreshold)
            {
                SettingsColumn.Width = new GridLength(DefaultSettingsWidth, GridUnitType.Pixel);
            }

            BtnShowSettingsPane.Visibility = Visibility.Collapsed;
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

            bool hasSong = !string.IsNullOrWhiteSpace(title) && title.Trim() != "-";
            _hasSong = hasSong;
            _isPlaying = isPlaying;

            TxtTitle.Text = string.IsNullOrWhiteSpace(title) ? "-" : title.Trim();
            TxtArtist.Text = string.IsNullOrWhiteSpace(artist) ? "-" : artist.Trim();
            TxtAlbum.Text = string.IsNullOrWhiteSpace(album) ? "-" : album.Trim();
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

            // The inline lyrics color set depends on _hasSong; refresh if open.
            if (_lyricsViewActive && _lyricsLines.Count > 0)
            {
                RebuildLyricsPanel();
                _lyricsHighlightedIndex = -1;
                Dispatcher.BeginInvoke(new Action(() => UpdateLyricsHighlightAndScroll(animate: false)), DispatcherPriority.Loaded);
            }
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
            {
                RebuildLyricsPanel();
                _lyricsHighlightedIndex = -1;
                Dispatcher.BeginInvoke(new Action(() => UpdateLyricsHighlightAndScroll(animate: false)), DispatcherPriority.Loaded);
            }
        }

        /// <summary>
        /// Updates the read-only progress bar and the position/duration labels.
        /// Position is the manually-counted-forward value from MediaController
        /// (Android only reports the last scrub location, so the controller
        /// ticks it forward each poll cycle while playing).
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

            long clamped = Math.Max(0, Math.Min(positionMs, durationMs));

            ProgressSlider.Maximum = durationMs;
            ProgressSlider.Value = clamped;

            // Show skip buttons inline in transport row for songs > 10 minutes
            var seekVisibility = durationMs > 10 * 60 * 1000L ? Visibility.Visible : Visibility.Collapsed;
            BtnSeekBack.Visibility = seekVisibility;
            BtnSeekFwd.Visibility = seekVisibility;

            RefreshPositionLabel();
            TxtDurationLabel.Text = FormatMs(durationMs);
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