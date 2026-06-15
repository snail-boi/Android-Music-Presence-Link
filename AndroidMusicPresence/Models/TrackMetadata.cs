using System;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// Plain data holder for the editable tag fields of a single track, plus the cover
    /// decision and the local working copy of the file. Lyricist is intentionally absent:
    /// it has no clean home through ffmpeg (TXXX-only on MP3, dropped entirely on M4A), so
    /// the feature does not expose it.
    ///
    /// All textual tags are kept as strings, including TrackNumber, DiscNumber, and Year,
    /// so values like "3/12" round-trip untouched. An empty string is a deliberate "clear
    /// this tag" instruction at write time, not "leave it alone".
    ///
    /// LocalSourcePath is the temp file pulled during the read pass. WriteAsync reuses it
    /// when it is still present so we only pull the track off the phone once, in keeping
    /// with the project's payload-reduction preference.
    /// </summary>
    internal sealed class TrackMetadata
    {
        public string Title { get; set; } = string.Empty;
        public string Album { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string AlbumArtist { get; set; } = string.Empty;
        public string Composer { get; set; } = string.Empty;
        public string Genre { get; set; } = string.Empty;
        public string TrackNumber { get; set; } = string.Empty;
        public string DiscNumber { get; set; } = string.Empty;
        public string Year { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;

        /// <summary>Local path of a JPG extracted from the file, used only for preview. Null if none.</summary>
        public string? CoverPreviewPath { get; set; }

        /// <summary>Local path of a replacement image the user picked. Null means "keep the existing art".</summary>
        public string? NewCoverImagePath { get; set; }

        /// <summary>True means strip embedded art and write none. Ignored when NewCoverImagePath is set.</summary>
        public bool RemoveCover { get; set; }

        /// <summary>Temp file pulled during ReadAsync, reused by WriteAsync when still present.</summary>
        public string? LocalSourcePath { get; set; }

        /// <summary>True means restore the original modification time (plus one second so the media scanner still re-reads tags).</summary>
        public bool RetainDateModified { get; set; } = true;

        // Lyrics. Lyrics is the text (plain or LRC-timestamped; sync is decided by content).
        // LyricsSourceField is the exact embedded tag key the lyrics were read from, so an
        // edit goes back to the same place. LyricsFromLrc/LyricsLrcPath track a sibling .lrc
        // source. SaveLyricsAsLrc means write to a .lrc instead of embedding.
        public string Lyrics { get; set; } = string.Empty;
        public string? LyricsSourceField { get; set; }
        public bool LyricsFromLrc { get; set; }
        public string? LyricsLrcPath { get; set; }
        public bool SaveLyricsAsLrc { get; set; }

        public TrackMetadata Clone()
        {
            return new TrackMetadata
            {
                Title = Title,
                Album = Album,
                Artist = Artist,
                AlbumArtist = AlbumArtist,
                Composer = Composer,
                Genre = Genre,
                TrackNumber = TrackNumber,
                DiscNumber = DiscNumber,
                Year = Year,
                Comment = Comment,
                CoverPreviewPath = CoverPreviewPath,
                NewCoverImagePath = NewCoverImagePath,
                RemoveCover = RemoveCover,
                LocalSourcePath = LocalSourcePath,
                RetainDateModified = RetainDateModified,
                Lyrics = Lyrics,
                LyricsSourceField = LyricsSourceField,
                LyricsFromLrc = LyricsFromLrc,
                LyricsLrcPath = LyricsLrcPath,
                SaveLyricsAsLrc = SaveLyricsAsLrc
            };
        }
    }
}