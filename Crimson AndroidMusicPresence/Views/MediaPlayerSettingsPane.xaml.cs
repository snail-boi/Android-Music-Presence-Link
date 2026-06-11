using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// Media player settings pane. All state and the config writes live in
    /// <see cref="MediaPlayerSettingsPaneViewModel"/>; this code-behind only wires the VM, keeps
    /// the public surface the host depends on (SettingChanged, LoadFromConfig, SyncTimeFormatButton,
    /// RefreshNextSongListStatus), and keeps the purely visual expander reveal animation.
    /// </summary>
    public partial class MediaPlayerSettingsPane : UserControl
    {
        // Raised whenever a setting changes so the host can react immediately.
        public event Action? SettingChanged;

        private readonly MediaPlayerSettingsPaneViewModel _vm = new MediaPlayerSettingsPaneViewModel();

        public MediaPlayerSettingsPane()
        {
            InitializeComponent();

            _vm.SettingChangedCallback = () => SettingChanged?.Invoke();
            _vm.OnTimeFormatToggled = () => (Window.GetWindow(this) as MediaPlayerWindow)?.SyncTimeFormatFromConfig();

            DataContext = _vm;
            Loaded += (_, _) => _vm.LoadFromConfig();
        }

        // ── Public surface preserved for the host ────────────────────────────

        public void LoadFromConfig() => _vm.LoadFromConfig();

        public void SyncTimeFormatButton(bool showTimeLeft) => _vm.SyncTimeFormat(showTimeLeft);

        public void RefreshNextSongListStatus() => _vm.RefreshNextSongListStatus();

        // ── Expander reveal animation (visual) ────────────────────────────────

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
