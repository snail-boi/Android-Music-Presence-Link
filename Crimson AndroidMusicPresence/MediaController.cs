using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;

namespace musicpresense
{
    internal class MediaController
    {
        private MediaPlayer? mediaPlayer;
        private SystemMediaTransportControls? smtcControls;
        private SystemMediaTransportControlsDisplayUpdater? smtcDisplayUpdater;
        private readonly Dispatcher dispatcher;
        private readonly Func<string> getCurrentDevice;
        private readonly Func<Task> updateCurrentSongCallback;
        private string? lastSMTCTitle;
        private string? lastTimelineTrackKey;
        private long? lastAdbPositionMs;
        private long realPositionMs;
        private TimeSpan? lastTrackDuration;

        private CoverCacheManager cacheManager;
        private List<string> remoteRoots = new();
        private string deviceName = string.Empty;

        public MediaController(Dispatcher dispatcher, Func<string> getCurrentDevice, Func<Task> updateCurrentSongCallback, MusicConfig config)
        {
            this.dispatcher = dispatcher;
            this.getCurrentDevice = getCurrentDevice;
            this.updateCurrentSongCallback = updateCurrentSongCallback;

            cacheManager = new CoverCacheManager(config.Paths.FfmpegPath, config.Paths.CoverCachePath, config.CachClearInMB, config.CoverArtFileNamePatterns);
            remoteRoots = GetNormalizedRemoteRoots(config);
            deviceName = config.SelectedDeviceName?.Trim() ?? string.Empty;
        }

        public void UpdateConfig(MusicConfig config)
        {
            try
            {
                cacheManager = new CoverCacheManager(config.Paths.FfmpegPath, config.Paths.CoverCachePath, config.CachClearInMB, config.CoverArtFileNamePatterns);
                remoteRoots = GetNormalizedRemoteRoots(config);
                deviceName = config.SelectedDeviceName?.Trim() ?? string.Empty;
                Debugger.show("MediaController configuration updated. RemoteRoots='" + string.Join(";", remoteRoots) + "'");
            }
            catch (Exception ex)
            {
                Debugger.show("MediaController.UpdateConfig failed: " + ex.Message);
            }
        }

