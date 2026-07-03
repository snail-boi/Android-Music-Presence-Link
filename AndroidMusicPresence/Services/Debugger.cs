using System.Diagnostics;
using System.IO;

namespace AndroidMusicPresenceLink
{
    internal static class Debugger
    {
        private static readonly object syncRoot = new();
        private static readonly string logDirectory = AppPaths.GetDataPath("logs");

        private const int MaxLogFiles = 5;
        private static readonly string latestLogPath = Path.Combine(logDirectory, "musicpresence_latest.log");
        private static readonly string advancedLogPath = Path.Combine(logDirectory, "advanced_debug.log");
        private static bool rotated;
        private static DateTime? lastEntryUtc;
        private static StreamWriter? advancedWriter;
        private static bool advancedEnabled;

        public static bool IsEnabled { get; set; }

        // Advanced mode replaces normal logging entirely: every show() call plus the
        // per-command adb traffic from AdbHelper goes to a single advanced_debug.log,
        // which starts fresh each time the mode turns on.
        public static bool AdvancedEnabled
        {
            get => advancedEnabled;
            set
            {
                lock (syncRoot)
                {
                    if (advancedEnabled == value)
                        return;

                    advancedEnabled = value;
                    if (value)
                        OpenAdvancedLog();
                    else
                        CloseAdvancedLog();
                }
            }
        }

        // Gap between entries that triggers a separator line. Kept in sync with the
        // poll interval by MusicPresenceService so slow/adaptive polling doesn't put
        // a separator between every routine tick.
        public static TimeSpan SeparatorGap { get; set; } = TimeSpan.FromSeconds(5);

        internal static string LogDirectory => logDirectory;

        public static void show(string message)
        {
            if (advancedEnabled)
            {
                WriteAdvanced(message);
                return;
            }

            if (!IsEnabled) return;

            lock (syncRoot)
            {
                if (!rotated)
                {
                    RotateLogs();
                    rotated = true;
                }

                var now = DateTime.Now;
                if (lastEntryUtc.HasValue && (now.ToUniversalTime() - lastEntryUtc.Value) > SeparatorGap)
                {
                    WriteToLogFile("--------------------------------------------------");
                }

                string logEntry = $"{now:yyyy-MM-dd HH:mm:ss} - {message}";
                Debug.WriteLine(logEntry);
                WriteToLogFile(logEntry);
                lastEntryUtc = now.ToUniversalTime();
            }
        }

        // Extra-verbose channel for per-adb-command tracing. No-op unless advanced
        // mode is on, so call sites don't need their own guards.
        public static void advanced(string message)
        {
            if (!advancedEnabled) return;
            WriteAdvanced(message);
        }

        private static void WriteAdvanced(string message)
        {
            lock (syncRoot)
            {
                if (advancedWriter == null) return;

                try
                {
                    string entry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - {message}";
                    Debug.WriteLine(entry);
                    advancedWriter.WriteLine(entry);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Advanced Logging Error] {ex.Message}");
                }
            }
        }

        private static void OpenAdvancedLog()
        {
            try
            {
                if (!Directory.Exists(logDirectory))
                    Directory.CreateDirectory(logDirectory);

                // AutoFlush keeps the file intact after a crash (the whole point of the
                // mode) while still being far cheaper than reopening the file per line.
                advancedWriter = new StreamWriter(advancedLogPath, append: false) { AutoFlush = true };
                advancedWriter.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - ===== ADVANCED DEBUG LOG STARTED =====");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Advanced Log Open Error] {ex.Message}");
                advancedWriter = null;
            }
        }

        private static void CloseAdvancedLog()
        {
            try
            {
                advancedWriter?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - ===== ADVANCED DEBUG LOG ENDED =====");
                advancedWriter?.Dispose();
            }
            catch { }

            advancedWriter = null;
        }

        private static void WriteToLogFile(string message)
        {
            try
            {
                if (!Directory.Exists(logDirectory))
                    Directory.CreateDirectory(logDirectory);

                File.AppendAllText(latestLogPath, message + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Logging Error] {ex.Message}");
            }
        }

        private static void RotateLogs()
        {
            try
            {
                if (!Directory.Exists(logDirectory))
                    Directory.CreateDirectory(logDirectory);

                string oldestLog = Path.Combine(logDirectory, $"musicpresence_debug{MaxLogFiles - 1}.log");
                if (File.Exists(oldestLog))
                    File.Delete(oldestLog);

                for (int i = MaxLogFiles - 2; i >= 1; i--)
                {
                    string src = Path.Combine(logDirectory, $"musicpresence_debug{i}.log");
                    string dest = Path.Combine(logDirectory, $"musicpresence_debug{i + 1}.log");

                    if (File.Exists(src))
                        File.Move(src, dest);
                }

                string firstBackup = Path.Combine(logDirectory, "musicpresence_debug1.log");
                if (File.Exists(latestLogPath))
                    File.Move(latestLogPath, firstBackup);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Log Rotation Error] {ex.Message}");
            }
        }
    }
}
