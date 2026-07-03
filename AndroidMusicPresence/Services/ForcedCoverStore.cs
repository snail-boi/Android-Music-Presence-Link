using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// User-chosen cover overrides ("forced covers"). Keyed by the same title+artist token
    /// the lyrics pipeline uses (LyricsCache.TrackToken), so an override applies to the
    /// track regardless of whether it is a local file or a Subsonic/cloud track, and
    /// survives the file moving on the phone.
    ///
    /// The chosen image is copied into the ForcedCovers data folder (so the override keeps
    /// working if the user deletes the original) and indexed by forced_covers.json, which
    /// also records the display title/artist for the management list in settings.
    /// The forced cover is a display-level override: the normal cover/duration lookup still
    /// runs, MediaController just swaps the image in afterwards.
    /// </summary>
    internal static class ForcedCoverStore
    {
        internal sealed record ForcedCover(string Token, string Title, string Artist, string ImagePath);

        private sealed class Entry
        {
            public string FileName { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public string Artist { get; set; } = string.Empty;
        }

        private static readonly object _gate = new object();
        private static Dictionary<string, Entry>? _index;

        private static string Dir => AppPaths.GetDataPath("ForcedCovers");
        private static string IndexPath => Path.Combine(Dir, "forced_covers.json");

        private static readonly string[] AllowedExtensions = { ".jpg", ".jpeg", ".png", ".bmp" };

        /// <summary>Returns the forced image path for the track, or null when there is no
        /// override (or its image file has gone missing).</summary>
        internal static string? TryGetPath(string? title, string? artist)
        {
            try
            {
                lock (_gate)
                {
                    EnsureLoaded();
                    var token = LyricsCache.TrackToken(title, artist);
                    if (!_index!.TryGetValue(token, out var entry))
                        return null;

                    var path = Path.Combine(Dir, entry.FileName);
                    return File.Exists(path) ? path : null;
                }
            }
            catch
            {
                return null;
            }
        }

        internal static bool Has(string? title, string? artist) => TryGetPath(title, artist) != null;

        /// <summary>Copies the image into the store and records the override.
        /// Returns the stored image path, or null on failure (unsupported type, IO error).</summary>
        internal static string? Set(string? title, string? artist, string sourceImagePath)
        {
            try
            {
                var ext = Path.GetExtension(sourceImagePath).ToLowerInvariant();
                if (!AllowedExtensions.Contains(ext) || !File.Exists(sourceImagePath))
                    return null;

                lock (_gate)
                {
                    EnsureLoaded();
                    Directory.CreateDirectory(Dir);

                    var token = LyricsCache.TrackToken(title, artist);
                    var fileName = Hash(token) + ext;
                    var destPath = Path.Combine(Dir, fileName);
                    File.Copy(sourceImagePath, destPath, overwrite: true);

                    // A replacement can change extension; drop the old file so it doesn't orphan.
                    if (_index!.TryGetValue(token, out var old) && !string.Equals(old.FileName, fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(Path.Combine(Dir, old.FileName)); } catch { }
                    }

                    _index[token] = new Entry
                    {
                        FileName = fileName,
                        Title = (title ?? string.Empty).Trim(),
                        Artist = (artist ?? string.Empty).Trim()
                    };
                    Persist();

                    Debugger.show($"[FORCEDCOVER] Set for '{title}' by '{artist}' -> {fileName}");
                    return destPath;
                }
            }
            catch (Exception ex)
            {
                Debugger.show("[FORCEDCOVER] Set failed: " + ex.Message);
                return null;
            }
        }

        internal static void Remove(string? title, string? artist)
            => RemoveByToken(LyricsCache.TrackToken(title, artist));

        internal static void RemoveByToken(string token)
        {
            try
            {
                lock (_gate)
                {
                    EnsureLoaded();
                    if (!_index!.TryGetValue(token, out var entry))
                        return;

                    _index.Remove(token);
                    try { File.Delete(Path.Combine(Dir, entry.FileName)); } catch { }
                    Persist();

                    Debugger.show($"[FORCEDCOVER] Removed for '{entry.Title}' by '{entry.Artist}'");
                }
            }
            catch (Exception ex)
            {
                Debugger.show("[FORCEDCOVER] Remove failed: " + ex.Message);
            }
        }

        internal static void RemoveAll()
        {
            try
            {
                lock (_gate)
                {
                    EnsureLoaded();
                    foreach (var entry in _index!.Values)
                    {
                        try { File.Delete(Path.Combine(Dir, entry.FileName)); } catch { }
                    }

                    _index.Clear();
                    Persist();
                    Debugger.show("[FORCEDCOVER] Cleared all");
                }
            }
            catch (Exception ex)
            {
                Debugger.show("[FORCEDCOVER] RemoveAll failed: " + ex.Message);
            }
        }

        /// <summary>All overrides, for the management list in settings. Entries whose image
        /// file disappeared are skipped.</summary>
        internal static List<ForcedCover> All()
        {
            var result = new List<ForcedCover>();
            try
            {
                lock (_gate)
                {
                    EnsureLoaded();
                    foreach (var kv in _index!)
                    {
                        var path = Path.Combine(Dir, kv.Value.FileName);
                        if (File.Exists(path))
                            result.Add(new ForcedCover(kv.Key, kv.Value.Title, kv.Value.Artist, path));
                    }
                }
            }
            catch (Exception ex)
            {
                Debugger.show("[FORCEDCOVER] All failed: " + ex.Message);
            }

            return result
                .OrderBy(c => c.Artist, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void EnsureLoaded()
        {
            if (_index != null) return;
            _index = new Dictionary<string, Entry>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(IndexPath))
                {
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(IndexPath, Encoding.UTF8));
                    if (loaded != null)
                    {
                        foreach (var kv in loaded)
                        {
                            if (!string.IsNullOrEmpty(kv.Key) && kv.Value != null && !string.IsNullOrEmpty(kv.Value.FileName))
                                _index[kv.Key] = kv.Value;
                        }
                    }
                }
            }
            catch
            {
                // Corrupt or unreadable index: start empty. Stored images stay on disk but
                // become unreferenced; re-forcing a cover simply overwrites them.
            }
        }

        private static void Persist()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(IndexPath, JsonSerializer.Serialize(_index, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Debugger.show("[FORCEDCOVER] persist failed: " + ex.Message);
            }
        }

        private static string Hash(string s)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
            return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
        }
    }
}
