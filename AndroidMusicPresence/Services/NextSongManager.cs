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
    /// Manages the local library list used to find the previous and next song.
    /// The list is built by running find + stat across all configured remote roots,
    /// giving full-precision epoch-second modification times.
    /// Each entry is stored as "remotepath\tepoch-datetime" on disk.
    /// The file is pre-sorted at scan time according to the user's chosen sort mode.
    /// Re-sorting on a sort-mode change does not require a rescan.
    /// </summary>
    internal class NextSongManager
    {
        private static readonly string[] AudioExtensions =
            { ".mp3", ".flac", ".wav", ".m4a", ".ogg", ".opus", ".aac", ".wma" };

        private readonly string _listFilePath;
        private readonly string _pathIndexFilePath;

        // In-memory copy of the sorted list. Loaded once and kept for the session.
        private List<LibraryEntry> _entries = new();
        private bool _loaded;

        public NextSongManager()
        {
            _listFilePath = AppPaths.GetDataPath("library_list.txt");
            _pathIndexFilePath = AppPaths.GetDataPath("library_paths.txt");
        }

        public bool IsListPresent => File.Exists(_listFilePath);

        // ── Scan ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Runs `adb shell ls -l` across all configured remote roots, parses the output,
        /// sorts by the current sort mode, and writes the result to disk.
        /// </summary>
        public async Task ScanAsync(string deviceId, List<string> remoteRoots, NextSongSortMode sortMode)
        {
            Debugger.show($"[NEXTSONG] ScanAsync started. Device: {deviceId}, roots: {remoteRoots.Count}, sort: {sortMode}");
            var entries = new List<LibraryEntry>();

            foreach (var root in remoteRoots)
            {
                var escapedRoot = root.Replace("\"", "\\\"");
                Debugger.show($"[NEXTSONG] Scanning root: {root}");

                var cmd = $"-s {deviceId} shell find \"{escapedRoot}\" -type f -print0 | xargs -0 stat -c \"%n %Y\"";
                Debugger.show($"[NEXTSONG] ADB command: {cmd}");

                var output = await AdbHelper.RunAdbCaptureAsync(cmd).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(output))
                {
                    Debugger.show($"[NEXTSONG] No output for root: {root}");
                    continue;
                }

                Debugger.show($"[NEXTSONG] Raw output length: {output.Length} chars");

                var parsed = ParseStatOutput(output);
                entries.AddRange(parsed);
                Debugger.show($"[NEXTSONG] Parsed {parsed.Count} audio files from root: {root}");
            }

            Debugger.show($"[NEXTSONG] Total entries before sort: {entries.Count}");

            var sorted = Sort(entries, sortMode);
            Debugger.show($"[NEXTSONG] Sort complete. Mode: {sortMode}, entries: {sorted.Count}");

            await WriteListAsync(sorted).ConfigureAwait(false);

            _entries = sorted;
            _loaded = true;

            Debugger.show($"[NEXTSONG] ScanAsync complete. {_entries.Count} entries in memory.");
        }

        /// <summary>
        /// Re-sorts the existing list file by a new sort mode without rescanning the device.
        /// </summary>
        public async Task ResortAsync(NextSongSortMode sortMode)
        {
            Debugger.show($"[NEXTSONG] ResortAsync started. New mode: {sortMode}");
            await EnsureLoadedAsync().ConfigureAwait(false);
            Debugger.show($"[NEXTSONG] Resorting {_entries.Count} entries.");
            _entries = Sort(_entries, sortMode);
            await WriteListAsync(_entries).ConfigureAwait(false);
            Debugger.show($"[NEXTSONG] ResortAsync complete. {_entries.Count} entries written.");
        }

        // ── Match ─────────────────────────────────────────────────────────────

        public record NeighbourResult(
            string? PrevPath,
            string? PrevTitle,
            string? NextPath,
            string? NextTitle,
            bool Found);

        /// <summary>
        /// Fuzzy-matches the current track against the library list and returns the
        /// paths and display names of the previous and next entries.
        /// Returns Found=false if the current track could not be matched.
        /// </summary>
        public async Task<NeighbourResult> FindNeighboursAsync(string? title, string? artist)
        {
            Debugger.show($"[NEXTSONG] FindNeighboursAsync. Title: \"{title}\", Artist: \"{artist}\"");
            await EnsureLoadedAsync().ConfigureAwait(false);

            if (_entries.Count == 0)
            {
                Debugger.show("[NEXTSONG] Entry list is empty, returning not found.");
                return new NeighbourResult(null, null, null, null, false);
            }

            int bestIndex = FindBestMatchIndex(title, artist);
            if (bestIndex < 0)
                return new NeighbourResult(null, null, null, null, false);

            Debugger.show($"[NEXTSONG] Matched: \"{_entries[bestIndex].RemotePath}\"");

            string? prevPath = null, prevTitle = null;
            string? nextPath = null, nextTitle = null;

            if (bestIndex > 0)
            {
                prevPath = _entries[bestIndex - 1].RemotePath;
                prevTitle = FilenameToDisplayTitle(prevPath);
                Debugger.show($"[NEXTSONG] Prev [{bestIndex - 1}]: \"{prevPath}\"");
            }
            else
            {
                Debugger.show("[NEXTSONG] No previous entry (matched at index 0).");
            }

            if (bestIndex < _entries.Count - 1)
            {
                nextPath = _entries[bestIndex + 1].RemotePath;
                nextTitle = FilenameToDisplayTitle(nextPath);
                Debugger.show($"[NEXTSONG] Next [{bestIndex + 1}]: \"{nextPath}\"");
            }
            else
            {
                Debugger.show("[NEXTSONG] No next entry (matched at last index).");
            }

            return new NeighbourResult(prevPath, prevTitle, nextPath, nextTitle, true);
        }

        public record NeighbourAtOffset(int Offset, string RemotePath, string Title);

        /// <summary>
        /// Like FindNeighboursAsync but returns every entry within ±radius of the
        /// matched track (offset 0, the match itself, is skipped). Used by the
        /// predictive UI/covers feature, which needs up to two entries per direction.
        /// Returns an empty list when the current track can't be matched.
        /// </summary>
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
                result.Add(new NeighbourAtOffset(offset, _entries[i].RemotePath, FilenameToDisplayTitle(_entries[i].RemotePath)));
            }

            Debugger.show($"[NEXTSONG] FindNeighboursAtOffsetsAsync radius {radius}: {result.Count} neighbours around index {bestIndex}.");
            return result;
        }

        // Fuzzy-matches the track against the loaded list and returns its index,
        // or -1 when nothing scores above the match threshold.
        private int FindBestMatchIndex(string? title, string? artist)
        {
            if (_entries.Count == 0 || string.IsNullOrWhiteSpace(title))
                return -1;

            int bestIndex = -1;
            int bestScore = int.MinValue;

            string normTitle = NormalizeForMatch(title);
            string normArtist = NormalizeForMatch(artist ?? string.Empty);
            Debugger.show($"[NEXTSONG] Normalized title: \"{normTitle}\", artist: \"{normArtist}\"");

            for (int i = 0; i < _entries.Count; i++)
            {
                var score = ScoreCandidate(_entries[i].RemotePath, normTitle, normArtist);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            Debugger.show($"[NEXTSONG] Best match index: {bestIndex}, score: {bestScore}");

            if (bestIndex < 0 || bestScore < 10)
            {
                Debugger.show($"[NEXTSONG] No match found (score {bestScore} below threshold).");
                return -1;
            }

            return bestIndex;
        }

        // ── Scoring (mirrors MediaController logic) ───────────────────────────

        private static int ScoreCandidate(string remotePath, string normTitle, string normArtist)
        {
            if (string.IsNullOrWhiteSpace(normTitle)) return int.MinValue;

            var stem = NormalizeForMatch(Path.GetFileNameWithoutExtension(remotePath));
            if (!stem.Contains(normTitle, StringComparison.OrdinalIgnoreCase))
                return int.MinValue;

            int score = 10;

            // Exact stem match is strongest signal.
            if (string.Equals(stem, normTitle, StringComparison.OrdinalIgnoreCase))
                score += 50;

            // Artist contained in path (parent folder often matches artist name).
            if (!string.IsNullOrWhiteSpace(normArtist))
            {
                var normPath = NormalizeForMatch(remotePath);
                if (normPath.Contains(normArtist, StringComparison.OrdinalIgnoreCase))
                    score += 10;
            }

            // Prefer lossless/high-quality formats.
            var ext = Path.GetExtension(remotePath).ToLowerInvariant();
            score += ext switch
            {
                ".wav" => 6,
                ".flac" => 5,
                ".opus" => 4,
                ".m4a" => 3,
                ".ogg" => 2,
                _ => 1
            };

            return score;
        }

        private static string NormalizeForMatch(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            // Strip filesystem-unsafe chars, collapse whitespace, lower.
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

        private static string FilenameToDisplayTitle(string remotePath)
        {
            var stem = Path.GetFileNameWithoutExtension(remotePath);
            // Strip leading track-number prefix like "01 - " or "01. ".
            stem = Regex.Replace(stem, @"^\d+[\s.\-_]+", string.Empty).Trim();
            return stem;
        }

        // ── Parsing ───────────────────────────────────────────────────────────

        /// <summary>
        /// Parses output of: find root -type f -print0 | xargs -0 stat -c "%n %Y"
        /// Each line is: /full/path/to/file.mp3 1699102951
        /// The epoch timestamp is always the last token; everything before it is the path.
        /// </summary>
        private static List<LibraryEntry> ParseStatOutput(string output)
        {
            var result = new List<LibraryEntry>();
            int skippedNonAudio = 0;
            int skippedMalformed = 0;

            foreach (var rawLine in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.TrimEnd();
                if (string.IsNullOrWhiteSpace(line)) continue;

                int lastSpace = line.LastIndexOf(' ');
                if (lastSpace < 1) { skippedMalformed++; continue; }

                var pathPart = line.Substring(0, lastSpace).Trim();
                var epochPart = line.Substring(lastSpace + 1).Trim();

                if (string.IsNullOrWhiteSpace(pathPart)) { skippedMalformed++; continue; }
                if (!pathPart.StartsWith('/')) { skippedMalformed++; continue; }

                var ext = Path.GetExtension(pathPart).ToLowerInvariant();
                if (!AudioExtensions.Contains(ext)) { skippedNonAudio++; continue; }

                DateTime date = DateTime.MinValue;
                if (long.TryParse(epochPart, out long epoch))
                    date = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
                else
                    Debugger.show($"[NEXTSONG] Failed to parse epoch \"{epochPart}\" for: {pathPart}");

                result.Add(new LibraryEntry(pathPart, date));
            }

            Debugger.show($"[NEXTSONG] ParseStatOutput: {result.Count} accepted, {skippedNonAudio} non-audio skipped, {skippedMalformed} malformed skipped.");
            return result;
        }

        // ── Sort ──────────────────────────────────────────────────────────────

        private static List<LibraryEntry> Sort(List<LibraryEntry> entries, NextSongSortMode mode)
        {
            return mode switch
            {
                NextSongSortMode.FilenameAZ =>
                    entries.OrderBy(e => Path.GetFileName(e.RemotePath), StringComparer.OrdinalIgnoreCase).ToList(),

                NextSongSortMode.FilenameZA =>
                    entries.OrderByDescending(e => Path.GetFileName(e.RemotePath), StringComparer.OrdinalIgnoreCase).ToList(),

                // Tiebreaker for date sorts is filename Z-A (descending) to match
                // the phone's media player insertion order within the same timestamp.
                NextSongSortMode.DateModifiedNewest =>
                    entries.OrderByDescending(e => e.DateModified)
                           .ThenByDescending(e => Path.GetFileName(e.RemotePath), StringComparer.OrdinalIgnoreCase)
                           .ToList(),

                NextSongSortMode.DateModifiedOldest =>
                    entries.OrderBy(e => e.DateModified)
                           .ThenByDescending(e => Path.GetFileName(e.RemotePath), StringComparer.OrdinalIgnoreCase)
                           .ToList(),

                _ => entries
            };
        }

        // ── Persistence ───────────────────────────────────────────────────────

        /// <summary>
        /// Builds a path segment index from the full entry list.
        /// Any path segment (directory component) that appears in more than one entry
        /// is assigned a random 5-digit ID. Single-use segments stay inline.
        /// Returns: dict of ID -> segment string.
        /// </summary>
        private static Dictionary<string, string> BuildPathIndex(List<LibraryEntry> entries)
        {
            Debugger.show($"[NEXTSONG] BuildPathIndex: analysing {entries.Count} entries for shared segments.");
            var segmentCounts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var entry in entries)
            {
                var path = entry.RemotePath;
                int pos = 0;
                while (true)
                {
                    int slash = path.IndexOf('/', pos + 1);
                    if (slash < 0) break;
                    var segment = path.Substring(0, slash + 1);
                    segmentCounts.TryGetValue(segment, out int c);
                    segmentCounts[segment] = c + 1;
                    pos = slash;
                }
            }

            var rng = new Random();
            var used = new HashSet<string>();
            var index = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var kvp in segmentCounts.OrderByDescending(k => k.Key.Length))
            {
                if (kvp.Value < 2) continue;

                string id;
                do { id = rng.Next(10000, 99999).ToString(); } while (used.Contains(id));
                used.Add(id);
                index[kvp.Key] = id;
                Debugger.show($"[NEXTSONG] Index: {id} = \"{kvp.Key}\" (used by {kvp.Value} entries)");
            }

            Debugger.show($"[NEXTSONG] BuildPathIndex complete. {index.Count} segments indexed.");
            return index;
        }

        /// <summary>
        /// Replaces the longest matching index segment in the path with its ID.
        /// Returns the compressed path string.
        /// </summary>
        private static string CompressPath(string fullPath, Dictionary<string, string> segmentToId)
        {
            // Try longest segments first (already ordered by BuildPathIndex, but we need
            // to search here so sort by key length descending).
            foreach (var kvp in segmentToId.OrderByDescending(k => k.Key.Length))
            {
                if (fullPath.StartsWith(kvp.Key, StringComparison.Ordinal))
                {
                    // Replace the prefix with its numeric ID.
                    return kvp.Value + "/" + fullPath.Substring(kvp.Key.Length);
                }
            }
            return fullPath;
        }

        private async Task WriteListAsync(List<LibraryEntry> entries)
        {
            try
            {
                Debugger.show($"[NEXTSONG] WriteListAsync: writing {entries.Count} entries.");
                var dir = Path.GetDirectoryName(_listFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var segmentToId = BuildPathIndex(entries);
                var idToSegment = segmentToId.ToDictionary(k => k.Value, k => k.Key);

                if (idToSegment.Count > 0)
                {
                    var indexLines = idToSegment.Select(kvp => $"{kvp.Key}\t{kvp.Value}");
                    await File.WriteAllLinesAsync(_pathIndexFilePath, indexLines, Encoding.UTF8).ConfigureAwait(false);
                    Debugger.show($"[NEXTSONG] Path index written to: {_pathIndexFilePath}");
                }
                else if (File.Exists(_pathIndexFilePath))
                {
                    File.Delete(_pathIndexFilePath);
                    Debugger.show("[NEXTSONG] No shared segments; deleted stale path index.");
                }

                var lines = entries.Select(e =>
                    $"{CompressPath(e.RemotePath, segmentToId)}\t{e.DateModified:O}");
                await File.WriteAllLinesAsync(_listFilePath, lines, Encoding.UTF8).ConfigureAwait(false);

                Debugger.show($"[NEXTSONG] List written to: {_listFilePath} ({entries.Count} entries, {idToSegment.Count} indexed segments).");
            }
            catch (Exception ex)
            {
                Debugger.show("[NEXTSONG] WriteListAsync failed: " + ex.Message);
            }
        }

        private async Task EnsureLoadedAsync()
        {
            if (_loaded) return;

            try
            {
                Debugger.show($"[NEXTSONG] EnsureLoadedAsync: loading from disk.");

                if (!File.Exists(_listFilePath))
                {
                    Debugger.show("[NEXTSONG] List file not found, nothing to load.");
                    return;
                }

                var idToSegment = new Dictionary<string, string>(StringComparer.Ordinal);
                if (File.Exists(_pathIndexFilePath))
                {
                    var indexLines = await File.ReadAllLinesAsync(_pathIndexFilePath, Encoding.UTF8).ConfigureAwait(false);
                    foreach (var line in indexLines)
                    {
                        var parts = line.Split('\t');
                        if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
                            idToSegment[parts[0].Trim()] = parts[1];
                    }
                    Debugger.show($"[NEXTSONG] Path index loaded: {idToSegment.Count} segments.");
                }
                else
                {
                    Debugger.show("[NEXTSONG] No path index file, paths stored as-is.");
                }

                var rawLines = await File.ReadAllLinesAsync(_listFilePath, Encoding.UTF8).ConfigureAwait(false);
                var entries = new List<LibraryEntry>(rawLines.Length);
                int expandedCount = 0;
                int failedCount = 0;

                foreach (var line in rawLines)
                {
                    var parts = line.Split('\t');
                    if (parts.Length < 1 || string.IsNullOrWhiteSpace(parts[0])) { failedCount++; continue; }

                    var storedPath = parts[0];
                    var date = parts.Length > 1 && DateTime.TryParse(parts[1], out var d) ? d : DateTime.MinValue;

                    var fullPath = ExpandPath(storedPath, idToSegment);
                    if (fullPath != storedPath) expandedCount++;

                    entries.Add(new LibraryEntry(fullPath, date));
                }

                _entries = entries;
                _loaded = true;
                Debugger.show($"[NEXTSONG] Loaded {_entries.Count} entries ({expandedCount} paths expanded, {failedCount} lines skipped).");
            }
            catch (Exception ex)
            {
                Debugger.show("[NEXTSONG] EnsureLoadedAsync failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Expands a compressed path back to its full form.
        /// A compressed path starts with a 5-digit numeric ID followed by '/'.
        /// </summary>
        private static string ExpandPath(string storedPath, Dictionary<string, string> idToSegment)
        {
            if (idToSegment.Count == 0) return storedPath;

            int firstSlash = storedPath.IndexOf('/');
            if (firstSlash < 1) return storedPath;

            var candidate = storedPath.Substring(0, firstSlash);
            if (idToSegment.TryGetValue(candidate, out var segment))
                return segment + storedPath.Substring(firstSlash + 1);

            return storedPath;
        }

        // Forces a reload on the next access (e.g. after a rescan).
        public void InvalidateCache()
        {
            _loaded = false;
            _entries = new();
        }

        // ── Internal record ───────────────────────────────────────────────────

        private record LibraryEntry(string RemotePath, DateTime DateModified);
    }
}