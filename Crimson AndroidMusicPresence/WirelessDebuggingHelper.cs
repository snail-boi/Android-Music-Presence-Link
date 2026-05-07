using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace musicpresense
{
    /// <summary>
    /// Wraps the Android 11+ "Wireless Debugging" workflow:
    /// pairing (one-time TLS handshake), mDNS service discovery
    /// (because the connection port is randomly assigned each time
    /// wireless debugging toggles on), and connecting.
    ///
    /// Notes:
    /// - "adb mdns services" needs to find the device. We force the
    ///   openscreen mDNS backend via the ADB_MDNS_OPENSCREEN env var
    ///   on the spawned process; the bundled mDNS in adb is unreliable.
    /// - Pairing port and connection port are different. Both are
    ///   shown in the phone's Wireless Debugging settings, but on
    ///   different sub-screens.
    /// - The mDNS service name (e.g. "adb-XXXXXXXX-XXXXXX") is the
    ///   only stable identifier across reboots and IP changes once
    ///   the device is paired. Persist it.
    /// </summary>
    public static class WirelessDebuggingHelper
    {
        private static readonly Regex MdnsLineRegex = new Regex(
            @"^\s*(?<name>adb-\S+)\s+(?<type>\S+)\s+(?<addr>\d+\.\d+\.\d+\.\d+):(?<port>\d+)\s*$",
            RegexOptions.Compiled);

        private static readonly Regex IpPortRegex = new Regex(
            @"^\s*(?<ip>\d+\.\d+\.\d+\.\d+):(?<port>\d+)\s*$",
            RegexOptions.Compiled);

        public sealed class MdnsService
        {
            public string Name { get; set; } = string.Empty;       // adb-XXXXXXXX-XXXXXX
            public string ServiceType { get; set; } = string.Empty; // _adb-tls-connect._tcp etc.
            public string Address { get; set; } = string.Empty;    // 192.168.x.y
            public int Port { get; set; }                          // current connect port
            public string IpPort => $"{Address}:{Port}";
        }

        public sealed class PairResult
        {
            public bool Success { get; set; }
            public string ServiceName { get; set; } = string.Empty;
            public string Output { get; set; } = string.Empty;
        }

        /// <summary>
        /// Pair this workstation with a phone using a 6-digit code shown on
        /// the phone's "Pair device with pairing code" screen.
        /// </summary>
        /// <param name="ipPair">"ip:pair_port" from the phone screen.</param>
        /// <param name="code">6-digit pairing code.</param>
        public static async Task<PairResult> PairAsync(string ipPair, string code)
        {
            var result = new PairResult();

            if (string.IsNullOrWhiteSpace(ipPair) || !IpPortRegex.IsMatch(ipPair))
            {
                result.Output = "Invalid pairing address. Expected format ip:port.";
                return result;
            }

            if (string.IsNullOrWhiteSpace(code) || code.Length < 4)
            {
                result.Output = "Invalid pairing code.";
                return result;
            }

            try
            {
                var output = await RunAdbProcessWithStdinAsync(
                    $"pair {ipPair}",
                    code + Environment.NewLine).ConfigureAwait(false);

                result.Output = output;

                // Successful output contains "Successfully paired to <ip:port> [guid=adb-XXXX...]"
                var nameMatch = Regex.Match(output, @"adb-[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*");
                if (output.IndexOf("Successfully paired", StringComparison.OrdinalIgnoreCase) >= 0
                    || nameMatch.Success)
                {
                    result.Success = true;
                    result.ServiceName = nameMatch.Success ? nameMatch.Value : string.Empty;
                }
            }
            catch (Exception ex)
            {
                Debugger.show("WirelessDebuggingHelper.PairAsync failed: " + ex.Message);
                result.Output = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Run "adb mdns services" and parse the result, looking for
        /// _adb-tls-connect._tcp services (the ones we can connect to).
        /// </summary>
        public static async Task<List<MdnsService>> ListServicesAsync()
        {
            var services = new List<MdnsService>();

            try
            {
                var output = await RunAdbProcessWithStdinAsync(
                    "mdns services",
                    string.Empty).ConfigureAwait(false);

                foreach (var raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var match = MdnsLineRegex.Match(raw);
                    if (!match.Success) continue;
                    if (!int.TryParse(match.Groups["port"].Value, out var port)) continue;

                    services.Add(new MdnsService
                    {
                        Name = match.Groups["name"].Value,
                        ServiceType = match.Groups["type"].Value,
                        Address = match.Groups["addr"].Value,
                        Port = port
                    });
                }
            }
            catch (Exception ex)
            {
                Debugger.show("WirelessDebuggingHelper.ListServicesAsync failed: " + ex.Message);
            }

            return services;
        }

        /// <summary>
        /// Find a previously-paired device by its mDNS service name.
        /// Filters to _adb-tls-connect._tcp services (the connectable ones).
        /// Returns null if no match was found.
        /// </summary>
        public static async Task<MdnsService?> FindServiceAsync(string serviceName)
        {
            if (string.IsNullOrWhiteSpace(serviceName)) return null;

            var all = await ListServicesAsync().ConfigureAwait(false);

            // Prefer _adb-tls-connect._tcp matches. These are the ports we can
            // actually use; pairing-only entries appear as _adb-tls-pairing._tcp.
            return all.FirstOrDefault(s =>
                       string.Equals(s.Name, serviceName, StringComparison.OrdinalIgnoreCase)
                       && s.ServiceType.Contains("connect", StringComparison.OrdinalIgnoreCase))
                   ?? all.FirstOrDefault(s =>
                       string.Equals(s.Name, serviceName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// adb connect ip:port, returning true if the device shows up
        /// as "device" in adb devices afterwards.
        /// </summary>
        public static async Task<bool> ConnectAsync(string ipPort)
        {
            if (string.IsNullOrWhiteSpace(ipPort) || !IpPortRegex.IsMatch(ipPort))
                return false;

            try
            {
                await AdbHelper.RunAdbCaptureAsync($"connect {ipPort}").ConfigureAwait(false);
                await Task.Delay(500).ConfigureAwait(false);

                var devices = await AdbHelper.RunAdbCaptureAsync("devices").ConfigureAwait(false);
                var lines = devices.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                return lines.Any(l => l.StartsWith(ipPort, StringComparison.OrdinalIgnoreCase)
                                      && l.EndsWith("device", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                Debugger.show("WirelessDebuggingHelper.ConnectAsync failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Full reconnect cycle for a paired device:
        /// 1. mDNS lookup for the persisted service name
        /// 2. If found, connect to the discovered ip:port
        /// 3. Returns the live ip:port on success, empty string on failure.
        /// </summary>
        public static async Task<string> ReconnectViaMdnsAsync(string serviceName)
        {
            var service = await FindServiceAsync(serviceName).ConfigureAwait(false);
            if (service == null) return string.Empty;

            var ok = await ConnectAsync(service.IpPort).ConfigureAwait(false);
            return ok ? service.IpPort : string.Empty;
        }

        /// <summary>
        /// Best-effort attempt to use a stale ip:port we cached from a previous
        /// successful connect. Useful if mDNS is blocked by the LAN but the
        /// phone happens to still be on the same port (rare but free to try).
        /// </summary>
        public static async Task<bool> TryConnectLastKnownAsync(string ipPort)
        {
            if (string.IsNullOrWhiteSpace(ipPort)) return false;
            return await ConnectAsync(ipPort).ConfigureAwait(false);
        }

        // ----- internal -----

        /// <summary>
        /// Run an adb command directly (no shell session) with optional stdin
        /// piping, and the ADB_MDNS_OPENSCREEN env var set so mDNS uses the
        /// more reliable openscreen backend. Returns combined stdout/stderr.
        /// </summary>
        private static async Task<string> RunAdbProcessWithStdinAsync(string args, string stdinPayload)
        {
            if (string.IsNullOrWhiteSpace(AdbHelper.AdbPath) || !File.Exists(AdbHelper.AdbPath))
            {
                Debugger.show("ADB path not set or missing: " + AdbHelper.AdbPath);
                return string.Empty;
            }

            var psi = new ProcessStartInfo(AdbHelper.AdbPath, args)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.Environment["ADB_MDNS_OPENSCREEN"] = "1";

            try
            {
                using var process = Process.Start(psi);
                if (process == null) return string.Empty;

                if (!string.IsNullOrEmpty(stdinPayload))
                {
                    await process.StandardInput.WriteAsync(stdinPayload).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync().ConfigureAwait(false);
                }
                process.StandardInput.Close();

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().ConfigureAwait(false);

                var output = await outputTask.ConfigureAwait(false);
                var error = await errorTask.ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(error))
                {
                    Debugger.show("ADB (wireless) stderr: " + error.Trim());
                }

                return string.IsNullOrEmpty(output) ? error : output + Environment.NewLine + error;
            }
            catch (Exception ex)
            {
                Debugger.show("WirelessDebuggingHelper subprocess failed: " + ex.Message);
                return string.Empty;
            }
        }
    }
}