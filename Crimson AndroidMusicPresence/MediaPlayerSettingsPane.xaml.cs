using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace musicpresense
{
    public partial class MediaPlayerSettingsPane : UserControl
    {
        // Called whenever a setting changes so the host can react immediately.
        public event Action? SettingChanged;

        // The pane reads from and writes to App.Config directly, same pattern as MainWindow settings.
        private bool _loading;

        public MediaPlayerSettingsPane()
        {
            InitializeComponent();
            Loaded += (_, _) => LoadFromConfig();
        }

        // ── Load ─────────────────────────────────────────────────────────────

        public void LoadFromConfig()
        {
            _loading = true;
            try
            {
                var c = App.Config;

                // Pills
                UpdatePillButton(BtnPillConnection, c.PillModeConnection, isConnection: true);
                UpdatePillButton(BtnPillAudioLink, c.PillModeAudioLink);
                UpdatePillButton(BtnPillQuality, c.PillModeQuality);
                UpdatePillButton(BtnPillAlwaysOnTop, c.PillModeAlwaysOnTop);

                // Track info
                ChkShowTitle.IsChecked = c.PlayerShowTitle;
                ChkShowArtist.IsChecked = c.PlayerShowArtist;
                ChkShowAlbum.IsChecked = c.PlayerShowAlbum;
                ChkSwapArtistAlbum.IsChecked = c.PlayerSwapArtistAlbum;

                // Cover
                ChkShowCover.IsChecked = c.PlayerShowCover;
                ChkCoverRounded.IsChecked = c.PlayerCoverRoundedCorners;
                ChkCoverShadow.IsChecked = c.PlayerCoverShadow;
                ChkTextShadow.IsChecked = c.PlayerTextShadow;
                UpdateGradientButtons(c.PlayerGradientSamplePoints);

                // Controls
                ChkShowVolume.IsChecked = c.PlayerShowVolumeButton;
                ChkShowLyrics.IsChecked = c.PlayerShowLyricsButton;
                ChkShowBattery.IsChecked = c.PlayerShowBattery;
                ChkShowHelp.IsChecked = c.PlayerShowHelpButton;
                ChkShowFullscreen.IsChecked = c.PlayerShowFullscreenButton;
                ChkShowSeekButtons.IsChecked = c.PlayerShowSeekButtons;

                // Seek threshold: stored as raw seconds, display in whatever unit fits.
                int threshSec = c.PlayerSeekButtonThresholdSeconds;
                bool useMin = threshSec % 60 == 0;
                BtnSeekThresholdUnit.Content = useMin ? "min" : "sec";
                TxtSeekButtonThreshold.Text = useMin ? (threshSec / 60).ToString() : threshSec.ToString();

                // Time format button
                BtnTimeFormat.Content = c.PlayerShowTimeLeft ? "Remaining" : "Elapsed";

                // Layout
                ChkSwapSettingsLocation.IsChecked = c.SwapSettingsLocation;

                // Next / previous song
                UpdateNextSongModeButton();
                UpdateNextSongSortButton();
                RefreshNextSongListStatus();
            }
            finally
            {
                _loading = false;
            }
        }

        // ── Pill cycle buttons ────────────────────────────────────────────────

        // Connection has 4 modes: 0=Full, 1=Mini, 2=Off, 3=Top
        private static readonly string[] ConnectionPillModeLabels = { "Full", "Mini", "Off", "Top" };
        // Other pills have 3 modes: 0=Full, 1=Mini, 2=Off
        private static readonly string[] PillModeLabels = { "Full", "Mini", "Off" };

        private static void UpdatePillButton(Button btn, int mode, bool isConnection = false)
        {
            var labels = isConnection ? ConnectionPillModeLabels : PillModeLabels;
            mode = Math.Clamp(mode, 0, labels.Length - 1);
            btn.Content = labels[mode];
            btn.Opacity = mode == 2 ? 0.45 : 1.0;
        }

        private static int NextPillMode(int current) => (current + 1) % 3;
        private static int NextConnectionPillMode(int current) => (current + 1) % 4;

        private void BtnPillConnection_Click(object sender, RoutedEventArgs e)
        {
            App.Config.PillModeConnection = NextConnectionPillMode(App.Config.PillModeConnection);
            UpdatePillButton(BtnPillConnection, App.Config.PillModeConnection, isConnection: true);
            SaveAndNotify();
        }

        private void BtnPillAudioLink_Click(object sender, RoutedEventArgs e)
        {
            App.Config.PillModeAudioLink = NextPillMode(App.Config.PillModeAudioLink);
            UpdatePillButton(BtnPillAudioLink, App.Config.PillModeAudioLink);
            SaveAndNotify();
        }

        private void BtnPillQuality_Click(object sender, RoutedEventArgs e)
        {
            App.Config.PillModeQuality = NextPillMode(App.Config.PillModeQuality);
            UpdatePillButton(BtnPillQuality, App.Config.PillModeQuality);
            SaveAndNotify();
        }

        private void BtnPillAlwaysOnTop_Click(object sender, RoutedEventArgs e)
        {
            App.Config.PillModeAlwaysOnTop = NextPillMode(App.Config.PillModeAlwaysOnTop);
            UpdatePillButton(BtnPillAlwaysOnTop, App.Config.PillModeAlwaysOnTop);
            SaveAndNotify();
        }

        // ── Gradient segmented buttons ────────────────────────────────────────

        private static readonly int[] GradientSteps = { 8, 6, 4, 2 };

        private void UpdateGradientButtons(int selected)
        {
            var accent = TryFindResource("ThemeAccentBrush") as Brush
                         ?? (IsDark() ? new SolidColorBrush(Color.FromRgb(80, 120, 200))
                                      : new SolidColorBrush(Color.FromRgb(60, 100, 180)));

            SetGradStep(BtnGrad8, 8, selected, accent);
            SetGradStep(BtnGrad6, 6, selected, accent);
            SetGradStep(BtnGrad4, 4, selected, accent);
            SetGradStep(BtnGrad2, 2, selected, accent);
        }

        private static void SetGradStep(Button btn, int value, int selected, Brush accentBrush)
        {
            bool active = value == selected;
            btn.Background = active ? accentBrush : Brushes.Transparent;
            btn.Foreground = active
                ? Brushes.White
                : Application.Current?.Resources["ThemeForegroundBrush"] as Brush ?? Brushes.White;
            btn.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }

        private void BtnGradient_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out int val))
            {
                App.Config.PlayerGradientSamplePoints = val;
                UpdateGradientButtons(val);
                SaveAndNotify();
            }
        }

        // ── Toggle checkboxes ─────────────────────────────────────────────────

        private void ChkShowTitle_Changed(object sender, RoutedEventArgs e) { if (!_loading) { App.Config.PlayerShowTitle = ChkShowTitle.IsChecked == true; SaveAndNotify(); } }
        private void ChkShowArtist_Changed(object sender, RoutedEventArgs e) { if (!_loading) { App.Config.PlayerShowArtist = ChkShowArtist.IsChecked == true; SaveAndNotify(); } }
        private void ChkShowAlbum_Changed(object sender, RoutedEventArgs e) { if (!_loading) { App.Config.PlayerShowAlbum = ChkShowAlbum.IsChecked == true; SaveAndNotify(); } }
        private void ChkSwapArtistAlbum_Changed(object sender, RoutedEventArgs e) { if (!_loading) { App.Config.PlayerSwapArtistAlbum = ChkSwapArtistAlbum.IsChecked == true; SaveAndNotify(); } }
        private void ChkShowCover_Changed(object sender, RoutedEventArgs e) { if (!_loading) { App.Config.PlayerShowCover = ChkShowCover.IsChecked == true; SaveAndNotify(); } }
        private void ChkCoverRounded_Changed(object sender, RoutedEventArgs e) { if (!_loading) { App.Config.PlayerCoverRoundedCorners = ChkCoverRounded.IsChecked == true; SaveAndNotify(); } }
        private void ChkCoverShadow_Changed(object sender, RoutedEventArgs e) { if (!_loading) { App.Config.PlayerCoverShadow = ChkCoverShadow.IsChecked == true; SaveAndNotify(); } }
        private void ChkTextShadow_Changed(object sender, RoutedEventArgs e) { if (!_loading) { App.Config.PlayerTextShadow = ChkTextShadow.IsChecked == true; SaveAndNotify(); } }
        private void ChkShowVolume_Changed(object sender, RoutedEventArgs e) { if (!_loading) { App.Config.PlayerShowVolumeButton = ChkShowVolume.IsChecked == true; SaveAndNotify(); } }
        private void ChkShowLyrics_Changed(object sender, RoutedEventArgs e) { if (!_loading) { App.Config.PlayerShowLyricsButton = ChkShowLyrics.IsChecked == true; SaveAndNotify(); } }
        private void ChkShowBattery_Changed(object sender, RoutedEventArgs e) { if (!_loading) { App.Config.PlayerShowBattery = ChkShowBattery.IsChecked == true; SaveAndNotify(); } }
        private void ChkShowHelp_Changed(object sender, RoutedEventArgs e) { if (!_loading) { App.Config.PlayerShowHelpButton = ChkShowHelp.IsChecked == true; SaveAndNotify(); } }
        private void ChkShowFullscreen_Changed(object sender, RoutedEventArgs e) { if (!_loading) { App.Config.PlayerShowFullscreenButton = ChkShowFullscreen.IsChecked == true; SaveAndNotify(); } }

        private void ChkShowSeekButtons_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            App.Config.PlayerShowSeekButtons = ChkShowSeekButtons.IsChecked == true;
            SaveAndNotify();
        }

        private void TxtSeekButtonThreshold_Changed(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            if (!int.TryParse(TxtSeekButtonThreshold.Text.Trim(), out int val) || val <= 0) return;
            bool isMin = BtnSeekThresholdUnit.Content as string == "min";
            App.Config.PlayerSeekButtonThresholdSeconds = isMin ? val * 60 : val;
            SaveAndNotify();
        }

        private void BtnSeekThresholdUnit_Click(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            bool currentlyMin = BtnSeekThresholdUnit.Content as string == "min";
            if (currentlyMin)
            {
                // Switch to sec: convert displayed value
                if (int.TryParse(TxtSeekButtonThreshold.Text.Trim(), out int min))
                {
                    _loading = true;
                    TxtSeekButtonThreshold.Text = (min * 60).ToString();
                    _loading = false;
                }
                BtnSeekThresholdUnit.Content = "sec";
            }
            else
            {
                // Switch to min: only if current seconds value is divisible by 60
                if (int.TryParse(TxtSeekButtonThreshold.Text.Trim(), out int sec) && sec % 60 == 0)
                {
                    _loading = true;
                    TxtSeekButtonThreshold.Text = (sec / 60).ToString();
                    _loading = false;
                }
                else
                {
                    // Not evenly divisible; round to nearest minute
                    if (int.TryParse(TxtSeekButtonThreshold.Text.Trim(), out int secR))
                    {
                        int rounded = (int)Math.Round(secR / 60.0);
                        if (rounded < 1) rounded = 1;
                        _loading = true;
                        TxtSeekButtonThreshold.Text = rounded.ToString();
                        _loading = false;
                    }
                }
                BtnSeekThresholdUnit.Content = "min";
            }
            // Re-read and save after unit flip
            if (int.TryParse(TxtSeekButtonThreshold.Text.Trim(), out int v) && v > 0)
            {
                bool nowMin = BtnSeekThresholdUnit.Content as string == "min";
                App.Config.PlayerSeekButtonThresholdSeconds = nowMin ? v * 60 : v;
                SaveAndNotify();
            }
        }

        // Called by the player window when the user clicks the time label directly,
        // so the settings pane button label stays in sync.
        public void SyncTimeFormatButton(bool showTimeLeft)
        {
            BtnTimeFormat.Content = showTimeLeft ? "Remaining" : "Elapsed";
        }

        private void BtnTimeFormat_Click(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            App.Config.PlayerShowTimeLeft = !App.Config.PlayerShowTimeLeft;
            BtnTimeFormat.Content = App.Config.PlayerShowTimeLeft ? "Remaining" : "Elapsed";
            SaveAndNotify();
            // Sync the live player window immediately.
            (Window.GetWindow(this) as MediaPlayerWindow)?.SyncTimeFormatFromConfig();
        }

        private void ChkSwapSettingsLocation_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            App.Config.SwapSettingsLocation = ChkSwapSettingsLocation.IsChecked == true;
            SaveAndNotify();
        }

        // ── Next / Previous song ──────────────────────────────────────────────

        // Display labels indexed by NextSongMode enum value (Off=0, TextOnly=1, FullArt=2, Kirsten=3)
        private static readonly string[] NextSongModeLabels = { "Off", "Text only", "Full art", "Kirsten" };
        private static readonly string[] NextSongSortLabels = { "A-Z", "Z-A", "Newest", "Oldest" };

        private void UpdateNextSongModeButton()
        {
            var mode = App.Config.NextSongMode;
            int idx = (int)mode;
            BtnNextSongMode.Content = idx >= 0 && idx < NextSongModeLabels.Length
                ? NextSongModeLabels[idx]
                : "Off";
            BtnNextSongMode.Opacity = mode == NextSongMode.Off ? 0.45 : 1.0;
            PanelNextSongOptions.Visibility = mode == NextSongMode.Off ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdateNextSongSortButton()
        {
            BtnNextSongSort.Content = NextSongSortLabels[(int)App.Config.NextSongSortMode];
        }

        public void RefreshNextSongListStatus()
        {
            var path = AppPaths.GetDataPath("library_list.txt");
            if (System.IO.File.Exists(path))
            {
                var info = new System.IO.FileInfo(path);
                TxtNextSongListStatus.Text = $"Last scan: {info.LastWriteTime:g}";
            }
            else
            {
                TxtNextSongListStatus.Text = "No list yet";
            }
        }

        private void BtnNextSongMode_Click(object sender, RoutedEventArgs e)
        {
            // Cycle order: FullArt -> TextOnly -> Off -> Kirsten -> FullArt
            var next = App.Config.NextSongMode switch
            {
                NextSongMode.FullArt => NextSongMode.TextOnly,
                NextSongMode.TextOnly => NextSongMode.Off,
                NextSongMode.Off => NextSongMode.Kirsten,
                NextSongMode.Kirsten => NextSongMode.FullArt,
                _ => NextSongMode.FullArt
            };
            App.Config.NextSongMode = next;
            UpdateNextSongModeButton();
            RefreshNextSongListStatus();
            SaveAndNotify();
            var app = Application.Current as App;
            if (app != null)
            {
                if (next == NextSongMode.Off)
                {
                    app.RefreshNextSongNeighboursAsync();
                }
                else
                {
                    _ = app.RefreshNextSongNeighboursAsync();
                }
            }
        }

        private void BtnNextSongSort_Click(object sender, RoutedEventArgs e)
        {
            var next = (NextSongSortMode)(((int)App.Config.NextSongSortMode + 1) % 4);
            var prev = App.Config.NextSongSortMode;
            App.Config.NextSongSortMode = next;
            UpdateNextSongSortButton();
            SaveAndNotify();
            // If sort changed and list exists, resort without a rescan.
            if (prev != next)
                (Application.Current as App)?.ResortNextSongListAsync();
        }

        private void BtnRescanLibrary_Click(object sender, RoutedEventArgs e)
        {
            BtnRescanLibrary.IsEnabled = false;
            BtnRescanLibrary.Content = "Scanning...";
            (Application.Current as App)?.RescanNextSongLibraryAsync(() =>
            {
                Dispatcher.Invoke(() =>
                {
                    BtnRescanLibrary.IsEnabled = true;
                    BtnRescanLibrary.Content = "Rescan";
                    RefreshNextSongListStatus();
                });
            });
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void SaveAndNotify()
        {
            MusicConfigManager.Save(App.Config);
            (Application.Current as App)?.UpdateConfig(App.Config);
            SettingChanged?.Invoke();
        }

        private static bool IsDark()
        {
            if (Application.Current?.Resources["ThemeBackgroundBrush"] is SolidColorBrush bg)
            {
                var c = bg.Color;
                return (c.R * 299 + c.G * 587 + c.B * 114) / 1000 < 128;
            }
            return true;
        }

        private void Expander_Expanded(object sender, RoutedEventArgs e)
        {
            if (sender is not Expander expander)
                return;

            if (expander.Content is not FrameworkElement content)
                return;

            content.RenderTransformOrigin = new Point(0.5, 0);
            if (content.RenderTransform is not ScaleTransform scaleTransform)
            {
                scaleTransform = new ScaleTransform(1, 0.9);
                content.RenderTransform = scaleTransform;
            }

            content.Opacity = 0;
            scaleTransform.ScaleY = 0.9;

            var duration = TimeSpan.FromMilliseconds(200);
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

            var scaleAnimation = new DoubleAnimation(0.9, 1, duration) { EasingFunction = easing };
            var opacityAnimation = new DoubleAnimation(0, 1, duration) { EasingFunction = easing };

            scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation);
            content.BeginAnimation(OpacityProperty, opacityAnimation);
        }
    }
}