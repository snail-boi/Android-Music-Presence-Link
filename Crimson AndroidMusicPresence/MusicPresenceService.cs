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

        internal string CurrentDevice => _currentDevice;

        public MusicPresenceService(Dispatcher dispatcher, MusicConfig config)
        {
            _dispatcher = dispatcher;
            _config = config;
            _mediaController = new MediaController(dispatcher, () => _currentDevice, UpdateCurrentSongAsync, config);
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
                if (!string.IsNullOrEmpty(_currentDevice))
                    await UpdateCurrentSongAsync().ConfigureAwait(false);
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

                if (!string.IsNullOrEmpty(_config.SelectedDeviceUSB))
                {
                    bool usbConnected = deviceList.Any(l => l.StartsWith(_config.SelectedDeviceUSB) && l.EndsWith("device"));
                    if (usbConnected)
                    {
                        _currentDevice = _config.SelectedDeviceUSB;
                        return;
                    }
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
                        _wifiReconnectPromptShown = false;
                        return;
                    }
                    if (MainWindow.isWifiEnabled == true)
                    {
                        await RecoverWirelessConnectionAsync().ConfigureAwait(false);
                    }
                }

                if (!string.IsNullOrEmpty(_currentDevice))
                {
                    _currentDevice = string.Empty;
                    _mediaController.Clear();
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
                (Application.Current as App)?.UpdateConfig(_config);
                _wifiReconnectPromptShown = false;

                await _dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show(
                        $"Wireless device has been re-setup and saved as {newWifi}.",
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
            await AdbHelper.RunAdbCaptureAsync($"connect {ip}:{port}"). ConfigureAwait(false);
            await Task.Delay(750).ConfigureAwait(false);

            var devices = await AdbHelper.RunAdbCaptureAsync("devices").ConfigureAwait(false);
            var deviceList = devices.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var wifiId = $"{ip}:{port}";
            if (deviceList.Any(l => l.StartsWith(wifiId) && l.EndsWith("device")))
            {
                _currentDevice = wifiId;
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

        private async Task UpdateCurrentSongAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(_currentDevice)) return;

                string output = await AdbHelper.RunAdbCaptureAsync($"-s {_currentDevice} shell dumpsys media_session");
                if (string.IsNullOrWhiteSpace(output)) return;

                var allowedApps = new HashSet<string>(
                    _config.AllowedApps?.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim())
                    ?? Enumerable.Empty<string>(),
                    StringComparer.OrdinalIgnoreCase);

                var sessionBlocks = output.Split(new[] { "queueTitle=" }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var block in sessionBlocks)
                {
                    if (!block.Contains("active=true"))
                        continue;

                    var pkgMatch = Regex.Match(block, @"package=([^\s]+)");
                    if (pkgMatch.Success)
                    {
                        var pkg = pkgMatch.Groups[1].Value.Trim();
                        if (allowedApps.Count > 0 && !allowedApps.Contains(pkg))
                            continue;
                    }
                    else if (allowedApps.Count > 0)
                    {
                        continue;
                    }

                    var metaMatch = Regex.Match(block,
                        @"metadata:\s+size=\d+,\s+description=(.+?),\s+(.+?),\s+(.+)",
                        RegexOptions.Singleline);

                    if (!metaMatch.Success)
                        continue;

                    string title = metaMatch.Groups[1].Value.Trim();
                    string artist = metaMatch.Groups[2].Value.Trim();
                    string album = metaMatch.Groups[3].Value.Trim();

                    var stateMatch = Regex.Match(block, @"state=PlaybackState\s*\{[^}]*state=(\w+)\((\d+)\),\s*position=(\d+)", RegexOptions.Singleline);
                    bool isPlaying = false;

                    if (stateMatch.Success)
                    {
                        string stateText = stateMatch.Groups[1].Value.Trim().ToUpper();
                        isPlaying = stateText == "PLAYING";
                    }

                    if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(artist))
                    {
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

                                    return;
                                }
                            }

                            if (_smtcPausedCleared)
                                return;
                        }
                        else
                        {
                            _pausedSince = null;
                            _pausedSignature = null;
                            _smtcPausedCleared = false;
                        }

                        await _mediaController.UpdateMediaControlsAsync(title, artist, album, isPlaying).ConfigureAwait(false);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Debugger.show("UpdateCurrentSongAsync failed: " + ex.Message);
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
