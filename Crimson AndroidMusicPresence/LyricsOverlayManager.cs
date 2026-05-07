using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace musicpresense
{
    internal sealed class LyricsOverlayManager : IDisposable
    {
        private const int LyricsLeadMs = 500;
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _timer;
        private MusicConfig _config;
        private readonly Func<string> _getCurrentDevice;
        private LyricsOverlayWindow? _overlay;

        private bool _overlayVisible = false;
        private bool _isPlaying;
        private long _basePositionMs;
        private DateTime _positionAnchorUtc = DateTime.UtcNow;
        private long? _lastReportedPositionMs;

        private string? _currentArtist;
        private string? _currentTitle;
        private string? _currentAlbum;

        private string? _loadedTrackKey;
        private List<LyricsLine> _lines = new();
        // True when at least one line in _lines came from a timestamped LRC entry.
        // False for plain-text lyrics files (no [mm:ss] markers anywhere).
        private bool _linesAreTimed;

        private readonly string _lyricsCachePath;
        private readonly Dictionary<string, LyricsCacheEntry?> _trackLyricsPathCache = new(StringComparer.OrdinalIgnoreCase);

        public LyricsOverlayManager(Dispatcher dispatcher, MusicConfig config, Func<string> getCurrentDevice)
        {
            _dispatcher = dispatcher;
            _config = config;
            _getCurrentDevice = getCurrentDevice;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += Timer_Tick;

            _lyricsCachePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Snail",
                "LyricsCache");

            try
            {
                Directory.CreateDirectory(_lyricsCachePath);
            }
            catch
            {
            }
        }

        public void UpdateConfig(MusicConfig config)
        {
            _config = config;
            _trackLyricsPathCache.Clear();
        }

        public void ToggleVisibility()
        {
            _overlayVisible = !_overlayVisible;

            Debugger.show(_overlayVisible ? "[LYRICS] Showing lyrics overlay." : "[LYRICS] Hiding lyrics overlay.");

            if (!_overlayVisible)
            {
                _dispatcher.BeginInvoke(() => _overlay?.Hide());
                return;
            }

            _dispatcher.BeginInvoke(() =>
            {
                EnsureOverlay();
                if (_lines.Count > 0)
                {
                    Debugger.show("[LYRICS] Displaying current lyric line.");
                    _overlay?.ShowLine(GetCurrentLineText(), true);
                }
            });
        }

        public void OnPlaybackChanged(string? artist, string? title, string? album, bool isPlaying, long positionMs)
        {
            _dispatcher.BeginInvoke(async () =>
            {
                _currentArtist = artist;
                _currentTitle = title;
                _currentAlbum = album;
                _isPlaying = isPlaying;

                var now = DateTime.UtcNow;
                var reported = Math.Max(0, positionMs);
                if (_isPlaying)
                {
                    if (_lastReportedPositionMs.HasValue && reported == _lastReportedPositionMs.Value)
                    {
                        var elapsedSinceLastReport = now - _positionAnchorUtc;
                        _basePositionMs += Math.Max(0, (long)elapsedSinceLastReport.TotalMilliseconds);
                    }
                    else
                    {
                        _basePositionMs = reported;
                    }
                }
                else
                {
                    _basePositionMs = reported;
                }

                _lastReportedPositionMs = reported;
                _positionAnchorUtc = now;

                var trackKey = BuildTrackKey(_currentArtist, _currentTitle, _currentAlbum);

                if (string.IsNullOrWhiteSpace(_currentTitle) || string.IsNullOrWhiteSpace(_currentArtist))
                {
                    _loadedTrackKey = null;
                    bool hadLines = _lines.Count > 0;
                    _lines.Clear();
                    _linesAreTimed = false;
                    _lastReportedPositionMs = null;
                    _timer.Stop();
                    _overlay?.Hide();
                    if (hadLines) RaiseLinesChanged();
                    return;
                }

                if (!string.Equals(_loadedTrackKey, trackKey, StringComparison.Ordinal))
                {
                    _loadedTrackKey = trackKey;
                    var loaded = await LoadLyricsForCurrentTrackAsync().ConfigureAwait(true);
                    _lines = loaded.lines;
                    _linesAreTimed = loaded.isTimed;
                    RaiseLinesChanged();
                }

                if (_lines.Count == 0 || !_overlayVisible)
                {
                    _overlay?.Hide();
                    _timer.Stop();
                    return;
                }

                EnsureOverlay();
                Debugger.show("[LYRICS] Updating lyric line display.");
                _overlay?.ShowLine(GetCurrentLineText(), true);

                if (!_timer.IsEnabled)
                    _timer.Start();
            });
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_lines.Count == 0 || !_overlayVisible)
            {
                _overlay?.Hide();
                _timer.Stop();
                return;
            }

            EnsureOverlay();
            _overlay?.ShowLine(GetCurrentLineText(), true);
        }

        private string GetCurrentLineText()
        {
            var posMs = _basePositionMs;
            if (_isPlaying)
            {
                var elapsed = DateTime.UtcNow - _positionAnchorUtc;
                posMs += Math.Max(0, (long)elapsed.TotalMilliseconds);
            }

            posMs += LyricsLeadMs;

            var position = TimeSpan.FromMilliseconds(Math.Max(0, posMs));

            var idx = -1;
            for (var i = 0; i < _lines.Count; i++)
            {
                if (_lines[i].Time <= position)
                    idx = i;
                else
                    break;
            }

            if (idx < 0)
                return _lines[0].Text;

            return _lines[idx].Text;
        }

        private async Task<(List<LyricsLine> lines, bool isTimed)> LoadLyricsForCurrentTrackAsync()
        {
            var artist = _currentArtist ?? string.Empty;
            var title = _currentTitle ?? string.Empty;
            var album = _currentAlbum ?? string.Empty;
            var key = BuildTrackKey(artist, title, album);

            if (_trackLyricsPathCache.TryGetValue(key, out var cachedEntry))
            {
                if (cachedEntry?.RemotePath == null)
                {
                    if (!string.IsNullOrWhiteSpace(cachedEntry?.LocalPath) && File.Exists(cachedEntry.LocalPath))
                        return await ParseLrcFileAsync(cachedEntry.LocalPath).ConfigureAwait(true);

                    return (new List<LyricsLine>(), false);
                }

                var refreshDevice = _getCurrentDevice();
                if (string.IsNullOrWhiteSpace(refreshDevice))
                {
                    if (!string.IsNullOrWhiteSpace(cachedEntry.LocalPath) && File.Exists(cachedEntry.LocalPath))
                        return await ParseLrcFileAsync(cachedEntry.LocalPath).ConfigureAwait(true);

                    return (new List<LyricsLine>(), false);
                }

                var refreshedPath = await PullAndCacheLyricsAsync(refreshDevice, cachedEntry.RemotePath, key).ConfigureAwait(true);
                var finalPath = !string.IsNullOrWhiteSpace(refreshedPath) ? refreshedPath : cachedEntry.LocalPath;
                _trackLyricsPathCache[key] = new LyricsCacheEntry(cachedEntry.RemotePath, finalPath);

                if (!string.IsNullOrWhiteSpace(finalPath) && File.Exists(finalPath))
                    return await ParseLrcFileAsync(finalPath).ConfigureAwait(true);

                return (new List<LyricsLine>(), false);
            }

            var device = _getCurrentDevice();
            if (string.IsNullOrWhiteSpace(device))
            {
                _trackLyricsPathCache[key] = new LyricsCacheEntry(null, null);
                return (new List<LyricsLine>(), false);
            }

            var remoteRoots = GetRemoteRoots(_config);
            string? bestRemotePath = null;
            int bestScore = int.MinValue;

            foreach (var root in remoteRoots)
            {
                var escapedRoot = EscapeForShell(root);
                var output = await AdbHelper.RunAdbCaptureAsync($"-s {device} shell find \"{escapedRoot}\" -type f -name \"*.lrc\"").ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(output))
                    continue;

                var files = output
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => x.StartsWith("/"))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var remoteFile in files)
                {
                    var score = ScoreCandidate(remoteFile, artist, title, album);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestRemotePath = remoteFile;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(bestRemotePath) || bestScore < 20)
            {
                _trackLyricsPathCache[key] = new LyricsCacheEntry(null, null);
                return (new List<LyricsLine>(), false);
            }

            var localPath = await PullAndCacheLyricsAsync(device, bestRemotePath, key).ConfigureAwait(true);
            _trackLyricsPathCache[key] = new LyricsCacheEntry(bestRemotePath, localPath);

            if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
                return (new List<LyricsLine>(), false);

            return await ParseLrcFileAsync(localPath).ConfigureAwait(true);
        }

        private async Task<string?> PullAndCacheLyricsAsync(string device, string remotePath, string trackKey)
        {
            try
            {
                var cacheKey = ComputeKey(remotePath, trackKey);
                var localPath = Path.Combine(_lyricsCachePath, cacheKey + ".lrc");
                string? existingPath = null;
                if (File.Exists(localPath) && new FileInfo(localPath).Length > 0)
                    existingPath = localPath;

                Directory.CreateDirectory(_lyricsCachePath);

                var escapedRemote = remotePath.Replace("\"", "\\\"");
                var escapedLocal = localPath.Replace("\"", "\\\"");
                await AdbHelper.RunAdbAsync($"-s {device} pull \"{escapedRemote}\" \"{escapedLocal}\"").ConfigureAwait(true);

                if (File.Exists(localPath) && new FileInfo(localPath).Length > 0)
                    return localPath;

                return existingPath;
            }
            catch (Exception ex)
            {
                Debugger.show("PullAndCacheLyricsAsync failed: " + ex.Message);
            }

            return null;
        }

        private static List<string> GetRemoteRoots(MusicConfig config)
        {
            var roots = new List<string>();

            if (!string.IsNullOrWhiteSpace(config.LyricsSearchFolderOverride))
            {
                roots.Add(config.LyricsSearchFolderOverride.Trim());
            }
            else
            {
                roots.AddRange((config.MusicRemoteRoots ?? new List<string>())
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => p.Trim()));

                if (roots.Count == 0 && !string.IsNullOrWhiteSpace(config.MusicRemoteRoot))
                    roots.Add(config.MusicRemoteRoot.Trim());

                if (roots.Count == 0)
                    roots.Add("/storage/emulated/0/Music");
            }

            return roots
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string EscapeForShell(string input)
        {
            return (input ?? string.Empty).Replace("\"", "\\\"");
        }

        private static int ScoreCandidate(string remotePath, string artist, string title, string album)
        {
            var fileName = Path.GetFileNameWithoutExtension(remotePath);
            var normalizedName = Normalize(fileName);
            var normalizedArtist = Normalize(artist);
            var normalizedTitle = Normalize(title);
            var normalizedAlbum = Normalize(album);

            var score = 0;
            if (!string.IsNullOrWhiteSpace(normalizedTitle) && normalizedName.Contains(normalizedTitle, StringComparison.OrdinalIgnoreCase))
                score += 80;
            if (!string.IsNullOrWhiteSpace(normalizedArtist) && normalizedName.Contains(normalizedArtist, StringComparison.OrdinalIgnoreCase))
                score += 40;
            if (!string.IsNullOrWhiteSpace(normalizedAlbum) && normalizedName.Contains(normalizedAlbum, StringComparison.OrdinalIgnoreCase))
                score += 20;

            var exact = $"{normalizedArtist} {normalizedTitle}".Trim();
            if (!string.IsNullOrWhiteSpace(exact) && string.Equals(normalizedName, exact, StringComparison.OrdinalIgnoreCase))
                score += 30;

            return score;
        }

        private static string BuildTrackKey(string? artist, string? title, string? album)
            => $"{artist ?? string.Empty}\n{title ?? string.Empty}\n{album ?? string.Empty}";

        private static string Normalize(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            var chars = input.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
            return Regex.Replace(new string(chars), "\\s+", " ").Trim();
        }

        private static string ComputeKey(string a, string b)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(a + "|" + b);
            var hash = sha.ComputeHash(bytes);
            return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static async Task<(List<LyricsLine> lines, bool isTimed)> ParseLrcFileAsync(string path)
        {
            string text;
            try
            {
                text = await File.ReadAllTextAsync(path, Encoding.UTF8).ConfigureAwait(true);
            }
            catch
            {
                try
                {
                    text = await File.ReadAllTextAsync(path).ConfigureAwait(true);
                }
                catch
                {
                    return (new List<LyricsLine>(), false);
                }
            }

            var lines = new List<LyricsLine>();
            var regex = new Regex(@"\[(\d{1,2}):(\d{2})(?:[\.:](\d{1,3}))?\]");
            // Tag-only lines like [ar:...], [ti:...], [length:...] - skip from plain-text fallback.
            var tagOnlyRegex = new Regex(@"^\s*\[[a-zA-Z]{2,}:[^\]]*\]\s*$");

            bool anyTimestamp = false;
            var rawLines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            foreach (var rawLine in rawLines)
            {
                if (string.IsNullOrWhiteSpace(rawLine))
                    continue;

                var matches = regex.Matches(rawLine);
                if (matches.Count == 0)
                    continue;

                anyTimestamp = true;

                var lyricText = regex.Replace(rawLine, string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(lyricText))
                    lyricText = "♪";

                foreach (Match match in matches)
                {
                    if (!int.TryParse(match.Groups[1].Value, out var mm)) continue;
                    if (!int.TryParse(match.Groups[2].Value, out var ss)) continue;

                    var ms = 0;
                    if (match.Groups[3].Success)
                    {
                        var frac = match.Groups[3].Value;
                        if (frac.Length == 1) frac += "00";
                        else if (frac.Length == 2) frac += "0";
                        else if (frac.Length > 3) frac = frac[..3];
                        int.TryParse(frac, out ms);
                    }

                    lines.Add(new LyricsLine(new TimeSpan(0, 0, mm, ss, ms), lyricText));
                }
            }

            if (anyTimestamp)
            {
                return (lines.OrderBy(l => l.Time).ToList(), true);
            }

            // Plain-text fallback: no usable timestamps anywhere in the file.
            // Return one entry per non-empty source line, with TimeSpan.Zero, IsTimed=false.
            var plain = new List<LyricsLine>();
            foreach (var rawLine in rawLines)
            {
                var trimmed = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    // Preserve paragraph breaks as empty entries so the renderer can render gaps.
                    if (plain.Count > 0 && plain[^1].Text.Length > 0)
                        plain.Add(new LyricsLine(TimeSpan.Zero, string.Empty));
                    continue;
                }

                if (tagOnlyRegex.IsMatch(trimmed))
                    continue;

                plain.Add(new LyricsLine(TimeSpan.Zero, trimmed));
            }

            // Trim trailing empty separator
            while (plain.Count > 0 && plain[^1].Text.Length == 0)
                plain.RemoveAt(plain.Count - 1);

            return (plain, false);
        }

        private void EnsureOverlay()
        {
            if (_overlay != null)
                return;

            _overlay = new LyricsOverlayWindow();
        }

        public void Dispose()
        {
            try
            {
                _timer.Stop();
                if (_overlay != null)
                {
                    _overlay.Close();
                    _overlay = null;
                }
            }
            catch
            {
            }
        }

        // ── Public surface for the inline media-player lyrics view ────────────

        /// <summary>
        /// Fired on the dispatcher whenever the loaded lyrics for the current track change
        /// (track switched, lyrics newly loaded, or cleared because no track is playing).
        /// </summary>
        public event Action<LyricsTrackData>? LinesChanged;

        /// <summary>
        /// Returns the lines currently loaded for the playing track, plus whether they
        /// carry usable timestamps. May be empty.
        /// </summary>
        public LyricsTrackData GetCurrentTrackData()
        {
            return new LyricsTrackData(_lines.Select(l => new LyricsLineDto(l.Time, l.Text)).ToList(), _linesAreTimed);
        }

        /// <summary>
        /// Index into the current lines list that should be highlighted right now,
        /// or -1 if there are no lines (or lyrics aren't timed).
        /// </summary>
        public int GetCurrentLineIndex()
        {
            if (_lines.Count == 0 || !_linesAreTimed)
                return -1;

            var posMs = _basePositionMs;
            if (_isPlaying)
            {
                var elapsed = DateTime.UtcNow - _positionAnchorUtc;
                posMs += Math.Max(0, (long)elapsed.TotalMilliseconds);
            }
            posMs += LyricsLeadMs;

            var position = TimeSpan.FromMilliseconds(Math.Max(0, posMs));

            var idx = -1;
            for (var i = 0; i < _lines.Count; i++)
            {
                if (_lines[i].Time <= position)
                    idx = i;
                else
                    break;
            }
            return idx < 0 ? 0 : idx;
        }

        private void RaiseLinesChanged()
        {
            try { LinesChanged?.Invoke(GetCurrentTrackData()); } catch { }
        }

        public sealed record LyricsLineDto(TimeSpan Time, string Text);

        public sealed record LyricsTrackData(IReadOnlyList<LyricsLineDto> Lines, bool IsTimed);

        private sealed record LyricsCacheEntry(string? RemotePath, string? LocalPath);

        private sealed record LyricsLine(TimeSpan Time, string Text);
    }

    internal sealed class LyricsOverlayWindow : Window
    {
        private readonly TextBlock _textBlock;

        public LyricsOverlayWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            IsHitTestVisible = false;

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(18, 10, 18, 10)
            };

            _textBlock = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 28,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 900
            };

            border.Child = _textBlock;
            Content = border;

            Loaded += (_, __) =>
            {
                PositionWindow();
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                int exStyle = GetWindowLong(hwnd, -20);
                SetWindowLong(hwnd, -20, exStyle | 0x20 | 0x80000);
            };
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

        public void ShowLine(string text, bool ensureVisible)
        {
            _textBlock.Text = text;

            if (ensureVisible && !IsVisible)
                Show();

            PositionWindow();
        }

        private void PositionWindow()
        {
            var area = SystemParameters.WorkArea;
            Width = Math.Min(area.Width * 0.9, 980);
            Height = 80;
            Left = area.Left + ((area.Width - Width) / 2);
            Top = area.Top + area.Height - Height - 90;
        }
    }
}