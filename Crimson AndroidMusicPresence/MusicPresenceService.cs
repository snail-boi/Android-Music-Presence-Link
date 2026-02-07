using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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
                        return;
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
