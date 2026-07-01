using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// Device-level queries that sit on top of AdbHelper: parsing `adb devices`,
    /// reading device properties, and resolving which connected device to target.
    /// AdbHelper stays a thin transport; the parsing lives here.
    /// </summary>
    internal static class DeviceQuery
    {
        public static async Task<string> GetConnectedUsbDeviceAsync()
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var devices = await AdbHelper.RunAdbCaptureAsync("devices");
                var deviceList = devices.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var entry in deviceList)
                {
                    if (!entry.EndsWith("device", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var serial = entry.Split('\t', ' ').FirstOrDefault();
                    if (string.IsNullOrWhiteSpace(serial))
                        continue;

                    if (!serial.Contains(':'))
                        return serial;
                }

                if (attempt < 7)
                {
                    await Task.Delay(500);
                }
            }

            return string.Empty;
        }

        public static async Task<int> GetWifiPortAsync(string usbSerial)
        {
            var output = await AdbHelper.RunAdbCaptureAsync($"-s {usbSerial} shell getprop service.adb.tcp.port");
            if (int.TryParse(output.Trim(), out var port) && port > 0)
                return port;

            return 5555;
        }

        public static async Task<string> GetDeviceWifiIpAsync(string usbDevice)
        {
            var ipOutput = await AdbHelper.RunAdbCaptureAsync($"-s {usbDevice} shell ip -f inet addr show wlan0");
            var match = Regex.Match(ipOutput, @"inet\s+(?<ip>\d+\.\d+\.\d+\.\d+)");
            if (match.Success)
                return match.Groups["ip"].Value;

            var routeOutput = await AdbHelper.RunAdbCaptureAsync($"-s {usbDevice} shell ip route");
            match = Regex.Match(routeOutput, @"src\s+(?<ip>\d+\.\d+\.\d+\.\d+)");
            return match.Success ? match.Groups["ip"].Value : string.Empty;
        }

        public static async Task<string> GetDeviceSerialAsync(string device)
        {
            if (string.IsNullOrWhiteSpace(device))
                return string.Empty;

            var serial = await AdbHelper.RunAdbCaptureAsync($"-s {device} shell getprop ro.serialno");
            serial = serial.Trim();
            if (!string.IsNullOrWhiteSpace(serial))
                return serial;

            serial = await AdbHelper.RunAdbCaptureAsync($"-s {device} shell getprop ro.boot.serialno");
            return serial.Trim();
        }

        /// <summary>
        /// Resolves the serial of the device we should currently talk to, given the saved
        /// config. USB always wins. In Wireless Debugging mode the live serial is discovered
        /// via mDNS reconnect; otherwise the saved Wi-Fi endpoint is used.
        /// </summary>
        public static async Task<string> ResolveActiveDeviceAsync(MusicConfig config)
        {
            var devices = await AdbHelper.RunAdbCaptureAsync("devices").ConfigureAwait(false);
            var deviceList = devices.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            bool IsDeviceConnected(string id) => deviceList.Any(l => l.StartsWith(id) && l.EndsWith("device"));

            bool IsWirelessSerial(string serial)
            {
                if (string.IsNullOrWhiteSpace(serial))
                    return false;

                return serial.Contains(':')
                    || serial.StartsWith("adb-", StringComparison.OrdinalIgnoreCase)
                    || serial.IndexOf("_adb-tls", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            string FindConnectedWirelessSerial()
            {
                foreach (var entry in deviceList)
                {
                    if (!entry.EndsWith("device", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var serial = entry.Split('\t', ' ').FirstOrDefault();
                    if (IsWirelessSerial(serial))
                        return serial ?? string.Empty;
                }

                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(config.Device.SelectedDeviceUSB) && IsDeviceConnected(config.Device.SelectedDeviceUSB))
            {
                return config.Device.SelectedDeviceUSB;
            }

            // USB-only mode: never attempt a wireless connection.
            if (config.Device.WifiMode == WirelessMode.UsbOnly)
                return string.Empty;

            if (config.Device.WifiMode == WirelessMode.WirelessDebugging && !string.IsNullOrWhiteSpace(config.Device.MdnsServiceName))
            {
                var ipPort = await WirelessDebuggingHelper.ReconnectViaMdnsAsync(config.Device.MdnsServiceName).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(ipPort))
                {
                    devices = await AdbHelper.RunAdbCaptureAsync("devices").ConfigureAwait(false);
                    deviceList = devices.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                    var liveWireless = FindConnectedWirelessSerial();
                    if (!string.IsNullOrWhiteSpace(liveWireless))
                        return liveWireless;

                    if (IsDeviceConnected(ipPort))
                        return ipPort;
                }

                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(config.Device.SelectedDeviceWiFi) && config.Device.SelectedDeviceWiFi != "None")
            {
                if (!IsDeviceConnected(config.Device.SelectedDeviceWiFi))
                {
                    await AdbHelper.RunAdbCaptureAsync($"connect {config.Device.SelectedDeviceWiFi}").ConfigureAwait(false);
                    devices = await AdbHelper.RunAdbCaptureAsync("devices").ConfigureAwait(false);
                    deviceList = devices.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                }

                if (IsDeviceConnected(config.Device.SelectedDeviceWiFi))
                    return config.Device.SelectedDeviceWiFi;
            }

            return string.Empty;
        }
    }
}