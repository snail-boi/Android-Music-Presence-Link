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
    /// One on-disk cache for resolved lyrics, shared by two writers: the cover pull
    /// (CoverCacheManager) stores embedded lyrics it grabs from the pulled file, and the
    /// overlay stores lyrics it finds in a sibling .lrc. Entries are keyed by the remote
    /// audio file path when one is known (exact), and by a track metadata key otherwise
    /// (for tracks that never resolve to a file).
    ///
    /// Songs that HAVE lyrics get a small "&lt;hash&gt;.lyr" file: first line is the source
    /// (EMBED / LRC), the rest is the raw lyric text. Songs that have NO lyrics are recorded
    /// as a single key in "nolyrics.json" rather than a marker file each, so we still avoid
    /// re-searching them without littering the folder.
    ///
    /// Precedence is enforced on write so ordering races do not matter: an embedded write
    /// wins over everything, an .lrc write never overwrites embedded, and a "no lyrics" mark
    /// is only recorded when there is no .lyr file yet.
    /// </summary>
    internal static class LyricsCache
    {
        internal enum Source { None, Lrc, Embed }

        internal sealed record Entry(Source Source, string Text);

        private static readonly object _gate = new object();
        private static HashSet<string>? _noLyrics; // lazily loaded set of keys with no lyrics

        private static string Dir => AppPaths.GetDataPath("LyricsCache");
        private static string NoLyricsPath => Path.Combine(Dir, "nolyrics.json");

        internal static string DeviceKey(string? configuredDeviceName, string? deviceId)
            => !string.IsNullOrWhiteSpace(configuredDeviceName)
                ? configuredDeviceName!.Trim()
                : (deviceId ?? string.Empty);

        /// <summary>
        /// Identity stamp for a now-playing track, used to verify that a resolved file path
        /// actually belongs to the track being shown (the file path is resolved on a separate,
        /// later pipeline than the metadata that drives lyric resolution, so it can lag a
        /// track behind). Both the resolver and the cover pipeline build this from the same
        /// raw title/artist, normalized lightly so trivial case/space differences still match.
        /// </summary>
        internal static string TrackToken(string? title, string? artist)
            => (title ?? string.Empty).Trim().ToLowerInvariant()
               + "\u0001"
               + (artist ?? string.Empty).Trim().ToLowerInvariant();

        /// <summary>Key for a track we can pin to an exact remote file. Matches CoverCacheManager's ComputeKey so both sides land on the same entry.</summary>
        internal static string FileKey(string deviceKey, string remoteAudioPath)
            => Hash(deviceKey + "|" + remoteAudioPath);

        /// <summary>Key for a track with no resolved file path; uses its artist/title/album key.</summary>
        internal static string MetaKey(string metadataKey)
            => Hash("meta|" + metadataKey);

        internal static Entry? TryLoad(string key)
        {
            try
            {
                string path = PathFor(key);
                if (File.Exists(path))
                {
                    string all = File.ReadAllText(path, Encoding.UTF8);
                    int nl = all.IndexOf('\n');
                    string head = (nl >= 0 ? all.Substring(0, nl) : all).Trim();
                    string body = nl >= 0 ? all.Substring(nl + 1) : string.Empty;
                    return new Entry(head == "EMBED" ? Source.Embed : Source.Lrc, body);
                }

                lock (_gate)
                {
                    EnsureNoLyricsLoaded();
                    if (_noLyrics!.Contains(key))
                        return new Entry(Source.None, string.Empty);
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        internal static void Save(string key, Source source, string? text)
        {
            try
            {
                lock (_gate)
                {
                    EnsureNoLyricsLoaded();
                    string path = PathFor(key);
                    bool hasFile = File.Exists(path);
                    Source existing = hasFile ? ReadSource(path) : Source.None;

                    if (source == Source.Embed)
                    {
                        WriteLyr(path, "EMBED", text);
                        if (_noLyrics!.Remove(key)) PersistNoLyrics();
                    }
                    else if (source == Source.Lrc)
                    {
                        // Embedded is authoritative: never let an .lrc overwrite it.
                        if (hasFile && existing == Source.Embed) return;
                        WriteLyr(path, "LRC", text);
                        if (_noLyrics!.Remove(key)) PersistNoLyrics();
                    }
                    else // None
                    {
                        // Do not mark a song "no lyrics" if we already have real lyrics for it.
                        if (hasFile) return;
                        if (_noLyrics!.Add(key)) PersistNoLyrics();
                    }
                }
            }
            catch (Exception ex)
            {
                Debugger.show("[LYRICS] cache save failed: " + ex.Message);
            }
        }

        internal static void Invalidate(string key)
        {
            try
            {
                lock (_gate)
                {
                    EnsureNoLyricsLoaded();
                    string path = PathFor(key);
                    if (File.Exists(path)) File.Delete(path);
                    if (_noLyrics!.Remove(key)) PersistNoLyrics();
                }
            }
            catch
            {
            }
        }

        private static void WriteLyr(string path, string head, string? text)
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(path, head + "\n" + (text ?? string.Empty), new UTF8Encoding(false));
        }

        private static Source ReadSource(string path)
        {
            try
            {
                using var reader = new StreamReader(path, Encoding.UTF8);
                string? head = reader.ReadLine()?.Trim();
                return head == "EMBED" ? Source.Embed : Source.Lrc;
            }
            catch
            {
                return Source.None;
            }
        }

        private static void EnsureNoLyricsLoaded()
        {
            if (_noLyrics != null) return;
            _noLyrics = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(NoLyricsPath))
                {
                    var keys = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(NoLyricsPath, Encoding.UTF8));
                    if (keys != null)
                        foreach (var k in keys)
                            if (!string.IsNullOrEmpty(k)) _noLyrics.Add(k);
                }
            }
            catch
            {
                // Corrupt or unreadable: start empty; affected songs just re-resolve once.
            }
        }

        private static void PersistNoLyrics()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(NoLyricsPath, JsonSerializer.Serialize(_noLyrics!.ToList()), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Debugger.show("[LYRICS] nolyrics persist failed: " + ex.Message);
            }
        }

        private static string PathFor(string key) => Path.Combine(Dir, key + ".lyr");

        private static string Hash(string s)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
            return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
        }
    }
}