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

namespace musicpresense
{
    internal class CoverCacheManager
    {
        private readonly string cachePath;
        private readonly string tempPath;
        private readonly string ffmpegPath;
        private readonly long maxCacheBytes = 200L * 1024L * 1024L;

        private readonly string indexFile;
        private readonly string folderIndexFile;
        private readonly string noCoverFile;

        private Dictionary<string, CacheEntry> index = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> folderIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, DateTime> nocover = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public CoverCacheManager(string ffmpegPath, string cachePath)
        {
            this.ffmpegPath = ffmpegPath;
            this.cachePath = cachePath;
            this.tempPath = Path.Combine(cachePath, "temp");

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
        public async Task<(string ImagePath, double? DurationSeconds, MediaMetadata? Metadata)> GetImagePathForNowPlayingAsync(string deviceId, string remoteFilePath)
        {
            try
            {
                if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(remoteFilePath)) return (null, null, null);

                string folderPath = Path.GetDirectoryName(remoteFilePath)?.Replace("\\", "/") ?? string.Empty;
                string key = ComputeKey(deviceId, remoteFilePath);
                string folderKey = ComputeFolderKey(deviceId, folderPath);

                if (folderIndex.TryGetValue(folderKey, out var mappedImageKey))
                {
                    if (index.TryGetValue(mappedImageKey, out var entry) && File.Exists(Path.Combine(cachePath, entry.FileName)))
                    {
                        entry.LastAccessUtc = DateTime.UtcNow;
                        SaveIndex();
                        Debugger.show("Using folder-mapped cover for " + remoteFilePath);
                        var p = Path.Combine(cachePath, entry.FileName);
                        return (p, entry.DurationSeconds > 0 ? (double?)entry.DurationSeconds : null, null);
                    }
                }

                if (index.TryGetValue(key, out var existing) && File.Exists(Path.Combine(cachePath, existing.FileName)))
                {
                    existing.LastAccessUtc = DateTime.UtcNow;
                    SaveIndex();
                    Debugger.show("Found cached cover for " + remoteFilePath);
                    var p = Path.Combine(cachePath, existing.FileName);
                    return (p, existing.DurationSeconds > 0 ? (double?)existing.DurationSeconds : null, null);
                }

                bool markedNoCover = nocover.ContainsKey(key);
                if (markedNoCover)
                {
                    Debugger.show("Key marked nocover: " + key + " — skipping embedded extraction/heavy pull but will still check for folder images");
                }

                try
                {
                    if (!markedNoCover)
                    {
                        string remoteExtEmb = Path.GetExtension(remoteFilePath);
                        string tempPullEmb = Path.Combine(tempPath, key + remoteExtEmb);
                        Debugger.show("Pulling remote file to temp for embedded cover extraction: " + remoteFilePath + " -> " + tempPullEmb);
                        await AdbHelper.RunAdbAsync($"-s {deviceId} pull \"{remoteFilePath}\" \"{tempPullEmb}\"");

                        if (File.Exists(tempPullEmb))
                        {
                            string cachedFilenameEmb = key + ".jpg";
                            string cachedFullEmb = Path.Combine(cachePath, cachedFilenameEmb);

                            var extractedEmbedded = await RunFfmpegExtractAsync(tempPullEmb, cachedFullEmb);

                            double? durEmb = null;
                            MediaMetadata? metaEmb = null;
                            try { durEmb = await GetMediaDurationAsync(tempPullEmb).ConfigureAwait(false); } catch { }
                            try { metaEmb = await GetMediaMetadataAsync(tempPullEmb).ConfigureAwait(false); } catch { }

                            try { File.Delete(tempPullEmb); } catch { }

                            if (extractedEmbedded && File.Exists(cachedFullEmb))
                            {
                                var fi = new FileInfo(cachedFullEmb);
                                var entry = new CacheEntry { FileName = cachedFilenameEmb, Size = fi.Length, LastAccessUtc = DateTime.UtcNow, FolderKey = null, DurationSeconds = durEmb ?? 0 };
                                index[key] = entry;
                                SaveIndex();
                                EnforceCacheSizeLimit();
                                Debugger.show("Extracted and cached embedded cover for " + remoteFilePath);
                                return (cachedFullEmb, durEmb, metaEmb);
                            }
                            else
                            {
                                Debugger.show("No embedded cover extracted for " + remoteFilePath);
                                if (metaEmb != null)
                                    return (null, durEmb, metaEmb);
                            }
                        }
                        else
                        {
                            Debugger.show("Failed to pull remote file for embedded extraction: " + remoteFilePath);
                        }
                    }
                    else
                    {
                        Debugger.show("Skipping embedded extraction because key is marked nocover: " + key);
                    }
                }
                catch (Exception ex)
                {
                    Debugger.show("Embedded extraction attempt failed: " + ex.Message);
                }

                var possibleNames = new[] { "cover.jpg", "cover.png", "folder.jpg" };

                foreach (var name in possibleNames)
                {
                    try
                    {
                        var listing = await AdbHelper.RunAdbCaptureAsync($"-s {deviceId} shell ls -1p \"{folderPath}\"");
                        if (!string.IsNullOrWhiteSpace(listing))
                        {
                            var entries = listing.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
                            var dirs = entries.Where(e => e.EndsWith("/")).Select(d => d.TrimEnd('/')).ToList();
                            foreach (var d in dirs)
                            {
                                try
                                {
                                    var subfolderPath = folderPath + "/" + d;
                                    if (!remoteFilePath.StartsWith(subfolderPath, StringComparison.OrdinalIgnoreCase))
                                        continue;

                                    string remoteCandidate = CombineRemotePath(subfolderPath, name);
                                    Debugger.show("Checking subfolder candidate on device: " + remoteCandidate);
                                    string tempImg = Path.Combine(tempPath, Guid.NewGuid().ToString() + Path.GetExtension(name));
                                    await AdbHelper.RunAdbAsync($"-s {deviceId} pull \"{remoteCandidate}\" \"{tempImg}\"");
                                    if (File.Exists(tempImg))
                                    {
                                        Debugger.show("Pulled subfolder image: " + remoteCandidate + " -> " + tempImg);
                                        string cachedFile = key + ".jpg";
                                        string cachedPath = Path.Combine(cachePath, cachedFile);

                                        var pulledExt = Path.GetExtension(tempImg).ToLowerInvariant();
                                        if (new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" }.Contains(pulledExt))
                                        {
                                            try { File.Copy(tempImg, cachedPath, true); } catch { }
                                            if (File.Exists(cachedPath))
                                            {
                                                var fi = new FileInfo(cachedPath);
                                                var entry = new CacheEntry { FileName = cachedFile, Size = fi.Length, LastAccessUtc = DateTime.UtcNow, FolderKey = null };
                                                index[key] = entry;
                                                SaveIndex();
                                                EnforceCacheSizeLimit();
                                                try { File.Delete(tempImg); } catch { }
                                                Debugger.show("Cached subfolder image for file: " + remoteFilePath);
                                                return (cachedPath, null, null);
                                            }
                                        }

                                        var ffOut = await RunFfmpegExtractAsync(tempImg, cachedPath);
                                        try { File.Delete(tempImg); } catch { }

                                        if (ffOut && File.Exists(cachedPath))
                                        {
                                            var fi = new FileInfo(cachedPath);
                                            var entry = new CacheEntry { FileName = cachedFile, Size = fi.Length, LastAccessUtc = DateTime.UtcNow, FolderKey = null };
                                            index[key] = entry;
                                            SaveIndex();
                                            EnforceCacheSizeLimit();
                                            Debugger.show("Cached subfolder image for file: " + remoteFilePath);
                                            return (cachedPath, null, null);
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Debugger.show("Subfolder image pull failed: " + ex.Message);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debugger.show("Subfolder image check failed: " + ex.Message);
                    }

                    try
                    {
                        string remoteCandidate = CombineRemotePath(folderPath, name);
                        Debugger.show("Attempting to pull folder-level candidate: " + remoteCandidate);
                        string tempImg = Path.Combine(tempPath, Guid.NewGuid().ToString() + Path.GetExtension(name));
                        await AdbHelper.RunAdbAsync($"-s {deviceId} pull \"{remoteCandidate}\" \"{tempImg}\"");
                        if (File.Exists(tempImg))
                        {
                            Debugger.show("Pulled folder image: " + remoteCandidate + " -> " + tempImg);
                            string cachedFile = key + ".jpg";
                            string cachedPath = Path.Combine(cachePath, cachedFile);

                            var pulledExt = Path.GetExtension(tempImg).ToLowerInvariant();
                            if (new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" }.Contains(pulledExt))
                            {
                                try { File.Copy(tempImg, cachedPath, true); } catch { }
                                if (File.Exists(cachedPath))
                                {
                                    var fi = new FileInfo(cachedPath);
                                    var entry = new CacheEntry { FileName = cachedFile, Size = fi.Length, LastAccessUtc = DateTime.UtcNow, FolderKey = null };
                                    index[key] = entry;
                                    SaveIndex();
                                    EnforceCacheSizeLimit();
                                    try { File.Delete(tempImg); } catch { }
                                    Debugger.show("Cached folder image for file: " + remoteFilePath);
                                    return (cachedPath, null, null);
                                }
                            }

                            var ffOut = await RunFfmpegExtractAsync(tempImg, cachedPath);
                            try { File.Delete(tempImg); } catch { }

                            if (ffOut && File.Exists(cachedPath))
                            {
                                var fi = new FileInfo(cachedPath);
                                var entry = new CacheEntry { FileName = cachedFile, Size = fi.Length, LastAccessUtc = DateTime.UtcNow, FolderKey = null };
                                index[key] = entry;
                                SaveIndex();
                                EnforceCacheSizeLimit();
                                Debugger.show("Cached folder image for file: " + remoteFilePath);
                                return (cachedPath, null, null);
                            }
                            else
                            {
                                Debugger.show("ffmpeg failed to extract folder image for " + remoteCandidate);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debugger.show("Folder-level image pull failed: " + ex.Message);
                    }
                }

                try
                {
                    var imgExts = new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" };

                    var listing = await AdbHelper.RunAdbCaptureAsync($"-s {deviceId} shell ls -1p \"{folderPath}\"");
                    if (!string.IsNullOrWhiteSpace(listing))
                    {
                        var entries = listing.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();

                        var sameFolderImages = entries
                            .Where(e => imgExts.Contains(Path.GetExtension(e).ToLowerInvariant()))
                            .ToList();

                        string? PickBestImageFromList(List<string> list)
                        {
                            if (list == null || list.Count == 0) return null;
                            var preferred = list.FirstOrDefault(n => Regex.IsMatch(n, "(?i)^(cover|front|folder|album|art)\\b") || Regex.IsMatch(n, "(?i)(cover|front|folder|album|art)"));
                            if (preferred != null) return preferred;
                            return list.First();
                        }

                        var pick = PickBestImageFromList(sameFolderImages);
                        if (!string.IsNullOrEmpty(pick))
                        {
                            string remoteCandidate = CombineRemotePath(folderPath, pick);
                            Debugger.show("Found image in same folder on device: " + remoteCandidate);
                            string tempImg = Path.Combine(tempPath, Guid.NewGuid().ToString() + Path.GetExtension(pick));
                            await AdbHelper.RunAdbAsync($"-s {deviceId} pull \"{remoteCandidate}\" \"{tempImg}\"");
                            if (File.Exists(tempImg))
                            {
                                string cachedFile = key + ".jpg";
                                string cachedPath = Path.Combine(cachePath, cachedFile);

                                var pulledExt = Path.GetExtension(tempImg).ToLowerInvariant();
                                if (new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" }.Contains(pulledExt))
                                {
                                    try { File.Copy(tempImg, cachedPath, true); } catch { }
                                    if (File.Exists(cachedPath))
                                    {
                                        var fi = new FileInfo(cachedPath);
                                        var entry = new CacheEntry { FileName = cachedFile, Size = fi.Length, LastAccessUtc = DateTime.UtcNow, FolderKey = null };
                                        index[key] = entry;
                                        SaveIndex();
                                        EnforceCacheSizeLimit();
                                        Debugger.show("Cached discovered folder image for file: " + remoteFilePath);
                                        try { File.Delete(tempImg); } catch { }
                                        return (cachedPath, null, null);
                                    }
                                }

                                var ffOut = await RunFfmpegExtractAsync(tempImg, cachedPath);
                                try { File.Delete(tempImg); } catch { }

                                if (ffOut && File.Exists(cachedPath))
                                {
                                    var fi = new FileInfo(cachedPath);
                                    var entry = new CacheEntry { FileName = cachedFile, Size = fi.Length, LastAccessUtc = DateTime.UtcNow, FolderKey = null };
                                    index[key] = entry;
                                    SaveIndex();
                                    EnforceCacheSizeLimit();
                                    Debugger.show("Cached discovered folder image for file: " + remoteFilePath);
                                    return (cachedPath, null, null);
                                }
                            }
                        }

                        var dirs = entries.Where(e => e.EndsWith("/")).Select(d => d.TrimEnd('/')).ToList();
                        foreach (var d in dirs)
                        {
                            try
                            {
                                var subfolderPath = folderPath + "/" + d;
                                if (!remoteFilePath.StartsWith(subfolderPath, StringComparison.OrdinalIgnoreCase))
                                    continue;

                                var subList = await AdbHelper.RunAdbCaptureAsync($"-s {deviceId} shell ls -1p \"{subfolderPath}\"");
                                if (string.IsNullOrWhiteSpace(subList)) continue;
                                var subEntries = subList.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
                                var imgs = subEntries.Where(e => imgExts.Contains(Path.GetExtension(e).ToLowerInvariant())).ToList();
                                var pickSub = PickBestImageFromList(imgs);
                                if (!string.IsNullOrEmpty(pickSub))
                                {
                                    string cand = CombineRemotePath(subfolderPath, pickSub);
                                    Debugger.show("Found image in subfolder on device: " + cand);
                                    string tempImg = Path.Combine(tempPath, Guid.NewGuid().ToString() + Path.GetExtension(pickSub));
                                    await AdbHelper.RunAdbAsync($"-s {deviceId} pull \"{cand}\" \"{tempImg}\"");
                                    if (File.Exists(tempImg))
                                    {
                                        string cachedFile = key + ".jpg";
                                        string cachedPath = Path.Combine(cachePath, cachedFile);
                                        var pulledExt = Path.GetExtension(tempImg).ToLowerInvariant();
                                        if (new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" }.Contains(pulledExt))
                                        {
                                            try { File.Copy(tempImg, cachedPath, true); } catch { }
                                            if (File.Exists(cachedPath))
                                            {
                                                var fi = new FileInfo(cachedPath);
                                                var entry = new CacheEntry { FileName = cachedFile, Size = fi.Length, LastAccessUtc = DateTime.UtcNow, FolderKey = null };
                                                index[key] = entry;
                                                SaveIndex();
                                                EnforceCacheSizeLimit();
                                                try { File.Delete(tempImg); } catch { }
                                                Debugger.show("Cached discovered subfolder image for file: " + remoteFilePath);
                                                return (cachedPath, null, null);
                                            }
                                        }

                                        var ffOut = await RunFfmpegExtractAsync(tempImg, cachedPath);
                                        try { File.Delete(tempImg); } catch { }

                                        if (ffOut && File.Exists(cachedPath))
                                        {
                                            var fi = new FileInfo(cachedPath);
                                            var entry = new CacheEntry { FileName = cachedFile, Size = fi.Length, LastAccessUtc = DateTime.UtcNow, FolderKey = null };
                                            index[key] = entry;
                                            SaveIndex();
                                            EnforceCacheSizeLimit();
                                            Debugger.show("Cached discovered subfolder image for file: " + remoteFilePath);
                                            return (cachedPath, null, null);
                                        }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debugger.show("Fallback folder image search failed: " + ex.Message);
                }

                string remoteExt = Path.GetExtension(remoteFilePath);
                string tempPull = Path.Combine(tempPath, key + remoteExt);
                Debugger.show("Pulling remote file to temp: " + remoteFilePath + " -> " + tempPull);
                await AdbHelper.RunAdbAsync($"-s {deviceId} pull \"{remoteFilePath}\" \"{tempPull}\"");

                if (!File.Exists(tempPull))
                {
                    Debugger.show("Failed to pull remote file: " + remoteFilePath);
                    nocover[key] = DateTime.UtcNow;
                    SaveNoCover();
                    return (null, null, null);
                }

                string cachedFilename = key + ".jpg";
                string cachedFull = Path.Combine(cachePath, cachedFilename);

                var extracted = await RunFfmpegExtractAsync(tempPull, cachedFull);

                double? dur = null;
                MediaMetadata? meta = null;
                try { dur = await GetMediaDurationAsync(tempPull).ConfigureAwait(false); } catch { }
                try { meta = await GetMediaMetadataAsync(tempPull).ConfigureAwait(false); } catch { }

                try { File.Delete(tempPull); } catch { }

                if (extracted && File.Exists(cachedFull))
                {
                    var fi = new FileInfo(cachedFull);
                    var entry = new CacheEntry { FileName = cachedFilename, Size = fi.Length, LastAccessUtc = DateTime.UtcNow, FolderKey = folderKey, DurationSeconds = dur ?? 0 };
                    index[key] = entry;
                    SaveIndex();
                    EnforceCacheSizeLimit();
                    Debugger.show("Extracted and cached cover for " + remoteFilePath);
                    return (cachedFull, dur, meta);
                }
                else
                {
                    Debugger.show("No cover extracted for " + remoteFilePath + "; marking as nocover");
                    nocover[key] = DateTime.UtcNow;
                    SaveNoCover();
                    return (null, dur, meta);
                }
            }
            catch (Exception ex)
            {
                Debugger.show("GetImagePathForNowPlayingAsync failed: " + ex.Message);
                return (null, null, null);
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
                if (imgExts.Contains(inExt))
                {
                    try { File.Copy(inputPath, outputJpgPath, true); } catch (Exception ex) { Debugger.show("Copy image failed: " + ex.Message); }
                    return File.Exists(outputJpgPath) && new FileInfo(outputJpgPath).Length > 0;
                }

                var args = $"-i \"{inputPath}\" -map 0:v -an -y \"{outputJpgPath}\"";

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

                SaveIndex();
            }
            catch (Exception ex)
            {
                Debugger.show("EnforceCacheSizeLimit failed: " + ex.Message);
            }
        }
    }
}