        private static List<string> GetNormalizedRemoteRoots(MusicConfig config)
        {
            var roots = (config.MusicRemoteRoots ?? new List<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (roots.Count == 0 && !string.IsNullOrWhiteSpace(config.MusicRemoteRoot))
            {
                roots.Add(config.MusicRemoteRoot.Trim());
            }

            return roots;
        }

        public void Initialize()
        {
            try
            {
                mediaPlayer = new MediaPlayer();
                smtcControls = mediaPlayer.SystemMediaTransportControls;
                smtcDisplayUpdater = smtcControls.DisplayUpdater;

                smtcControls.IsEnabled = true;
                smtcControls.IsPlayEnabled = true;
                smtcControls.IsPauseEnabled = true;
                smtcControls.IsNextEnabled = true;
                smtcControls.IsPreviousEnabled = true;

                smtcControls.ButtonPressed += SmTc_ButtonPressed;
                smtcDisplayUpdater.Type = MediaPlaybackType.Music;
            }
            catch (Exception ex)
            {
                Debugger.show($"MediaPlayer initialization failed: {ex.Message}");
            }
        }

        private void SmTc_ButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
        {
            _ = dispatcher.InvokeAsync(() => HandleSmTcButtonAsync(args.Button)).Task;
        }

        private async Task HandleSmTcButtonAsync(SystemMediaTransportControlsButton button)
        {
            try
            {
                switch (button)
                {
                    case SystemMediaTransportControlsButton.Play:
                        await PlayTrack().ConfigureAwait(false);
                        break;
                    case SystemMediaTransportControlsButton.Pause:
                        await PauseTrack().ConfigureAwait(false);
                        break;
                    case SystemMediaTransportControlsButton.Next:
                        await NextTrack().ConfigureAwait(false);
                        break;
                    case SystemMediaTransportControlsButton.Previous:
                        await PreviousTrack().ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception ex)
            {
                Debugger.show($"SMTC handler error: {ex.Message}");
            }
        }

        private async Task PlayTrack()
        {
            try
            {
                var device = getCurrentDevice();
                if (string.IsNullOrEmpty(device)) return;
                await AdbHelper.RunAdbAsync($"-s {device} shell input keyevent 85").ConfigureAwait(false);
                if (smtcControls != null)
                    smtcControls.PlaybackStatus = MediaPlaybackStatus.Playing;
                Debugger.show("Play requested");
            }
            catch (Exception ex)
            {
                Debugger.show($"PlayTrack failed: {ex.Message}");
            }
        }

        private async Task PauseTrack()
        {
            try
            {
                var device = getCurrentDevice();
                if (string.IsNullOrEmpty(device)) return;
                await AdbHelper.RunAdbAsync($"-s {device} shell input keyevent 85").ConfigureAwait(false);
                if (smtcControls != null)
                    smtcControls.PlaybackStatus = MediaPlaybackStatus.Paused;
                Debugger.show("Pause requested.");
            }
            catch (Exception ex)
            {
                Debugger.show($"PauseTrack failed: {ex.Message}");
            }
        }

        private async Task NextTrack()
        {
            try
            {
                var device = getCurrentDevice();
                if (string.IsNullOrEmpty(device)) return;
                await AdbHelper.RunAdbAsync($"-s {device} shell input keyevent 87").ConfigureAwait(false);
                await Task.Delay(500).ConfigureAwait(false);
                if (updateCurrentSongCallback != null)
                {
                    try { await updateCurrentSongCallback().ConfigureAwait(false); } catch (Exception ex) { Debugger.show($"updateCurrentSongCallback failed: {ex.Message}"); }
                }
                Debugger.show("Next track requested.");
            }
            catch (Exception ex)
            {
                Debugger.show($"NextTrack failed: {ex.Message}");
            }
        }

        private async Task PreviousTrack()
        {
            try
            {
                var device = getCurrentDevice();
                if (string.IsNullOrEmpty(device)) return;
                await AdbHelper.RunAdbAsync($"-s {device} shell input keyevent 88").ConfigureAwait(false);
                await Task.Delay(500).ConfigureAwait(false);
                if (updateCurrentSongCallback != null)
                {
                    try { await updateCurrentSongCallback().ConfigureAwait(false); } catch (Exception ex) { Debugger.show($"updateCurrentSongCallback failed: {ex.Message}"); }
                }
                Debugger.show("Previous track requested.");
            }
            catch (Exception ex)
            {
                Debugger.show($"PreviousTrack failed: {ex.Message}");
            }
        }

        public bool IsPaused { get; private set; }

        public async Task UpdateMediaControlsAsync(string title, string artist, string album, bool isPlaying, bool enableCoverSearch, long adbPositionMs, TimeSpan updateCycleTime)
        {
            try
            {
                IsPaused = !isPlaying;
                if (smtcControls != null)
                    smtcControls.PlaybackStatus = isPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;

                var trackKey = $"{title}\n{artist}\n{album}";
                if (!string.Equals(lastTimelineTrackKey, trackKey, StringComparison.Ordinal))
                {
                    lastTimelineTrackKey = trackKey;
                    lastAdbPositionMs = null;
                    realPositionMs = 0;
                    lastTrackDuration = null;
                }

                bool metadataChanged = !string.Equals(lastSMTCTitle, trackKey, StringComparison.OrdinalIgnoreCase);
                lastSMTCTitle = trackKey;

                TimeSpan? duration = lastTrackDuration;
                CoverCacheManager.MediaMetadata? meta = null;

                if (metadataChanged)
                {
                    if (enableCoverSearch)
                    {
                        var result = await SetSMTCImageAsync(title, artist).ConfigureAwait(false);
                        duration = result.Duration ?? duration;
                        meta = result.Metadata;
                    }
                    else
                    {
                        Debugger.show("Cover art search disabled for current app.");
                        await SetDefaultImage().ConfigureAwait(false);
                    }
                }

                if (meta != null)
                {
                    if (!string.IsNullOrWhiteSpace(meta.Title)) title = meta.Title;
                    if (!string.IsNullOrWhiteSpace(meta.Artist)) artist = meta.Artist;
                    if (!string.IsNullOrWhiteSpace(meta.Album)) album = meta.Album;
                }

                if (duration.HasValue && duration.Value > TimeSpan.Zero)
                    lastTrackDuration = duration;

                long cycleMs = Math.Max(0, (long)updateCycleTime.TotalMilliseconds);
                if (adbPositionMs >= 0)
                {
                    if (!lastAdbPositionMs.HasValue || adbPositionMs != lastAdbPositionMs.Value)
                    {
                        realPositionMs = adbPositionMs + cycleMs;
                        lastAdbPositionMs = adbPositionMs;
                    }
                    else if (isPlaying)
                    {
                        realPositionMs += cycleMs;
                    }
                }

                if (realPositionMs < 0)
                    realPositionMs = 0;

                if (lastTrackDuration.HasValue)
                {
                    var durationMs = (long)lastTrackDuration.Value.TotalMilliseconds;
                    if (durationMs > 0 && realPositionMs >= durationMs)
                    {
                        realPositionMs = 0;
                    }
                }

                var currentPosition = TimeSpan.FromMilliseconds(Math.Max(0, realPositionMs));

                if (mediaPlayer == null || smtcDisplayUpdater == null)
                    return;

                await dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        if (metadataChanged)
                        {
                            var musicProperties = smtcDisplayUpdater.MusicProperties;
                            musicProperties.Title = title;
                            musicProperties.Artist = artist;
                            musicProperties.AlbumTitle = album;

                            smtcDisplayUpdater.Update();
                        }

                        if (lastTrackDuration.HasValue && smtcControls != null)
                        {
                            var trackDuration = lastTrackDuration.Value;
                            var timelineProperties = new SystemMediaTransportControlsTimelineProperties
                            {
                                StartTime = TimeSpan.Zero,
                                MinSeekTime = TimeSpan.Zero,
                                Position = currentPosition,
                                MaxSeekTime = trackDuration,
                                EndTime = trackDuration
                            };

                            smtcControls.UpdateTimelineProperties(timelineProperties);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debugger.show($"Failed updating SMTC metadata/timeline on UI thread: {ex.Message}");
                    }
                }).Task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debugger.show($"UpdateMediaControlsAsync failed: {ex.Message}");
            }
        }

