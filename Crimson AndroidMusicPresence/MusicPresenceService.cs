using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace musicpresense
{
    internal sealed class MusicPresenceService : IDisposable
    {
        private MusicConfig _config;
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _timer;
        private readonly SemaphoreSlim _updateLock = new SemaphoreSlim(1, 1);
        private readonly MediaController _mediaController;
        private string _currentDevice = string.Empty;
        private bool _wifiReconnectPromptShown;
        private bool _isRecoveringWifi;
        private DateTimeOffset? _pausedSince;
        private string? _pausedSignature;
        private bool _smtcPausedCleared;
        private bool _currentDeviceIsUsb;
        private bool _wifiNeedsUsbReconnect;
        private string? _lastNowPlayingTitle;
        private string? _lastNowPlayingArtist;
        private string? _lastNowPlayingAlbum;
        private string? _lastScrambledMetadata;
        private string? _lastParsedTitle;
        private string? _lastParsedArtist;
        private string? _lastParsedAlbum;
        private bool _lastParseSuccess;
        private int _reparseTicksRemaining;

        internal string CurrentDevice => _currentDevice;
        internal event Action<TrayIconState>? TrayStateChanged;
        internal event Action<string?, string?, string?>? NowPlayingChanged;
        internal event Action<string?, string?, string?, bool, long>? LyricsPlaybackChanged;
        internal event Action<string?, string?, string?, string?, bool, long, long>? MediaPlayerStateChanged;

        public MusicPresenceService(Dispatcher dispatcher, MusicConfig config)
        {
            _dispatcher = dispatcher;
            _config = config;
            _mediaController = new MediaController(dispatcher, () => _currentDevice, async () => { await UpdateCurrentSongAsync().ConfigureAwait(false); }, config);
            _mediaController.Initialize();

            _timer = new DispatcherTimer
            {
                Interval = GetInterval(config.UpdateIntervalMode)
            };
            _timer.Tick += async (_, __) => await TickAsync();
        }

        public void Start()
        {
            if (_timer.Interval.TotalSeconds > 0)
                _timer.Start();

            NotifyTrayState(TrayIconState.NoDevice);
            _ = TickAsync();
        }

        public void UpdateConfig(MusicConfig config)
        {
            _config = config;
            _mediaController.UpdateConfig(config);

            var interval = GetInterval(config.UpdateIntervalMode);
            _timer.Interval = interval;
            if (interval.TotalSeconds <= 0)
            {
                _timer.Stop();
            }
            else if (!_timer.IsEnabled)
            {
                _timer.Start();
            }
        }

        private async Task TickAsync()
        {
            if (!await _updateLock.WaitAsync(0)) return;

            try
            {
                await DetectDeviceAsync().ConfigureAwait(false);

                bool hasActiveSong = false;
                if (!string.IsNullOrEmpty(_currentDevice))
                {
                    hasActiveSong = await UpdateCurrentSongAsync().ConfigureAwait(false);
                }
                else
                {
                    NotifyNowPlaying(null, null, null);
                    NotifyLyricsPlayback(null, null, null, false, 0);
                    NotifyMediaPlayerState(null, null, null, null, false, 0, 0);
                }

                NotifyTrayState(BuildTrayState(hasActiveSong));
            }
            catch (Exception ex)
            {
                Debugger.show("MusicPresenceService tick failed: " + ex.Message);
            }
            finally
            {
                _updateLock.Release();
            }
        }

        // True if the given adb-devices serial is a wired USB device.
        // Wireless serials come in two shapes:
        //   - TCP/IP:             "192.168.1.50:5555"   (contains ':')
        //   - Wireless Debugging: "adb-XXXXXX-XXXX._adb-tls-connect._tcp"
        //                         (starts with "adb-", contains "._tcp" or "_adb-tls")
        // Anything else is treated as USB.
        private static bool IsWirelessSerial(string serial)
        {
            if (string.IsNullOrWhiteSpace(serial)) return false;
            if (serial.Contains(':')) return true;
            if (serial.StartsWith("adb-", StringComparison.OrdinalIgnoreCase)) return true;
            if (serial.IndexOf("_adb-tls", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // Returns the serial of the first online wireless device in the
        // adb-devices output, or empty if none. Handles both transports.
        private static string FindConnectedWifiSerial(string[] deviceList)
        {
            foreach (var entry in deviceList)
            {
                if (!entry.EndsWith("device"))
                    continue;

                var serial = entry.Split('\t', ' ').FirstOrDefault();
                if (string.IsNullOrWhiteSpace(serial))
                    continue;

                if (IsWirelessSerial(serial))
                    return serial;
            }
            return string.Empty;
        }

        // True if any wireless device is currently online in adb devices.
        private static bool IsWifiCurrentlyConnected(string[] deviceList)
        {
            return !string.IsNullOrEmpty(FindConnectedWifiSerial(deviceList));
        }

        private static string GetOnlineSerial(string entry)
        {
            if (string.IsNullOrWhiteSpace(entry))
                return string.Empty;

            var parts = entry.Trim().Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return string.Empty;

            return parts[^1].Equals("device", StringComparison.OrdinalIgnoreCase)
                ? parts[0].Trim()
                : string.Empty;
        }

        private static string FindConnectedWirelessSerial(string[] deviceList)
        {
            foreach (var entry in deviceList)
            {
                var serial = GetOnlineSerial(entry);
                if (IsWirelessSerial(serial))
                    return serial;
            }

            return string.Empty;
        }

        private async Task DetectDeviceAsync()
        {
            try
            {
                var devices = await AdbHelper.RunAdbCaptureAsync("devices");
                var deviceList = devices.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                string connectedUsb = string.Empty;
                if (!string.IsNullOrWhiteSpace(_config.SelectedDeviceUSB))
                {
                    bool selectedUsbConnected = deviceList.Any(l => GetOnlineSerial(l).Equals(_config.SelectedDeviceUSB, StringComparison.OrdinalIgnoreCase));
                    if (selectedUsbConnected)
                    {
                        connectedUsb = _config.SelectedDeviceUSB;
                    }
                }

                if (string.IsNullOrWhiteSpace(connectedUsb))
                {
                    foreach (var entry in deviceList)
                    {
                        var serial = GetOnlineSerial(entry);
                        if (string.IsNullOrWhiteSpace(serial))
                            continue;

                        if (IsWirelessSerial(serial))
                            continue;

                        connectedUsb = serial;
                        break;
                    }
                }

                if (!string.IsNullOrWhiteSpace(connectedUsb))
                {
                    _currentDevice = connectedUsb;
                    _currentDeviceIsUsb = true;

                    bool wifiConfigured = !string.IsNullOrWhiteSpace(_config.SelectedDeviceWiFi) && _config.SelectedDeviceWiFi != "None";
                    bool wifiConnected = wifiConfigured && IsWifiCurrentlyConnected(deviceList);

                    if (wifiConfigured && !wifiConnected && _config.IsWifiEnabled == true)
                    {
                        _wifiNeedsUsbReconnect = true;
                        await RecoverWirelessConnectionAsync().ConfigureAwait(false);

                        // Recovery may have set _currentDevice to a wireless
                        // serial. Recheck using IsWirelessSerial rather than
                        // a naive contains-':' check, since Wireless Debugging
                        // serials have no colon.
                        if (!string.IsNullOrEmpty(_currentDevice) && IsWirelessSerial(_currentDevice))
                        {
                            _currentDeviceIsUsb = false;
                            _wifiNeedsUsbReconnect = false;
                        }
                        else
                        {
                            _currentDeviceIsUsb = true;
                        }
                    }
                    else
                    {
                        _wifiNeedsUsbReconnect = false;
                    }

                    return;
                }

                var connectedWireless = FindConnectedWirelessSerial(deviceList);
                if (!string.IsNullOrWhiteSpace(connectedWireless))
                {
                    _currentDevice = connectedWireless;
                    _currentDeviceIsUsb = false;
                    _wifiReconnectPromptShown = false;
                    _wifiNeedsUsbReconnect = false;
                    return;
                }

                if (!string.IsNullOrEmpty(_config.SelectedDeviceWiFi) && _config.SelectedDeviceWiFi != "None")
                {
                    bool wifiConnected = IsWifiCurrentlyConnected(deviceList);
                    if (!wifiConnected)
                    {
                        if (_config.WifiMode == WirelessMode.WirelessDebugging)
                        {
                            // The stored ip:port is likely stale (the wireless
                            // debugging port changes every time it toggles),
                            // so route through the recovery path which does
                            // mDNS lookup, then last-known, then USB-assisted.
                            await RecoverWirelessConnectionAsync().ConfigureAwait(false);
                            if (!string.IsNullOrEmpty(_currentDevice) && IsWirelessSerial(_currentDevice))
                            {
                                _currentDeviceIsUsb = false;
                                _wifiReconnectPromptShown = false;
                                _wifiNeedsUsbReconnect = false;
                                return;
                            }
                        }
                        else
                        {
                            // TcpIp: try a direct connect to the fixed port.
                            await AdbHelper.RunAdbCaptureAsync($"connect {_config.SelectedDeviceWiFi}");
                            devices = await AdbHelper.RunAdbCaptureAsync("devices");
                            deviceList = devices.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                            wifiConnected = IsWifiCurrentlyConnected(deviceList);
                        }
                    }

                    if (wifiConnected)
                    {
                        if (deviceList.Any(l => l.StartsWith(_config.SelectedDeviceUSB) && l.EndsWith("device")))
                        {
                            _currentDevice = _config.SelectedDeviceUSB;
                            _currentDeviceIsUsb = true;
                            _wifiReconnectPromptShown = false;
                            _wifiNeedsUsbReconnect = false;
                            return;
                        }

                        // Use the actual serial as it appears in adb devices,
                        // not the configured ip:port. For Wireless Debugging
                        // the serial is the mDNS service name; for TCP/IP the
                        // serial IS the ip:port. Either way, the live serial
                        // is what other code needs to talk to the device.
                        var liveSerial = FindConnectedWifiSerial(deviceList);
                        _currentDevice = string.IsNullOrEmpty(liveSerial)
                            ? _config.SelectedDeviceWiFi
                            : liveSerial;
                        _currentDeviceIsUsb = false;
                        _wifiReconnectPromptShown = false;
                        _wifiNeedsUsbReconnect = false;
                        return;
                    }

                    if (_config.IsWifiEnabled == true)
                    {
                        _wifiNeedsUsbReconnect = true;
                        await RecoverWirelessConnectionAsync().ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(_currentDevice) && IsWirelessSerial(_currentDevice))
                        {
                            _currentDeviceIsUsb = false;
                            return;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(_currentDevice))
                {
                    _currentDevice = string.Empty;
                    _mediaController.Clear();
                    NotifyNowPlaying(null, null, null);
                    NotifyLyricsPlayback(null, null, null, false, 0);
                }

                _currentDeviceIsUsb = !string.IsNullOrEmpty(_config.SelectedDeviceUSB)
                    && deviceList.Any(l => l.StartsWith(_config.SelectedDeviceUSB) && l.EndsWith("device"));
                if (!_wifiNeedsUsbReconnect)
                {
                    _wifiNeedsUsbReconnect = !string.IsNullOrWhiteSpace(_config.SelectedDeviceWiFi)
                        && _config.SelectedDeviceWiFi != "None"
                        && _config.IsWifiEnabled == true;
                }
            }
            catch (Exception ex)
            {
                Debugger.show("DetectDeviceAsync failed: " + ex.Message);
            }
        }

        private async Task RecoverWirelessConnectionAsync()
        {
            if (_isRecoveringWifi) return;
            _isRecoveringWifi = true;

            try
            {
                if (_config.WifiMode == WirelessMode.WirelessDebugging)
                {
                    await RecoverWirelessDebuggingAsync().ConfigureAwait(false);
                }
                else
                {
                    await RecoverTcpIpAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debugger.show("RecoverWirelessConnectionAsync failed: " + ex.Message);
            }
            finally
            {
                _isRecoveringWifi = false;
            }
        }

        // -------------------------------------------------------------------
        // TcpIp mode: classic adb tcpip 5555 flow. Needs USB to re-enable.
        // This is the original behavior, untouched apart from being moved
        // into its own method so the router above can pick a path.
        // -------------------------------------------------------------------
        private async Task RecoverTcpIpAsync()
        {
            if (!_wifiReconnectPromptShown)
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show(
                        "Wireless connection failed. Please reconnect your phone via USB to re-setup wireless.",
                        "Reconnect Device",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                });
                _wifiReconnectPromptShown = true;
            }

            var usbDevice = await GetUsbDeviceForRecoveryAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(usbDevice))
                return;

            var newWifi = await SetupWirelessFromUsbAsync(usbDevice).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(newWifi))
                return;

            _config.SelectedDeviceWiFi = newWifi;
            MusicConfigManager.Save(_config);
            await _dispatcher.InvokeAsync(() =>
            {
                (Application.Current as App)?.UpdateConfig(_config);
            });
            _wifiReconnectPromptShown = false;

            await _dispatcher.InvokeAsync(() =>
            {
                MessageBox.Show(
                    $"Wireless device has been re-setup and saved as {newWifi}.\n\nIf you want to continue using USB, disconnect and reconnect the cable now (USB may stay unavailable right after Wi-Fi setup).",
                    "Wireless Reconnected",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            });
        }

        // -------------------------------------------------------------------
        // WirelessDebugging mode: try mDNS, then last-known ip:port, then
        // fall back to USB-assisted recovery. We can't silently re-pair
        // (that requires a code from the phone screen), but we can read a
        // fresh IP via USB and try mDNS again now that the device is on
        // the LAN.
        // -------------------------------------------------------------------
        private async Task RecoverWirelessDebuggingAsync()
        {
            // Step 1: mDNS lookup for the persisted service name.
            if (!string.IsNullOrWhiteSpace(_config.WifiMdnsServiceName))
            {
                var ipPort = await WirelessDebuggingHelper.ReconnectViaMdnsAsync(_config.WifiMdnsServiceName).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(ipPort))
                {
                    _config.SelectedDeviceWiFi = ipPort;
                    _currentDevice = ipPort;
                    _currentDeviceIsUsb = false;
                    _wifiNeedsUsbReconnect = false;
                    _wifiReconnectPromptShown = false;
                    MusicConfigManager.Save(_config);
                    await _dispatcher.InvokeAsync(() =>
                    {
                        (Application.Current as App)?.UpdateConfig(_config);
                    });
                    return;
                }
            }

            // Step 2: try the last-known ip:port directly. Cheap, sometimes
            // works on networks where mDNS multicast is blocked but the
            // phone happens to still be on the same port.
            if (!string.IsNullOrWhiteSpace(_config.SelectedDeviceWiFi)
                && _config.SelectedDeviceWiFi != "None")
            {
                if (await WirelessDebuggingHelper.TryConnectLastKnownAsync(_config.SelectedDeviceWiFi).ConfigureAwait(false))
                {
                    _currentDevice = _config.SelectedDeviceWiFi;
                    _currentDeviceIsUsb = false;
                    _wifiNeedsUsbReconnect = false;
                    _wifiReconnectPromptShown = false;
                    return;
                }
            }

            // Step 3: USB-assisted recovery. Prompt the user, get a USB
            // device, retry mDNS once, and as a last resort try the last
            // known port at the phone's current wlan0 IP.
            var usbDevice = await GetUsbDeviceForRecoveryAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(usbDevice))
            {
                _wifiNeedsUsbReconnect = false;
                _wifiReconnectPromptShown = false;
                return;
            }

            // Retry mDNS now that we have USB context. Some networks only
            // surface the service after the phone has been actively used.
            if (!string.IsNullOrWhiteSpace(_config.WifiMdnsServiceName))
            {
                var ipPort = await WirelessDebuggingHelper.ReconnectViaMdnsAsync(_config.WifiMdnsServiceName).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(ipPort))
                {
                    _config.SelectedDeviceWiFi = ipPort;
                    _currentDevice = ipPort;
                    _currentDeviceIsUsb = false;
                    _wifiNeedsUsbReconnect = false;
                    _wifiReconnectPromptShown = false;
                    MusicConfigManager.Save(_config);
                    await _dispatcher.InvokeAsync(() =>
                    {
                        (Application.Current as App)?.UpdateConfig(_config);
                    });
                    return;
                }
            }

            // Last-ditch: read the phone's current wlan0 IP and try the
            // last-known port at that IP. If THAT also fails, the user
            // needs to re-pair, which we can't do silently.
            var freshIp = await GetDeviceWifiIpAsync(usbDevice).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(freshIp) && !string.IsNullOrWhiteSpace(_config.SelectedDeviceWiFi))
            {
                var lastPort = ExtractWifiPort(_config.SelectedDeviceWiFi);
                var candidate = $"{freshIp}:{lastPort}";
                if (await WirelessDebuggingHelper.TryConnectLastKnownAsync(candidate).ConfigureAwait(false))
                {
                    _config.SelectedDeviceWiFi = candidate;
                    _currentDevice = candidate;
                    _currentDeviceIsUsb = false;
                    _wifiNeedsUsbReconnect = false;
                    _wifiReconnectPromptShown = false;
                    MusicConfigManager.Save(_config);
                    await _dispatcher.InvokeAsync(() =>
                    {
                        (Application.Current as App)?.UpdateConfig(_config);
                    });
                    return;
                }
            }

            await _dispatcher.InvokeAsync(() =>
            {
                Debugger.show("Wireless Debugging reconnect failed; clearing connection state.");
            });
            _wifiNeedsUsbReconnect = false;
            _wifiReconnectPromptShown = false;
        }

        private async Task<string> GetUsbDeviceForRecoveryAsync()
        {
            var devices = await AdbHelper.RunAdbCaptureAsync("devices").ConfigureAwait(false);
            var deviceList = devices.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            bool IsDeviceConnected(string id) => deviceList.Any(l => l.StartsWith(id) && l.EndsWith("device"));

            if (!string.IsNullOrWhiteSpace(_config.SelectedDeviceUSB) && IsDeviceConnected(_config.SelectedDeviceUSB))
                return _config.SelectedDeviceUSB;

            foreach (var entry in deviceList)
            {
                if (!entry.EndsWith("device"))
                    continue;

                var serial = entry.Split('\t', ' ').FirstOrDefault();
                if (string.IsNullOrWhiteSpace(serial))
                    continue;

                if (!serial.Contains(':'))
                    return serial;
            }

            return string.Empty;
        }

        private async Task<string> SetupWirelessFromUsbAsync(string usbDevice)
        {
            int port = ExtractWifiPort(_config.SelectedDeviceWiFi);
            var ip = await GetDeviceWifiIpAsync(usbDevice).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(ip))
                return string.Empty;

            await AdbHelper.RunAdbCaptureAsync($"-s {usbDevice} tcpip {port}").ConfigureAwait(false);
            await Task.Delay(750).ConfigureAwait(false);
            await AdbHelper.RunAdbCaptureAsync($"connect {ip}:{port}").ConfigureAwait(false);
            await Task.Delay(750).ConfigureAwait(false);

            var devices = await AdbHelper.RunAdbCaptureAsync("devices").ConfigureAwait(false);
            var deviceList = devices.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var wifiId = $"{ip}:{port}";
            if (deviceList.Any(l => l.StartsWith(wifiId) && l.EndsWith("device")))
            {
                _currentDevice = wifiId;
                _currentDeviceIsUsb = false;
                _wifiNeedsUsbReconnect = false;
                return wifiId;
            }

            return string.Empty;
        }

        private static int ExtractWifiPort(string wifiAddress)
        {
            if (string.IsNullOrWhiteSpace(wifiAddress))
                return 5555;

            var parts = wifiAddress.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[1], out var port) && port > 0)
                return port;

            return 5555;
        }

        private static async Task<string> GetDeviceWifiIpAsync(string usbDevice)
        {
            var ipOutput = await AdbHelper.RunAdbCaptureAsync($"-s {usbDevice} shell ip -f inet addr show wlan0").ConfigureAwait(false);
            var match = Regex.Match(ipOutput, @"inet\s+(?<ip>\d+\.\d+\.\d+\.\d+)");
            if (match.Success)
                return match.Groups["ip"].Value;

            var routeOutput = await AdbHelper.RunAdbCaptureAsync($"-s {usbDevice} shell ip route").ConfigureAwait(false);
            match = Regex.Match(routeOutput, @"src\s+(?<ip>\d+\.\d+\.\d+\.\d+)");
            return match.Success ? match.Groups["ip"].Value : string.Empty;
        }

        /// <summary>
        /// Parses title, artist and album from the scrambled media_session metadata string by
        /// reading the exact field lengths from `dumpsys notification --noredact` for the active
        /// package. Mapping: title = android.title, artist = android.text, album = android.subText.
        /// Field separator in scrambled string is assumed to be ", " (comma + space, 2 chars).
        /// Returns success=false if the notification cannot be located or lengths cannot be read,
        /// so the caller can fall back to a simple split.
        /// </summary>
        private async Task<(string? title, string? artist, string? album, bool success)> TryParseMediaMetadataAsync(
            string scrambledMetadata, string packageName)
        {
            try
            {
                Debugger.show($"[NOTIFPARSER] Fetching notification data for package: {packageName}");
                // Server-side filtering: same rationale as media_session, cuts the notification
                // dump (often the largest ADB payload we fetch) down to just the lines the
                // parser consumes. We keep NotificationRecord( as the block delimiter, pkg= to
                // match the active record, and the three android.* length lines the parser
                // reads via ExtractStringLength. grep -E is portable toybox on Android 6+.
                string notifOutput = await AdbHelper.RunAdbCaptureAsync(
                    $"-s {_currentDevice} shell dumpsys notification | grep -E 'NotificationRecord\\(|pkg=|android\\.title=String|android\\.text=String|android\\.subText=String'")
                    .ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(notifOutput))
                {
                    Debugger.show("[NOTIFPARSER] No notification data available");
                    return (null, null, null, false);
                }

                // Find the NotificationRecord block for our package and isolate it from the next record.
                // We grab everything from "pkg=<package>" up to the next "NotificationRecord(" or end of dump.
                var pkgPattern = Regex.Escape(packageName);
                var blockMatch = Regex.Match(notifOutput,
                    $@"pkg={pkgPattern}\b.*?(?=NotificationRecord\(|\Z)",
                    RegexOptions.Singleline);

                if (!blockMatch.Success)
                {
                    Debugger.show($"[NOTIFPARSER] No notification record found for {packageName}");
                    return (null, null, null, false);
                }

                string notifBlock = blockMatch.Value;

                // Extract lengths from the extras block. Format example:
                //   android.title=String [length=52]
                //   android.text=String [length=24]
                //   android.subText=String [length=44]
                int? titleLen = ExtractStringLength(notifBlock, "android.title");
                int? artistLen = ExtractStringLength(notifBlock, "android.text");
                int? albumLen = ExtractStringLength(notifBlock, "android.subText");

                if (!titleLen.HasValue || !artistLen.HasValue)
                {
                    Debugger.show($"[NOTIFPARSER] Missing required lengths (title={titleLen}, text={artistLen}, subText={albumLen})");
                    return (null, null, null, false);
                }

                // Separator between fields in the scrambled string is ", " (2 chars).
                const int sep = 2;
                int needed = titleLen.Value + sep + artistLen.Value;
                if (albumLen.HasValue) needed += sep + albumLen.Value;

                if (scrambledMetadata.Length < needed)
                {
                    Debugger.show($"[NOTIFPARSER] Scrambled string ({scrambledMetadata.Length}) shorter than expected ({needed})");
                    return (null, null, null, false);
                }

                string title = scrambledMetadata.Substring(0, titleLen.Value);
                string artist = scrambledMetadata.Substring(titleLen.Value + sep, artistLen.Value);
                string album = albumLen.HasValue
                    ? scrambledMetadata.Substring(titleLen.Value + sep + artistLen.Value + sep, albumLen.Value)
                    : string.Empty;

                Debugger.show($"[NOTIFPARSER] ✓ Parsed via lengths (T={titleLen}, A={artistLen}, Al={albumLen}): Title='{title}', Artist='{artist}', Album='{album}'");
                return (title, artist, album, true);
            }
            catch (Exception ex)
            {
                Debugger.show($"[NOTIFPARSER] ✗ Exception: {ex.Message}");
                return (null, null, null, false);
            }
        }

        /// <summary>
        /// Extracts the [length=N] value for a given extras key from a notification dump block.
        /// Returns null if the key is missing or the length cannot be parsed.
        /// </summary>
        private static int? ExtractStringLength(string notifBlock, string extrasKey)
        {
            var pattern = Regex.Escape(extrasKey) + @"=String\s*\[length=(\d+)\]";
            var m = Regex.Match(notifBlock, pattern);
            if (!m.Success) return null;
            return int.TryParse(m.Groups[1].Value, out var len) ? len : (int?)null;
        }

        /// <summary>
        /// Simple comma-split fallback used when notification data is unavailable.
        /// </summary>
        private static (string? title, string? artist, string? album, bool success) SimpleSplitFallback(string scrambledMetadata)
        {
            var parts = scrambledMetadata.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3)
                return (parts[0].Trim(), parts[1].Trim(), parts[2].Trim(), true);
            if (parts.Length == 2)
                return (parts[0].Trim(), parts[1].Trim(), string.Empty, true);
            return (null, null, null, false);
        }

        private async Task<bool> UpdateCurrentSongAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(_currentDevice)) return false;

                // Server-side filtering: pipe dumpsys output through grep on the device so only
                // the lines we actually parse cross the wire. This cuts payload by ~90% and is
                // the single biggest CPU/bandwidth win on the polling path. toybox grep ships
                // with Android 6+, guaranteed on Android 13+. -A 2 keeps the two lines after any
                // match so the multi-line state=PlaybackState{...} block stays intact even if
                // position= ends up on its own line. Pipe exit code reflects grep's (may be 1
                // when nothing matches); we already treat empty output as "no session" so that
                // is harmless.
                string output = await AdbHelper.RunAdbCaptureAsync(
                    $"-s {_currentDevice} shell dumpsys media_session | grep -E -A 2 'queueTitle=|active=|package=|description=|state=PlaybackState'");
                if (string.IsNullOrWhiteSpace(output)) return false;

                var eligibleApps = (_config.EligibleApps ?? new List<EligibleAppConfig>())
                    .Where(a => !string.IsNullOrWhiteSpace(a.PackageName))
                    .GroupBy(a => a.PackageName.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        g => g.Key,
                        g => new EligibleAppConfig
                        {
                            PackageName = g.Key,
                            IsEnabled = g.Any(x => x.IsEnabled),
                            EnableCoverSearch = g.Any(x => x.EnableCoverSearch)
                        },
                        StringComparer.OrdinalIgnoreCase);

                var sessionBlocks = output.Split(new[] { "queueTitle=" }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var block in sessionBlocks)
                {
                    if (!block.Contains("active=true"))
                        continue;

                    bool enableCoverSearchForApp = false;
                    string pkg = string.Empty;

                    var pkgMatch = Regex.Match(block, @"package=([^\s]+)");
                    if (pkgMatch.Success)
                    {
                        pkg = pkgMatch.Groups[1].Value.Trim();
                        if (!eligibleApps.TryGetValue(pkg, out var appSettings) || !appSettings.IsEnabled)
                            continue;

                        enableCoverSearchForApp = appSettings.EnableCoverSearch;
                    }
                    else
                    {
                        continue;
                    }

                    // Pull the full metadata line (everything after "description=" up to end-of-line).
                    // Earlier versions used a non-greedy comma regex here which silently truncated
                    // titles/artists/albums that contained commas; we now hand the FULL scrambled
                    // string to the notification-length parser so it can split it correctly.
                    var metaMatch = Regex.Match(block,
                        @"description=(?<desc>[^\r\n]+)",
                        RegexOptions.Singleline);

                    if (!metaMatch.Success)
                        continue;

                    string scrambledData = metaMatch.Groups["desc"].Value.Trim();

                    // Parse title/artist/album. If the scrambled string is identical to last tick
                    // we reuse the cached parse result so we don't re-run the notification parser
                    // (which would fire another adb call). We must NOT skip the rest of the loop:
                    // the position-ticking logic in MediaController.UpdateMediaControlsAsync needs
                    // to run every tick because Android reports a static position that we have to
                    // increment ourselves.
                    //
                    // Timing quirk: when a track changes, `dumpsys media_session` sometimes reflects
                    // the new scrambled metadata before `dumpsys notification` has caught up with the
                    // matching length fields. That yields a bad first parse. To correct for it we
                    // force a re-parse for 2 further ticks after any scrambled change, so the cache
                    // ends up populated with the result from a tick where both surfaces agree.
                    //
                    // We always cache the scrambled string after the parse attempt, even when the
                    // parse failed. Otherwise an unchanging-but-unparsable string (e.g. when no song
                    // is playing) would re-fire two adb calls every tick forever.
                    bool scrambledChanged = !string.Equals(_lastScrambledMetadata, scrambledData, StringComparison.Ordinal);
                    if (scrambledChanged)
                    {
                        _reparseTicksRemaining = 1;
                    }

                    bool mustReparse = scrambledChanged || _reparseTicksRemaining > 0;

                    (string? title, string? artist, string? album, bool success) parseResult;
                    if (!mustReparse)
                    {
                        parseResult = (_lastParsedTitle, _lastParsedArtist, _lastParsedAlbum, _lastParseSuccess);
                    }
                    else
                    {
                        // Always try notification-based parsing first (uses exact field lengths).
                        // Fall back to a naive comma split only when notification data isn't available.
                        parseResult = await TryParseMediaMetadataAsync(scrambledData, pkg).ConfigureAwait(false);
                        if (!parseResult.success)
                        {
                            Debugger.show($"[NOTIFPARSER] Falling back to simple split for package: {pkg}");
                            parseResult = SimpleSplitFallback(scrambledData);
                            if (parseResult.success)
                                Debugger.show($"[NOTIFPARSER] Fallback parsed - Title: '{parseResult.title}', Artist: '{parseResult.artist}', Album: '{parseResult.album}'");
                        }

                        _lastScrambledMetadata = scrambledData;
                        _lastParsedTitle = parseResult.title;
                        _lastParsedArtist = parseResult.artist;
                        _lastParsedAlbum = parseResult.album;
                        _lastParseSuccess = parseResult.success;

                        // Only decrement AFTER we actually did a reparse this tick.
                        if (!scrambledChanged && _reparseTicksRemaining > 0)
                            _reparseTicksRemaining--;
                    }

                    string? title = parseResult.title;
                    string? artist = parseResult.artist;
                    string? album = parseResult.album;
                    bool parseSuccess = parseResult.success;

                    if (!parseSuccess)
                        continue;

                    var stateMatch = Regex.Match(block, @"state=PlaybackState\s*\{[^}]*state=(?:(\w+)\((\d+)\)|(\d+)),\s*position=(-?\d+)", RegexOptions.Singleline);
                    bool isPlaying = false;
                    long adbPositionMs = 0;

                    if (stateMatch.Success)
                    {
                        string stateText = stateMatch.Groups[1].Value.Trim().ToUpper();
                        string stateNumStr = string.IsNullOrEmpty(stateMatch.Groups[2].Value)
                            ? stateMatch.Groups[3].Value
                            : stateMatch.Groups[2].Value;
                        if (!string.IsNullOrEmpty(stateText))
                            isPlaying = stateText == "PLAYING";
                        else if (int.TryParse(stateNumStr, out int stateNum))
                            isPlaying = stateNum == 3;
                        _ = long.TryParse(stateMatch.Groups[4].Value, out adbPositionMs);
                    }

                    if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(artist))
                    {
                        NotifyNowPlaying(artist, title, album);
                        NotifyLyricsPlayback(artist, title, album, isPlaying, Math.Max(0, adbPositionMs));

                        if (!isPlaying)
                        {
                            var signature = $"{title}\n{artist}\n{album}";
                            if (_pausedSince == null || !string.Equals(_pausedSignature, signature, StringComparison.Ordinal))
                            {
                                _pausedSince = DateTimeOffset.UtcNow;
                                _pausedSignature = signature;
                            }

                            if (_config.SmtcPauseClearDelayMinutes > 0 && _pausedSince.HasValue)
                            {
                                var elapsed = DateTimeOffset.UtcNow - _pausedSince.Value;
                                if (elapsed.TotalMinutes >= _config.SmtcPauseClearDelayMinutes)
                                {
                                    if (!_smtcPausedCleared)
                                    {
                                        _mediaController.Clear();
                                        NotifyMediaPlayerState(null, null, null, null, false, 0, 0);
                                        _smtcPausedCleared = true;
                                    }

                                    return false;
                                }
                            }
                            else if (_config.SmtcPauseClearDelayMinutes == 0)
                            {
                                // legacy logging, this probably won't be changed and if it is then just uncomment
                                //Debugger.show("not clearing value 0 is no timeout");
                            }

                            if (_smtcPausedCleared)
                                return false;
                        }
                        else
                        {
                            _pausedSince = null;
                            _pausedSignature = null;
                            _smtcPausedCleared = false;
                        }

                        await _mediaController.UpdateMediaControlsAsync(title, artist, album, isPlaying, enableCoverSearchForApp, adbPositionMs, _timer.Interval).ConfigureAwait(false);
                        NotifyMediaPlayerState(_mediaController.CurrentTitle, _mediaController.CurrentArtist, _mediaController.CurrentAlbum, _mediaController.CurrentCoverPath, isPlaying, _mediaController.CurrentPositionMs, _mediaController.CurrentDurationMs);
                        return isPlaying;
                    }
                }

                NotifyNowPlaying(null, null, null);
                NotifyLyricsPlayback(null, null, null, false, 0);
                NotifyMediaPlayerState(null, null, null, null, false, 0, 0);
                return false;
            }
            catch (Exception ex)
            {
                Debugger.show("UpdateCurrentSongAsync failed: " + ex.Message);
                return false;
            }
        }

        private void NotifyMediaPlayerState(string? title, string? artist, string? album, string? coverPath, bool isPlaying, long positionMs, long durationMs)
        {
            try
            {
                MediaPlayerStateChanged?.Invoke(title, artist, album, coverPath, isPlaying, positionMs, durationMs);
            }
            catch
            {
            }
        }

        public Task PauseCurrentAsync() => _mediaController.PauseTrackAsync();

        public Task NextCurrentAsync() => _mediaController.NextTrackAsync();

        public Task PreviousCurrentAsync() => _mediaController.PreviousTrackAsync();

        public Task SeekRelativeCurrentAsync(int seconds) => _mediaController.SeekRelativeAsync(seconds);

        private void NotifyLyricsPlayback(string? artist, string? title, string? album, bool isPlaying, long positionMs)
        {
            try
            {
                LyricsPlaybackChanged?.Invoke(artist, title, album, isPlaying, positionMs);
            }
            catch
            {
            }
        }

        private void NotifyNowPlaying(string? artist, string? title, string? album)
        {
            if (string.Equals(_lastNowPlayingArtist, artist, StringComparison.Ordinal)
                && string.Equals(_lastNowPlayingTitle, title, StringComparison.Ordinal)
                && string.Equals(_lastNowPlayingAlbum, album, StringComparison.Ordinal))
            {
                return;
            }

            _lastNowPlayingArtist = artist;
            _lastNowPlayingTitle = title;
            _lastNowPlayingAlbum = album;

            try
            {
                NowPlayingChanged?.Invoke(artist, title, album);
            }
            catch
            {
            }
        }

        private TrayIconState BuildTrayState(bool hasActiveSong)
        {
            if (!string.IsNullOrEmpty(_currentDevice))
            {
                if (_currentDeviceIsUsb)
                    return hasActiveSong ? TrayIconState.ActiveUsb : TrayIconState.InactiveUsb;

                // We're on wifi. Trust the configured WifiMode first because
                // the serial shape is NOT a reliable distinguisher: once a
                // Wireless Debugging connection has been established via mDNS,
                // adb caches it as "ip:port" in subsequent `adb devices`
                // output, identical to a TCP/IP serial. So we can only fall
                // back to serial-shape inference if WifiMode is somehow unset.
                bool isWirelessDebugging = _config.WifiMode == WirelessMode.WirelessDebugging;

                if (isWirelessDebugging)
                    return hasActiveSong ? TrayIconState.ActiveWifiDebug : TrayIconState.InactiveWifiDebug;

                return hasActiveSong ? TrayIconState.ActiveWifi : TrayIconState.InactiveWifi;
            }

            if (_wifiNeedsUsbReconnect)
                return TrayIconState.NeedsUsbReconnect;

            return TrayIconState.NoDevice;
        }

        private void NotifyTrayState(TrayIconState state)
        {
            try
            {
                TrayStateChanged?.Invoke(state);
            }
            catch
            {
            }
        }

        private static TimeSpan GetInterval(UpdateIntervalMode mode)
        {
            return mode switch
            {
                UpdateIntervalMode.Extreme => TimeSpan.FromSeconds(1),
                UpdateIntervalMode.Fast => TimeSpan.FromSeconds(3),
                UpdateIntervalMode.Medium => TimeSpan.FromSeconds(5),
                UpdateIntervalMode.Slow => TimeSpan.FromSeconds(10),
                _ => TimeSpan.FromSeconds(1)
            };
        }

        public void Dispose()
        {
            try
            {
                _timer.Stop();
                _mediaController.Clear();
                _updateLock.Dispose();
            }
            catch { }
        }
    }
}