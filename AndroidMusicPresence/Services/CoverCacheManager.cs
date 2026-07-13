using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AndroidMusicPresenceLink
{
    internal class CoverCacheManager
    {
        private readonly string cachePath;
        private readonly string tempPath;
        private readonly string ffmpegPath;
        private long maxCacheBytes;
        private readonly string coverFilePatterns;
        // Longest side newly cached covers are downscaled to; 0 = keep original size.
        private readonly int maxCoverSizePx;

        private readonly string indexFile;
        private readonly string folderIndexFile;
        private readonly string noCoverFile;

        private Dictionary<string, CacheEntry> index = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> folderIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, DateTime> nocover = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public CoverCacheManager(string ffmpegPath, string cachePath, int MaxCacheSizeInBytes, string? coverFilePatterns = null, int maxCoverSizePx = 0)
        {
            this.maxCacheBytes = MaxCacheSizeInBytes * 1024L * 1024L;
            this.maxCoverSizePx = Math.Max(0, maxCoverSizePx);
            this.ffmpegPath = ffmpegPath;
            this.cachePath = cachePath;
            this.tempPath = Path.Combine(cachePath, "temp");
            this.coverFilePatterns = string.IsNullOrWhiteSpace(coverFilePatterns) ? "cover.jpg;cover.png;folder.jpg" : coverFilePatterns;

            this.indexFile = Path.Combine(cachePath, "index.json");
            this.folderIndexFile = Path.Combine(cachePath, "folder_index.json");
            this.noCoverFile = Path.Combine(cachePath, "nocover.json");

            EnsureCacheInitialized();
        }

        private void EnsureCacheInitialized()
        {
            try
            {
                if (!Directory.Exists(cachePath)) Directory.CreateDirectory(cachePath);
                if (!Directory.Exists(tempPath)) Directory.CreateDirectory(tempPath);

                if (File.Exists(indexFile))
                {
                    try { index = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(indexFile)) ?? new Dictionary<string, CacheEntry>(); } catch { index = new Dictionary<string, CacheEntry>(); }
                }

                if (File.Exists(folderIndexFile))
                {
                    try { folderIndex = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(folderIndexFile)) ?? new Dictionary<string, string>(); } catch { folderIndex = new Dictionary<string, string>(); }
                }

                if (File.Exists(noCoverFile))
                {
                    try { nocover = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(noCoverFile)) ?? new Dictionary<string, DateTime>(); } catch { nocover = new Dictionary<string, DateTime>(); }
                }
            }
            catch (Exception ex)
            {
                Debugger.show("EnsureCacheInitialized failed: " + ex.Message);
            }
        }

        private void SaveIndex()
        {
            try
            {
                var opts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(indexFile, JsonSerializer.Serialize(index, opts));
            }
            catch (Exception ex)
            {
                Debugger.show("SaveIndex failed: " + ex.Message);
            }
        }

        private void SaveFolderIndex()
        {
            try
            {
                var opts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(folderIndexFile, JsonSerializer.Serialize(folderIndex, opts));
            }
            catch (Exception ex)
            {
                Debugger.show("SaveFolderIndex failed: " + ex.Message);
            }
        }

        private void SaveNoCover()
        {
            try
            {
                var opts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(noCoverFile, JsonSerializer.Serialize(nocover, opts));
            }
            catch (Exception ex)
            {
                Debugger.show("SaveNoCover failed: " + ex.Message);
            }
        }

        private static string ComputeKey(string deviceId, string remotePath)
        {
            using var sha = SHA256.Create();
            var input = Encoding.UTF8.GetBytes(deviceId + "|" + remotePath);
            var hash = sha.ComputeHash(input);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        private static string ComputeFolderKey(string deviceId, string folderPath)
        {
            using var sha = SHA256.Create();
            var input = Encoding.UTF8.GetBytes(deviceId + "|" + folderPath);
            var hash = sha.ComputeHash(input);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        // Distinct "subsonic|..." namespace so a Subsonic cover key can never collide with a
        // local-file ComputeKey(deviceId, remotePath) hash. Keyed on the stable library song id.
        private static string ComputeSubsonicKey(string serverUrl, string songId)
        {
            using var sha = SHA256.Create();
            var input = Encoding.UTF8.GetBytes("subsonic|" + serverUrl + "|" + songId);
            var hash = sha.ComputeHash(input);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        private static string ResolveCacheDeviceKey(string? configuredDeviceName, string deviceId)
        {
            if (!string.IsNullOrWhiteSpace(configuredDeviceName))
                return configuredDeviceName.Trim();

            return deviceId;
        }

        private List<string> GetConfiguredCoverNames()
        {
            var names = (coverFilePatterns ?? string.Empty)
                .Split(new[] { ';', ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (names.Count == 0)
            {
                names.AddRange(new[] { "cover.jpg", "cover.png", "folder.jpg" });
            }

            return names;
        }

        private class CacheEntry
        {
            public string FileName { get; set; } = string.Empty;
            public long Size { get; set; }
            public DateTime LastAccessUtc { get; set; }
            public string? FolderKey { get; set; }
            public double DurationSeconds { get; set; }
        }

        public void ClearCache()
        {
            try
            {
                if (Directory.Exists(cachePath))
                {
                    foreach (var f in Directory.GetFiles(cachePath))
                    {
                        try { File.Delete(f); } catch { }
                    }

                    var temp = Path.Combine(cachePath, "temp");
                    if (Directory.Exists(temp))
                    {
                        try { Directory.Delete(temp, true); } catch { }
                    }
                }

                index.Clear();
                folderIndex.Clear();
                nocover.Clear();
                SaveIndex();
                SaveFolderIndex();
                SaveNoCover();
                Directory.CreateDirectory(Path.Combine(cachePath, "temp"));

                Debugger.show("Cleared cover cache");
            }
            catch (Exception ex)
            {
                Debugger.show("ClearCache failed: " + ex.Message);
            }
        }

        internal sealed record MediaMetadata(string? Title, string? Artist, string? Album);

        // Returns tuple of cached image path (or null), optional duration, and optional metadata
        public async Task<(string ImagePath, double? DurationSeconds, MediaMetadata? Metadata)> GetImagePathForNowPlayingAsync(string deviceId, string remoteFilePath, string? configuredDeviceName)
        {
            try
            {
                if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(remoteFilePath))
                    return (null, null, null);

                var cacheDeviceKey = ResolveCacheDeviceKey(configuredDeviceName, deviceId);
                string folderPath = Path.GetDirectoryName(remoteFilePath)?.Replace("\\", "/") ?? string.Empty;

                string key = ComputeKey(cacheDeviceKey, remoteFilePath);
                string folderKey = ComputeFolderKey(cacheDeviceKey, folderPath);

                double? embeddedDuration = null;
                MediaMetadata? embeddedMetadata = null;

                // =========================
                // ✅ FIXED CACHE HIT LOGIC
                // =========================
                if (index.TryGetValue(key, out var existing))
                {
                    existing.LastAccessUtc = DateTime.UtcNow;
                    SaveIndex();

                    // Album song (no file, uses folder cover)
                    if (existing.FileName == null && existing.FolderKey != null)
                    {
                        if (folderIndex.TryGetValue(existing.FolderKey, out var imgKey) &&
                            index.TryGetValue(imgKey, out var imgEntry) &&
                            !string.IsNullOrEmpty(imgEntry.FileName))
                        {
                            var imgPath = Path.Combine(cachePath, imgEntry.FileName);
                            if (File.Exists(imgPath))
                                return (imgPath, existing.DurationSeconds > 0 ? existing.DurationSeconds : null, null);
                        }

                        return (null, existing.DurationSeconds > 0 ? existing.DurationSeconds : null, null);
                    }

                    // Normal cached file
                    if (!string.IsNullOrEmpty(existing.FileName))
                    {
                        var p = Path.Combine(cachePath, existing.FileName);
                        if (File.Exists(p))
                            return (p, existing.DurationSeconds > 0 ? existing.DurationSeconds : null, null);
                    }
                }

                // =========================
                // ✅ FOLDER COVER HIT
                // =========================
                if (folderIndex.TryGetValue(folderKey, out var mappedImageKey))
                {
                    if (index.TryGetValue(mappedImageKey, out var entry) &&
                        !string.IsNullOrEmpty(entry.FileName) &&
                        File.Exists(Path.Combine(cachePath, entry.FileName)))
                    {
                        entry.LastAccessUtc = DateTime.UtcNow;
                        SaveIndex();

                        var p = Path.Combine(cachePath, entry.FileName);

                        // 🔥 CREATE SONG CACHE ENTRY
                        // entry is the folder cover IMAGE entry, so entry.DurationSeconds
                        // describes the image (always 0), not the song. Pull the song's own
                        // metadata to get the real duration, cache it, and return THAT value.
                        // Returning entry.DurationSeconds left the first play with duration 0,
                        // and the real value only surfaced on a later cache hit. That is the
                        // "reload the song to make the time show up" bug.
                        double? songDuration = null;
                        if (!index.ContainsKey(key))
                        {
                            var songInfo = await PullSongInfoAsync(deviceId, remoteFilePath, key).ConfigureAwait(false);
                            songDuration = songInfo.DurationSeconds;

                            index[key] = new CacheEntry
                            {
                                FileName = null,
                                Size = entry.Size,
                                LastAccessUtc = DateTime.UtcNow,
                                FolderKey = folderKey,
                                DurationSeconds = songInfo.DurationSeconds ?? 0
                            };

                            SaveIndex();
                        }
                        else if (index.TryGetValue(key, out var songEntry) && songEntry.DurationSeconds > 0)
                        {
                            songDuration = songEntry.DurationSeconds;
                        }

                        return (p, songDuration.HasValue && songDuration.Value > 0 ? songDuration : null, null);
                    }
                }

                // =========================
                // (unchanged) EMBEDDED COVER
                // =========================
                try
                {
                    string remoteExt = Path.GetExtension(remoteFilePath);
                    string tempPull = Path.Combine(tempPath, key + remoteExt);

                    await AdbHelper.RunAdbAsync($"-s {deviceId} pull \"{remoteFilePath}\" \"{tempPull}\"");

                    if (File.Exists(tempPull))
                    {
                        string cachedFilename = key + ".jpg";
                        string cachedFull = Path.Combine(cachePath, cachedFilename);

                        var extracted = await RunFfmpegExtractAsync(tempPull, cachedFull);

                        double? dur = null;
                        MediaMetadata? meta = null;

                        try { dur = await GetMediaDurationAsync(tempPull); } catch { }
                        try { meta = await GetMediaMetadataAsync(tempPull); } catch { }

                        // Piggyback on this pull: grab embedded lyrics from the same local file
                        // and cache them. "key" is ComputeKey(cacheDeviceKey, remoteFilePath),
                        // the exact key the lyrics resolver looks up by file path.
                        try
                        {
                            var embeddedLyrics = await MetadataEditService.ReadEmbeddedLyricsAsync(ffmpegPath, tempPull).ConfigureAwait(false);
                            if (!string.IsNullOrWhiteSpace(embeddedLyrics))
                                LyricsCache.Save(key, LyricsCache.Source.Embed, embeddedLyrics);
                        }
                        catch { }

                        try { File.Delete(tempPull); } catch { }

                        if (extracted && File.Exists(cachedFull))
                        {
                            var fi = new FileInfo(cachedFull);

                            index[key] = new CacheEntry
                            {
                                FileName = cachedFilename,
                                Size = fi.Length,
                                LastAccessUtc = DateTime.UtcNow,
                                FolderKey = null,
                                DurationSeconds = dur ?? 0
                            };

                            SaveIndex();
                            EnforceCacheSizeLimit();

                            return (cachedFull, dur, meta);
                        }

                        embeddedDuration = dur;
                        embeddedMetadata = meta;
                    }
                }
                catch { }

                // =========================
                // ✅ FOLDER IMAGE PULL (FIXED)
                // =========================
                var possibleNames = GetConfiguredCoverNames();

                foreach (var name in possibleNames)
                {
                    try
                    {
                        string remoteCandidate = CombineRemotePath(folderPath, name);
                        string tempImg = Path.Combine(tempPath, Guid.NewGuid().ToString() + Path.GetExtension(name));

                        await AdbHelper.RunAdbAsync($"-s {deviceId} pull \"{remoteCandidate}\" \"{tempImg}\"");

                        if (File.Exists(tempImg))
                        {
                            var referencedPath = await CacheFolderReferenceFromPulledImageAsync(
                                cacheDeviceKey, folderPath, remoteCandidate, tempImg);

                            try { File.Delete(tempImg); } catch { }

                            if (!string.IsNullOrEmpty(referencedPath))
                            {
                                // 🔥 CREATE SONG CACHE ENTRY
                                if (!index.ContainsKey(key))
                                {
                                    index[key] = new CacheEntry
                                    {
                                        FileName = null,
                                        Size = 0,
                                        LastAccessUtc = DateTime.UtcNow,
                                        FolderKey = folderKey,
                                        DurationSeconds = embeddedDuration ?? 0
                                    };

                                    SaveIndex();
                                }

                                return (referencedPath, embeddedDuration, embeddedMetadata);
                            }
                        }
                    }
                    catch { }
                }

                // =========================
                // ❌ NO COVER FOUND
                // =========================
                nocover[key] = DateTime.UtcNow;
                SaveNoCover();

                return (null, embeddedDuration, embeddedMetadata);
            }
            catch (Exception ex)
            {
                Debugger.show("GetImagePathForNowPlayingAsync failed: " + ex.Message);
                return (null, null, null);
            }
        }

        // Network fallback: downloads (and caches) cover art for a Subsonic library song.
        // Reuses the same index.json / size-limit machinery as the local-file path, keyed on
        // the song id so repeated plays hit cache. Returns the cached image path, or null when
        // the song has no cover art or the download fails. Never throws.
        public async Task<string?> CacheSubsonicCoverArtAsync(string serverUrl, string username, string password, string songId, string? coverArtId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(songId) || string.IsNullOrWhiteSpace(coverArtId))
                    return null;

                string key = ComputeSubsonicKey(serverUrl, songId);

                if (index.TryGetValue(key, out var existing) && !string.IsNullOrEmpty(existing.FileName))
                {
                    var cached = Path.Combine(cachePath, existing.FileName);
                    if (File.Exists(cached))
                    {
                        existing.LastAccessUtc = DateTime.UtcNow;
                        SaveIndex();
                        return cached;
                    }
                }

                string cachedFilename = key + ".jpg";
                string cachedFull = Path.Combine(cachePath, cachedFilename);

                var downloaded = await SubsonicClient.DownloadCoverArtAsync(serverUrl, username, password, coverArtId!, cachedFull, maxCoverSizePx)
                    .ConfigureAwait(false);
                if (downloaded == null || !File.Exists(cachedFull))
                    return null;

                var fi = new FileInfo(cachedFull);
                index[key] = new CacheEntry
                {
                    FileName = cachedFilename,
                    Size = fi.Length,
                    LastAccessUtc = DateTime.UtcNow,
                    FolderKey = null,
                    DurationSeconds = 0
                };
                SaveIndex();
                EnforceCacheSizeLimit();

                return cachedFull;
            }
            catch (Exception ex)
            {
                Debugger.show("CacheSubsonicCoverArtAsync failed: " + ex.Message);
                return null;
            }
        }

        private async Task<MediaMetadata?> GetMediaMetadataAsync(string inputPath)
        {
            try
            {
                if (!File.Exists(ffmpegPath))
                {
                    Debugger.show("ffmpeg not found at: " + ffmpegPath);
                    return null;
                }

                var args = $"-i \"{inputPath}\" -hide_banner";
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    StandardErrorEncoding = Encoding.UTF8,
                    StandardOutputEncoding = Encoding.UTF8
                };

                using var proc = Process.Start(psi);
                if (proc == null) return null;

                string stderr = await proc.StandardError.ReadToEndAsync();
                proc.WaitForExit();

                string? title = null;
                string? artist = null;
                string? album = null;

                foreach (var line in stderr.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = line.Trim();
                    var match = Regex.Match(trimmed, @"^(title|artist|album)\s*:\s*(.+)$", RegexOptions.IgnoreCase);
                    if (!match.Success) continue;

                    var key = match.Groups[1].Value.ToLowerInvariant();
                    var value = match.Groups[2].Value.Trim();
                    if (string.IsNullOrWhiteSpace(value)) continue;

                    switch (key)
                    {
                        case "title":
                            title ??= value;
                            break;
                        case "artist":
                            artist ??= value;
                            break;
                        case "album":
                            album ??= value;
                            break;
                    }
                }

                if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(album))
                    return null;

                return new MediaMetadata(title, artist, album);
            }
            catch (Exception ex)
            {
                Debugger.show("GetMediaMetadataAsync failed: " + ex.Message);
                return null;
            }
        }

        private static string CombineRemotePath(string folder, string name)
        {
            if (folder.EndsWith("/")) return folder + name;
            return folder + "/" + name;
        }

        private static bool IsCoverReferenceCandidate(string fileName)
        {
            return fileName.Equals("cover.jpg", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("cover.png", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<(double? DurationSeconds, MediaMetadata? Metadata)> PullSongInfoAsync(string deviceId, string remoteFilePath, string key)
        {
            try
            {
                string remoteExt = Path.GetExtension(remoteFilePath);
                string tempPull = Path.Combine(tempPath, key + "_meta" + remoteExt);

                await AdbHelper.RunAdbAsync($"-s {deviceId} pull \"{remoteFilePath}\" \"{tempPull}\"").ConfigureAwait(false);
                if (!File.Exists(tempPull))
                    return (null, null);

                double? dur = null;
                MediaMetadata? meta = null;
                try { dur = await GetMediaDurationAsync(tempPull).ConfigureAwait(false); } catch { }
                try { meta = await GetMediaMetadataAsync(tempPull).ConfigureAwait(false); } catch { }

                try { File.Delete(tempPull); } catch { }
                return (dur, meta);
            }
            catch (Exception ex)
            {
                Debugger.show("PullSongInfoAsync failed: " + ex.Message);
                return (null, null);
            }
        }

        private async Task<string?> CacheFolderReferenceFromPulledImageAsync(string cacheDeviceKey, string albumFolderPath, string remoteImagePath, string tempImagePath)
        {
            try
            {
                if (!File.Exists(tempImagePath)) return null;

                string imageKey = ComputeKey(cacheDeviceKey, remoteImagePath);
                string albumFolderKey = ComputeFolderKey(cacheDeviceKey, albumFolderPath);
                string cachedFile = imageKey + ".jpg";
                string cachedPath = Path.Combine(cachePath, cachedFile);

                if (!File.Exists(cachedPath))
                {
                    var pulledExt = Path.GetExtension(tempImagePath).ToLowerInvariant();
                    if (maxCoverSizePx <= 0 && new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" }.Contains(pulledExt))
                    {
                        try { File.Copy(tempImagePath, cachedPath, true); } catch { }
                    }
                    else
                    {
                        await RunFfmpegExtractAsync(tempImagePath, cachedPath).ConfigureAwait(false);
                    }
                }

                if (!File.Exists(cachedPath)) return null;

                var fi = new FileInfo(cachedPath);
                index[imageKey] = new CacheEntry
                {
                    FileName = cachedFile,
                    Size = fi.Length,
                    LastAccessUtc = DateTime.UtcNow,
                    FolderKey = albumFolderKey,
                    DurationSeconds = 0
                };

                folderIndex[albumFolderKey] = imageKey;
                SaveIndex();
                SaveFolderIndex();
                EnforceCacheSizeLimit();
                return cachedPath;
            }
            catch (Exception ex)
            {
                Debugger.show("CacheFolderReferenceFromPulledImageAsync failed: " + ex.Message);
                return null;
            }
        }

        // ffmpeg filter that shrinks covers to fit within maxCoverSizePx without ever upscaling
        // smaller images. Null when the user chose max quality, so callers keep the copy path.
        private string? BuildDownscaleFilter()
        {
            if (maxCoverSizePx <= 0) return null;
            return $"scale='min(iw,{maxCoverSizePx})':'min(ih,{maxCoverSizePx})':force_original_aspect_ratio=decrease";
        }

        private async Task<bool> RunFfmpegExtractAsync(string inputPath, string outputJpgPath)
        {
            try
            {
                if (!File.Exists(ffmpegPath))
                {
                    Debugger.show("ffmpeg not found at: " + ffmpegPath);
                    return false;
                }

                var outDir = Path.GetDirectoryName(outputJpgPath);
                if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir!);

                var imgExts = new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" };
                var inExt = Path.GetExtension(inputPath).ToLowerInvariant();
                var scaleFilter = BuildDownscaleFilter();
                if (imgExts.Contains(inExt) && scaleFilter == null)
                {
                    try { File.Copy(inputPath, outputJpgPath, true); } catch (Exception ex) { Debugger.show("Copy image failed: " + ex.Message); }
                    return File.Exists(outputJpgPath) && new FileInfo(outputJpgPath).Length > 0;
                }

                var args = scaleFilter == null
                    ? $"-i \"{inputPath}\" -map 0:v -an -y \"{outputJpgPath}\""
                    : $"-i \"{inputPath}\" -map 0:v -an -vf \"{scaleFilter}\" -y \"{outputJpgPath}\"";

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    StandardErrorEncoding = Encoding.UTF8,
                    StandardOutputEncoding = Encoding.UTF8
                };

                Debugger.show("Running ffmpeg: " + psi.FileName + " " + psi.Arguments);

                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    Debugger.show("Failed to start ffmpeg process");
                    return false;
                }

                string stderr = await proc.StandardError.ReadToEndAsync();
                string stdout = await proc.StandardOutput.ReadToEndAsync();
                proc.WaitForExit();

                Debugger.show("ffmpeg exit code: " + proc.ExitCode);
                if (!string.IsNullOrWhiteSpace(stderr)) Debugger.show("ffmpeg stderr: " + stderr);

                if (!File.Exists(outputJpgPath) || new FileInfo(outputJpgPath).Length == 0)
                {
                    var args2 = $"-i \"{inputPath}\" -an -vcodec copy -y \"{outputJpgPath}\"";
                    var psi2 = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = args2,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        StandardErrorEncoding = Encoding.UTF8,
                        StandardOutputEncoding = Encoding.UTF8
                    };
                    using var proc2 = Process.Start(psi2);
                    if (proc2 != null)
                    {
                        string stderr2 = await proc2.StandardError.ReadToEndAsync();
                        string stdout2 = await proc2.StandardOutput.ReadToEndAsync();
                        proc2.WaitForExit();
                        Debugger.show("ffmpeg fallback exit code: " + proc2.ExitCode);
                        if (!string.IsNullOrWhiteSpace(stderr2)) Debugger.show("ffmpeg fallback stderr: " + stderr2);
                    }
                }

                return File.Exists(outputJpgPath) && new FileInfo(outputJpgPath).Length > 0;
            }
            catch (Exception ex)
            {
                Debugger.show("RunFfmpegExtractAsync exception: " + ex.Message);
                return false;
            }
        }

        private async Task<double?> GetMediaDurationAsync(string inputPath)
        {
            try
            {
                if (!File.Exists(ffmpegPath))
                {
                    Debugger.show("ffmpeg not found at: " + ffmpegPath);
                    return null;
                }

                var args = $"-i \"{inputPath}\" -hide_banner";
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    StandardErrorEncoding = Encoding.UTF8,
                    StandardOutputEncoding = Encoding.UTF8
                };

                using var proc = Process.Start(psi);
                if (proc == null) return null;

                string stderr = await proc.StandardError.ReadToEndAsync();
                proc.WaitForExit();

                var m = Regex.Match(stderr, "Duration:\\s*(\\d+):(\\d+):(\\d+(?:\\.\\d+)?)");
                if (m.Success)
                {
                    int hh = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    int mm = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    double ss = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                    double total = hh * 3600 + mm * 60 + ss;
                    return total;
                }
            }
            catch (Exception ex)
            {
                Debugger.show("GetMediaDurationAsync failed: " + ex.Message);
            }

            return null;
        }

        private void EnforceCacheSizeLimit()
        {
            try
            {
                long total = index.Values.Sum(e => e.Size);
                if (total <= maxCacheBytes) return;

                Debugger.show($"Cache size {total} bytes exceeds limit {maxCacheBytes}. Evicting...");

                var ordered = index.OrderBy(kv => kv.Value.LastAccessUtc).ToList();
                foreach (var kv in ordered)
                {
                    try
                    {
                        var path = Path.Combine(cachePath, kv.Value.FileName);
                        if (File.Exists(path)) File.Delete(path);
                    }
                    catch { }

                    index.Remove(kv.Key);
                    total = index.Values.Sum(e => e.Size);
                    if (total <= maxCacheBytes) break;
                }

                var staleFolderMappings = folderIndex.Where(kv => !index.ContainsKey(kv.Value)).Select(kv => kv.Key).ToList();
                foreach (var mapKey in staleFolderMappings)
                    folderIndex.Remove(mapKey);

                SaveIndex();
                if (staleFolderMappings.Count > 0)
                    SaveFolderIndex();
            }
            catch (Exception ex)
            {
                Debugger.show("EnforceCacheSizeLimit failed: " + ex.Message);
            }
        }
    }
}