using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
        private string remoteRoot;

        public MediaController(Dispatcher dispatcher, Func<string> getCurrentDevice, Func<Task> updateCurrentSongCallback, MusicConfig config)
        {
            this.dispatcher = dispatcher;
            this.getCurrentDevice = getCurrentDevice;
            this.updateCurrentSongCallback = updateCurrentSongCallback;

            cacheManager = new CoverCacheManager(config.Paths.FfmpegPath, config.Paths.CoverCachePath, config.CachClearInMB);
            remoteRoot = config.MusicRemoteRoot ?? string.Empty;
        }

        public void UpdateConfig(MusicConfig config)
        {
            try
            {
                cacheManager = new CoverCacheManager(config.Paths.FfmpegPath, config.Paths.CoverCachePath, config.CachClearInMB);
                remoteRoot = config.MusicRemoteRoot ?? string.Empty;
                Debugger.show("MediaController configuration updated. RemoteRoot='" + remoteRoot + "'");
            }
            catch (Exception ex)
            {
                Debugger.show("MediaController.UpdateConfig failed: " + ex.Message);
            }
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

        private IEnumerable<string> TokenizeForMatch(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return Array.Empty<string>();
            s = NormalizeSlashVariants(s);
            var matches = Regex.Matches(s.ToLowerInvariant(), @"[\p{L}\p{N}]{3,}");
            return matches.Select(m => m.Value).Distinct();
        }

        private static string NormalizeSlashVariants(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            return input.Replace('\u2215', '/')
                        .Replace('\u2044', '/')
                        .Replace('\uFF0F', '/');
        }

        private bool TokensMatchEnough(IEnumerable<string> titleTokens, IEnumerable<string> fileTokens)
        {
            var t = titleTokens.ToList();
            var f = new HashSet<string>(fileTokens);
            if (t.Count == 0) return false;
            int match = t.Count(tok => f.Contains(tok));
            return match >= Math.Max(1, t.Count / 2);
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

            string localRemoteRoot = remoteRoot;
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

                var findOutput = await AdbHelper.RunAdbCaptureAsync($"-s {device} shell find \"{localRemoteRoot}\" -type f");
                if (string.IsNullOrWhiteSpace(findOutput))
                {
                    Debugger.show("No output from remote find");
                    await SetDefaultImage().ConfigureAwait(false);
                    return (null, null);
                }

                var lines = findOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var allFiles = new List<string>();
                foreach (var raw in lines)
                {
                    var path = raw.Trim();
                    if (string.IsNullOrEmpty(path)) continue;
                    allFiles.Add(path);
                }

                var titleTokens = TokenizeForMatch(fileNameWithoutExtension).ToList();
                var candidates = allFiles.Where(p => audioExtensions.Contains(Path.GetExtension(p).ToLowerInvariant())).ToList();

                var matched = new List<string>();
                foreach (var candidate in candidates)
                {
                    var fn = Path.GetFileName(candidate);
                    var nameNoExt = Path.GetFileNameWithoutExtension(fn);
                    var fileTokens = TokenizeForMatch(nameNoExt);

                    bool match = false;
                    if (titleTokens.Any())
                    {
                        match = TokensMatchEnough(titleTokens, fileTokens);
                    }
                    else
                    {
                        match = nameNoExt.IndexOf(fileNameWithoutExtension, StringComparison.OrdinalIgnoreCase) >= 0;
                    }

                    if (match) matched.Add(candidate);
                }

                if (matched.Count == 0)
                {
                    Debugger.show("No candidates found on device for title token");
                    await SetDefaultImage().ConfigureAwait(false);
                    return (null, null);
                }

                var artistMatches = new List<string>();
                if (!string.IsNullOrEmpty(artist) && matched.Count > 1)
                {
                    artistMatches = matched.Where(c => c.IndexOf(artist, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                }

                var filesToProcess = artistMatches.Count > 0 ? artistMatches : matched;

                var titleStr = fileNameWithoutExtension ?? string.Empty;
                var ranked = filesToProcess
                    .Select(p =>
                    {
                        var nameNoExt = Path.GetFileNameWithoutExtension(p);
                        int score = 0;
                        if (!string.IsNullOrEmpty(titleStr) && nameNoExt.IndexOf(titleStr, StringComparison.OrdinalIgnoreCase) >= 0)
                            score += 100;

                        var fileTokens = TokenizeForMatch(nameNoExt).ToList();
                        var tCount = titleTokens.Count;
                        if (tCount > 0)
                        {
                            int inter = fileTokens.Count(ft => titleTokens.Contains(ft));
                            score += inter * 10;
                        }

                        if (!string.IsNullOrEmpty(artist) && p.IndexOf(artist, StringComparison.OrdinalIgnoreCase) >= 0)
                            score += 50;

                        int depth = p.Count(ch => ch == '/');
                        score += Math.Min(depth, 10);

                        return (path: p, score);
                    })
                    .OrderByDescending(x => x.score)
                    .ThenByDescending(x => x.path.Count(ch => ch == '/'))
                    .ToList();

                filesToProcess = ranked.Select(r => r.path).Take(20).ToList();

                Debugger.show($"Files to process for cover art lookup (ranked): {filesToProcess.Count}");

                TimeSpan? duration = null;
                CoverCacheManager.MediaMetadata? metadata = null;

                foreach (var remotePath in filesToProcess)
                {
                    Debugger.show($"Processing remote file for cover art: {remotePath}");

                    var result = await cacheManager.GetImagePathForNowPlayingAsync(device, remotePath).ConfigureAwait(false);
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