        public void Clear()
        {
            try
            {
                if (smtcDisplayUpdater != null)
                {
                    smtcDisplayUpdater.ClearAll();
                    smtcDisplayUpdater.Update();
                }

                if (mediaPlayer != null)
                {
                    mediaPlayer.Pause();
                    mediaPlayer.Dispose();
                    mediaPlayer = null;
                }

                smtcControls = null;
                smtcDisplayUpdater = null;
                lastSMTCTitle = null;
                lastTimelineTrackKey = null;
                lastAdbPositionMs = null;
                realPositionMs = 0;
                lastTrackDuration = null;
            }
            catch (Exception ex)
            {
                Debugger.show($"Failed to clear media controls: {ex.Message}");
            }
        }

        /// <summary>
        /// Normalizes a string for cross-comparison between media metadata and filenames on disk.
        /// Many players report titles with characters that the filesystem cannot store, so saved
        /// files use a substitute (or are stripped). We map all of those to a single space and
        /// collapse runs, so a Contains() check works regardless of which side did the rewrite.
        /// Also folds Unicode slash variants down to '/' first so the rest of the rules apply.
        /// </summary>
        private static string NormalizeForMatch(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            // Fold Unicode slash variants to ASCII '/' so the next pass treats them uniformly.
            var folded = input
                .Replace('\u2215', '/')   // ∕  DIVISION SLASH
                .Replace('\u2044', '/')   // ⁄  FRACTION SLASH
                .Replace('\u29F8', '/')   // ⧸  BIG SOLIDUS
                .Replace('\uFF0F', '/')   // /  FULLWIDTH SOLIDUS
                .Replace('\u29F9', '\\')  // ⧹  BIG REVERSE SOLIDUS
                .Replace('\uFF3C', '\\')  // \  FULLWIDTH REVERSE SOLIDUS
                .Replace('\u201C', '"')   // "  LEFT DOUBLE QUOTATION MARK
                .Replace('\u201D', '"')   // "  RIGHT DOUBLE QUOTATION MARK
                .Replace('\uFF02', '"')   // "  FULLWIDTH QUOTATION MARK
                .Replace('\u2018', '\'')  // '  LEFT SINGLE QUOTATION MARK
                .Replace('\u2019', '\'')  // '  RIGHT SINGLE QUOTATION MARK
                .Replace('\uFF07', '\'')  // '  FULLWIDTH APOSTROPHE
                .Replace("\u2026", "...")  // …  HORIZONTAL ELLIPSIS -> three dots
                .Replace('\uFF1A', ':')   // :  FULLWIDTH COLON
                .Replace('\uFF5C', '|')   // |  FULLWIDTH VERTICAL LINE
                .Replace('\uFF1F', '?')   // ?  FULLWIDTH QUESTION MARK
                .Replace('\uFF0A', '*')   // *  FULLWIDTH ASTERISK
                .Replace('\uFF1C', '<')   // <  FULLWIDTH LESS-THAN SIGN
                .Replace('\uFF1E', '>');  // >  FULLWIDTH GREATER-THAN SIGN

            // Replace every char Android/Windows filesystems can't store, plus stray whitespace,
            // with a single space. We then collapse runs and trim, so "A: B" and "A  B" both
            // normalize to "A B" and a Contains() check survives the substitution either side did.
            var sb = new System.Text.StringBuilder(folded.Length);
            foreach (var ch in folded)
            {
                bool unsafeChar = ch == '/' || ch == '\\' || ch == ':' || ch == '*' ||
                                  ch == '?' || ch == '"' || ch == '<' || ch == '>' || ch == '|';
                if (unsafeChar || char.IsWhiteSpace(ch) || char.IsControl(ch))
                    sb.Append(' ');
                else
                    sb.Append(ch);
            }

            // Collapse multiple spaces into one.
            var collapsed = new System.Text.StringBuilder(sb.Length);
            bool prevSpace = false;
            foreach (var ch in sb.ToString())
            {
                if (ch == ' ')
                {
                    if (!prevSpace) collapsed.Append(' ');
                    prevSpace = true;
                }
                else
                {
                    collapsed.Append(ch);
                    prevSpace = false;
                }
            }

            return collapsed.ToString().Trim();
        }

