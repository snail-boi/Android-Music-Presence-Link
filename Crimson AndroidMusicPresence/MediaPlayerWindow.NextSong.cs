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

            if (mode == NextSongMode.TextOnly)
            {
                _prevPanel.Height = 220;
                _nextPanel.Height = 220;
            }
            else
            {
                _prevPanel.Height = 220;
                _nextPanel.Height = 220;
            }

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

            if (App.Config.NextSongMode == NextSongMode.Off)
            {
                _prevPanel.Hide();
                _nextPanel.Hide();
                PrevPanelHost.Visibility = Visibility.Collapsed;
                NextPanelHost.Visibility = Visibility.Collapsed;
                return;
            }

            _prevPanel.SetRoundedCorners(roundedCorners);
            _nextPanel.SetRoundedCorners(roundedCorners);
            _prevPanel.SetShowCover(showCover);
            _nextPanel.SetShowCover(showCover);
        }
    }
}