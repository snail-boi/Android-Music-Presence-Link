using Microsoft.Win32;
using System;
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

namespace musicpresense
{

    // stuff
    // either keep lyrics a toggle or make it switch to a different lyrics view
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

        // Connection status info (set from outside)
        private string _connectionStatusText = "Not connected";
        private string _connectionDetailText = "";
        private Color _connectionColor = Color.FromRgb(0xFF, 0x3B, 0x30);

        // Fast-seek ADB action
        private readonly Func<int, Task>? _seekRelativeSeconds;

        public MediaPlayerWindow(
            Func<Task> pauseAction,
            Func<Task> nextAction,
            Func<Task> previousAction,
            Action? lyricsToggleAction = null,
            Func<bool>? isScrcpyAudioAvailable = null,
            Func<float?>? getVolume = null,
            Action<float>? setVolume = null,
            Action<bool>? stepVolume = null,
            Action<bool>? setAudioLink = null,
            Func<int, Task>? seekRelativeSeconds = null)
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

            RenderTransportIcons(isPlaying: false);
            RenderAuxiliaryIcons();
            RenderSettingsPaneArrowIcon();
            RenderFastSeekIcons();
            RenderAudioLinkButton();
            ApplyCoverGradientBackground(null);

            SizeChanged += MediaPlayerWindow_SizeChanged;
            PlayerPaneBorder.SizeChanged += (_, _) => UpdateGradientClip();
            Loaded += (_, _) =>
            {
                UpdateSettingsColumnMaxWidth();
                RefreshVolumeIcon();
                UpdateGradientClip();
                RefreshConnectionButton();
            };
        }

        private void MediaPlayerWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!e.WidthChanged) return;
            UpdateSettingsColumnMaxWidth();
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

        private void RefreshPositionLabel()
        {
            if (_showTimeLeft && _lastDurationMs > 0)
            {
                long left = _lastDurationMs - Math.Min(_lastPositionMs, _lastDurationMs);
                TxtPositionLabel.Text = "-" + FormatMs(left);
            }
            else
            {
                TxtPositionLabel.Text = FormatMs(_lastPositionMs);
            }
        }

        private void BtnPositionLabel_Click(object sender, RoutedEventArgs e)
        {
            _showTimeLeft = !_showTimeLeft;
            RefreshPositionLabel();
        }

        private static string FormatMs(long ms)
        {
            var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{t.Minutes}:{t.Seconds:00}";
        }

        private void ProgressSlider_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Seeking from Windows -> Android isn't supported (Android only
            // reports last scrub location, no arbitrary seek API over ADB).
            // Swallow the click so the thumb doesn't move, then flash a
            // small notice so the user understands why nothing happened.
            e.Handled = true;
            FlashSeekUnsupportedNotice();
        }

        private void FlashSeekUnsupportedNotice()
        {
            if (SeekUnsupportedNotice == null) return;

            // Reset any in-flight animation so repeated clicks restart cleanly.
            SeekUnsupportedNotice.BeginAnimation(UIElement.OpacityProperty, null);

            var fadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(120)));
            var fadeOut = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(400)))
            {
                BeginTime = TimeSpan.FromMilliseconds(1400)
            };

            var sb = new Storyboard();
            Storyboard.SetTarget(fadeIn, SeekUnsupportedNotice);
            Storyboard.SetTargetProperty(fadeIn, new PropertyPath(UIElement.OpacityProperty));
            Storyboard.SetTarget(fadeOut, SeekUnsupportedNotice);
            Storyboard.SetTargetProperty(fadeOut, new PropertyPath(UIElement.OpacityProperty));
            sb.Children.Add(fadeIn);
            sb.Children.Add(fadeOut);
            sb.Begin();
        }

        private void FadeCoverImage(string? path)
        {
            BitmapImage? bitmap = null;

            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                try
                {
                    bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(path, UriKind.Absolute);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();
                }
                catch
                {
                    bitmap = null;
                }
            }

            // Determine incoming / outgoing layers
            var incoming = _coverUseLayerA ? ImgCoverA : ImgCoverB;
            var outgoing = _coverUseLayerA ? ImgCoverB : ImgCoverA;

            // Snap directly on first paint
            if (outgoing.Source == null && incoming.Source == null)
            {
                incoming.Source = bitmap;
                incoming.Opacity = 1;
                outgoing.Opacity = 0;
                _coverUseLayerA = !_coverUseLayerA;
                return;
            }

            incoming.BeginAnimation(UIElement.OpacityProperty, null);
            outgoing.BeginAnimation(UIElement.OpacityProperty, null);
            incoming.Source = bitmap;
            incoming.Opacity = 0;

            var fadeIn = new DoubleAnimation(0, 1, CoverFadeDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
            var fadeOut = new DoubleAnimation(1, 0, CoverFadeDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };

            incoming.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            outgoing.BeginAnimation(UIElement.OpacityProperty, fadeOut);

            _coverUseLayerA = !_coverUseLayerA;
        }

        // Legacy single-image setter kept in case called directly
        private void SetCoverImage(string? path) => FadeCoverImage(path);

        private void ApplyCoverGradientBackground(string? imagePath)
        {
            // When idle (no path) the brush depends on the active theme, not the path,
            // so the path-equality cache would wrongly skip a rebuild on theme flip.
            // Only honor the cache when we actually have a cover image.
            bool hasImage = !string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath);
            if (hasImage && string.Equals(_lastGradientSourcePath, imagePath, StringComparison.OrdinalIgnoreCase))
                return;

            // Idle-to-idle with the same theme: the solid brush is identical to what's
            // already on screen, so a crossfade just produces a visible pulse. Skip it.
            // We still need to update _lastIdleIsDark so a later theme flip is detected.
            bool isDarkNow = IsDarkThemeActive();
            if (!hasImage && _lastGradientSourcePath == null && _lastIdleIsDark == isDarkNow && GradientLayerA.Fill != null)
            {
                return;
            }

            _lastGradientSourcePath = imagePath;
            if (!hasImage)
            {
                _lastIdleIsDark = isDarkNow;
            }

            Brush newBrush;
            if (!hasImage)
            {
                // No song playing: follow the active theme. Dark mode stays near-black,
                // light mode goes near-white so it matches the rest of the UI.
                // Read from live resources rather than App.Config because the dark-mode
                // toggle calls ApplyTheme without updating Config.UseDarkMode.
                var solid = new SolidColorBrush(isDarkNow
                    ? Color.FromRgb(22, 22, 22)
                    : Color.FromRgb(247, 247, 247));
                solid.Freeze();
                newBrush = solid;
            }
            else
            {
                var colors = ExtractGradientColors(imagePath);
                newBrush = BuildFourToneCornerBrush(colors.topLeft, colors.topRight, colors.bottomLeft, colors.bottomRight);
            }

            // First call (initial paint): fill layer A immediately, no fade.
            if (GradientLayerA.Fill == null)
            {
                GradientLayerA.Fill = newBrush;
                GradientLayerA.Opacity = 1;
                GradientLayerB.Opacity = 0;
                _useLayerA = true;
                return;
            }

            var incoming = _useLayerA ? GradientLayerB : GradientLayerA;
            var outgoing = _useLayerA ? GradientLayerA : GradientLayerB;

            incoming.BeginAnimation(UIElement.OpacityProperty, null);
            incoming.Fill = newBrush;
            incoming.Opacity = 0;

            var fadeIn = new DoubleAnimation(0, 1, GradientFadeDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            var fadeOut = new DoubleAnimation(1, 0, GradientFadeDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };

            incoming.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            outgoing.BeginAnimation(UIElement.OpacityProperty, fadeOut);

            _useLayerA = !_useLayerA;
        }

        private static Brush BuildFourToneCornerBrush(Color topLeft, Color topRight, Color bottomLeft, Color bottomRight)
        {
            var bitmap = new WriteableBitmap(2, 2, 96, 96, PixelFormats.Bgra32, null);

            // Row-major BGRA pixels: [top-left, top-right, bottom-left, bottom-right]
            byte[] pixels =
            {
                topLeft.B, topLeft.G, topLeft.R, 255,
                topRight.B, topRight.G, topRight.R, 255,
                bottomLeft.B, bottomLeft.G, bottomLeft.R, 255,
                bottomRight.B, bottomRight.G, bottomRight.R, 255,
            };

            bitmap.WritePixels(new Int32Rect(0, 0, 2, 2), pixels, 8, 0);
            bitmap.Freeze();

            var brush = new ImageBrush(bitmap)
            {
                Stretch = Stretch.Fill
            };
            brush.Freeze();
            return brush;
        }

        private static (Color topLeft, Color topRight, Color bottomLeft, Color bottomRight) ExtractGradientColors(string? imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                return (DefaultTopLeft, DefaultTopRight, DefaultBottomLeft, DefaultBottomRight);

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 64;
                bitmap.DecodePixelHeight = 64;
                bitmap.EndInit();
                bitmap.Freeze();

                var formatted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                formatted.Freeze();

                int width = formatted.PixelWidth;
                int height = formatted.PixelHeight;
                if (width <= 0 || height <= 0)
                    return (DefaultTopLeft, DefaultTopRight, DefaultBottomLeft, DefaultBottomRight);

                int stride = width * 4;
                var pixels = new byte[stride * height];
                formatted.CopyPixels(pixels, stride, 0);

                long tlR = 0, tlG = 0, tlB = 0, tlCount = 0;
                long trR = 0, trG = 0, trB = 0, trCount = 0;
                long blR = 0, blG = 0, blB = 0, blCount = 0;
                long brR = 0, brG = 0, brB = 0, brCount = 0;

                for (int y = 0; y < height; y++)
                {
                    int rowStart = y * stride;
                    bool isTop = y < (height / 2);

                    for (int x = 0; x < width; x++)
                    {
                        int i = rowStart + (x * 4);
                        byte b = pixels[i];
                        byte g = pixels[i + 1];
                        byte r = pixels[i + 2];
                        byte a = pixels[i + 3];

                        if (a < 24)
                            continue;

                        int max = Math.Max(r, Math.Max(g, b));
                        int min = Math.Min(r, Math.Min(g, b));
                        int saturation = max - min;

                        if (max < 20 || saturation < 8)
                            continue;

                        bool isLeft = x < (width / 2);
                        if (isTop && isLeft)
                        {
                            tlR += r; tlG += g; tlB += b; tlCount++;
                        }
                        else if (isTop)
                        {
                            trR += r; trG += g; trB += b; trCount++;
                        }
                        else if (isLeft)
                        {
                            blR += r; blG += g; blB += b; blCount++;
                        }
                        else
                        {
                            brR += r; brG += g; brB += b; brCount++;
                        }
                    }
                }

                if (tlCount == 0 && trCount == 0 && blCount == 0 && brCount == 0)
                    return (DefaultTopLeft, DefaultTopRight, DefaultBottomLeft, DefaultBottomRight);

                Color Avg(long r, long g, long b, long count, Color fallback)
                {
                    if (count <= 0) return fallback;
                    return Color.FromRgb((byte)(r / count), (byte)(g / count), (byte)(b / count));
                }

                var tl = Avg(tlR, tlG, tlB, tlCount, DefaultTopLeft);
                var tr = Avg(trR, trG, trB, trCount, DefaultTopRight);
                var bl = Avg(blR, blG, blB, blCount, DefaultBottomLeft);
                var br = Avg(brR, brG, brB, brCount, DefaultBottomRight);

                tl = BlendWith(tl, Color.FromRgb(255, 255, 255), 0.06);
                tr = BlendWith(tr, Color.FromRgb(255, 255, 255), 0.06);
                bl = BlendWith(bl, Color.FromRgb(0, 0, 0), 0.25);
                br = BlendWith(br, Color.FromRgb(0, 0, 0), 0.25);

                return (tl, tr, bl, br);
            }
            catch
            {
                return (DefaultTopLeft, DefaultTopRight, DefaultBottomLeft, DefaultBottomRight);
            }
        }

        private static Color BlendWith(Color source, Color mix, double ratio)
        {
            ratio = Math.Clamp(ratio, 0, 1);
            double inverse = 1 - ratio;

            byte r = (byte)Math.Clamp((source.R * inverse) + (mix.R * ratio), 0, 255);
            byte g = (byte)Math.Clamp((source.G * inverse) + (mix.G * ratio), 0, 255);
            byte b = (byte)Math.Clamp((source.B * inverse) + (mix.B * ratio), 0, 255);
            return Color.FromRgb(r, g, b);
        }

        private async void BtnPrevious_Click(object sender, RoutedEventArgs e)
        {
            try { await _previousAction().ConfigureAwait(true); } catch { }
        }

        private async void BtnPause_Click(object sender, RoutedEventArgs e)
        {
            try { await _pauseAction().ConfigureAwait(true); } catch { }
        }

        private async void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            try { await _nextAction().ConfigureAwait(true); } catch { }
        }

        private void BtnVolume_Click(object sender, RoutedEventArgs e)
        {
            // Toggle behavior: clicking the volume icon while the popup is open closes it.
            if (VolumePopup.IsOpen)
            {
                VolumePopup.IsOpen = false;
                return;
            }

            // Pick the variant based on whether scrcpy's audio session is reachable.
            // If it is, we can read+write absolute volume so we show the slider.
            // If not, we fall back to step buttons that go through the same code
            // path the hotkey uses (scrcpy volume if it comes online, else ADB
            // keyevents to the device).
            bool sliderMode = _isScrcpyAudioAvailable?.Invoke() == true;

            if (sliderMode)
            {
                float current = _getVolume?.Invoke() ?? 0f;

                _suppressVolumeSliderEcho = true;
                try
                {
                    VolumeSlider.Value = Math.Clamp(current * 100f, 0, 100);
                }
                finally
                {
                    _suppressVolumeSliderEcho = false;
                }

                TxtVolumePercent.Text = $"{(int)Math.Round(VolumeSlider.Value)}%";
                VolumeSliderHost.Visibility = Visibility.Visible;
                VolumeStepHost.Visibility = Visibility.Collapsed;
            }
            else
            {
                VolumeSliderHost.Visibility = Visibility.Collapsed;
                VolumeStepHost.Visibility = Visibility.Visible;
            }

            VolumePopup.IsOpen = true;
            RefreshVolumeIcon();
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressVolumeSliderEcho) return;

            // Slider is 0..100, scrcpy volume is 0..1.
            float volume = (float)Math.Clamp(e.NewValue / 100.0, 0.0, 1.0);
            _setVolume?.Invoke(volume);
            TxtVolumePercent.Text = $"{(int)Math.Round(e.NewValue)}%";
            RefreshVolumeIcon();
        }

        private void BtnVolumeDown_Click(object sender, RoutedEventArgs e)
        {
            _stepVolume?.Invoke(false);
            RefreshVolumeIcon();
        }

        private void BtnVolumeUp_Click(object sender, RoutedEventArgs e)
        {
            _stepVolume?.Invoke(true);
            RefreshVolumeIcon();
        }

        private void BtnLyrics_Click(object sender, RoutedEventArgs e)
        {
            try { _lyricsToggleAction?.Invoke(); } catch { }
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

        private void RefreshConnectionButton()
        {
            ConnectionDot.Fill = new SolidColorBrush(_connectionColor);
            BtnConnectionInfo.BorderBrush = new SolidColorBrush(_connectionColor) { Opacity = 0.7 };
        }

        private void BtnConnectionInfo_Click(object sender, RoutedEventArgs e)
        {
            TxtConnectionStatus.Text = _connectionStatusText;
            TxtConnectionDetail.Text = _connectionDetailText;
            ConnectionInfoPopup.IsOpen = !ConnectionInfoPopup.IsOpen;
        }

        // ── Audio Link ────────────────────────────────────────────────────────

        private void BtnAudioLink_Click(object sender, RoutedEventArgs e)
        {
            _audioLinkActive = !_audioLinkActive;
            _setAudioLink?.Invoke(_audioLinkActive);
            RenderAudioLinkButton();
        }

        /// <summary>
        /// Called by the host to keep the audio-link button in sync when
        /// scrcpy is started or stopped from somewhere else (e.g. the tray menu).
        /// Does NOT invoke the _setAudioLink callback.
        /// </summary>
        public void SetAudioLinkState(bool active)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetAudioLinkState(active));
                return;
            }
            if (_audioLinkActive == active) return;
            _audioLinkActive = active;
            RenderAudioLinkButton();
        }

        private void RenderAudioLinkButton()
        {
            var brush = ResolveIconBrush();
            BtnAudioLink.Content = BuildAudioLinkIcon(brush, _audioLinkActive, 22);
            BtnAudioLink.ToolTip = _audioLinkActive ? "Disable audio link" : "Enable audio link";
        }

        // ── Fast Seek (ADB) ───────────────────────────────────────────────────

        private async void BtnSeekBack_Click(object sender, RoutedEventArgs e)
        {
            try { if (_seekRelativeSeconds != null) await _seekRelativeSeconds(-30).ConfigureAwait(true); } catch { }
        }

        private async void BtnSeekFwd_Click(object sender, RoutedEventArgs e)
        {
            try { if (_seekRelativeSeconds != null) await _seekRelativeSeconds(30).ConfigureAwait(true); } catch { }
        }

        private void RenderFastSeekIcons()
        {
            var brush = ResolveIconBrush();
            BtnSeekBack.Content = BuildSeekIcon(brush, -30, 30);
            BtnSeekFwd.Content = BuildSeekIcon(brush, 30, 30);
        }

        // ── Copy cover / track info ───────────────────────────────────────────

        private void CopyCoverInfoMenuItem_Click(object sender, RoutedEventArgs e)
        {
            string title = TxtTitle.Text == "-" ? "" : TxtTitle.Text;
            string artist = TxtArtist.Text == "-" ? "" : TxtArtist.Text;
            string album = TxtAlbum.Text == "-" ? "" : TxtAlbum.Text;

            // Template: "Artist - Title [Album]"
            string text = $"{artist} - {title} [{album}]".Trim(' ', '-', '[', ']').Trim();
            try { Clipboard.SetText(text); } catch { }
        }

        private void RenderTransportIcons(bool isPlaying)
        {
            var iconBrush = ResolveIconBrush();

            const double sideIconSize = 30;
            const double centerIconSize = 42;

            BtnPrevious.Content = BuildPreviousIcon(iconBrush, sideIconSize);
            BtnPause.Content = isPlaying ? BuildPauseIcon(iconBrush, centerIconSize) : BuildPlayIcon(iconBrush, centerIconSize);
            BtnNext.Content = BuildNextIcon(iconBrush, sideIconSize);
        }

        private void RenderAuxiliaryIcons()
        {
            var iconBrush = ResolveIconBrush();
            const double auxIconSize = 22;

            BtnVolume.Content = BuildVolumeIcon(iconBrush, auxIconSize, VolumeIconLevel.High);
            BtnLyrics.Content = BuildLyricsIcon(iconBrush, auxIconSize);

            RenderFastSeekIcons();
            RenderAudioLinkButton();
        }

        /// <summary>
        /// Picks the appropriate volume icon level and re-renders BtnVolume's content.
        /// When scrcpy isn't active, always shows High (the OS volume mixer is the
        /// effective control then, and we don't have a meaningful "level" to display).
        /// </summary>
        private void RefreshVolumeIcon()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(RefreshVolumeIcon);
                return;
            }

            var iconBrush = ResolveIconBrush();
            const double auxIconSize = 22;

            VolumeIconLevel level;
            if (_isScrcpyAudioAvailable?.Invoke() == true)
            {
                float v = _getVolume?.Invoke() ?? 1f;
                level = LevelFromVolume(v);
            }
            else
            {
                level = VolumeIconLevel.High;
            }

            BtnVolume.Content = BuildVolumeIcon(iconBrush, auxIconSize, level);
        }

        private void RenderSettingsPaneArrowIcon()
        {
            var iconBrush = TryFindResource("ThemeControlForegroundBrush") as Brush ?? Brushes.White;
            BtnShowSettingsPane.Content = BuildRevealSettingsArrowIcon(iconBrush);
        }

        private Brush ResolveIconBrush()
        {
            // While a song is playing the player background is always a dark gradient,
            // so icons must always be white regardless of the app theme.
            // When idle, the background follows the theme: white in light mode (need
            // black icons), near-black in dark mode (need white icons).
            if (_hasSong)
                return Brushes.White;

            return IsDarkThemeActive() ? Brushes.White : Brushes.Black;
        }

        /// <summary>
        /// Returns whether the dark theme is currently active. Reads from the live
        /// ThemeBackgroundBrush resource because the dark-mode toggle only calls
        /// ApplyTheme, leaving App.Config.UseDarkMode stale until the next save.
        /// </summary>
        private static bool IsDarkThemeActive()
        {
            if (Application.Current?.Resources["ThemeBackgroundBrush"] is SolidColorBrush bg)
            {
                // Use luminance midpoint: anything darker than mid-grey is "dark".
                var c = bg.Color;
                int luma = (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
                return luma < 128;
            }
            return App.Config?.UseDarkMode ?? true;
        }

        private static Viewbox BuildPreviousIcon(Brush brush, double size = 20)
        {
            var canvas = new Canvas { Width = 20, Height = 20 };

            var bar = new Rectangle
            {
                Width = 2.4,
                Height = 12,
                Fill = brush
            };
            Canvas.SetLeft(bar, 2);
            Canvas.SetTop(bar, 4);

            var triangle = new Polygon
            {
                Fill = brush,
                Points = new PointCollection
                {
                    new Point(15, 4),
                    new Point(6, 10),
                    new Point(15, 16)
                }
            };

            canvas.Children.Add(bar);
            canvas.Children.Add(triangle);

            return new Viewbox { Width = size, Height = size, Child = canvas };
        }

        private static Viewbox BuildRevealSettingsArrowIcon(Brush brush)
        {
            var canvas = new Canvas { Width = 14, Height = 20 };

            var chevron = new Polygon
            {
                Fill = brush,
                Points = new PointCollection
                {
                    new Point(3, 3),
                    new Point(11, 10),
                    new Point(3, 17),
                    new Point(6, 17),
                    new Point(14, 10),
                    new Point(6, 3)
                }
            };

            canvas.Children.Add(chevron);
            return new Viewbox { Width = 14, Height = 20, Child = canvas };
        }

        private static Viewbox BuildPlayIcon(Brush brush, double size = 20)
        {
            var canvas = new Canvas { Width = 20, Height = 20 };

            var triangle = new Polygon
            {
                Fill = brush,
                Points = new PointCollection
                {
                    new Point(6, 4),
                    new Point(15, 10),
                    new Point(6, 16)
                }
            };

            canvas.Children.Add(triangle);
            return new Viewbox { Width = size, Height = size, Child = canvas };
        }

        private static Viewbox BuildPauseIcon(Brush brush, double size = 20)
        {
            var canvas = new Canvas { Width = 20, Height = 20 };

            var leftBar = new Rectangle
            {
                Width = 3,
                Height = 12,
                Fill = brush
            };
            Canvas.SetLeft(leftBar, 5);
            Canvas.SetTop(leftBar, 4);

            var rightBar = new Rectangle
            {
                Width = 3,
                Height = 12,
                Fill = brush
            };
            Canvas.SetLeft(rightBar, 12);
            Canvas.SetTop(rightBar, 4);

            canvas.Children.Add(leftBar);
            canvas.Children.Add(rightBar);

            return new Viewbox { Width = size, Height = size, Child = canvas };
        }

        private static Viewbox BuildNextIcon(Brush brush, double size = 20)
        {
            var canvas = new Canvas { Width = 20, Height = 20 };

            var triangle = new Polygon
            {
                Fill = brush,
                Points = new PointCollection
                {
                    new Point(5, 4),
                    new Point(14, 10),
                    new Point(5, 16)
                }
            };

            var bar = new Rectangle
            {
                Width = 2.4,
                Height = 12,
                Fill = brush
            };
            Canvas.SetLeft(bar, 16);
            Canvas.SetTop(bar, 4);

            canvas.Children.Add(triangle);
            canvas.Children.Add(bar);

            return new Viewbox { Width = size, Height = size, Child = canvas };
        }

        /// <summary>
        /// Volume glyph levels mapped from the absolute (0..1) volume.
        /// </summary>
        private enum VolumeIconLevel { Muted, Low, Medium, High }

        private static VolumeIconLevel LevelFromVolume(float v)
        {
            if (v <= 0.001f) return VolumeIconLevel.Muted;
            if (v < 0.34f) return VolumeIconLevel.Low;
            if (v < 0.67f) return VolumeIconLevel.Medium;
            return VolumeIconLevel.High;
        }

        /// <summary>
        /// Builds the speaker glyph with 0, 1, or 2 sound-wave arcs depending on level.
        /// Muted gets a small slash through the speaker.
        /// </summary>
        private static Viewbox BuildVolumeIcon(Brush brush, double size = 20, VolumeIconLevel level = VolumeIconLevel.High)
        {
            // Canvas is 22 wide (vs 20) so the outermost High-level arc has room
            // without clipping. Viewbox scales the whole thing to the requested size.
            var canvas = new Canvas { Width = 22, Height = 20 };

            // Speaker body: small rectangle (back) + triangle horn projecting right.
            var speaker = new Polygon
            {
                Fill = brush,
                Points = new PointCollection
                {
                    new Point(2, 7.5),
                    new Point(6, 7.5),
                    new Point(11, 3),
                    new Point(11, 17),
                    new Point(6, 12.5),
                    new Point(2, 12.5)
                }
            };
            canvas.Children.Add(speaker);

            // Inner arc (Low/Medium/High).
            if (level == VolumeIconLevel.Low || level == VolumeIconLevel.Medium || level == VolumeIconLevel.High)
            {
                canvas.Children.Add(new System.Windows.Shapes.Path
                {
                    Stroke = brush,
                    StrokeThickness = 1.6,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Data = Geometry.Parse("M13.5,8 Q15.5,10 13.5,12")
                });
            }

            // Middle arc (Medium/High).
            if (level == VolumeIconLevel.Medium || level == VolumeIconLevel.High)
            {
                canvas.Children.Add(new System.Windows.Shapes.Path
                {
                    Stroke = brush,
                    StrokeThickness = 1.6,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Data = Geometry.Parse("M15.5,6.5 Q18,10 15.5,13.5")
                });
            }

            // Outer arc (High only).
            if (level == VolumeIconLevel.High)
            {
                canvas.Children.Add(new System.Windows.Shapes.Path
                {
                    Stroke = brush,
                    StrokeThickness = 1.6,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Data = Geometry.Parse("M17.5,5 Q20.5,10 17.5,15")
                });
            }

            // Muted: draw a diagonal slash across the speaker.
            if (level == VolumeIconLevel.Muted)
            {
                canvas.Children.Add(new System.Windows.Shapes.Line
                {
                    X1 = 13,
                    Y1 = 6,
                    X2 = 18,
                    Y2 = 14,
                    Stroke = brush,
                    StrokeThickness = 1.8,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                });
            }

            return new Viewbox { Width = size, Height = size, Child = canvas };
        }

        private static Viewbox BuildLyricsIcon(Brush brush, double size = 20)
        {
            // Stack of horizontal text lines, with one line indented to suggest lyric text.
            var canvas = new Canvas { Width = 20, Height = 20 };

            void AddLine(double x, double y, double width)
            {
                var line = new Rectangle
                {
                    Width = width,
                    Height = 2,
                    Fill = brush,
                    RadiusX = 1,
                    RadiusY = 1
                };
                Canvas.SetLeft(line, x);
                Canvas.SetTop(line, y);
                canvas.Children.Add(line);
            }

            AddLine(3, 4, 14);
            AddLine(3, 8.5, 10);
            AddLine(3, 13, 13);
            AddLine(3, 17.5, 8);

            return new Viewbox { Width = size, Height = size, Child = canvas };
        }

        /// <summary>
        /// Builds a seek icon: a triangle (direction) plus a small number label.
        /// seconds is e.g. -30, 10, 30 etc.
        /// </summary>
        private static Viewbox BuildSeekIcon(Brush brush, int seconds, double size = 22)
        {
            bool forward = seconds > 0;
            var canvas = new Canvas { Width = 28, Height = 20 };

            // Arrow triangle
            var tri = new Polygon
            {
                Fill = brush,
                Points = forward
                    ? new PointCollection { new Point(4, 4), new Point(12, 10), new Point(4, 16) }
                    : new PointCollection { new Point(12, 4), new Point(4, 10), new Point(12, 16) }
            };
            canvas.Children.Add(tri);

            // Second triangle (double chevron feel)
            var tri2 = new Polygon
            {
                Fill = brush,
                Opacity = 0.6,
                Points = forward
                    ? new PointCollection { new Point(10, 4), new Point(18, 10), new Point(10, 16) }
                    : new PointCollection { new Point(18, 4), new Point(10, 10), new Point(18, 16) }
            };
            canvas.Children.Add(tri2);

            return new Viewbox { Width = size, Height = size * 20 / 28, Child = canvas };
        }

        /// <summary>
        /// Builds an audio-link icon: a waveform/chain link that is solid when active,
        /// dimmed/crossed when inactive.
        /// </summary>
        private static Viewbox BuildAudioLinkIcon(Brush brush, bool active, double size = 22)
        {
            var canvas = new Canvas { Width = 22, Height = 20 };

            // Draw two chain-link ovals
            void AddLink(double cx, double cy)
            {
                var e1 = new System.Windows.Shapes.Path
                {
                    Stroke = brush,
                    StrokeThickness = 1.8,
                    Data = Geometry.Parse($"M {cx - 4},{cy} A 4,3 0 1 1 {cx + 4},{cy} A 4,3 0 1 1 {cx - 4},{cy}")
                };
                canvas.Children.Add(e1);
            }

            AddLink(7, 10);
            AddLink(15, 10);

            // Connecting bar
            var bar = new Rectangle { Width = 4, Height = 2, Fill = brush };
            Canvas.SetLeft(bar, 9);
            Canvas.SetTop(bar, 9);
            canvas.Children.Add(bar);

            // If inactive: draw a diagonal slash
            if (!active)
            {
                canvas.Children.Add(new System.Windows.Shapes.Line
                {
                    X1 = 3,
                    Y1 = 3,
                    X2 = 19,
                    Y2 = 17,
                    Stroke = brush,
                    StrokeThickness = 1.8,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Opacity = 0.7
                });
            }

            var vb = new Viewbox { Width = size, Height = size, Child = canvas };
            if (!active) vb.Opacity = 0.45;
            return vb;
        }

        private void SaveCoverMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_currentCoverPath) || !File.Exists(_currentCoverPath))
                {
                    MessageBox.Show(this, "No cover image is available to save right now.", "Save Cover", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var extension = System.IO.Path.GetExtension(_currentCoverPath);
                if (string.IsNullOrWhiteSpace(extension)) extension = ".png";

                var dialog = new SaveFileDialog
                {
                    Title = "Save Cover Image",
                    FileName = "cover" + extension,
                    Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.webp|All Files|*.*",
                    DefaultExt = extension
                };

                if (dialog.ShowDialog(this) == true)
                {
                    File.Copy(_currentCoverPath, dialog.FileName, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to save cover image: " + ex.Message, "Save Cover", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}