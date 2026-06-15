using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// Reads and writes track tags for a file that lives on the Android device.
    ///
    /// Read pass: pull the file once, dump its tags with ffmpeg's ffmetadata muxer, and
    /// extract the current embedded cover to a temp JPG for preview.
    ///
    /// Write pass: re-tag the pulled copy with ffmpeg (audio stream-copied, never
    /// re-encoded), push the result to a temp name in the SAME folder, copy the original
    /// file's timestamp onto it with `touch -r`, and only then `mv` it over the original.
    /// Because mv is a rename within one filesystem it is atomic and preserves the mtime we
    /// just set, so the original is never half-written and its date-modified is retained.
    /// If anything fails before the mv, the original is untouched.
    ///
    /// The local ffmpeg call uses ArgumentList so arbitrary tag text (quotes, semicolons,
    /// unicode) never passes through a shell. The on-device shell calls single-quote the
    /// remote paths.
    /// </summary>
    internal static class MetadataEditService
    {
        // ffmpeg's normalized generic keys -> our model fields.
        private static readonly (string key, Action<TrackMetadata, string> set)[] ReadMap =
        {
            ("title",        (m, v) => m.Title = v),
            ("album",        (m, v) => m.Album = v),
            ("artist",       (m, v) => m.Artist = v),
            ("album_artist", (m, v) => m.AlbumArtist = v),
            ("composer",     (m, v) => m.Composer = v),
            ("genre",        (m, v) => m.Genre = v),
            ("track",        (m, v) => m.TrackNumber = v),
            ("disc",         (m, v) => m.DiscNumber = v),
            ("date",         (m, v) => m.Year = v),
            ("comment",      (m, v) => m.Comment = v),
        };

        public static async Task<TrackMetadata?> ReadAsync(string device, string remotePath, string ffmpegPath, string tempDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(device) || string.IsNullOrWhiteSpace(remotePath))
                    return null;

                Directory.CreateDirectory(tempDir);
                string ext = SafeExtension(remotePath);
                string local = Path.Combine(tempDir, "tagedit_src_" + Guid.NewGuid().ToString("N") + ext);

                await AdbHelper.RunAdbCaptureAsync($"-s {device} pull \"{remotePath}\" \"{local}\"").ConfigureAwait(false);
                if (!File.Exists(local) || new FileInfo(local).Length == 0)
                {
                    Debugger.show("[TAGEDIT] Pull produced no file for read: " + remotePath);
                    TryDelete(local);
                    return null;
                }

                var meta = new TrackMetadata { LocalSourcePath = local };

                // The ffmetadata muxer only emits global/format-level tags and silently drops
                // stream-level ones. OGG/Opus always keep their Vorbis comments on the stream,
                // and some FLAC and other files do too, so a single dump returns empty fields
                // for them. Dump twice: the stream-mapped pass first, then the global pass so
                // genuinely global tags win on the rare conflict, and the union catches a tag
                // wherever it physically lives.
                string metaGuid = Guid.NewGuid().ToString("N");
                string streamMetaPath = Path.Combine(tempDir, "tagedit_meta_s_" + metaGuid + ".txt");
                string globalMetaPath = Path.Combine(tempDir, "tagedit_meta_g_" + metaGuid + ".txt");

                bool streamDumped = await RunFfmpegAsync(ffmpegPath, new List<string>
                {
                    "-i", local, "-map_metadata:g", "0:s:0", "-f", "ffmetadata", "-y", streamMetaPath
                }).ConfigureAwait(false);
                if (streamDumped && File.Exists(streamMetaPath))
                {
                    ParseFfmetadata(File.ReadAllText(streamMetaPath, Encoding.UTF8), meta);
                    TryDelete(streamMetaPath);
                }

                bool globalDumped = await RunFfmpegAsync(ffmpegPath, new List<string>
                {
                    "-i", local, "-f", "ffmetadata", "-y", globalMetaPath
                }).ConfigureAwait(false);
                if (globalDumped && File.Exists(globalMetaPath))
                {
                    ParseFfmetadata(File.ReadAllText(globalMetaPath, Encoding.UTF8), meta);
                    TryDelete(globalMetaPath);
                }

                // Some files carry no embedded title/album at all (the player shows the file
                // name and the containing folder instead, which is what makes them look
                // tagged). Seed those fields so the editor starts from something useful and a
                // save writes real tags: the file name without extension as the title, and the
                // immediate parent folder as the album. These are normal editable values, so
                // the user can adjust or clear them before saving.
                if (string.IsNullOrWhiteSpace(meta.Title))
                    meta.Title = FileNameWithoutExtension(remotePath);
                if (string.IsNullOrWhiteSpace(meta.Album))
                {
                    string folder = ParentFolderName(remotePath);
                    if (!string.IsNullOrWhiteSpace(folder))
                        meta.Album = folder;
                }

                // No embedded lyrics: fall back to a sibling .lrc next to the track.
                if (string.IsNullOrWhiteSpace(meta.Lyrics))
                {
                    string lrcRemote = RemoteLrcPath(remotePath);
                    string lrcLocal = Path.Combine(tempDir, "tagedit_lrc_" + Guid.NewGuid().ToString("N") + ".lrc");
                    await AdbHelper.RunAdbCaptureAsync($"-s {device} pull \"{lrcRemote}\" \"{lrcLocal}\"").ConfigureAwait(false);
                    if (File.Exists(lrcLocal) && new FileInfo(lrcLocal).Length > 0)
                    {
                        meta.Lyrics = File.ReadAllText(lrcLocal, Encoding.UTF8);
                        meta.LyricsFromLrc = true;
                        meta.LyricsLrcPath = lrcRemote;
                    }
                    TryDelete(lrcLocal);
                }

                // Extract current cover for preview (best effort).
                string coverPath = Path.Combine(tempDir, "tagedit_cover_" + Guid.NewGuid().ToString("N") + ".jpg");
                bool gotCover = await RunFfmpegAsync(ffmpegPath, new List<string>
                {
                    "-i", local, "-an", "-map", "0:v:0?", "-frames:v", "1", "-y", coverPath
                }).ConfigureAwait(false);

                if (gotCover && File.Exists(coverPath) && new FileInfo(coverPath).Length > 0)
                    meta.CoverPreviewPath = coverPath;
                else
                    TryDelete(coverPath);

                return meta;
            }
            catch (Exception ex)
            {
                Debugger.show("[TAGEDIT] ReadAsync failed: " + ex.Message);
                return null;
            }
        }

        public static async Task<(bool ok, string message)> WriteAsync(string device, string remotePath, TrackMetadata edited, string ffmpegPath, string tempDir, bool retainDate)
        {
            string? local = null;
            bool pulledFresh = false;
            string ext = SafeExtension(remotePath);
            string outPath = Path.Combine(tempDir, "tagedit_out_" + Guid.NewGuid().ToString("N") + ext);

            try
            {
                if (string.IsNullOrWhiteSpace(device) || string.IsNullOrWhiteSpace(remotePath))
                    return (false, "No device or file path.");

                Directory.CreateDirectory(tempDir);

                // Reuse the copy pulled during the read pass if it survived; otherwise pull fresh.
                if (!string.IsNullOrWhiteSpace(edited.LocalSourcePath) && File.Exists(edited.LocalSourcePath))
                {
                    local = edited.LocalSourcePath;
                }
                else
                {
                    local = Path.Combine(tempDir, "tagedit_src_" + Guid.NewGuid().ToString("N") + ext);
                    pulledFresh = true;
                    await AdbHelper.RunAdbCaptureAsync($"-s {device} pull \"{remotePath}\" \"{local}\"").ConfigureAwait(false);
                    if (!File.Exists(local) || new FileInfo(local).Length == 0)
                        return (false, "Could not pull the file from the device.");
                }

                bool encoded;
                if (IsStreamTagFormat(ext))
                {
                    // OGG/Opus keep their cover as a base64 METADATA_BLOCK_PICTURE comment that
                    // ffmpeg surfaces as a fake mjpeg stream it then refuses to remux, so the
                    // ordinary "-map 0 -c copy" path dies with "Unsupported codec id". Instead
                    // map audio only and apply tags plus the rebuilt picture from a ffmetadata
                    // input file (no command-line length limit on the big base64).
                    encoded = await WriteOggAsync(ffmpegPath, local, outPath, ext, edited, tempDir).ConfigureAwait(false);
                }
                else
                {
                    var args = BuildFfmpegWriteArgs(local, outPath, ext, edited);
                    encoded = await RunFfmpegAsync(ffmpegPath, args).ConfigureAwait(false);
                }
                if (!encoded || !File.Exists(outPath) || new FileInfo(outPath).Length == 0)
                    return (false, "ffmpeg could not write the new tags (the original was left untouched).");

                // Push to a temp name in the same folder so the later move is a same-filesystem rename.
                string dir = RemoteDirectory(remotePath);
                string remoteTmp = dir + "/.ampl_tag_tmp_" + Guid.NewGuid().ToString("N") + ext;

                string pushOut = await AdbHelper.RunAdbCaptureAsync($"-s {device} push \"{outPath}\" \"{remoteTmp}\"").ConfigureAwait(false);

                // Verify the temp landed and is non-empty before touching the original.
                string check = await AdbHelper.RunAdbCaptureAsync(
                    $"-s {device} shell test -s {Sq(remoteTmp)} && echo __AMPL_OK__").ConfigureAwait(false);
                if (check.IndexOf("__AMPL_OK__", StringComparison.Ordinal) < 0)
                {
                    await AdbHelper.RunAdbAsync($"-s {device} shell rm -f {Sq(remoteTmp)}").ConfigureAwait(false);
                    return (false, "The edited file did not transfer to the device (the original was left untouched). " + pushOut.Trim());
                }

                // Set the temp's timestamp before swapping it in. When retaining the date we
                // use the original mtime plus one second: visually the same date, but a
                // different value so the media scanner still sees the file as changed and
                // re-reads its tags. When not retaining, the freshly pushed (current) time is
                // left in place, which the scanner also treats as changed.
                if (retainDate)
                {
                    string statOut = await AdbHelper.RunAdbCaptureAsync($"-s {device} shell stat -c %Y {Sq(remotePath)}").ConfigureAwait(false);
                    long? origEpoch = ParseFirstLong(statOut);
                    if (origEpoch.HasValue)
                        await AdbHelper.RunAdbCaptureAsync($"-s {device} shell touch -d @{origEpoch.Value + 1} {Sq(remoteTmp)}").ConfigureAwait(false);
                }

                string mvOut = await AdbHelper.RunAdbCaptureAsync($"-s {device} shell mv -f {Sq(remoteTmp)} {Sq(remotePath)}").ConfigureAwait(false);

                // Confirm the swap actually happened. A rename over a file the player holds
                // open can be refused on some storage layers; if so the temp is still present
                // and the original is untouched. Report that rather than a false success.
                string leftover = await AdbHelper.RunAdbCaptureAsync(
                    $"-s {device} shell test -e {Sq(remoteTmp)} && echo __AMPL_LEFT__").ConfigureAwait(false);
                if (leftover.IndexOf("__AMPL_LEFT__", StringComparison.Ordinal) >= 0)
                {
                    await AdbHelper.RunAdbAsync($"-s {device} shell rm -f {Sq(remoteTmp)}").ConfigureAwait(false);
                    return (false, "Could not replace the file; it may be locked while the track is playing. The original was left untouched. " + mvOut.Trim());
                }

                // Replacing the file out-of-band leaves MediaStore holding the old extracted
                // tags, so the player keeps showing them. An explicit per-file scan forces
                // MediaStore to re-read the new tags from the file.
                await AdbHelper.RunAdbAsync($"-s {device} shell am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d {Sq("file://" + remotePath)}").ConfigureAwait(false);

                // Lyrics destined for a .lrc file (checkbox, or WAV): write the sibling file,
                // or remove it when the lyrics were cleared.
                if (edited.SaveLyricsAsLrc || ext == ".wav")
                {
                    string lrcRemote = RemoteLrcPath(remotePath);
                    if (!string.IsNullOrWhiteSpace(edited.Lyrics))
                        await PushLrcAsync(device, lrcRemote, edited.Lyrics, tempDir).ConfigureAwait(false);
                    else
                        await AdbHelper.RunAdbAsync($"-s {device} shell rm -f {Sq(lrcRemote)}").ConfigureAwait(false);
                }

                return (true, "Tags saved.");
            }
            catch (Exception ex)
            {
                Debugger.show("[TAGEDIT] WriteAsync failed: " + ex.Message);
                return (false, "Saving tags failed: " + ex.Message);
            }
            finally
            {
                TryDelete(outPath);
                if (pulledFresh && local != null) TryDelete(local);
            }
        }

        private static List<string> BuildFfmpegWriteArgs(string local, string outPath, string ext, TrackMetadata m)
        {
            var args = new List<string> { "-i", local };

            bool addingCover = !m.RemoveCover && !string.IsNullOrWhiteSpace(m.NewCoverImagePath) && File.Exists(m.NewCoverImagePath!);
            if (addingCover)
                args.AddRange(new[] { "-i", m.NewCoverImagePath! });

            if (m.RemoveCover)
            {
                // Keep audio only, drop any embedded art.
                args.AddRange(new[] { "-map", "0:a", "-c:a", "copy" });
            }
            else if (addingCover)
            {
                // Replace art: audio from input 0, image from input 1.
                args.AddRange(new[] { "-map", "0:a", "-map", "1:v", "-c:a", "copy", "-c:v", "copy", "-disposition:v", "attached_pic" });
            }
            else
            {
                // Keep everything, including existing embedded art.
                args.AddRange(new[] { "-map", "0", "-c", "copy" });
            }

            // MP3 cover compatibility.
            if (ext == ".mp3")
            {
                args.AddRange(new[] { "-id3v2_version", "3" });
                if (addingCover)
                    args.AddRange(new[] { "-metadata:s:v", "title=Album cover", "-metadata:s:v", "comment=Cover (front)" });
            }

            // OGG/Opus store Vorbis comments on the stream; a global -metadata write is
            // silently ignored for them (the old tags survive). Those formats must be written
            // with -metadata:s:a:0. Everything else (mp3, flac, m4a, wma, wav) takes tags at
            // the global level, so a stream-level write would be the one that no-ops there.
            string metaOpt = IsStreamTagFormat(ext) ? "-metadata:s:a:0" : "-metadata";

            // Always write every field. Empty string clears the tag, which is the intended
            // behaviour when the user blanks a box.
            AddMeta(args, metaOpt, "title", m.Title);
            AddMeta(args, metaOpt, "album", m.Album);
            AddMeta(args, metaOpt, "artist", m.Artist);
            AddMeta(args, metaOpt, "album_artist", m.AlbumArtist);
            AddMeta(args, metaOpt, "composer", m.Composer);
            AddMeta(args, metaOpt, "genre", m.Genre);
            AddMeta(args, metaOpt, "track", m.TrackNumber);
            AddMeta(args, metaOpt, "disc", m.DiscNumber);
            AddMeta(args, metaOpt, "date", m.Year);
            AddMeta(args, metaOpt, "comment", m.Comment);

            // Lyrics. When the lyrics are going to a .lrc file (checkbox, or WAV which can't
            // embed), we do not embed and instead clear any existing embedded field so it
            // does not shadow the .lrc (embedded wins on read). Otherwise embed into the
            // field the lyrics came from, or the per-format default for new lyrics.
            bool lyricsToLrc = m.SaveLyricsAsLrc || ext == ".wav";
            if (lyricsToLrc)
            {
                if (!m.LyricsFromLrc && !string.IsNullOrEmpty(m.LyricsSourceField))
                    AddMeta(args, metaOpt, m.LyricsSourceField!, string.Empty);
            }
            else
            {
                string? field = (!m.LyricsFromLrc && !string.IsNullOrEmpty(m.LyricsSourceField))
                    ? m.LyricsSourceField
                    : DefaultLyricsField(ext);
                if (!string.IsNullOrEmpty(field))
                    AddMeta(args, metaOpt, field!, m.Lyrics ?? string.Empty);
            }

            args.AddRange(new[] { "-y", outPath });
            return args;
        }

        private static bool IsStreamTagFormat(string ext)
            => ext == ".ogg" || ext == ".opus" || ext == ".oga";

        private static void AddMeta(List<string> args, string metaOpt, string key, string value)
        {
            args.Add(metaOpt);
            args.Add(key + "=" + (value ?? string.Empty));
        }

        // OGG/Opus write: audio stream copied, all tags (managed + preserved unknown + lyrics)
        // and the cover rebuilt as a METADATA_BLOCK_PICTURE comment come from a ffmetadata
        // input file mapped onto the audio stream. -map_metadata replaces the stream's tags
        // wholesale, which is why every tag we want to keep must be written into the file.
        private static async Task<bool> WriteOggAsync(string ffmpegPath, string local, string outPath, string ext, TrackMetadata m, string tempDir)
        {
            string? coverImage = null;
            string? extractedCover = null;
            try
            {
                if (!m.RemoveCover)
                {
                    if (!string.IsNullOrWhiteSpace(m.NewCoverImagePath) && File.Exists(m.NewCoverImagePath!))
                    {
                        coverImage = m.NewCoverImagePath;
                    }
                    else
                    {
                        // Pull the existing cover out losslessly (-c copy) so re-embedding does
                        // not recompress it on every edit.
                        extractedCover = Path.Combine(tempDir, "tagedit_covraw_" + Guid.NewGuid().ToString("N") + ".img");
                        bool got = await RunFfmpegAsync(ffmpegPath, new List<string>
                        {
                            "-i", local, "-an", "-map", "0:v:0?", "-frames:v", "1", "-c", "copy", "-f", "image2", "-y", extractedCover
                        }).ConfigureAwait(false);
                        if (got && File.Exists(extractedCover) && new FileInfo(extractedCover).Length > 0)
                            coverImage = extractedCover;
                        else
                            TryDelete(extractedCover);
                    }
                }

                string? pictureB64 = coverImage != null ? BuildPictureBlockBase64(coverImage) : null;
                string metaFile = BuildOggFfmetadataFile(tempDir, ext, m, pictureB64);
                try
                {
                    return await RunFfmpegAsync(ffmpegPath, new List<string>
                    {
                        "-i", local, "-i", metaFile, "-map", "0:a", "-c:a", "copy", "-map_metadata:s:a:0", "1:g", "-y", outPath
                    }).ConfigureAwait(false);
                }
                finally
                {
                    TryDelete(metaFile);
                }
            }
            finally
            {
                if (extractedCover != null) TryDelete(extractedCover);
            }
        }

        private static string BuildOggFfmetadataFile(string tempDir, string ext, TrackMetadata m, string? pictureB64)
        {
            var sb = new StringBuilder();
            sb.Append(";FFMETADATA1\n");

            if (m.ExtraTags != null)
                foreach (var kv in m.ExtraTags)
                    AppendFfmeta(sb, kv.Key, kv.Value);

            AppendFfmeta(sb, "title", m.Title);
            AppendFfmeta(sb, "album", m.Album);
            AppendFfmeta(sb, "artist", m.Artist);
            AppendFfmeta(sb, "album_artist", m.AlbumArtist);
            AppendFfmeta(sb, "composer", m.Composer);
            AppendFfmeta(sb, "genre", m.Genre);
            AppendFfmeta(sb, "track", m.TrackNumber);
            AppendFfmeta(sb, "disc", m.DiscNumber);
            AppendFfmeta(sb, "date", m.Year);
            AppendFfmeta(sb, "comment", m.Comment);

            // Embed lyrics unless they are going to a .lrc file. When going to .lrc, omitting
            // the field here drops any old embedded lyrics so the .lrc is not shadowed.
            if (!m.SaveLyricsAsLrc)
            {
                string? field = (!m.LyricsFromLrc && !string.IsNullOrEmpty(m.LyricsSourceField))
                    ? m.LyricsSourceField
                    : DefaultLyricsField(ext);
                if (!string.IsNullOrEmpty(field))
                    AppendFfmeta(sb, field!, m.Lyrics ?? string.Empty);
            }

            if (!string.IsNullOrEmpty(pictureB64))
                AppendFfmeta(sb, "metadata_block_picture", pictureB64!);

            string path = Path.Combine(tempDir, "tagedit_wmeta_" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }

        private static void AppendFfmeta(StringBuilder sb, string key, string value)
        {
            sb.Append(key);
            sb.Append('=');
            sb.Append(EscapeFfmeta(value ?? string.Empty));
            sb.Append('\n');
        }

        // ffmetadata escapes \, =, ;, # and newline with a leading backslash.
        private static string EscapeFfmeta(string v)
        {
            if (string.IsNullOrEmpty(v)) return string.Empty;
            var sb = new StringBuilder(v.Length + 8);
            foreach (char c in v)
            {
                if (c == '\r') continue;
                if (c == '\\' || c == '=' || c == ';' || c == '#') { sb.Append('\\'); sb.Append(c); }
                else if (c == '\n') { sb.Append('\\'); sb.Append('\n'); }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // Build a FLAC METADATA_BLOCK_PICTURE (front cover, type 3) and base64-encode it.
        // Width/height/depth/colors are left at zero; players read them from the image data.
        private static string? BuildPictureBlockBase64(string imagePath)
        {
            try
            {
                byte[] data = File.ReadAllBytes(imagePath);
                if (data.Length == 0) return null;
                byte[] mime = Encoding.ASCII.GetBytes(SniffImageMime(data));

                using var ms = new MemoryStream();
                void U32(uint v)
                {
                    ms.WriteByte((byte)(v >> 24));
                    ms.WriteByte((byte)(v >> 16));
                    ms.WriteByte((byte)(v >> 8));
                    ms.WriteByte((byte)v);
                }
                U32(3);
                U32((uint)mime.Length); ms.Write(mime, 0, mime.Length);
                U32(0);                      // empty description
                U32(0); U32(0); U32(0); U32(0);
                U32((uint)data.Length); ms.Write(data, 0, data.Length);
                return Convert.ToBase64String(ms.ToArray());
            }
            catch (Exception ex)
            {
                Debugger.show("[TAGEDIT] picture block build failed: " + ex.Message);
                return null;
            }
        }

        private static string SniffImageMime(byte[] d)
        {
            if (d.Length >= 4 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47)
                return "image/png";
            return "image/jpeg";
        }

        private static void ParseFfmetadata(string text, TrackMetadata meta)
        {
            foreach (var (rawKey, value) in ParseFfmetadataEntries(text))
            {
                string key = rawKey.Trim();
                string lower = key.ToLowerInvariant();

                bool mapped = false;
                foreach (var (k, set) in ReadMap)
                {
                    if (k == lower) { set(meta, value); mapped = true; break; }
                }
                if (mapped) continue;

                // Lyrics can live under several keys depending on the format/tagger:
                // USLT shows as "lyrics" or "lyrics-<lang>", Vorbis uses UNSYNCEDLYRICS,
                // MP4 uses "lyrics" (the c-lyr atom), WMA uses WM/Lyrics. Keep the first
                // non-empty one and remember the exact key so we can write back to it.
                if (string.IsNullOrEmpty(meta.Lyrics) && !string.IsNullOrWhiteSpace(value) && IsLyricKey(lower))
                {
                    meta.Lyrics = value;
                    meta.LyricsSourceField = key;
                    continue;
                }
                if (IsLyricKey(lower)) continue;

                // Anything else is a tag we do not manage. Keep it (e.g. PURL, SYNOPSIS,
                // LANGUAGE) so rewriting an OGG/Opus file does not strip it. Skip ffmpeg's
                // own "encoder" line and the cover picture comment, which are handled
                // elsewhere or regenerated.
                if (lower == "encoder" || lower == "metadata_block_picture" || key.Length == 0)
                    continue;
                meta.ExtraTags[key] = value;
            }
        }

        private static bool IsLyricKey(string lowerKey)
            => lowerKey == "lyrics"
            || lowerKey.StartsWith("lyrics-", StringComparison.Ordinal)
            || lowerKey == "unsyncedlyrics"
            || lowerKey == "syncedlyrics"
            || lowerKey == "wm/lyrics";

        // ffmetadata uses key=value lines, with \, =, ;, # and newline escaped by a leading
        // backslash. A backslash before a real newline means the value continues onto the
        // next physical line, so multi-line values (lyrics) must be reassembled before split.
        private static List<(string key, string value)> ParseFfmetadataEntries(string text)
        {
            var entries = new List<(string, string)>();
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");

            var logical = new List<string>();
            var cur = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\\' && i + 1 < text.Length)
                {
                    cur.Append(c);
                    cur.Append(text[i + 1]);
                    i++;
                    continue;
                }
                if (c == '\n')
                {
                    logical.Add(cur.ToString());
                    cur.Clear();
                    continue;
                }
                cur.Append(c);
            }
            if (cur.Length > 0) logical.Add(cur.ToString());

            foreach (var line in logical)
            {
                if (line.Length == 0) continue;
                if (line[0] == ';' || line[0] == '#' || line[0] == '[') continue;

                int eq = -1;
                for (int k = 0; k < line.Length; k++)
                {
                    if (line[k] == '\\') { k++; continue; }
                    if (line[k] == '=') { eq = k; break; }
                }
                if (eq <= 0) continue;

                entries.Add((Unescape(line.Substring(0, eq)), Unescape(line.Substring(eq + 1))));
            }
            return entries;
        }

        // ffmetadata escapes \, =, ;, # and newline with a leading backslash.
        private static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char next = s[i + 1];
                    if (next == '\\' || next == '=' || next == ';' || next == '#' || next == '\n')
                    {
                        sb.Append(next);
                        i++;
                        continue;
                    }
                }
                sb.Append(s[i]);
            }
            return sb.ToString();
        }

        private static async Task<bool> RunFfmpegAsync(string ffmpegPath, List<string> argList)
        {
            try
            {
                if (!File.Exists(ffmpegPath))
                {
                    Debugger.show("[TAGEDIT] ffmpeg not found at: " + ffmpegPath);
                    return false;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    StandardErrorEncoding = Encoding.UTF8,
                    StandardOutputEncoding = Encoding.UTF8
                };
                psi.ArgumentList.Add("-hide_banner");
                foreach (var a in argList) psi.ArgumentList.Add(a);

                using var proc = Process.Start(psi);
                if (proc == null) return false;

                string stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
                _ = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                await proc.WaitForExitAsync().ConfigureAwait(false);

                if (proc.ExitCode != 0)
                    Debugger.show("[TAGEDIT] ffmpeg exit " + proc.ExitCode + ": " + stderr);

                return proc.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Debugger.show("[TAGEDIT] ffmpeg run failed: " + ex.Message);
                return false;
            }
        }

        private static string SafeExtension(string remotePath)
        {
            string ext = Path.GetExtension(remotePath);
            return string.IsNullOrWhiteSpace(ext) ? string.Empty : ext.ToLowerInvariant();
        }

        private static string RemoteDirectory(string remotePath)
        {
            string p = remotePath.Replace('\\', '/');
            int slash = p.LastIndexOf('/');
            return slash <= 0 ? "" : p.Substring(0, slash);
        }

        private static string FileNameWithoutExtension(string remotePath)
        {
            string p = remotePath.Replace('\\', '/');
            int slash = p.LastIndexOf('/');
            string file = slash >= 0 ? p.Substring(slash + 1) : p;
            int dot = file.LastIndexOf('.');
            return dot > 0 ? file.Substring(0, dot) : file;
        }

        private static string ParentFolderName(string remotePath)
        {
            string p = remotePath.Replace('\\', '/').TrimEnd('/');
            int slash = p.LastIndexOf('/');
            if (slash <= 0) return string.Empty;
            string dir = p.Substring(0, slash);            // drop the file name
            int slash2 = dir.LastIndexOf('/');
            return slash2 >= 0 ? dir.Substring(slash2 + 1) : dir;
        }

        // The field a new (no prior source) embedded lyric goes into, matching what Musicolet
        // writes per format. WAV is not here because WAV lyrics are .lrc only.
        private static string? DefaultLyricsField(string ext) => ext switch
        {
            ".mp3" => "lyrics-",                 // USLT, empty language
            ".flac" => "UNSYNCEDLYRICS",
            ".ogg" => "UNSYNCEDLYRICS",
            ".opus" => "UNSYNCEDLYRICS",
            ".m4a" => "lyrics",                  // c-lyr atom
            ".mp4" => "lyrics",
            ".m4b" => "lyrics",
            ".wma" => "WM/Lyrics",
            _ => "lyrics"
        };

        private static string RemoteLrcPath(string remotePath)
        {
            string p = remotePath.Replace('\\', '/');
            int slash = p.LastIndexOf('/');
            string dir = slash >= 0 ? p.Substring(0, slash) : "";
            string file = slash >= 0 ? p.Substring(slash + 1) : p;
            int dot = file.LastIndexOf('.');
            string baseName = dot > 0 ? file.Substring(0, dot) : file;
            return (dir.Length > 0 ? dir + "/" : "") + baseName + ".lrc";
        }

        // Write the lyrics to a local temp .lrc, push to a temp name in the same folder, then
        // rename over the sibling .lrc so a failed push never leaves a half-written file.
        private static async Task PushLrcAsync(string device, string lrcRemote, string text, string tempDir)
        {
            string localLrc = Path.Combine(tempDir, "tagedit_lrcout_" + Guid.NewGuid().ToString("N") + ".lrc");
            try
            {
                Directory.CreateDirectory(tempDir);
                File.WriteAllText(localLrc, text, new UTF8Encoding(false));

                string dir = RemoteDirectory(lrcRemote);
                string remoteTmp = dir + "/.ampl_lrc_tmp_" + Guid.NewGuid().ToString("N") + ".lrc";

                await AdbHelper.RunAdbCaptureAsync($"-s {device} push \"{localLrc}\" \"{remoteTmp}\"").ConfigureAwait(false);

                string check = await AdbHelper.RunAdbCaptureAsync(
                    $"-s {device} shell test -s {Sq(remoteTmp)} && echo __AMPL_OK__").ConfigureAwait(false);
                if (check.IndexOf("__AMPL_OK__", StringComparison.Ordinal) >= 0)
                    await AdbHelper.RunAdbAsync($"-s {device} shell mv -f {Sq(remoteTmp)} {Sq(lrcRemote)}").ConfigureAwait(false);
                else
                    await AdbHelper.RunAdbAsync($"-s {device} shell rm -f {Sq(remoteTmp)}").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debugger.show("[TAGEDIT] PushLrcAsync failed: " + ex.Message);
            }
            finally
            {
                TryDelete(localLrc);
            }
        }

        // Single-quote a path for an adb shell command, escaping embedded single quotes.
        private static string Sq(string s) => "'" + s.Replace("'", "'\\''") + "'";

        private static long? ParseFirstLong(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            int i = 0;
            while (i < s.Length && !char.IsDigit(s[i])) i++;
            int start = i;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            if (i > start && long.TryParse(s.Substring(start, i - start), out var v)) return v;
            return null;
        }

        private static void TryDelete(string? path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }
}