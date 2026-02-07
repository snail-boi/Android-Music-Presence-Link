using System.IO;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace musicpresense
{
    public static class AdbHelper
    {
        public static string AdbPath { get; set; } = string.Empty;

        public static Task RunAdbAsync(string args)
        {
            return Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(AdbPath) || !File.Exists(AdbPath))
                {
                    Debugger.show("ADB path not set or missing: " + AdbPath);
                    return;
                }

                var psi = new ProcessStartInfo(AdbPath, args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                try
                {
                    using var proc = Process.Start(psi);
                    proc?.WaitForExit();
                }
                catch (Exception ex)
                {
                    Debugger.show("ADB error: " + ex.Message);
                }
            });
        }

        public static Task<string> RunAdbCaptureAsync(string args)
        {
            return Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(AdbPath) || !File.Exists(AdbPath))
                {
                    Debugger.show("ADB path not set or missing: " + AdbPath);
                    return string.Empty;
                }

                var psi = new ProcessStartInfo(AdbPath, args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                try
                {
                    using var proc = Process.Start(psi);
                    if (proc == null) return string.Empty;

                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit();
                    return output;
                }
                catch (Exception ex)
                {
                    Debugger.show("ADB error: " + ex.Message);
                    return string.Empty;
                }
            });
        }
    }
}
