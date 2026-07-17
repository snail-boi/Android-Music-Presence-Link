using System;
using System.Collections.Generic;
using System.Linq;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// Shared path compressor for the persisted library lists. Any directory prefix shared by
    /// two or more entries is replaced with a short numeric id kept in a side dictionary, so a
    /// long root like "/storage/emulated/0/Download/YTDLnis/Audio/" is written once instead of
    /// on every song line.
    ///
    /// The dictionary itself is stored HIERARCHICALLY: each entry is kept relative to its
    /// parent entry (its immediate indexed directory), so the shared root is written exactly
    /// once at the top of the chain rather than repeated on every album entry.
    ///
    /// Safety (the #1 rule for these files): ids are random 5-digit numbers, and a literal
    /// (uncompressed) path is always distinguishable from a compressed reference because a
    /// literal begins with '/' while a compressed reference begins with its numeric id. For
    /// relative (non-rooted) paths a leading '/' is added purely as that marker. This means a
    /// folder whose name happens to be digits (e.g. "2017" or "14") can never be mistaken for
    /// an id — the collision the sequential-id idea would have risked simply cannot occur.
    ///
    /// Backward compatibility: an old dictionary stored absolute values (no parent chain) and
    /// an old list stored uncompressed relative paths. Both still expand correctly here — an
    /// absolute value is treated as a literal and returned as-is, and an unknown leading token
    /// is treated as literal text — so existing files load without a forced rescan.
    /// </summary>
    internal sealed class LibraryPathCodec
    {
        private const int IdMin = 10000;
        private const int IdMax = 99999;

        // true  -> paths are absolute and already start with '/' (the local library list).
        // false -> paths are relative (the Subsonic list); literals get a leading '/' marker.
        private readonly bool _rooted;

        // Reader side: id -> stored (possibly parent-relative) value.
        private readonly Dictionary<string, string> _idToValue;
        private readonly Dictionary<string, string> _expandCache = new(StringComparer.Ordinal);

        // Builder side only (null after Load): indexed directory prefix -> id.
        private readonly Dictionary<string, string>? _prefixToId;
        private readonly List<string>? _prefixesByLenDesc;

        private LibraryPathCodec(bool rooted,
            Dictionary<string, string> idToValue,
            Dictionary<string, string>? prefixToId,
            List<string>? prefixesByLenDesc)
        {
            _rooted = rooted;
            _idToValue = idToValue;
            _prefixToId = prefixToId;
            _prefixesByLenDesc = prefixesByLenDesc;
        }

        public bool IsEmpty => _idToValue.Count == 0;

        // ── Build ───────────────────────────────────────────────────────────────

        /// <summary>Analyses the paths and builds an index of shared directory prefixes.</summary>
        public static LibraryPathCodec Build(IEnumerable<string> paths, bool rooted)
        {
            // Count every cumulative directory prefix (each ends with '/').
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                int pos = 0;
                while (true)
                {
                    int slash = path.IndexOf('/', pos + 1);
                    if (slash < 0) break;
                    var prefix = path.Substring(0, slash + 1);
                    counts.TryGetValue(prefix, out int c);
                    counts[prefix] = c + 1;
                    pos = slash;
                }
            }

            var rng = new Random();
            var used = new HashSet<string>();
            var prefixToId = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kvp in counts)
            {
                if (kvp.Value < 2) continue; // single-use prefixes stay inline
                string id;
                do { id = rng.Next(IdMin, IdMax + 1).ToString(); } while (!used.Add(id));
                prefixToId[kvp.Key] = id;
            }

            // Store each indexed prefix relative to its immediate indexed parent. The parent is
            // the same prefix with its final path segment removed; because a shorter prefix is
            // always at least as common as a longer one, that parent is guaranteed to also be
            // indexed unless it is the very top of the chain, which is stored literally.
            var idToValue = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (prefix, id) in prefixToId)
            {
                var parent = ParentPrefix(prefix);
                if (parent != null && prefixToId.TryGetValue(parent, out var parentId))
                    idToValue[id] = parentId + "/" + prefix.Substring(parent.Length);
                else
                    idToValue[id] = Literal(prefix, rooted);
            }

            var byLen = prefixToId.Keys.OrderByDescending(k => k.Length).ToList();
            return new LibraryPathCodec(rooted, idToValue, prefixToId, byLen);
        }

        // ── Load ────────────────────────────────────────────────────────────────

        /// <summary>Rebuilds a reader-only codec from serialized dictionary lines.</summary>
        public static LibraryPathCodec Load(IEnumerable<string> lines, bool rooted)
        {
            var idToValue = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line)) continue;
                int tab = line.IndexOf('\t');
                if (tab < 1) continue;
                var id = line.Substring(0, tab);
                if (!string.IsNullOrEmpty(id)) idToValue[id] = line.Substring(tab + 1);
            }
            return new LibraryPathCodec(rooted, idToValue, null, null);
        }

        /// <summary>Dictionary lines to persist ("id\tvalue"). Empty when nothing was shared.</summary>
        public IEnumerable<string> Serialize()
            => _idToValue.Select(kvp => kvp.Key + "\t" + kvp.Value);

        // ── Compress / Expand ─────────────────────────────────────────────────────

        /// <summary>Compresses a full path to its stored form. Requires a Build()-created codec.</summary>
        public string Compress(string path)
        {
            if (_prefixToId == null || _prefixesByLenDesc == null)
                throw new InvalidOperationException("Compress requires a codec built with Build().");
            if (string.IsNullOrEmpty(path)) return path;

            foreach (var prefix in _prefixesByLenDesc)
            {
                if (path.StartsWith(prefix, StringComparison.Ordinal))
                    return _prefixToId[prefix] + "/" + path.Substring(prefix.Length);
            }
            return Literal(path, _rooted);
        }

        /// <summary>Expands a stored form back to the full path.</summary>
        public string Expand(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return stored;
            if (_expandCache.TryGetValue(stored, out var cached)) return cached;

            string result;
            if (stored[0] == '/')
            {
                // Literal. For relative paths the leading '/' is only a marker and is stripped;
                // for rooted paths it is a genuine part of the absolute path and kept.
                result = _rooted ? stored : stored.Substring(1);
            }
            else
            {
                int slash = stored.IndexOf('/');
                if (slash < 1)
                {
                    result = stored; // no id token — treat as literal (defensive / legacy)
                }
                else
                {
                    var idToken = stored.Substring(0, slash);
                    // Only the leading token is ever interpreted as an id; the remainder is
                    // appended verbatim and never re-parsed, so folder names can be anything.
                    result = _idToValue.TryGetValue(idToken, out var value)
                        ? Expand(value) + stored.Substring(slash + 1)
                        : stored; // unknown id — legacy uncompressed path, keep as-is
                }
            }

            _expandCache[stored] = result;
            return result;
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static string Literal(string path, bool rooted)
            => rooted ? path : "/" + path;

        // The immediate parent directory prefix: the prefix with its final path segment
        // removed. Returns null when there is no '/' boundary above the first segment.
        private static string? ParentPrefix(string prefix)
        {
            if (prefix.Length < 2) return null;
            int prevSlash = prefix.LastIndexOf('/', prefix.Length - 2);
            if (prevSlash < 0) return null;
            return prefix.Substring(0, prevSlash + 1);
        }
    }
}
