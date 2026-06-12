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
    /// RefreshNextSongListStatus), and keeps the purely visual expander animations and scroll indicator.
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
            Loaded += (_, _) =>
            {
                _vm.LoadFromConfig();
                UpdatePaneScrollIndicator();
                InitExpanderVisibility(this);
            };
        }

        // ── Public surface preserved for the host ────────────────────────────

        public void LoadFromConfig() => _vm.LoadFromConfig();

        public void SyncTimeFormatButton(bool showTimeLeft) => _vm.SyncTimeFormat(showTimeLeft);

        public void RefreshNextSongListStatus() => _vm.RefreshNextSongListStatus();

        // ── Scroll indicator (visual) ─────────────────────────────────────────

        private void PaneScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            UpdatePaneScrollIndicator();
        }

        private void UpdatePaneScrollIndicator()
        {
            if (PaneScrollIndicator == null || PaneScrollViewer == null) return;

            double remaining = PaneScrollViewer.ScrollableHeight - PaneScrollViewer.VerticalOffset;
            bool atBottom = remaining < 8;

            double targetOpacity = atBottom ? 0 : 1;
            if (Math.Abs(PaneScrollIndicator.Opacity - targetOpacity) < 0.01) return;

            var anim = new DoubleAnimation(targetOpacity, new Duration(TimeSpan.FromMilliseconds(180)));
            PaneScrollIndicator.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        // ── Expander visibility init (no template trigger) ───────────────────

        private static void InitExpanderVisibility(System.Windows.DependencyObject root)
        {
            foreach (var expander in FindVisualChildren<Expander>(root))
            {
                var contentSite = expander.Template?.FindName("ContentSite", expander) as FrameworkElement;
                if (contentSite != null)
                    contentSite.Visibility = expander.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private static System.Collections.Generic.IEnumerable<T> FindVisualChildren<T>(System.Windows.DependencyObject parent)
            where T : System.Windows.DependencyObject
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T t) yield return t;
                foreach (var desc in FindVisualChildren<T>(child)) yield return desc;
            }
        }

        // ── Expander open animation (visual) ──────────────────────────────────

        private void Expander_Expanded(object sender, RoutedEventArgs e)
        {
            if (sender is not Expander expander)
                return;

            if (expander.Content is not FrameworkElement content)
                return;

            var contentSite = expander.Template?.FindName("ContentSite", expander) as FrameworkElement;
            if (contentSite != null)
                contentSite.Visibility = Visibility.Visible;

            // Cancel any in-progress collapse animation so we start clean.
            content.BeginAnimation(OpacityProperty, null);
            if (content.RenderTransform is ScaleTransform st)
                st.BeginAnimation(ScaleTransform.ScaleYProperty, null);

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

        // ── Expander close animation (visual) ─────────────────────────────────

        private void Expander_Collapsed(object sender, RoutedEventArgs e)
        {
            if (sender is not Expander expander)
                return;

            if (expander.Content is not FrameworkElement content)
                return;

            // We own ContentSite visibility (trigger removed from template).
            var contentSite = expander.Template?.FindName("ContentSite", expander) as FrameworkElement;

            content.RenderTransformOrigin = new Point(0.5, 0);
            if (content.RenderTransform is not ScaleTransform scaleTransform)
            {
                scaleTransform = new ScaleTransform(1, 1);
                content.RenderTransform = scaleTransform;
            }

            var duration = TimeSpan.FromMilliseconds(160);
            var easing = new CubicEase { EasingMode = EasingMode.EaseIn };

            var scaleAnimation = new DoubleAnimation(1, 0.9, duration) { EasingFunction = easing };
            var opacityAnimation = new DoubleAnimation(1, 0, duration) { EasingFunction = easing };

            opacityAnimation.Completed += (_, _) =>
            {
                if (expander.IsExpanded)
                    return;

                if (contentSite != null)
                    contentSite.Visibility = Visibility.Collapsed;

                content.BeginAnimation(OpacityProperty, null);
                scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                content.Opacity = 1;
                scaleTransform.ScaleY = 1;
            };

            scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation);
            content.BeginAnimation(OpacityProperty, opacityAnimation);
        }
    }
}