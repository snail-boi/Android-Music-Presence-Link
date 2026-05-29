using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

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
                UpdatePillButton(BtnPillConnection, c.PillModeConnection);
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
                UpdateGradientButtons(c.PlayerGradientSamplePoints);

                // Controls
                ChkShowVolume.IsChecked = c.PlayerShowVolumeButton;
                ChkShowLyrics.IsChecked = c.PlayerShowLyricsButton;
                ChkShowBattery.IsChecked = c.PlayerShowBattery;
                ChkShowHelp.IsChecked = c.PlayerShowHelpButton;
                ChkShowFullscreen.IsChecked = c.PlayerShowFullscreenButton;

                // Layout
                ChkSettingsPaneOnRight.IsChecked = c.SettingsPaneOnRight;
                ChkPlayerSettingsPaneOnLeft.IsChecked = c.PlayerSettingsPaneOnLeft;
            }
            finally
            {
                _loading = false;
            }
        }

        // ── Pill cycle buttons ────────────────────────────────────────────────

        // 0 = Full, 1 = Mini, 2 = Off
        private static readonly string[] PillModeLabels = { "Full", "Mini", "Off" };

        private static void UpdatePillButton(Button btn, int mode)
        {
            mode = Math.Clamp(mode, 0, 2);
            btn.Content = PillModeLabels[mode];

            // Dim the button when Off so there is a clear visual cue.
            btn.Opacity = mode == 2 ? 0.45 : 1.0;
        }

        private void CyclePill(Button btn, ref int configValue)
        {
            configValue = (configValue + 1) % 3;
            UpdatePillButton(btn, configValue);
            SaveAndNotify();
        }

        private void BtnPillConnection_Click(object sender, RoutedEventArgs e)
        {
            var v = App.Config.PillModeConnection;
            CyclePill(BtnPillConnection, ref v);
            App.Config.PillModeConnection = v;
        }

        private void BtnPillAudioLink_Click(object sender, RoutedEventArgs e)
        {
            var v = App.Config.PillModeAudioLink;
            CyclePill(BtnPillAudioLink, ref v);
            App.Config.PillModeAudioLink = v;
        }

        private void BtnPillQuality_Click(object sender, RoutedEventArgs e)
        {
            var v = App.Config.PillModeQuality;
            CyclePill(BtnPillQuality, ref v);
            App.Config.PillModeQuality = v;
        }

        private void BtnPillAlwaysOnTop_Click(object sender, RoutedEventArgs e)
        {
            var v = App.Config.PillModeAlwaysOnTop;
            CyclePill(BtnPillAlwaysOnTop, ref v);
            App.Config.PillModeAlwaysOnTop = v;
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
        private void ChkShowVolume_Changed(object sender, RoutedEventArgs e) { if (!_loading) { App.Config.PlayerShowVolumeButton = ChkShowVolume.IsChecked == true; SaveAndNotify(); } }
        private void ChkShowLyrics_Changed(object sender, RoutedEventArgs e) { if (!_loading) { App.Config.PlayerShowLyricsButton = ChkShowLyrics.IsChecked == true; SaveAndNotify(); } }
        private void ChkShowBattery_Changed(object sender, RoutedEventArgs e) { if (!_loading) { App.Config.PlayerShowBattery = ChkShowBattery.IsChecked == true; SaveAndNotify(); } }
        private void ChkShowHelp_Changed(object sender, RoutedEventArgs e) { if (!_loading) { App.Config.PlayerShowHelpButton = ChkShowHelp.IsChecked == true; SaveAndNotify(); } }
        private void ChkShowFullscreen_Changed(object sender, RoutedEventArgs e) { if (!_loading) { App.Config.PlayerShowFullscreenButton = ChkShowFullscreen.IsChecked == true; SaveAndNotify(); } }

        private void ChkSettingsPaneOnRight_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            App.Config.SettingsPaneOnRight = ChkSettingsPaneOnRight.IsChecked == true;
            SaveAndNotify();
        }

        private void ChkPlayerSettingsPaneOnLeft_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            App.Config.PlayerSettingsPaneOnLeft = ChkPlayerSettingsPaneOnLeft.IsChecked == true;
            SaveAndNotify();
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
    }
}