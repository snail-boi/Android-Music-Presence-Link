using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// Subsonic counterpart of <see cref="NextSongManager"/>: maintains a sorted list of
    /// every song on the configured server so the next/previous panels and the predictive
    /// features work for streamed (cloud) tracks that have no file on the phone.
    /// The list is fetched once via paged search3 and persisted; a header line records
    /// which server it came from so a server change invalidates it automatically.
    /// </summary>
    internal class SubsonicSongListManager
    {
        private readonly string _listFilePath;

        private List<SubsonicClient.LibrarySong> _entries = new();
        private string _loadedServerUrl = string.Empty;
        private bool _loaded;

        // Throttles re-scan attempts when the server is unreachable, so a track change
        // doesn't fire a failing HTTP call every poll tick.
        private DateTimeOffset _lastFailedScanUtc = DateTimeOffset.MinValue;
        private static readonly TimeSpan FailedScanRetryInterval = TimeSpan.FromSeconds(60);

        public SubsonicSongListManager()
        {
            _listFilePath = AppPaths.GetDataPath("subsonic_library_list.txt");
        }

        public bool IsListPresent => File.Exists(_listFilePath);

        public void InvalidateCache()
        {
            _loaded = false;
            _entries = new();
            _loadedServerUrl = string.Empty;
        }

        // ── Scan ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Fetches the full song list from the server, sorts it, and persists it.
        /// Keeps the previous in-memory list when the fetch returns nothing.
        /// </summary>
        public async Task ScanAsync(string serverUrl, string username, string password, NextSongSortMode sortMode)
        {
            Debugger.show($"[SUBSONGLIST] ScanAsync started (sort: {sortMode}).");

            var songs = await SubsonicClient.FetchAllSongsAsync(serverUrl, username, password).ConfigureAwait(false);
            if (songs.Count == 0)
            {
                Debugger.show("[SUBSONGLIST] Scan returned no songs; keeping previous list.");
                _lastFailedScanUtc = DateTimeOffset.UtcNow;
                return;
            }

            var sorted = Sort(songs, sortMode);
            await WriteListAsync(serverUrl, sorted).ConfigureAwait(false);

            _entries = sorted;
            _loadedServerUrl = serverUrl.Trim();
            _loaded = true;
            _lastFailedScanUtc = DateTimeOffset.MinValue;

            Debugger.show($"[SUBSONGLIST] ScanAsync complete. {_entries.Count} entries in memory.");
        }

        /// <summary>
        /// Re-sorts the loaded list by a new sort mode without hitting the server.
        /// </summary>
        public async Task ResortAsync(NextSongSortMode sortMode)
        {
            await EnsureLoadedAsync().ConfigureAwait(false);
            if (_entries.Count == 0) return;

            _entries = Sort(_entries, sortMode);
            await WriteListAsync(_loadedServerUrl, _entries).ConfigureAwait(false);
            Debugger.show($"[SUBSONGLIST] ResortAsync complete ({sortMode}, {_entries.Count} entries).");
        }

        /// <summary>
        /// Makes sure a usable list for the given server is loaded, scanning when the
        /// list is missing or belongs to a different server. Failed scans are throttled.
        /// Returns true when entries are available afterwards.
        /// </summary>
        public async Task<bool> EnsureFreshAsync(string serverUrl, string username, string password, NextSongSortMode sortMode)
        {
            await EnsureLoadedAsync().ConfigureAwait(false);

            bool usable = _entries.Count > 0
                && string.Equals(_loadedServerUrl, serverUrl.Trim(), StringComparison.OrdinalIgnoreCase);
            if (usable)
                return true;

            if (DateTimeOffset.UtcNow - _lastFailedScanUtc < FailedScanRetryInterval)
            {
                Debugger.show("[SUBSONGLIST] Scan skipped (recent failure, retry throttled).");
                return false;
            }

            await ScanAsync(serverUrl, username, password, sortMode).ConfigureAwait(false);
            return _entries.Count > 0;
        }

        // ── Match ─────────────────────────────────────────────────────────────

        public record Neighbour(string Id, string? CoverArtId, string Title);
        public record NeighbourResult(Neighbour? Prev, Neighbour? Next, bool Found);
        public record NeighbourAtOffset(int Offset, string Id, string? CoverArtId, string Title);

        public async Task<NeighbourResult> FindNeighboursAsync(string? title, string? artist)
        {
            await EnsureLoadedAsync().ConfigureAwait(false);

            int bestIndex = FindBestMatchIndex(title, artist);
            if (bestIndex < 0)
                return new NeighbourResult(null, null, false);

            Neighbour? prev = bestIndex > 0 ? ToNeighbour(_entries[bestIndex - 1]) : null;
            Neighbour? next = bestIndex < _entries.Count - 1 ? ToNeighbour(_entries[bestIndex + 1]) : null;

            Debugger.show($"[SUBSONGLIST] Neighbours around index {bestIndex}: prev={prev?.Title ?? "-"}, next={next?.Title ?? "-"}");
            return new NeighbourResult(prev, next, true);
        }

        public async Task<List<NeighbourAtOffset>> FindNeighboursAtOffsetsAsync(string? title, string? artist, int radius)
        {
            await EnsureLoadedAsync().ConfigureAwait(false);

            var result = new List<NeighbourAtOffset>();
            int bestIndex = FindBestMatchIndex(title, artist);
            if (bestIndex < 0)
                return result;

            for (int offset = -radius; offset <= radius; offset++)
            {
                if (offset == 0) continue;
                int i = bestIndex + offset;
                if (i < 0 || i >= _entries.Count) continue;
                var e = _entries[i];
                result.Add(new NeighbourAtOffset(offset, e.Id, e.CoverArtId, DisplayTitle(e)));
            }

            Debugger.show($"[SUBSONGLIST] FindNeighboursAtOffsetsAsync radius {radius}: {result.Count} neighbours around index {bestIndex}.");
            return result;
        }

        private static Neighbour ToNeighbour(SubsonicClient.LibrarySong e)
            => new(e.Id, e.CoverArtId, DisplayTitle(e));

        private static string DisplayTitle(SubsonicClient.LibrarySong e)
            => string.IsNullOrWhiteSpace(e.Title) ? Path.GetFileNameWithoutExtension(e.Path ?? string.Empty) : e.Title;

        // Matches on the server's own title/artist metadata (exact-ish), unlike the
        // local list which has to fuzzy-match filenames.
        private int FindBestMatchIndex(string? title, string? artist)
        {
            if (_entries.Count == 0 || string.IsNullOrWhiteSpace(title))
                return -1;

            string normTitle = NormalizeForMatch(title);
            string normArtist = NormalizeForMatch(artist ?? string.Empty);
            if (normTitle.Length == 0)
                return -1;

            int bestIndex = -1;
            int bestScore = int.MinValue;

            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                var candidate = NormalizeForMatch(e.Title);
                if (candidate.Length == 0 || !candidate.Contains(normTitle, StringComparison.OrdinalIgnoreCase))
                    continue;

                int score = 10;
                if (string.Equals(candidate, normTitle, StringComparison.OrdinalIgnoreCase))
                    score += 50;

                if (normArtist.Length > 0)
                {
                    var candArtist = NormalizeForMatch(e.Artist);
                    if (string.Equals(candArtist, normArtist, StringComparison.OrdinalIgnoreCase))
                        score += 20;
                    else if (candArtist.Contains(normArtist, StringComparison.OrdinalIgnoreCase)
                        || normArtist.Contains(candArtist, StringComparison.OrdinalIgnoreCase))
                        score += 10;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
            {
                Debugger.show($"[SUBSONGLIST] No match for \"{title}\" / \"{artist}\".");
                return -1;
            }

            Debugger.show($"[SUBSONGLIST] Best match index {bestIndex} (score {bestScore}): \"{_entries[bestIndex].Title}\"");
            return bestIndex;
        }

        private static string NormalizeForMatch(string? input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            var sb = new StringBuilder(input.Length);
            foreach (var ch in input)
            {
                if (char.IsLetterOrDigit(ch) || ch == ' ')
                    sb.Append(char.ToLowerInvariant(ch));
                else
                    sb.Append(' ');
            }
            return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        }

        // ── Sort ──────────────────────────────────────────────────────────────

        // Mirrors NextSongManager.Sort: filename sorts use the server-side path's
        // filename (falling back to the title), date sorts use the server's "created"
        // stamp with the same filename Z-A tiebreaker as the local list.
        private static List<SubsonicClient.LibrarySong> Sort(List<SubsonicClient.LibrarySong> entries, NextSongSortMode mode)
        {
            string FileName(SubsonicClient.LibrarySong e)
                => string.IsNullOrWhiteSpace(e.Path) ? e.Title : Path.GetFileName(e.Path);

            return mode switch
            {
                NextSongSortMode.FilenameAZ =>
                    entries.OrderBy(FileName, StringComparer.OrdinalIgnoreCase).ToList(),

                NextSongSortMode.FilenameZA =>
                    entries.OrderByDescending(FileName, StringComparer.OrdinalIgnoreCase).ToList(),

                NextSongSortMode.DateModifiedNewest =>
                    entries.OrderByDescending(e => e.CreatedUtc)
                           .ThenByDescending(FileName, StringComparer.OrdinalIgnoreCase)
                           .ToList(),

                NextSongSortMode.DateModifiedOldest =>
                    entries.OrderBy(e => e.CreatedUtc)
                           .ThenByDescending(FileName, StringComparer.OrdinalIgnoreCase)
                           .ToList(),

                _ => entries
            };
        }

        // ── Persistence ───────────────────────────────────────────────────────
        // Line 1: "#server\t<url>", then one song per line:
        // id \t coverArtId \t created(ISO) \t path \t title \t artist
        // Tabs inside values are folded to spaces on write so the format stays parseable.

        private async Task WriteListAsync(string serverUrl, List<SubsonicClient.LibrarySong> entries)
        {
            try
            {
                var dir = Path.GetDirectoryName(_listFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                static string Safe(string? v) => (v ?? string.Empty).Replace('\t', ' ');

                var lines = new List<string>(entries.Count + 1)
                {
                    $"#server\t{Safe(serverUrl.Trim())}"
                };
                lines.AddRange(entries.Select(e =>
                    $"{Safe(e.Id)}\t{Safe(e.CoverArtId)}\t{e.CreatedUtc:O}\t{Safe(e.Path)}\t{Safe(e.Title)}\t{Safe(e.Artist)}"));

                await File.WriteAllLinesAsync(_listFilePath, lines, Encoding.UTF8).ConfigureAwait(false);
                Debugger.show($"[SUBSONGLIST] List written: {entries.Count} entries.");
            }
            catch (Exception ex)
            {
                Debugger.show("[SUBSONGLIST] WriteListAsync failed: " + ex.Message);
            }
        }

        private async Task EnsureLoadedAsync()
        {
            if (_loaded) return;

            try
            {
                if (!File.Exists(_listFilePath))
                {
                    Debugger.show("[SUBSONGLIST] List file not found, nothing to load.");
                    _loaded = true;
                    return;
                }

                var rawLines = await File.ReadAllLinesAsync(_listFilePath, Encoding.UTF8).ConfigureAwait(false);
                var entries = new List<SubsonicClient.LibrarySong>(rawLines.Length);
                string serverUrl = string.Empty;

                foreach (var line in rawLines)
                {
                    if (line.StartsWith("#server\t", StringComparison.Ordinal))
                    {
                        serverUrl = line.Substring("#server\t".Length).Trim();
                        continue;
                    }

                    var parts = line.Split('\t');
                    if (parts.Length < 6 || string.IsNullOrWhiteSpace(parts[0]))
                        continue;

                    DateTime created = DateTime.TryParse(parts[2], null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var d)
                        ? d : DateTime.MinValue;

                    entries.Add(new SubsonicClient.LibrarySong(
                        parts[0],
                        parts[4],
                        parts[5],
                        string.IsNullOrWhiteSpace(parts[3]) ? null : parts[3],
                        string.IsNullOrWhiteSpace(parts[1]) ? null : parts[1],
                        created));
                }

                _entries = entries;
                _loadedServerUrl = serverUrl;
                _loaded = true;
                Debugger.show($"[SUBSONGLIST] Loaded {_entries.Count} entries for server \"{serverUrl}\".");
            }
            catch (Exception ex)
            {
                Debugger.show("[SUBSONGLIST] EnsureLoadedAsync failed: " + ex.Message);
                _loaded = true;
            }
        }
    }
}
