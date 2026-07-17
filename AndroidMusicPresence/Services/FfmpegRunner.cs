using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// Runs an ffmpeg process with a hard timeout so a hung or stalled ffmpeg (a corrupt
    /// file, a bad codec, a network stall) can never block the app forever. On timeout the
    /// whole process tree is killed, which also unblocks the stdout/stderr reads, and
    /// <see cref="Result.TimedOut"/> is set so callers can treat it as a failure.
    /// </summary>
    internal static class FfmpegRunner
    {
        // Every ffmpeg job here is a tag read/write or cover extraction against a single
        // local file, so 30s is very generous for a healthy run yet still bounds a hang.
        public const int DefaultTimeoutMs = 30000;

        public readonly record struct Result(bool Started, bool TimedOut, int ExitCode, string StdOut, string StdErr)
        {
            public bool Success => Started && !TimedOut && ExitCode == 0;
        }

        public static async Task<Result> RunAsync(ProcessStartInfo psi, int timeoutMs = DefaultTimeoutMs)
        {
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;
            psi.StandardErrorEncoding ??= Encoding.UTF8;
            psi.StandardOutputEncoding ??= Encoding.UTF8;

            using var proc = Process.Start(psi);
            if (proc == null)
                return new Result(false, false, -1, string.Empty, string.Empty);

            // Drain both streams concurrently. If ffmpeg hangs these reads would block
            // forever too; killing the process on timeout is what lets them complete.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            bool timedOut = false;
            using (var cts = new CancellationTokenSource(timeoutMs))
            {
                try
                {
                    await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    timedOut = true;
                    try { if (!proc.HasExited) proc.Kill(true); } catch { }
                    try { await proc.WaitForExitAsync().ConfigureAwait(false); } catch { }
                }
            }

            string stdout = string.Empty;
            string stderr = string.Empty;
            try { stdout = await stdoutTask.ConfigureAwait(false); } catch { }
            try { stderr = await stderrTask.ConfigureAwait(false); } catch { }

            int exit = -1;
            if (!timedOut)
            {
                try { exit = proc.ExitCode; } catch { }
            }

            return new Result(true, timedOut, exit, stdout, stderr);
        }
    }
}
