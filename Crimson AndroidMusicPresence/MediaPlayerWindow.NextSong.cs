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
        /// Applies or removes the Kirsten layout: in Kirsten mode the panels are narrower
        /// and use negative margins so they slide inward to overlap the edges of the main cover.
        /// The panels' Panel.ZIndex is raised so they render on top of the cover image.
        /// In all other modes the layout is restored to its default side-by-side position.
        /// </summary>
        private void ApplyKirstenLayout(bool kirsten)
        {
            // How many pixels the panel should overlap into the cover from each side.
            const double overlapIntocover = 90;
            // Spacer column width between panel column and cover column is 12px.
            const double spacerWidth = 12;
            // Panel width to use in Kirsten mode.
            const double kirstenPanelWidth = 110;
            const double kirstenPanelHeight = 160;
            // Normal panel size.
            const double normalPanelHeight = 220;

            if (kirsten)
            {
                // Pull PrevPanelHost right so it overlaps the left edge of the cover.
                // Margin on the right side = -(spacerWidth + overlapIntocover).
                PrevPanelHost.Margin = new System.Windows.Thickness(0, 0, -(spacerWidth + overlapIntocover), 0);
                PrevPanelHost.VerticalAlignment = System.Windows.VerticalAlignment.Bottom;
                System.Windows.Controls.Panel.SetZIndex(PrevPanelHost, 5);

                // Pull NextPanelHost left so it overlaps the right edge of the cover.
                NextPanelHost.Margin = new System.Windows.Thickness(-(spacerWidth + overlapIntocover), 0, 0, 0);
                NextPanelHost.VerticalAlignment = System.Windows.VerticalAlignment.Bottom;
                System.Windows.Controls.Panel.SetZIndex(NextPanelHost, 5);

                _prevPanel.Width = kirstenPanelWidth;
                _prevPanel.Height = kirstenPanelHeight;
                _nextPanel.Width = kirstenPanelWidth;
                _nextPanel.Height = kirstenPanelHeight;
            }
            else
            {
                PrevPanelHost.Margin = new System.Windows.Thickness(0);
                PrevPanelHost.VerticalAlignment = System.Windows.VerticalAlignment.Bottom;
                System.Windows.Controls.Panel.SetZIndex(PrevPanelHost, 0);

                NextPanelHost.Margin = new System.Windows.Thickness(0);
                NextPanelHost.VerticalAlignment = System.Windows.VerticalAlignment.Bottom;
                System.Windows.Controls.Panel.SetZIndex(NextPanelHost, 0);

                _prevPanel.Width = 140;
                _prevPanel.Height = normalPanelHeight;
                _nextPanel.Width = 140;
                _nextPanel.Height = normalPanelHeight;
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
            _prevPanel.SetRoundedCorners(roundedCorners);
            _nextPanel.SetRoundedCorners(roundedCorners);
            _prevPanel.SetShowCover(showCover);
            _nextPanel.SetShowCover(showCover);
        }
    }
}