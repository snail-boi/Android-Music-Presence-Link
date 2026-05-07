using System.Diagnostics;
using System.IO;
using System.Globalization;

namespace musicpresense
{
    internal static class Debugger
    {
        private static readonly object syncRoot = new();
        private static readonly string logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Snail");

        private const int MaxLogFiles = 5;
        private static readonly string latestLogPath = Path.Combine(logDirectory, "musicpresence_latest.log");
        private static bool rotated;

        public static bool IsEnabled { get; set; }

        public static void show(string message)
        {
            if (!IsEnabled) return;

            lock (syncRoot)
            {
                if (!rotated)
                {
                    RotateLogs();
                    rotated = true;
                }

                var now = DateTime.Now;
                var lastEntryUtc = GetLastLogEntryUtc();
                if (lastEntryUtc.HasValue && (now.ToUniversalTime() - lastEntryUtc.Value).TotalSeconds > 5)
                {
                    WriteToLogFile("--------------------------------------------------");
                }

                string logEntry = $"{now:yyyy-MM-dd HH:mm:ss} - {message}";
                Debug.WriteLine(logEntry);
                WriteToLogFile(logEntry);
            }
        }

        private static DateTime? GetLastLogEntryUtc()
        {
            if (!File.Exists(latestLogPath))
                return null;

            try
            {
                var lines = File.ReadAllLines(latestLogPath);
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    var line = lines[i];
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    if (line.Length < 19)
                        continue;

                    var stamp = line.Substring(0, 19);
                    if (DateTime.TryParseExact(stamp, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                        return DateTime.SpecifyKind(parsed, DateTimeKind.Local).ToUniversalTime();
                }
            }
            catch
            {
            }

            return null;
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
