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
        private string? lastSmtcPushedKey;
        private bool _smtcClearedForHalf = true;
        private long? lastAdbPositionMs;
        private long realPositionMs;
        private TimeSpan? lastTrackDuration;
        private readonly string _defaultImagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Snail", "Resources", "Musiclogo.png");
        private const string SmtcAppLabel = "Android Music Presence Link";

        private CoverCacheManager cacheManager;
        private List<string> remoteRoots = new();
        private string deviceName = string.Empty;

        public string? CurrentTitle { get; private set; }
        public string? CurrentArtist { get; private set; }
        public string? CurrentAlbum { get; private set; }
        public string? CurrentCoverPath { get; private set; }

        // Android only reports the last scrub position, so realPositionMs is the
        // value we manually tick forward each poll cycle inside
        // UpdateMediaControlsAsync. That's the one we expose, NOT the raw ADB
        // value, otherwise the progress bar would freeze between scrubs.
        public long CurrentPositionMs => Math.Max(0, realPositionMs);

        public long CurrentDurationMs =>
            lastTrackDuration.HasValue ? Math.Max(0, (long)lastTrackDuration.Value.TotalMilliseconds) : 0;

        public MediaController(Dispatcher dispatcher, Func<string> getCurrentDevice, Func<Task> updateCurrentSongCallback, MusicConfig config)
        {
            this.dispatcher = dispatcher;
            this.getCurrentDevice = getCurrentDevice;
            this.updateCurrentSongCallback = updateCurrentSongCallback;

            cacheManager = new CoverCacheManager(config.Paths.FfmpegPath, config.Paths.CoverCachePath, config.CachClearInMB, config.CoverArtFileNamePatterns);
            remoteRoots = GetNormalizedRemoteRoots(config);
            deviceName = config.SelectedDeviceName?.Trim() ?? string.Empty;
        }

        public Task PauseTrackAsync() => PauseTrack();

        public Task NextTrackAsync() => NextTrack();

        public Task PreviousTrackAsync() => PreviousTrack();

        public Task SeekRelativeAsync(int seconds) => SeekRelative(seconds);

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
                mediaPlayer.CommandManager.IsEnabled = false;
                smtcControls = mediaPlayer.SystemMediaTransportControls;
                smtcDisplayUpdater = smtcControls.DisplayUpdater;

                smtcControls.IsEnabled = true;
                smtcControls.IsPlayEnabled = true;
                smtcControls.IsPauseEnabled = true;
                smtcControls.IsNextEnabled = true;
                smtcControls.IsPreviousEnabled = true;

                smtcControls.ButtonPressed += SmTc_ButtonPressed;
                smtcDisplayUpdater.Type = MediaPlaybackType.Music;

                // Clear immediately so no empty/default session is visible until
                // a Full-mode app is detected.
                smtcDisplayUpdater.ClearAll();
                smtcDisplayUpdater.Update();
                smtcControls.PlaybackStatus = MediaPlaybackStatus.Stopped;
            }
            catch (Exception ex)
            {
                Debugger.show($"MediaPlayer initialization failed: {ex.Message}");
            }
        }

        private void ApplyDefaultSmtcLabel()
        {
            if (smtcDisplayUpdater == null)
                return;

            var musicProperties = smtcDisplayUpdater.MusicProperties;
            musicProperties.Title = SmtcAppLabel;
            musicProperties.Artist = " ";
            musicProperties.AlbumTitle = " ";
            smtcDisplayUpdater.Update();
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

        // Seeks relative to the current track position. Positive seconds = forward, negative = rewind.
        // Uses the standard Android media-key seek dispatch (KEYCODE_MEDIA_FAST_FORWARD = 90,
        // KEYCODE_MEDIA_REWIND = 89). The actual seek amount is decided by the playing app;
        // most modern players step by 10-30 seconds per press, so we issue multiple presses
        // for larger requested deltas (one press per 30 seconds, rounded up).
        private async Task SeekRelative(int seconds)
        {
            try
            {
                if (seconds == 0) return;

                var device = getCurrentDevice();
                if (string.IsNullOrEmpty(device)) return;

                int keycode = seconds > 0 ? 90 : 89;
                int magnitude = Math.Abs(seconds);
                // One key press per 30s chunk, minimum one press.
                int presses = Math.Max(1, (int)Math.Ceiling(magnitude / 30.0));
                // Hard cap so a stray request can't spam ADB.
                presses = Math.Min(presses, 8);

                for (int i = 0; i < presses; i++)
                {
                    await AdbHelper.RunAdbAsync($"-s {device} shell input keyevent {keycode}").ConfigureAwait(false);
                    if (i < presses - 1)
                    {
                        await Task.Delay(60).ConfigureAwait(false);
                    }
                }

                Debugger.show($"Seek {(seconds > 0 ? "+" : "")}{seconds}s requested ({presses} keypress(es)).");
            }
            catch (Exception ex)
            {
                Debugger.show($"SeekRelative failed: {ex.Message}");
            }
        }

        public bool IsPaused { get; private set; }

        public async Task UpdateMediaControlsAsync(string title, string artist, string album, bool isPlaying, bool enableCoverSearch, bool enableSmtc, long adbPositionMs, TimeSpan updateCycleTime)
        {
            try
            {
                IsPaused = !isPlaying;
                if (enableSmtc && smtcControls != null)
                {
                    smtcControls.PlaybackStatus = isPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
                    if (_smtcClearedForHalf)
                    {
                        Debugger.show($"[SMTC] First Full tick after Half/Off — forcing re-push. lastSmtcPushedKey={lastSmtcPushedKey ?? "null"}");
                        _smtcClearedForHalf = false;
                        lastSmtcPushedKey = null;
                    }
                }
                else if (!enableSmtc && !_smtcClearedForHalf)
                {
                    Debugger.show("[SMTC] Clearing for Half/Off mode.");
                    _smtcClearedForHalf = true;
                    lastSmtcPushedKey = null;
                    if (smtcDisplayUpdater != null)
                    {
                        smtcDisplayUpdater.ClearAll();
                        smtcDisplayUpdater.Update();
                    }
                    if (smtcControls != null)
                        smtcControls.PlaybackStatus = MediaPlaybackStatus.Stopped;
                }

                var trackKey = $"{title}\n{artist}\n{album}";
                if (!string.Equals(lastTimelineTrackKey, trackKey, StringComparison.Ordinal))
                {
                    lastTimelineTrackKey = trackKey;
                    lastAdbPositionMs = null;
                    realPositionMs = 0;
                    lastTrackDuration = null;
                }

                // metadataChanged drives cover art search — uses lastSMTCTitle (never nulled by Half clear).
                bool metadataChanged = !string.Equals(lastSMTCTitle, trackKey, StringComparison.OrdinalIgnoreCase);
                lastSMTCTitle = trackKey;

                // smtcMetadataChanged drives SMTC push — uses lastSmtcPushedKey (nulled by Half clear).
                bool smtcMetadataChanged = !string.Equals(lastSmtcPushedKey, trackKey, StringComparison.OrdinalIgnoreCase);

                TimeSpan? duration = lastTrackDuration;
                CoverCacheManager.MediaMetadata? meta = null;

                if (metadataChanged || smtcMetadataChanged)
                {
                    if (metadataChanged)
                    {
                        if (enableCoverSearch && enableSmtc)
                        {
                            var result = await SetSMTCImageAsync(title, artist).ConfigureAwait(false);
                            duration = result.Duration ?? duration;
                            meta = result.Metadata;
                            CurrentCoverPath = result.ImagePath;
                        }
                        else if (enableCoverSearch && !enableSmtc)
                        {
                            // Half mode: search for cover so it's cached and ready when switching to Full,
                            // but don't push it to SMTC yet.
                            var result = await SetSMTCImageAsync(title, artist).ConfigureAwait(false);
                            duration = result.Duration ?? duration;
                            meta = result.Metadata;
                            CurrentCoverPath = result.ImagePath;
                            // Undo the thumbnail that SetSMTCImageAsync pushed.
                            await dispatcher.InvokeAsync(() =>
                            {
                                try
                                {
                                    if (smtcDisplayUpdater != null)
                                    {
                                        smtcDisplayUpdater.ClearAll();
                                        smtcDisplayUpdater.Update();
                                    }
                                }
                                catch { }
                            }).Task.ConfigureAwait(false);
                        }
                        else
                        {
                            Debugger.show("Cover art search disabled for current app.");
                        }
                    }
                    else if (smtcMetadataChanged && enableSmtc)
                    {
                        // SMTC was cleared (Half/Off -> Full): re-push cached cover without re-searching.
                        if (!string.IsNullOrWhiteSpace(CurrentCoverPath))
                            await SetCachedImage(CurrentCoverPath).ConfigureAwait(false);
                        else
                            await SetDefaultImage().ConfigureAwait(false);
                    }
                }

                if (meta != null)
                {
                    if (!string.IsNullOrWhiteSpace(meta.Title)) title = meta.Title;
                    if (!string.IsNullOrWhiteSpace(meta.Artist)) artist = meta.Artist;
                    if (!string.IsNullOrWhiteSpace(meta.Album)) album = meta.Album;
                }

                CurrentTitle = title;
                CurrentArtist = artist;
                CurrentAlbum = album;

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

                if (!enableSmtc || mediaPlayer == null || smtcDisplayUpdater == null)
                    return;

                await dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        if (smtcMetadataChanged)
                        {
                            lastSmtcPushedKey = trackKey;
                            smtcDisplayUpdater.Type = MediaPlaybackType.Music;
                            var musicProperties = smtcDisplayUpdater.MusicProperties;
                            musicProperties.Title = title ?? string.Empty;
                            musicProperties.Artist = NormalizeSmtcMetadata(artist);
                            musicProperties.AlbumTitle = NormalizeSmtcMetadata(album);

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

        private static string NormalizeSmtcMetadata(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return " ";

            var trimmed = value.Trim();
            return trimmed.Equals("null", StringComparison.OrdinalIgnoreCase) ? " " : trimmed;
        }

        public void ClearDisplay()
        {
            try
            {
                if (smtcDisplayUpdater != null)
                {
                    smtcDisplayUpdater.ClearAll();
                    smtcDisplayUpdater.Update();
                }
                if (smtcControls != null)
                    smtcControls.PlaybackStatus = MediaPlaybackStatus.Stopped;

                lastSMTCTitle = null;
                lastSmtcPushedKey = null;
                _smtcClearedForHalf = true;
                CurrentTitle = null;
                CurrentArtist = null;
                CurrentAlbum = null;
                CurrentCoverPath = _defaultImagePath;
            }
            catch (Exception ex)
            {
                Debugger.show($"Failed to clear SMTC display: {ex.Message}");
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

                    mediaPlayer.Dispose();
                    mediaPlayer = null;
                }

                smtcControls = null;
                smtcDisplayUpdater = null;
                lastSMTCTitle = null;
                lastSmtcPushedKey = null;
                lastTimelineTrackKey = null;
                _smtcClearedForHalf = true;
                lastAdbPositionMs = null;
                realPositionMs = 0;
                lastTrackDuration = null;
                CurrentTitle = null;
                CurrentArtist = null;
                CurrentAlbum = null;
                CurrentCoverPath = _defaultImagePath;
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

            // If the title contains slashes, treat them as separators and keep the longest
            // slash-delimited segment instead of replacing '/' with whitespace. This makes
            // metadata like "A/B/C" resolve to the most informative fragment rather than a
            // flattened string that can match too broadly.
            var segments = folded.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
                return string.Empty;

            string? best = null;
            foreach (var segment in segments)
            {
                // Replace every char Android/Windows filesystems can't store, plus stray whitespace,
                // with a single space. We then collapse runs and trim, so "A: B" and "A  B" both
                // normalize to "A B" and a Contains() check survives the substitution either side did.
                // Tilde variants (wave dash, fullwidth tilde, tilde operator, ASCII tilde) are also
                // treated as whitespace here. Japanese titles commonly use these as separators and
                // different players/tools save them differently (and the Shift-JIS U+301C/U+FF5E
                // conflation means the same file can exist under either code point). Folding them
                // all to space + collapsing yields a stable comparison regardless of which variant
                // (or no variant at all, if the player stripped it) ended up on disk.
                var sb = new System.Text.StringBuilder(segment.Length);
                foreach (var ch in segment)
                {
                    bool unsafeChar = ch == '\\' || ch == ':' || ch == '*' ||
                                      ch == '?' || ch == '"' || ch == '<' || ch == '>' || ch == '|';
                    bool tildeVariant = ch == '~' ||        // U+007E ASCII TILDE
                                        ch == '\u301C' ||   // 〜 WAVE DASH
                                        ch == '\uFF5E' ||   // ～ FULLWIDTH TILDE
                                        ch == '\u223C' ||   // ∼ TILDE OPERATOR
                                        ch == '\u2053';     // ⁓ SWUNG DASH
                    if (unsafeChar || tildeVariant || char.IsWhiteSpace(ch) || char.IsControl(ch))
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

                var normalized = collapsed.ToString().Trim().TrimEnd('.');
                if (best == null || normalized.Length > best.Length)
                    best = normalized;
            }

            // Also trim trailing periods. Windows filesystems silently drop them, and
            // Path.GetFileNameWithoutExtension treats the last dot as the extension delimiter.
            // A title like "Realm of a Born Sea, Colorful." gets saved as
            // "Realm of a Born Sea, Colorful..mp3" (or similar) and after extension-strip
            // becomes "Realm of a Born Sea, Colorful" (no trailing dot). Stripping trailing
            // periods in normalization keeps the match symmetric so "<title>." == "<file-no-ext>".
            return best ?? string.Empty;
        }

        /// <summary>
        /// Determines whether a title is safe to hand directly to the phone's <c>find -iname</c>
        /// glob without going through PC-side normalization. If normalizing the title is a no-op
        /// (beyond trimming), we know no unicode folding or FS-unsafe-char substitution happened,
        /// which means the filename on disk should contain the title verbatim (case-insensitive).
        /// In that case the phone can filter before sending paths over the wire, cutting bandwidth
        /// by 100x+ on typical libraries. When this returns false we fall back to the full scan
        /// plus PC-side matching, so correctness is preserved on pathological titles.
        /// </summary>
        private static bool IsTitleSafeForPhoneFilter(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return false;

            var trimmed = title.Trim();
            if (trimmed.Length == 0) return false;

            // The normalizer trims, collapses whitespace, folds unicode punctuation, and
            // replaces FS-unsafe chars. If its output equals the trimmed input, none of
            // those rewrites fired, so we can rely on byte-for-byte filename matching
            // via toybox find -iname (which is case-insensitive but otherwise literal).
            // Case-sensitive compare here: the check is "did any substitution happen", not
            // "does this match a file", so case differences are irrelevant.
            return string.Equals(NormalizeForMatch(trimmed), trimmed, StringComparison.Ordinal);
        }

        /// <summary>
        /// Escapes a string for safe inclusion inside single-quoted shell arguments on Android's
        /// toybox sh. The only char that can break single-quoting is the single quote itself,
        /// which we close out, escape literally, and re-open. Every other char (including spaces,
        /// parentheses, ampersands, dollars, backticks) is literal inside single quotes.
        /// </summary>
        private static string ShellSingleQuoteEscape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            // Split on ', rejoin with '\'' between fragments.
            return value.Replace("'", "'\\''");
        }

        /// <summary>
        /// Picks the longest whitespace-delimited fragment from the normalized title. Used when
        /// the title contains chars that would be rewritten at save time (so we can't use the
        /// title verbatim as a glob). Normalization converts FS-unsafe chars and folded unicode
        /// punctuation to spaces and collapses runs, so splitting the normalized string on spaces
        /// gives us clean ASCII-ish fragments, the longest of which is most likely to survive
        /// whatever rewriting the player applied at save time.
        /// Returns empty string only if the title normalizes to nothing at all (pathological).
        /// Uses a single fragment deliberately: multi-fragment globs like *a*b* imply order,
        /// which fails if the player saved them in a different order than the metadata reports.
        /// </summary>
        private static string PickLongestFragment(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return string.Empty;

            var normalized = NormalizeForMatch(title);
            if (string.IsNullOrWhiteSpace(normalized)) return string.Empty;

            // Normalizer already collapsed whitespace, so a single-char split is enough.
            var parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return string.Empty;

            string longest = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                if (parts[i].Length > longest.Length)
                    longest = parts[i];
            }

            return longest;
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

        private async Task<(TimeSpan? Duration, CoverCacheManager.MediaMetadata? Metadata, string? ImagePath)> SetSMTCImageAsync(string fileNameWithoutExtension, string artist)
        {
            if (mediaPlayer == null || smtcDisplayUpdater == null)
            {
                Initialize();
                if (mediaPlayer == null || smtcDisplayUpdater == null)
                {
                    Debugger.show("Failed to initialize media player");
                    return (null, null, _defaultImagePath);
                }
            }

            var localRemoteRoots = remoteRoots.ToList();
            string[] audioExtensions = { ".mp3", ".flac", ".wav", ".m4a", ".ogg", ".opus" };

            try
            {
                Debugger.show($"[COVERART] Starting cover art search for: '{fileNameWithoutExtension}' by '{artist}'");

                var device = getCurrentDevice();
                if (string.IsNullOrEmpty(device))
                {
                    Debugger.show("[COVERART] No device selected for cover lookup");
                    await SetDefaultImage().ConfigureAwait(false);
                    return (null, null, _defaultImagePath);
                }

                if (localRemoteRoots.Count == 0)
                {
                    Debugger.show("[COVERART] No remote roots configured for cover lookup");
                    await SetDefaultImage().ConfigureAwait(false);
                    return (null, null, _defaultImagePath);
                }

                Debugger.show($"[COVERART] Searching in remote roots: {string.Join("; ", localRemoteRoots)}");

                // Declared here (not in the scoring block below) because the hybrid scan
                // strategy uses it to build the phone-side glob before we ever run `find`.
                var titleStr = fileNameWithoutExtension ?? string.Empty;

                // --- Hybrid scan strategy ---
                // Tier 1 (best case, title is clean): title contains no chars that would be
                // rewritten at save time, so we ask the phone to filter via `find -iname
                // '*<title>*'`. Only matching paths cross the wire.
                //
                // Tier 2 (title has unsafe chars): we can't use the whole title as a glob
                // because we don't know exactly how it was rewritten on disk, but we can still
                // ask the phone to filter on the longest clean fragment from the normalized
                // title. That fragment is guaranteed to appear verbatim in the filename if the
                // file exists at all, so this still returns only the needle plus some false
                // positives (which PC-side scoring filters out). Any reduction from the full
                // scan is a bandwidth win, even if the fragment is short and imprecise, the
                // phone does the work and we only pay for what it sends back.
                //
                // Tier 3 (fallback): if we can't build any glob (title normalized to nothing)
                // OR the phone-side filter returned zero matches across all roots (rare
                // save-time rewrite we still couldn't predict), enumerate every file and let
                // the existing PC-side matching handle it. Correctness preserved in all cases.
                var allFiles = new List<string>();
                bool usedFastPath = false;

                // Pick the glob payload: full title if safe, else longest safe fragment.
                bool titleSafe = IsTitleSafeForPhoneFilter(titleStr);
                string globPayload = titleSafe ? titleStr.Trim() : PickLongestFragment(titleStr);

                if (!string.IsNullOrEmpty(globPayload))
                {
                    // Wrap the payload in '*...*' and single-quote the whole thing so shell
                    // metacharacters (spaces, parens, ampersands) stay literal. Single quotes
                    // are escaped via '\'' which is safe across toybox/mksh/bash on Android.
                    var escapedForGlob = ShellSingleQuoteEscape(globPayload);
                    var globArg = $"'*{escapedForGlob}*'";

                    if (titleSafe)
                        Debugger.show($"[COVERART] Fast path (full title): glob {globArg}");
                    else
                        Debugger.show($"[COVERART] Fast path (fragment of '{titleStr}'): glob {globArg}");

                    foreach (var root in localRemoteRoots)
                    {
                        var escapedRoot = ShellSingleQuoteEscape(root);
                        var findOutput = await AdbHelper.RunAdbCaptureAsync(
                            $"-s {device} shell find '{escapedRoot}' -type f -iname {globArg}");
                        if (string.IsNullOrWhiteSpace(findOutput))
                        {
                            Debugger.show($"[COVERART] Fast path: no matches in {root}");
                            continue;
                        }

                        var lines = findOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        Debugger.show($"[COVERART] Fast path: {lines.Length} matches in {root}");
                        foreach (var raw in lines)
                        {
                            var path = raw.Trim();
                            if (string.IsNullOrEmpty(path) || !path.StartsWith("/"))
                                continue;

                            allFiles.Add(path);
                        }
                    }

                    if (allFiles.Count > 0)
                    {
                        usedFastPath = true;
                        Debugger.show($"[COVERART] Fast path succeeded: {allFiles.Count} candidates (skipped full scan)");
                    }
                    else
                    {
                        Debugger.show("[COVERART] Fast path returned zero matches across all roots; falling back to full scan");
                    }
                }
                else
                {
                    Debugger.show($"[COVERART] Title '{titleStr}' yielded no usable glob fragment; using full scan");
                }

                if (!usedFastPath)
                {
                    foreach (var root in localRemoteRoots)
                    {
                        var escapedRoot = root.Replace("\"", "\\\"");
                        Debugger.show($"[COVERART] Scanning root: {root}");
                        var findOutput = await AdbHelper.RunAdbCaptureAsync($"-s {device} shell find \"{escapedRoot}\" -type f");
                        if (string.IsNullOrWhiteSpace(findOutput))
                        {
                            Debugger.show($"[COVERART] No files found in this root");
                            continue;
                        }

                        var lines = findOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        Debugger.show($"[COVERART] Found {lines.Length} files in this root");
                        foreach (var raw in lines)
                        {
                            var path = raw.Trim();
                            if (string.IsNullOrEmpty(path) || !path.StartsWith("/"))
                                continue;

                            allFiles.Add(path);
                        }
                    }
                }

                Debugger.show($"[COVERART] Total files found: {allFiles.Count} (fast path: {usedFastPath})");

                if (allFiles.Count == 0)
                {
                    Debugger.show("[COVERART] No files found in configured remote roots");
                    await SetDefaultImage().ConfigureAwait(false);
                    return (null, null, _defaultImagePath);
                }

                // --- New simple matching & scoring ---
                // Eligibility: filename Contains(title, IgnoreCase).
                // Scoring:
                //   extension bonus: .wav +150, .flac +100, .opus +80, .m4a +60, .ogg +40, other audio +20
                //   artist contains in filename: +10
                //   in subfolder (deeper than its remote root): +100
                // Tiebreakers: largest file size, then first occurrence.
                // titleStr is declared above (hoisted so the hybrid scan block can use it).

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
                    Debugger.show($"[COVERART] No filename contains the title '{titleStr}' (normalized: '{normTitle}')");
                    await SetDefaultImage().ConfigureAwait(false);
                    return (null, null, _defaultImagePath);
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
                    Debugger.show($"[COVERART] Tie at score {topScore} between {topGroup.Count} files, comparing sizes");
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

                Debugger.show($"[COVERART] Files to process for cover art lookup (ranked): {filesToProcess.Count}");
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
                    Debugger.show($"[COVERART] Processing remote file for cover art: {remotePath}");

                    var result = await cacheManager.GetImagePathForNowPlayingAsync(device, remotePath, deviceName).ConfigureAwait(false);
                    string imagePath = result.ImagePath;
                    double? durSeconds = result.DurationSeconds;
                    metadata = result.Metadata ?? metadata;
                    if (durSeconds.HasValue && durSeconds.Value > 0)
                        duration = TimeSpan.FromSeconds(durSeconds.Value);

                    if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
                    {


                        Debugger.show($"[COVERARTCACHE] Found cached image at {imagePath}, setting SMTC thumbnail");
                        var imageFile = await StorageFile.GetFileFromPathAsync(imagePath).AsTask().ConfigureAwait(false);

                        await dispatcher.InvokeAsync(() =>
                        {
                            try
                            {
                                smtcDisplayUpdater.Thumbnail = RandomAccessStreamReference.CreateFromFile(imageFile);
                                smtcDisplayUpdater.Update();
                                Debugger.show($"[COVERARTCACHE] Thumbnail set from cached file: {imagePath}");
                            }
                            catch (Exception ex)
                            {
                                Debugger.show($"[COVERART] Failed to set thumbnail on dispatcher: {ex.Message}");
                            }
                        }).Task.ConfigureAwait(false);

                        return (duration, metadata, imagePath);
                    }
                    else
                    {
                        Debugger.show($"[COVERART] No image returned for {remotePath}, continuing to next candidate");
                    }
                }

                Debugger.show("[COVERART] No cover art found for any candidates; using default image");
                await SetDefaultImage().ConfigureAwait(false);
                return (duration, metadata, _defaultImagePath);
            }
            catch (Exception ex)
            {
                Debugger.show($"[COVERART] Critical error in SetSMTCImageAsync: {ex.Message}");
                await SetDefaultImage().ConfigureAwait(false);
                return (null, null, _defaultImagePath);
            }
        }

        private async Task SetCachedImage(string imagePath)
        {
            try
            {
                var imageFile = await StorageFile.GetFileFromPathAsync(imagePath).AsTask().ConfigureAwait(false);
                await dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        smtcDisplayUpdater!.Thumbnail = RandomAccessStreamReference.CreateFromFile(imageFile);
                        smtcDisplayUpdater.Update();
                    }
                    catch (Exception ex)
                    {
                        Debugger.show($"[COVERART] Failed to re-set cached thumbnail: {ex.Message}");
                    }
                }).Task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debugger.show($"[COVERART] SetCachedImage failed: {ex.Message}");
                await SetDefaultImage().ConfigureAwait(false);
            }
        }

        private async Task SetDefaultImage()
        {
            try
            {
                Debugger.show($"[COVERART] Setting default image from: {_defaultImagePath}");

                var imageFile = await StorageFile.GetFileFromPathAsync(_defaultImagePath).AsTask().ConfigureAwait(false);
                await dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        smtcDisplayUpdater!.Thumbnail = RandomAccessStreamReference.CreateFromFile(imageFile);
                        smtcDisplayUpdater.Update();
                        CurrentCoverPath = _defaultImagePath;
                        Debugger.show("[COVERART] Default thumbnail set successfully");
                    }
                    catch (Exception ex)
                    {
                        Debugger.show($"[COVERART] Failed to set default thumbnail on dispatcher: {ex.Message}");
                    }
                }).Task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debugger.show($"[COVERART] Failed to set default image: {ex.Message}");
            }
        }
    }
}