        /// <summary>
        /// Returns the size in bytes of a remote file via `stat -c %s`. Returns -1 on failure.
        /// </summary>
        private static async Task<long> GetRemoteFileSizeAsync(string device, string remotePath)
        {
            try
            {
                var escaped = remotePath.Replace("\"", "\\\"");
                var output = await AdbHelper.RunAdbCaptureAsync(
                    $"-s {device} shell stat -c %s \"{escaped}\"").ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(output)) return -1;
                var trimmed = output.Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                return long.TryParse(trimmed, out var size) ? size : -1;
            }
            catch (Exception ex)
            {
                Debugger.show($"GetRemoteFileSizeAsync failed for {remotePath}: {ex.Message}");
                return -1;
            }
        }

        private async Task<(TimeSpan? Duration, CoverCacheManager.MediaMetadata? Metadata)> SetSMTCImageAsync(string fileNameWithoutExtension, string artist)
        {
            if (mediaPlayer == null || smtcDisplayUpdater == null)
            {
                Initialize();
                if (mediaPlayer == null || smtcDisplayUpdater == null)
                {
                    Debugger.show("Failed to initialize media player");
                    return (null, null);
                }
            }

            var localRemoteRoots = remoteRoots.ToList();
            string[] audioExtensions = { ".mp3", ".flac", ".wav", ".m4a", ".ogg", ".opus" };

            try
            {
                Debugger.show($"Starting cover art search for: '{fileNameWithoutExtension}' by '{artist}'");

                var device = getCurrentDevice();
                if (string.IsNullOrEmpty(device))
                {
                    Debugger.show("No device selected for cover lookup");
                    await SetDefaultImage().ConfigureAwait(false);
                    return (null, null);
                }

                if (localRemoteRoots.Count == 0)
                {
                    Debugger.show("No remote roots configured for cover lookup");
                    await SetDefaultImage().ConfigureAwait(false);
                    return (null, null);
                }

                Debugger.show($"Searching in remote roots: {string.Join("; ", localRemoteRoots)}");

                var allFiles = new List<string>();
                foreach (var root in localRemoteRoots)
                {
                    var escapedRoot = root.Replace("\"", "\\\"");
                    Debugger.show($"Scanning root: {root}");
                    var findOutput = await AdbHelper.RunAdbCaptureAsync($"-s {device} shell find \"{escapedRoot}\" -type f");
                    if (string.IsNullOrWhiteSpace(findOutput))
                    {
                        Debugger.show($"  No files found in this root");
                        continue;
                    }

                    var lines = findOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    Debugger.show($"  Found {lines.Length} files in this root");
                    foreach (var raw in lines)
                    {
                        var path = raw.Trim();
                        if (string.IsNullOrEmpty(path) || !path.StartsWith("/"))
                            continue;

                        allFiles.Add(path);
                    }
                }

                Debugger.show($"Total files found: {allFiles.Count}");

                if (allFiles.Count == 0)
                {
                    Debugger.show("No files found in configured remote roots");
                    await SetDefaultImage().ConfigureAwait(false);
                    return (null, null);
                }

                // --- New simple matching & scoring ---
                // Eligibility: filename Contains(title, IgnoreCase).
                // Scoring:
                //   extension bonus: .wav +150, .flac +100, .opus +80, .m4a +60, .ogg +40, other audio +20
                //   artist contains in filename: +10
                //   in subfolder (deeper than its remote root): +100
                // Tiebreakers: largest file size, then first occurrence.
                var titleStr = fileNameWithoutExtension ?? string.Empty;

                int ExtensionScore(string ext) => ext switch
                {
                    ".wav" => 150,
                    ".flac" => 100,
                    ".opus" => 80,
                    ".m4a" => 60,
                    ".ogg" => 40,
                    _ => 20, // any other audio file present in candidates
                };

                // Determine the depth of each remote root so we know when a file is "in a subfolder".
                var rootDepths = localRemoteRoots
                    .Select(r => r.TrimEnd('/'))
                    .ToDictionary(r => r, r => r.Count(c => c == '/'), StringComparer.OrdinalIgnoreCase);

                int DepthOfRootFor(string filePath)
                {
                    string? bestRoot = null;
                    foreach (var root in rootDepths.Keys)
                    {
                        if (filePath.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase) ||
                            filePath.Equals(root, StringComparison.OrdinalIgnoreCase))
                        {
                            if (bestRoot == null || root.Length > bestRoot.Length)
                                bestRoot = root;
                        }
                    }
                    return bestRoot != null ? rootDepths[bestRoot] : -1;
                }

                var candidates = allFiles
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(p => audioExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()))
                    .ToList();

