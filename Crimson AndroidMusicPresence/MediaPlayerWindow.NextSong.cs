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

        /// <summary>
        /// Called once by App after the window is constructed, wires up the rescan callback
        /// and injects the two overlay panels into the player grid.
        /// </summary>
        internal void InitNextSongPanels(Action rescanRequested)
        {
            _rescanRequested = rescanRequested;

            _prevPanel = new NextSongPanel();
            _nextPanel = new NextSongPanel();

            _prevPanel.RefreshRequested += () => _rescanRequested?.Invoke();
            _nextPanel.RefreshRequested += () => _rescanRequested?.Invoke();

            // The panels live inside the player pane grid (PlayerGrid), overlaid on top of
            // existing content. Left panel anchors to the left edge, right to the right edge,
            // both vertically centered.
            _prevPanel.HorizontalAlignment = HorizontalAlignment.Left;
            _prevPanel.VerticalAlignment = VerticalAlignment.Center;
            _prevPanel.Margin = new Thickness(8, 0, 0, 0);
            Panel.SetZIndex(_prevPanel, 20);

            _nextPanel.HorizontalAlignment = HorizontalAlignment.Right;
            _nextPanel.VerticalAlignment = VerticalAlignment.Center;
            _nextPanel.Margin = new Thickness(0, 0, 8, 0);
            Panel.SetZIndex(_nextPanel, 20);

            // PlayerGrid is the inner Grid inside PlayerPaneBorder.
            PlayerGrid.Children.Add(_prevPanel);
            PlayerGrid.Children.Add(_nextPanel);

            Grid.SetRowSpan(_prevPanel, 6);
            Grid.SetRowSpan(_nextPanel, 6);
        }

        /// <summary>
        /// Updates the prev/next panels. Called from App on every track change when
        /// the media player window is open and the feature is not Off.
        /// </summary>
        internal void UpdateNeighbours(NextSongManager.NeighbourResult result, NextSongMode mode, string? prevCoverPath, string? nextCoverPath)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => UpdateNeighbours(result, mode, prevCoverPath, nextCoverPath));
                return;
            }

            if (_prevPanel == null || _nextPanel == null) return;

            if (mode == NextSongMode.Off)
            {
                _prevPanel.Hide();
                _nextPanel.Hide();
                return;
            }

            if (!result.Found)
            {
                // Show stale indicator on the left panel, hide right.
                _prevPanel.ShowStale();
                _nextPanel.Hide();
                return;
            }

            // Previous panel
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

            // Next panel
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
        /// Hides both panels. Called when the feature is turned off or the window is about to close.
        /// </summary>
        internal void HideNeighbourPanels()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(HideNeighbourPanels); return; }
            _prevPanel?.Hide();
            _nextPanel?.Hide();
        }
    }
}
