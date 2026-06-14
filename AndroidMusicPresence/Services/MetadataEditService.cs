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

                // Dump tags via ffmetadata. Parse every key=value line regardless of section
                // so stream-level tags (OGG/Opus store comments on the stream, not globally)
                // are captured too.
                string ffmetaPath = Path.Combine(tempDir, "tagedit_meta_" + Guid.NewGuid().ToString("N") + ".txt");
                bool dumped = await RunFfmpegAsync(ffmpegPath, new List<string>
                {
                    "-i", local, "-f", "ffmetadata", "-y", ffmetaPath
                }).ConfigureAwait(false);

                if (dumped && File.Exists(ffmetaPath))
                {
                    ParseFfmetadata(File.ReadAllText(ffmetaPath, Encoding.UTF8), meta);
                    TryDelete(ffmetaPath);
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

                var args = BuildFfmpegWriteArgs(local, outPath, ext, edited);
                bool encoded = await RunFfmpegAsync(ffmpegPath, args).ConfigureAwait(false);
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

            // Always write every field. Empty string clears the tag, which is the intended
            // behaviour when the user blanks a box.
            AddMeta(args, "title", m.Title);
            AddMeta(args, "album", m.Album);
            AddMeta(args, "artist", m.Artist);
            AddMeta(args, "album_artist", m.AlbumArtist);
            AddMeta(args, "composer", m.Composer);
            AddMeta(args, "genre", m.Genre);
            AddMeta(args, "track", m.TrackNumber);
            AddMeta(args, "disc", m.DiscNumber);
            AddMeta(args, "date", m.Year);
            AddMeta(args, "comment", m.Comment);

            args.AddRange(new[] { "-y", outPath });
            return args;
        }

        private static void AddMeta(List<string> args, string key, string value)
        {
            args.Add("-metadata");
            args.Add(key + "=" + (value ?? string.Empty));
        }

        private static void ParseFfmetadata(string text, TrackMetadata meta)
        {
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw;
                if (line.Length == 0) continue;
                if (line[0] == ';' || line[0] == '#' || line[0] == '[') continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string value = Unescape(line.Substring(eq + 1));

                foreach (var (k, set) in ReadMap)
                {
                    if (k == key)
                    {
                        set(meta, value);
                        break;
                    }
                }
            }
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