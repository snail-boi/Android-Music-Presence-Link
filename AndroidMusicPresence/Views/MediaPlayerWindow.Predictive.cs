using System;
using System.Windows;

namespace AndroidMusicPresenceLink
{
    public partial class MediaPlayerWindow
    {
        // How long a predicted state is protected from poll updates that still carry
        // the pre-click track/play-state. Long enough to cover the phone's reaction
        // plus one poll tick; after this the real state always wins, which is also
        // how a command that never went through gets corrected.
        private static readonly TimeSpan PredictionHold = TimeSpan.FromMilliseconds(1500);

        // Raw (unnormalized) values from the last real UpdateTrack call, used to
        // recognize "still the pre-click track" updates during the hold window.
        private string? _lastRawTitle;
        private string? _lastRawArtist;

        private DateTime _predictionHoldUntil = DateTime.MinValue;
        private string? _predictionPreClickTitle;
        private string? _predictionPreClickArtist;

        private bool? _pausePredictedIsPlaying;
        private DateTime _pausePredictHoldUntil = DateTime.MinValue;

        /// <summary>
        /// Instantly flips the play/pause icon on click. The next MediaSession poll
        /// confirms the flip (clearing the override) or, once the hold expires,
        /// corrects it if the command never reached the phone.
        /// </summary>
        internal void ApplyPredictedPlayPause()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(ApplyPredictedPlayPause); return; }
            if (!_hasSong) return;

            _pausePredictedIsPlaying = !_isPlaying;
            _pausePredictHoldUntil = DateTime.UtcNow + PredictionHold;
            _isPlaying = _pausePredictedIsPlaying.Value;
            RenderTransportIcons(_isPlaying);
        }

        /// <summary>
        /// Instantly swaps the displayed track to the predicted neighbour on a
        /// next/prev click. Text is only touched when predictive UI is on; the cover
        /// is either the prefetched neighbour cover or the no-cover placeholder.
        /// The real state overwrites all of this when it arrives.
        /// </summary>
        internal void ShowPredictedTrack(string? predictedTitle, string? coverPath, bool applyCover)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => ShowPredictedTrack(predictedTitle, coverPath, applyCover)); return; }
            if (!_hasSong) return;

            _predictionPreClickTitle = _lastRawTitle;
            _predictionPreClickArtist = _lastRawArtist;
            _predictionHoldUntil = DateTime.UtcNow + PredictionHold;

            if (!string.IsNullOrWhiteSpace(predictedTitle))
            {
                _vm.Title = predictedTitle;
                // Artist/album are unknown until the real metadata arrives.
                _vm.Artist = " ";
                _vm.Album = " ";
            }

            if (applyCover)
            {
                _currentCoverPath = coverPath;
                if (!string.Equals(_lastCoverImagePath, coverPath, StringComparison.OrdinalIgnoreCase))
                {
                    _lastCoverImagePath = coverPath;
                    FadeCoverImage(coverPath);
                }
                ApplyCoverGradientBackground(coverPath);
            }
        }
    }
}