                // Eligibility: filename (without extension) must contain the title (case-insensitive).
                // Both sides are normalized so unicode slash variants and FS-unsafe chars (which
                // get replaced/stripped at save time) don't break the match. If we have no title at
                // all we cannot match anything meaningfully.
                string normTitle = NormalizeForMatch(titleStr);
                string normArtist = NormalizeForMatch(artist ?? string.Empty);

                var matched = string.IsNullOrWhiteSpace(normTitle)
                    ? new List<string>()
                    : candidates
                        .Where(p => NormalizeForMatch(Path.GetFileNameWithoutExtension(p))
                            .IndexOf(normTitle, StringComparison.OrdinalIgnoreCase) >= 0)
                        .ToList();

                if (matched.Count == 0)
                {
                    Debugger.show($"No filename contains the title '{titleStr}' (normalized: '{normTitle}')");
                    await SetDefaultImage().ConfigureAwait(false);
                    return (null, null);
                }

                var ranked = matched
                    .Select((p, idx) =>
                    {
                        var ext = Path.GetExtension(p).ToLowerInvariant();
                        int score = 0;
                        var bd = new System.Text.StringBuilder();

                        int extScore = ExtensionScore(ext);
                        score += extScore;
                        bd.Append($"+{extScore}({ext}) ");

                        if (!string.IsNullOrEmpty(normArtist) &&
                            NormalizeForMatch(Path.GetFileName(p)).IndexOf(normArtist, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            score += 10;
                            bd.Append("+10(artist) ");
                        }

                        int rootDepth = DepthOfRootFor(p);
                        int fileDepth = p.Count(c => c == '/');
                        bool inSubfolder = rootDepth >= 0 && fileDepth > rootDepth + 1;
                        if (inSubfolder)
                        {
                            score += 100;
                            bd.Append("+100(subfolder) ");
                        }

                        return (path: p, score, breakdown: bd.ToString(), originalIndex: idx);
                    })
                    .ToList();

                // Resolve ties using file size (larger wins). We only call `stat` for paths that
                // share the highest score, so we don't pay the cost when there's a clear winner.
                int topScore = ranked.Max(r => r.score);
                var topGroup = ranked.Where(r => r.score == topScore).ToList();

                List<(string path, int score, string breakdown, int originalIndex, long size)> sized;
                if (topGroup.Count > 1)
                {
                    Debugger.show($"Tie at score {topScore} between {topGroup.Count} files, comparing sizes");
                    var sizeTasks = topGroup.Select(async r =>
                    {
                        long sz = await GetRemoteFileSizeAsync(device, r.path).ConfigureAwait(false);
                        return (r.path, r.score, r.breakdown, r.originalIndex, size: sz);
                    });
                    sized = (await Task.WhenAll(sizeTasks).ConfigureAwait(false)).ToList();
                }
                else
                {
                    sized = topGroup.Select(r => (r.path, r.score, r.breakdown, r.originalIndex, size: -1L)).ToList();
                }

                var ordered = sized
                    .OrderByDescending(x => x.size)       // largest first
                    .ThenBy(x => x.originalIndex)         // then first occurrence
                    .ToList();

                // Build the final processing list: top-group ordered, then the rest by raw score
                // as backup candidates (so cover lookup can keep trying if the winner has no art).
                var rest = ranked
                    .Where(r => r.score < topScore)
                    .OrderByDescending(r => r.score)
                    .ThenBy(r => r.originalIndex)
                    .Select(r => r.path);

                var filesToProcess = ordered.Select(o => o.path).Concat(rest).Take(20).ToList();

                Debugger.show($"Files to process for cover art lookup (ranked): {filesToProcess.Count}");
                for (int i = 0; i < ranked.Count; i++)
                {
                    var r = ranked[i];
                    string fileName = Path.GetFileName(r.path);
                    Debugger.show($"  [{i + 1}] Score={r.score:D3} | {r.breakdown}| {fileName}");
                }
                if (ordered.Count > 1)
                {
                    Debugger.show("Tie-broken order:");
                    foreach (var o in ordered)
                        Debugger.show($"  size={o.size} | {Path.GetFileName(o.path)}");
                }

                TimeSpan? duration = null;
                CoverCacheManager.MediaMetadata? metadata = null;

                foreach (var remotePath in filesToProcess)
                {
                    Debugger.show($"Processing remote file for cover art: {remotePath}");

                    var result = await cacheManager.GetImagePathForNowPlayingAsync(device, remotePath, deviceName).ConfigureAwait(false);
                    string imagePath = result.ImagePath;
                    double? durSeconds = result.DurationSeconds;
                    metadata = result.Metadata ?? metadata;
                    if (durSeconds.HasValue && durSeconds.Value > 0)
                        duration = TimeSpan.FromSeconds(durSeconds.Value);

                    if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
                    {


                        Debugger.show($"Found cached image at {imagePath}, setting SMTC thumbnail");
                        var imageFile = await StorageFile.GetFileFromPathAsync(imagePath).AsTask().ConfigureAwait(false);

                        await dispatcher.InvokeAsync(() =>
                        {
                            try
                            {
                                smtcDisplayUpdater.Thumbnail = RandomAccessStreamReference.CreateFromFile(imageFile);
                                smtcDisplayUpdater.Update();
                                Debugger.show($"Thumbnail set from cached file: {imagePath}");
                            }
                            catch (Exception ex)
                            {
                                Debugger.show($"Failed to set thumbnail on dispatcher: {ex.Message}");
                            }
                        }).Task.ConfigureAwait(false);

                        return (duration, metadata);
                    }
                    else
                    {
                        Debugger.show($"No image returned for {remotePath}, continuing to next candidate");
                    }
                }

                Debugger.show("No cover art found for any candidates; using default image");
                await SetDefaultImage().ConfigureAwait(false);
                return (duration, metadata);
            }
            catch (Exception ex)
            {
                Debugger.show($"Critical error in SetSMTCImageAsync: {ex.Message}");
                await SetDefaultImage().ConfigureAwait(false);
                return (null, null);
            }
        }

        private async Task SetDefaultImage()
        {
            try
            {
                string defaultImagePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Snail", "Resources", "Musiclogo.png"
                );

                Debugger.show($"Setting default image from: {defaultImagePath}");

                var imageFile = await StorageFile.GetFileFromPathAsync(defaultImagePath).AsTask().ConfigureAwait(false);
                await dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        smtcDisplayUpdater!.Thumbnail = RandomAccessStreamReference.CreateFromFile(imageFile);
                        smtcDisplayUpdater.Update();
                        Debugger.show("Default thumbnail set successfully");
                    }
                    catch (Exception ex)
                    {
                        Debugger.show($"Failed to set default thumbnail on dispatcher: {ex.Message}");
                    }
                }).Task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debugger.show($"Failed to set default image: {ex.Message}");
            }
        }
    }
}