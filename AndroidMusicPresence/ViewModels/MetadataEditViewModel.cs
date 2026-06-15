using System;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// ViewModel for MetadataEditWindow. Holds one bound string per editable tag, plus the
    /// cover state (preview path, a chosen replacement, and a "remove" flag). Save raises
    /// RequestClose(true); Cancel raises RequestClose(false). The window reads BuildResult()
    /// back afterwards.
    ///
    /// Picking a replacement image is a view concern, so it comes in through the injected
    /// pickImageFile delegate (the window wires it to an OpenFileDialog), matching the
    /// dialog-seam pattern used elsewhere.
    /// </summary>
    internal sealed class MetadataEditViewModel : ViewModelBase
    {
        public event Action<bool>? RequestClose;

        private readonly Func<string?>? _pickImageFile;
        private readonly string? _originalCoverPreviewPath;

        public MetadataEditViewModel(TrackMetadata initial, string fileLabel, Func<string?>? pickImageFile = null)
        {
            _pickImageFile = pickImageFile;
            _fileLabel = fileLabel ?? string.Empty;

            _title = initial.Title;
            _album = initial.Album;
            _artist = initial.Artist;
            _albumArtist = initial.AlbumArtist;
            _composer = initial.Composer;
            _genre = initial.Genre;
            _trackNumber = initial.TrackNumber;
            _discNumber = initial.DiscNumber;
            _year = initial.Year;
            _comment = initial.Comment;
            _retainDate = initial.RetainDateModified;

            _originalCoverPreviewPath = initial.CoverPreviewPath;
            _coverPreviewPath = initial.CoverPreviewPath;
            _localSourcePath = initial.LocalSourcePath;

            _lyrics = initial.Lyrics ?? string.Empty;
            _lyricsSourceField = initial.LyricsSourceField;
            _lyricsFromLrc = initial.LyricsFromLrc;
            _lyricsLrcPath = initial.LyricsLrcPath;
            _lrcLocked = (_fileLabel ?? string.Empty).EndsWith(".wav", StringComparison.OrdinalIgnoreCase);
            _saveLyricsAsLrc = _lrcLocked || initial.SaveLyricsAsLrc;
        }

        private readonly string? _lyricsSourceField;
        private readonly bool _lyricsFromLrc;
        private readonly string? _lyricsLrcPath;
        private readonly bool _lrcLocked;

        private readonly string? _localSourcePath;

        private string _fileLabel;
        public string FileLabel { get => _fileLabel; set => Set(ref _fileLabel, value); }

        private string _title;
        public string Title { get => _title; set => Set(ref _title, value); }

        private string _album;
        public string Album { get => _album; set => Set(ref _album, value); }

        private string _artist;
        public string Artist { get => _artist; set => Set(ref _artist, value); }

        private string _albumArtist;
        public string AlbumArtist { get => _albumArtist; set => Set(ref _albumArtist, value); }

        private string _composer;
        public string Composer { get => _composer; set => Set(ref _composer, value); }

        private string _genre;
        public string Genre { get => _genre; set => Set(ref _genre, value); }

        private string _trackNumber;
        public string TrackNumber { get => _trackNumber; set => Set(ref _trackNumber, value); }

        private string _discNumber;
        public string DiscNumber { get => _discNumber; set => Set(ref _discNumber, value); }

        private string _year;
        public string Year { get => _year; set => Set(ref _year, value); }

        private string _comment;
        public string Comment { get => _comment; set => Set(ref _comment, value); }

        private bool _retainDate;
        public bool RetainDate { get => _retainDate; set => Set(ref _retainDate, value); }

        // Lyrics. The inline editor commits its draft into Lyrics/SaveLyricsAsLrc; the
        // indicators below drive the button label and status text.
        private string _lyrics;
        public string Lyrics
        {
            get => _lyrics;
            set
            {
                if (Set(ref _lyrics, value))
                {
                    RaisePropertyChanged(nameof(HasLyrics));
                    RaisePropertyChanged(nameof(LyricsButtonLabel));
                    RaisePropertyChanged(nameof(LyricsStatus));
                }
            }
        }

        private bool _saveLyricsAsLrc;
        public bool SaveLyricsAsLrc { get => _saveLyricsAsLrc; set => Set(ref _saveLyricsAsLrc, value); }

        public bool LrcLocked => _lrcLocked;
        public bool HasLyrics => !string.IsNullOrWhiteSpace(Lyrics);
        public string LyricsButtonLabel => HasLyrics ? "Edit lyrics" : "Create lyrics";
        public string LyricsStatus => !HasLyrics ? string.Empty : (LyricsIsSynced(Lyrics) ? "Lyrics: synced" : "Lyrics: unsynced");

        private static bool LyricsIsSynced(string text)
            => !string.IsNullOrEmpty(text)
            && System.Text.RegularExpressions.Regex.IsMatch(text, @"\[\d{1,2}:\d{2}");

        // Cover state.
        private string? _coverPreviewPath;
        public string? CoverPreviewPath { get => _coverPreviewPath; set => Set(ref _coverPreviewPath, value); }

        private string? _newCoverImagePath;
        private bool _removeCover;

        private RelayCommand? _okCommand;
        public RelayCommand OkCommand => _okCommand ??= new RelayCommand(() => RequestClose?.Invoke(true));

        private RelayCommand? _cancelCommand;
        public RelayCommand CancelCommand => _cancelCommand ??= new RelayCommand(() => RequestClose?.Invoke(false));

        private RelayCommand? _changeCoverCommand;
        public RelayCommand ChangeCoverCommand => _changeCoverCommand ??= new RelayCommand(ChangeCover);

        private RelayCommand? _removeCoverCommand;
        public RelayCommand RemoveCoverCommand => _removeCoverCommand ??= new RelayCommand(RemoveCoverArt);

        private void ChangeCover()
        {
            var picked = _pickImageFile?.Invoke();
            if (string.IsNullOrWhiteSpace(picked)) return;

            _newCoverImagePath = picked;
            _removeCover = false;
            CoverPreviewPath = picked;
        }

        private void RemoveCoverArt()
        {
            _newCoverImagePath = null;
            _removeCover = true;
            CoverPreviewPath = null;
        }

        /// <summary>Read the edited values back as a TrackMetadata after a true result.</summary>
        public TrackMetadata BuildResult()
        {
            return new TrackMetadata
            {
                Title = (Title ?? string.Empty).Trim(),
                Album = (Album ?? string.Empty).Trim(),
                Artist = (Artist ?? string.Empty).Trim(),
                AlbumArtist = (AlbumArtist ?? string.Empty).Trim(),
                Composer = (Composer ?? string.Empty).Trim(),
                Genre = (Genre ?? string.Empty).Trim(),
                TrackNumber = (TrackNumber ?? string.Empty).Trim(),
                DiscNumber = (DiscNumber ?? string.Empty).Trim(),
                Year = (Year ?? string.Empty).Trim(),
                Comment = (Comment ?? string.Empty).Trim(),
                NewCoverImagePath = _newCoverImagePath,
                RemoveCover = _removeCover,
                CoverPreviewPath = _originalCoverPreviewPath,
                LocalSourcePath = _localSourcePath,
                RetainDateModified = RetainDate,
                Lyrics = Lyrics ?? string.Empty,
                LyricsSourceField = _lyricsSourceField,
                LyricsFromLrc = _lyricsFromLrc,
                LyricsLrcPath = _lyricsLrcPath,
                SaveLyricsAsLrc = SaveLyricsAsLrc
            };
        }
    }
}