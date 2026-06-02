using System;
using System.Windows;
using System.Windows.Controls;

namespace musicpresense
{
    public partial class MediaPlayerWindow : Window
    {
        private NextSongPanel? _prevPanel;
        private NextSongPanel? _nextPanel;

        private Action? _rescanRequested;

        internal void InitNextSongPanels(Action rescanRequested)
        {
            _rescanRequested = rescanRequested;

            _prevPanel = new NextSongPanel();
            _nextPanel = new NextSongPanel();

            _prevPanel.SetRoundedCorners(App.Config.PlayerCoverRoundedCorners);
            _nextPanel.SetRoundedCorners(App.Config.PlayerCoverRoundedCorners);
            _prevPanel.SetShowCover(App.Config.PlayerShowCover);
            _nextPanel.SetShowCover(App.Config.PlayerShowCover);

            _prevPanel.RefreshRequested += () => _rescanRequested?.Invoke();
            _nextPanel.RefreshRequested += () => _rescanRequested?.Invoke();

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

            if (mode == NextSongMode.Off)
            {
                _prevPanel.Hide();
                _nextPanel.Hide();
                return;
            }

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

            _prevPanel.SetRoundedCorners(roundedCorners);
            _nextPanel.SetRoundedCorners(roundedCorners);
            _prevPanel.SetShowCover(showCover);
            _nextPanel.SetShowCover(showCover);
        }
    }
}