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

        internal string CurrentDevice => _currentDevice;
        internal event Action<TrayIconState>? TrayStateChanged;
        internal event Action<string?, string?, string?>? NowPlayingChanged;

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

        private async Task DetectDeviceAsync()
        {
            try
            {
                var devices = await AdbHelper.RunAdbCaptureAsync("devices");
                var deviceList = devices.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                string connectedUsb = string.Empty;
                if (!string.IsNullOrWhiteSpace(_config.SelectedDeviceUSB))
                {
                    bool selectedUsbConnected = deviceList.Any(l => l.StartsWith(_config.SelectedDeviceUSB) && l.EndsWith("device"));
                    if (selectedUsbConnected)
                    {
                        connectedUsb = _config.SelectedDeviceUSB;
                    }
                }

                if (string.IsNullOrWhiteSpace(connectedUsb))
                {
                    foreach (var entry in deviceList)
                    {
                        if (!entry.EndsWith("device"))
                            continue;

                        var serial = entry.Split('\t', ' ').FirstOrDefault();
                        if (string.IsNullOrWhiteSpace(serial))
                            continue;

                        if (!serial.Contains(':'))
                        {
                            connectedUsb = serial;
                            break;
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(connectedUsb))
                {
                    _currentDevice = connectedUsb;
                    _currentDeviceIsUsb = true;

                    bool wifiConfigured = !string.IsNullOrWhiteSpace(_config.SelectedDeviceWiFi) && _config.SelectedDeviceWiFi != "None";
                    bool wifiConnected = wifiConfigured && deviceList.Any(l => l.StartsWith(_config.SelectedDeviceWiFi) && l.EndsWith("device"));

                    if (wifiConfigured && !wifiConnected && _config.IsWifiEnabled == true)
                    {
                        _wifiNeedsUsbReconnect = true;
                        await RecoverWirelessConnectionAsync().ConfigureAwait(false);

                        if (!string.IsNullOrEmpty(_currentDevice))
                        {
                            _currentDeviceIsUsb = !_currentDevice.Contains(':');
                            if (!_currentDeviceIsUsb)
                            {
                                _wifiNeedsUsbReconnect = false;
                            }
                        }
                    }
                    else
                    {
                        _wifiNeedsUsbReconnect = false;
                    }

                    return;
                }

                if (!string.IsNullOrEmpty(_config.SelectedDeviceWiFi) && _config.SelectedDeviceWiFi != "None")
                {
                    bool wifiConnected = deviceList.Any(l => l.StartsWith(_config.SelectedDeviceWiFi) && l.EndsWith("device"));
                    if (!wifiConnected)
                    {
                        await AdbHelper.RunAdbCaptureAsync($"connect {_config.SelectedDeviceWiFi}");
                        devices = await AdbHelper.RunAdbCaptureAsync("devices");
                        deviceList = devices.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        wifiConnected = deviceList.Any(l => l.StartsWith(_config.SelectedDeviceWiFi) && l.EndsWith("device"));
                    }

                    if (wifiConnected)
                    {
                        _currentDevice = _config.SelectedDeviceWiFi;
                        _currentDeviceIsUsb = false;
                        _wifiReconnectPromptShown = false;
                        _wifiNeedsUsbReconnect = false;
                        return;
                    }

                    if (_config.IsWifiEnabled == true)
                    {
                        _wifiNeedsUsbReconnect = true;
                        await RecoverWirelessConnectionAsync().ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(_currentDevice))
                        {
                            _currentDeviceIsUsb = !_currentDevice.Contains(':');
                            return;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(_currentDevice))
                {
                    _currentDevice = string.Empty;
                    _mediaController.Clear();
                    NotifyNowPlaying(null, null, null);
                }

                _currentDeviceIsUsb = false;
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
            catch (Exception ex)
            {
                Debugger.show("RecoverWirelessConnectionAsync failed: " + ex.Message);
            }
            finally
            {
                _isRecoveringWifi = false;
            }
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
                Debugger.show($"[NotifParser] Fetching notification data for package: {packageName}");
                string notifOutput = await AdbHelper.RunAdbCaptureAsync(
                    $"-s {_currentDevice} shell dumpsys notification")
                    .ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(notifOutput))
                {
                    Debugger.show("[NotifParser] No notification data available");
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
                    Debugger.show($"[NotifParser] No notification record found for {packageName}");
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
                    Debugger.show($"[NotifParser] Missing required lengths (title={titleLen}, text={artistLen}, subText={albumLen})");
                    return (null, null, null, false);
                }

                // Separator between fields in the scrambled string is ", " (2 chars).
                const int sep = 2;
                int needed = titleLen.Value + sep + artistLen.Value;
                if (albumLen.HasValue) needed += sep + albumLen.Value;

                if (scrambledMetadata.Length < needed)
                {
                    Debugger.show($"[NotifParser] Scrambled string ({scrambledMetadata.Length}) shorter than expected ({needed})");
                    return (null, null, null, false);
                }

                string title = scrambledMetadata.Substring(0, titleLen.Value);
                string artist = scrambledMetadata.Substring(titleLen.Value + sep, artistLen.Value);
                string album = albumLen.HasValue
                    ? scrambledMetadata.Substring(titleLen.Value + sep + artistLen.Value + sep, albumLen.Value)
                    : string.Empty;

                Debugger.show($"[NotifParser] ✓ Parsed via lengths (T={titleLen}, A={artistLen}, Al={albumLen}): Title='{title}', Artist='{artist}', Album='{album}'");
                return (title, artist, album, true);
            }
            catch (Exception ex)
            {
                Debugger.show($"[NotifParser] ✗ Exception: {ex.Message}");
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

                string output = await AdbHelper.RunAdbCaptureAsync($"-s {_currentDevice} shell dumpsys media_session");
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

                    // Skip parsing if metadata hasn't changed since last update
                    if (string.Equals(_lastScrambledMetadata, scrambledData, StringComparison.Ordinal))
                    {
                        Debugger.show($"[NotifParser] ⊘ Skipped parsing (metadata unchanged)");
                        continue;
                    }

                    _lastScrambledMetadata = scrambledData;

                    // Always try notification-based parsing first (uses exact field lengths).
                    // Fall back to a naive comma split only when notification data isn't available.
                    var parseResult = await TryParseMediaMetadataAsync(scrambledData, pkg).ConfigureAwait(false);
                    if (!parseResult.success)
                    {
                        Debugger.show($"[NotifParser] Falling back to simple split for package: {pkg}");
                        parseResult = SimpleSplitFallback(scrambledData);
                        if (parseResult.success)
                            Debugger.show($"[NotifParser] Fallback parsed - Title: '{parseResult.title}', Artist: '{parseResult.artist}', Album: '{parseResult.album}'");
                    }

                    string? title = parseResult.title;
                    string? artist = parseResult.artist;
                    string? album = parseResult.album;
                    bool parseSuccess = parseResult.success;

                    if (!parseSuccess)
                        continue;

                    var stateMatch = Regex.Match(block, @"state=PlaybackState\s*\{[^}]*state=(\w+)\((\d+)\),\s*position=(-?\d+)", RegexOptions.Singleline);
                    bool isPlaying = false;
                    long adbPositionMs = 0;

                    if (stateMatch.Success)
                    {
                        string stateText = stateMatch.Groups[1].Value.Trim().ToUpper();
                        isPlaying = stateText == "PLAYING";
                        _ = long.TryParse(stateMatch.Groups[3].Value, out adbPositionMs);
                    }

                    if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(artist))
                    {
                        NotifyNowPlaying(artist, title, album);

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
                                        _smtcPausedCleared = true;
                                    }

                                    return false;
                                }
                            }
                            else if (_config.SmtcPauseClearDelayMinutes == 0)
                            {
                                Debugger.show("not clearing value 0 is no timeout");
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
                        return isPlaying;
                    }
                }

                NotifyNowPlaying(null, null, null);
                return false;
            }
            catch (Exception ex)
            {
                Debugger.show("UpdateCurrentSongAsync failed: " + ex.Message);
                return false;
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
                UpdateIntervalMode.Fast => TimeSpan.FromSeconds(5),
                UpdateIntervalMode.Medium => TimeSpan.FromSeconds(15),
                UpdateIntervalMode.Slow => TimeSpan.FromSeconds(30),
                UpdateIntervalMode.None => TimeSpan.FromSeconds(0),
                _ => TimeSpan.FromSeconds(15)
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