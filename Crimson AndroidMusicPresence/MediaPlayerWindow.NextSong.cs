using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace musicpresense
{
    public partial class MediaPlayerWindow : Window
    {
        private NextSongPanel? _prevPanel;
        private NextSongPanel? _nextPanel;

        private Action? _rescanRequested;
        private Func<Task>? _nextRequested;
        private Func<Task>? _previousRequested;

        internal void InitNextSongPanels(Action rescanRequested, Func<Task> nextRequested, Func<Task> previousRequested)
        {
            _rescanRequested = rescanRequested;
            _nextRequested = nextRequested;
            _previousRequested = previousRequested;

            _prevPanel = new NextSongPanel();
            _nextPanel = new NextSongPanel();
            _prevPanel.SetDirection(isPrevious: true);
            _nextPanel.SetDirection(isPrevious: false);

            _prevPanel.SetRoundedCorners(App.Config.PlayerCoverRoundedCorners);
            _nextPanel.SetRoundedCorners(App.Config.PlayerCoverRoundedCorners);
            _prevPanel.SetShowCover(App.Config.PlayerShowCover);
            _nextPanel.SetShowCover(App.Config.PlayerShowCover);

            _prevPanel.RefreshRequested += () => _rescanRequested?.Invoke();
            _nextPanel.RefreshRequested += () => _rescanRequested?.Invoke();
            _prevPanel.PreviousRequested += () => _ = _previousRequested?.Invoke();
            _nextPanel.NextRequested += () => _ = _nextRequested?.Invoke();

            PrevPanelHost.Child = _prevPanel;
            NextPanelHost.Child = _nextPanel;
        }

        internal void UpdateNeighbours(NextSongManager.NeighbourResult result, NextSongMode mode, string? prevCoverPath, string? nextCoverPath)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => UpdateNeighbours(result, mode, prevCoverPath, nextCoverPath));
                return;
            }

            if (_prevPanel == null || _nextPanel == null) return;

            bool roundedCorners = App.Config.PlayerCoverRoundedCorners;
            _prevPanel.SetRoundedCorners(roundedCorners);
            _nextPanel.SetRoundedCorners(roundedCorners);
            _prevPanel.SetShowCover(App.Config.PlayerShowCover);
            _nextPanel.SetShowCover(App.Config.PlayerShowCover);

            // Kirsten mode: smaller inset panels that partially overlap the main cover.
            bool kirsten = mode == NextSongMode.Kirsten;
            ApplyKirstenLayout(kirsten);
            _prevPanel.SetCoverOnly(kirsten);
            _nextPanel.SetCoverOnly(kirsten);

            if (mode == NextSongMode.Off)
            {
                _prevPanel.Hide();
                _nextPanel.Hide();
                PrevPanelHost.Visibility = Visibility.Collapsed;
                NextPanelHost.Visibility = Visibility.Collapsed;
                return;
            }

            PrevPanelHost.Visibility = Visibility.Visible;
            NextPanelHost.Visibility = Visibility.Visible;

            if (!result.Found)
            {
                _prevPanel.Hide();
                _nextPanel.Hide();
                return;
            }

            if (string.IsNullOrWhiteSpace(result.PrevPath) && string.IsNullOrWhiteSpace(result.NextPath))
            {
                _prevPanel.Hide();
                _nextPanel.Hide();
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.PrevPath))
            {
                if (mode == NextSongMode.TextOnly)
                    _prevPanel.ShowTextOnly("Previous", result.PrevTitle ?? string.Empty);
                else
                    _prevPanel.ShowWithCover("Previous", result.PrevTitle ?? string.Empty, prevCoverPath);
            }
            else
            {
                _prevPanel.Hide();
            }

            if (!string.IsNullOrWhiteSpace(result.NextPath))
            {
                if (mode == NextSongMode.TextOnly)
                    _nextPanel.ShowTextOnly("Next", result.NextTitle ?? string.Empty);
                else
                    _nextPanel.ShowWithCover("Next", result.NextTitle ?? string.Empty, nextCoverPath);
            }
            else
            {
                _nextPanel.Hide();
            }
        }

        /// <summary>
        /// Applies or removes the Kirsten layout. In Kirsten mode the two neighbour
        /// panels are scaled up to roughly 40% of the main cover and tucked into the
        /// bottom corners: they hang off to the side and slightly below the cover,
        /// overlapping the corner by a small amount. This is done with a RenderTransform
        /// (scale + translate) rather than by changing Width/Height or margins, because
        /// a RenderTransform is purely visual and does NOT affect what the surrounding
        /// Viewbox measures. That keeps the Full art / Text only layout completely
        /// unchanged (transform is cleared), and avoids the earlier problem where
        /// setting the UserControl's Width/Height left the inner fixed-size RootPanel
        /// at 140x220 and never actually resized the content.
        /// The panels' Panel.ZIndex is raised so they render above the cover image.
        /// </summary>
        private void ApplyKirstenLayout(bool kirsten)
        {
            if (_prevPanel == null || _nextPanel == null) return;

            if (kirsten)
            {
                // Panel grows from 140px wide to ~168px (~40% of the 420px cover).
                const double scale = 1.2;
                // Spacer column width between a panel column and the cover column.
                const double spacerWidth = 12;
                // How far the inner edge of each panel overlaps into the cover corner.
                const double overlapIntoCover = 40;
                // How far the panel is pushed down so the cover thumbnail sits at the
                // bottom corner of the main cover and pokes slightly below it.
                const double dropBelow = 110;

                // Shift the panel inward (toward the cover) far enough to clear the
                // spacer column and overlap the cover corner by overlapIntoCover px.
                double inwardShift = spacerWidth + overlapIntoCover;

                // Prev panel sits to the LEFT of the cover: scale toward its bottom-right
                // corner, then push right + down so it tucks into the cover's lower-left.
                var prevTransform = new System.Windows.Media.TransformGroup();
                prevTransform.Children.Add(new System.Windows.Media.ScaleTransform(scale, scale));
                prevTransform.Children.Add(new System.Windows.Media.TranslateTransform(inwardShift, dropBelow));
                _prevPanel.RenderTransformOrigin = new System.Windows.Point(1, 1);
                _prevPanel.RenderTransform = prevTransform;

                // Next panel sits to the RIGHT of the cover: scale toward its bottom-left
                // corner, then push left + down so it tucks into the cover's lower-right.
                var nextTransform = new System.Windows.Media.TransformGroup();
                nextTransform.Children.Add(new System.Windows.Media.ScaleTransform(scale, scale));
                nextTransform.Children.Add(new System.Windows.Media.TranslateTransform(-inwardShift, dropBelow));
                _nextPanel.RenderTransformOrigin = new System.Windows.Point(0, 1);
                _nextPanel.RenderTransform = nextTransform;

                PrevPanelHost.Margin = new System.Windows.Thickness(0);
                NextPanelHost.Margin = new System.Windows.Thickness(0);
                PrevPanelHost.VerticalAlignment = System.Windows.VerticalAlignment.Bottom;
                NextPanelHost.VerticalAlignment = System.Windows.VerticalAlignment.Bottom;
                // Behind the main cover: the overlapping inner edge is hidden by the
                // cover, only the part poking out at the corner is visible.
                System.Windows.Controls.Panel.SetZIndex(PrevPanelHost, -1);
                System.Windows.Controls.Panel.SetZIndex(NextPanelHost, -1);

                _prevPanel.Opacity = 0.65;
                _nextPanel.Opacity = 0.65;
            }
            else
            {
                // Restore the default side-by-side layout used by Full art / Text only:
                // no transform, normal stacking order, default size from XAML (140x220).
                _prevPanel.RenderTransform = null;
                _nextPanel.RenderTransform = null;
                _prevPanel.RenderTransformOrigin = new System.Windows.Point(0, 0);
                _nextPanel.RenderTransformOrigin = new System.Windows.Point(0, 0);

                PrevPanelHost.Margin = new System.Windows.Thickness(0);
                NextPanelHost.Margin = new System.Windows.Thickness(0);
                PrevPanelHost.VerticalAlignment = System.Windows.VerticalAlignment.Bottom;
                NextPanelHost.VerticalAlignment = System.Windows.VerticalAlignment.Bottom;
                System.Windows.Controls.Panel.SetZIndex(PrevPanelHost, 0);
                System.Windows.Controls.Panel.SetZIndex(NextPanelHost, 0);

                _prevPanel.Opacity = 1.0;
                _nextPanel.Opacity = 1.0;
            }
        }

        internal void HideNeighbourPanels()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(HideNeighbourPanels); return; }
            _prevPanel?.Hide();
            _nextPanel?.Hide();
        }

        internal void RefreshNextSongPanelSettings()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(RefreshNextSongPanelSettings); return; }
            if (_prevPanel == null || _nextPanel == null) return;

            bool roundedCorners = App.Config.PlayerCoverRoundedCorners;
            bool showCover = App.Config.PlayerShowCover;
            var mode = App.Config.NextSongMode;

            if (mode == NextSongMode.Off)
            {
                ApplyKirstenLayout(false);
                _prevPanel.Hide();
                _nextPanel.Hide();
                PrevPanelHost.Visibility = Visibility.Collapsed;
                NextPanelHost.Visibility = Visibility.Collapsed;
                return;
            }

            ApplyKirstenLayout(mode == NextSongMode.Kirsten);
            _prevPanel.SetCoverOnly(mode == NextSongMode.Kirsten);
            _nextPanel.SetCoverOnly(mode == NextSongMode.Kirsten);
            _prevPanel.SetRoundedCorners(roundedCorners);
            _nextPanel.SetRoundedCorners(roundedCorners);
            _prevPanel.SetShowCover(showCover);
            _nextPanel.SetShowCover(showCover);
        }
    }
}