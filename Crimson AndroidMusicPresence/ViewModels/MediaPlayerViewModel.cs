namespace musicpresense
{
    /// <summary>
    /// Thin display state for the media player's now-playing pane. The window keeps doing all of
    /// the rendering (cover crossfade, gradient, icons, slider, lyrics, chrome); this only holds
    /// the text the pane shows, pushed in by the window's UpdateTrack and UpdateProgress methods.
    /// Scoped to PlayerPaneBorder via its DataContext, so it does not affect the hosted settings
    /// panes.
    /// </summary>
    internal sealed class MediaPlayerViewModel : ViewModelBase
    {
        private string _title = "-";
        public string Title { get => _title; set => Set(ref _title, value); }

        private string _artist = "-";
        public string Artist { get => _artist; set => Set(ref _artist, value); }

        private string _album = "-";
        public string Album { get => _album; set => Set(ref _album, value); }

        private string _positionLabel = "0:00";
        public string PositionLabel { get => _positionLabel; set => Set(ref _positionLabel, value); }

        private string _durationLabel = "0:00";
        public string DurationLabel { get => _durationLabel; set => Set(ref _durationLabel, value); }
    }
}
