using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace AndroidMusicPresenceLink
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
        private bool _wifiReconnectFailurePromptShown;
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

        // Set by UpdateCurrentSongAsync when the media_session query returns no
        // output. That is the ambiguous "idle or disconnected" signal: the
        // persistent adb shell returns empty in both cases. TickAsync uses it to
        // decide whether a one-off `adb devices` reconcile is needed.
        private bool _deviceQueryCameBackEmpty;

        // Set when a connection-relevant setting changes (mode, enabled flag, or a
        // selected serial). Forces the next tick to re-run detection even while a
        // link is currently held, so a manual switch to/from USB takes effect
        // without waiting for the connection to drop.
        private bool _forceRedetect;

        // Throttles the expensive active wireless reconnect (mDNS for WD, the single
        // connect for TCP/IP) while disconnected. The cheap `adb devices` probe still
        // runs every poll to spot a cable or an already-up link; only the costly
        // re-establish (which spawns an mDNS scan or a blocking connect) is gated to
        // this interval so a dropped link doesn't hammer adb every tick. Reset to
        // MinValue whenever a device is acquired or the mode changes, so a fresh
        // disconnect attempts immediately.
        private DateTimeOffset _lastWirelessReconnectUtc = DateTimeOffset.MinValue;
        private static readonly TimeSpan WirelessReconnectInterval = TimeSpan.FromSeconds(10);

        // Coalesces bursts of WM_DEVICECHANGE notifications so a single physical
        // plug event doesn't fire several `adb devices` probes back to back.
        private DateTimeOffset _lastUsbPromotionProbeUtc = DateTimeOffset.MinValue;
        private static readonly TimeSpan UsbPromotionProbeThrottle = TimeSpan.FromMilliseconds(750);

        // Windows reports the device-tree change the instant the cable is seen, but
        // adb needs a moment to enumerate and authorize the device before it lists
        // in `adb devices`. Wait this long after a device-change before probing.
        private static readonly TimeSpan UsbPromotionProbeDelay = TimeSpan.FromMilliseconds(1500);

        // After the delay, wait briefly for any in-flight tick to release the lock
        // rather than bailing, so a freshly plugged device isn't missed.
        private const int UsbPromotionLockWaitMs = 2000;

        // ── Adaptive polling ────────────────────────────────────────────────
        // Most recent activity: a user interaction (any `input keyevent`) or a song
        // change. The poll interval slows down the longer this stays unchanged.
        private DateTimeOffset _lastActivityUtc = DateTimeOffset.UtcNow;
        // True while the interval is slowed below the user's configured base, so an
        // interaction knows to snap straight back to base instead of waiting it out.
        private volatile bool _adaptiveActive;

        // When idle (not playing) we start slowing sooner; while a single track plays
        // we wait longer so long songs don't trip it (normal albums reset on every
        // track change and stay fast). After the start point we step down the ladder.
        // Thresholds are computed from AdaptivePollingThresholdMinutes (N):
        //   idle:    after N         -> 3s, ceil(N/2) -> 5s, ceil(N/4) -> 10s, ceil(N/8) -> 30s
        //   playing: after 2N        -> 3s, ceil(N/2) -> 5s, ceil(N/4) -> 10s, ceil(N/8) -> 30s
        private static readonly TimeSpan AdaptiveStage1 = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan AdaptiveStage2 = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan AdaptiveStage3 = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan AdaptiveStage4 = TimeSpan.FromSeconds(30);

        internal string CurrentDevice => _currentDevice;
        // Flags of the currently matched eligible app, refreshed every tick that finds
        // an active session. Used by the neighbour/predictive features to decide
        // between the local library list and the Subsonic one.
        internal bool CurrentAppUseSubsonic { get; private set; }
        internal bool CurrentAppCoverSearch { get; private set; }
        internal string? CurrentRemoteFilePath => _mediaController.CurrentRemoteFilePath;
        internal string? CurrentRemoteFileToken => _mediaController.CurrentRemoteFileToken;
        internal string? CurrentSubsonicSongId => _mediaController.CurrentSubsonicSongId;
        internal string? CurrentSubsonicSuffix => _mediaController.CurrentSubsonicSuffix;
        internal string? CurrentCoverPath => _mediaController.CurrentCoverPath;
        internal CoverCacheManager? GetCoverCacheManager() => _mediaController.CoverCache;
        internal event Action<TrayIconState>? TrayStateChanged;
        internal event Action<string?, string?, string?>? NowPlayingChanged;
        internal event Action<string?, string?, string?, bool, bool, long>? LyricsPlaybackChanged;
        internal event Action<string?, string?, string?, string?, bool, long, long>? MediaPlayerStateChanged;

        public MusicPresenceService(Dispatcher dispatcher, MusicConfig config)
        {
            _dispatcher = dispatcher;
            _config = config;
            _mediaController = new MediaController(dispatcher, () => _currentDevice, async () => { await UpdateCurrentSongAsync().ConfigureAwait(false); }, config, NotifyUserInteraction);
            _mediaController.Initialize();

            _timer = new DispatcherTimer();
            SetPollInterval(GetInterval(config.Polling.Interval));
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
            // If anything that affects which transport/device we use changed, force
            // the next tick to re-detect. Otherwise a manual switch (e.g. Wi-Fi to
            // USB) would be ignored while the current link keeps answering.
            bool connectionChanged =
                _config.Device.WifiMode != config.Device.WifiMode
                || _config.Device.IsWifiEnabled != config.Device.IsWifiEnabled
                || !string.Equals(_config.Device.SelectedDeviceUSB, config.Device.SelectedDeviceUSB, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(_config.Device.SelectedDeviceWiFi, config.Device.SelectedDeviceWiFi, StringComparison.OrdinalIgnoreCase);

            _config = config;
            _mediaController.UpdateConfig(config);

            if (connectionChanged)
            {
                _forceRedetect = true;
                // Let the new mode/target attempt a wireless reconnect immediately
                // rather than waiting out the throttle interval.
                _lastWirelessReconnectUtc = DateTimeOffset.MinValue;
            }

            var interval = GetInterval(config.Polling.Interval);
            SetPollInterval(interval);
            if (interval.TotalSeconds <= 0)
            {
                _timer.Stop();
            }
            else if (!_timer.IsEnabled)
            {
                _timer.Start();
            }
        }

        public void ResetCoverSearch() => _mediaController.ResetCoverSearch();

        private async Task TickAsync()
        {
            if (!await _updateLock.WaitAsync(0)) return;

            try
            {
                // Connection tracking is split to avoid an `adb devices` call on
                // every tick. When we already hold a device we trust the
                // media_session query in UpdateCurrentSongAsync as the liveness
                // signal and skip detection entirely. Detection only runs when:
                //   - we have no device (cold start, or a confirmed disconnect), or
                //   - the media_session query returned nothing, which means the
                //     device is either idle or gone and only `adb devices` can say
                //     which (this is also the USB->Wi-Fi fallback path: a pulled
                //     cable makes the query go empty, and detection then picks up
                //     the wireless link if it's available).
                if (string.IsNullOrEmpty(_currentDevice) || _forceRedetect)
                {
                    _forceRedetect = false;
                    await DetectDeviceAsync().ConfigureAwait(false);

                    // Detection's recovery branch can settle on a wireless link in
                    // WD/TCP modes even when a USB cable is present. The app has
                    // always preferred USB whenever it's plugged in, so if we landed
                    // on wireless, prefer USB now. This is the "USB already plugged
                    // in at startup" case that WM_DEVICECHANGE can't see.
                    if (!string.IsNullOrEmpty(_currentDevice) && !_currentDeviceIsUsb)
                        await TryPromoteToUsbAsync().ConfigureAwait(false);
                }

                bool hasActiveSong = false;
                if (!string.IsNullOrEmpty(_currentDevice))
                {
                    hasActiveSong = await UpdateCurrentSongAsync().ConfigureAwait(false);

                    if (_deviceQueryCameBackEmpty)
                    {
                        await DetectDeviceAsync().ConfigureAwait(false);
                        if (string.IsNullOrEmpty(_currentDevice))
                            hasActiveSong = false;
                    }
                }

                if (string.IsNullOrEmpty(_currentDevice))
                {
                    NotifyNowPlaying(null, null, null);
                    NotifyLyricsPlayback(null, null, null, false, false, 0);
                    NotifyMediaPlayerState(null, null, null, null, false, 0, 0);
                }

                NotifyTrayState(BuildTrayState(hasActiveSong));

                // hasActiveSong reflects this tick's playing state, so use it to pick
                // the slowdown threshold and adjust the interval for the next tick.
                ApplyAdaptivePolling(hasActiveSong);
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
                // One adb-devices probe per detection pass. Detection only runs on
                // cold start, a forced redetect, a manual mode switch, or when the
                // media_session liveness query came back empty. The per-poll path
                // never calls adb devices; dumpsys media_session is the liveness
                // signal. With no _currentDevice there is no serial to target dumpsys
                // against, so this single probe is the one unavoidable spot.
                var devices = await AdbHelper.RunAdbCaptureAsync("devices");
                var deviceList = devices.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                // Drop to "no device" and notify the UI once on the transition.
                void Disconnect()
                {
                    if (!string.IsNullOrEmpty(_currentDevice))
                    {
                        _currentDevice = string.Empty;
                        _mediaController.Clear();
                        NotifyNowPlaying(null, null, null);
                        NotifyLyricsPlayback(null, null, null, false, false, 0);
                    }
                }

                // -- USB: always the preferred transport when a cable is present --
                // True in every mode (USB-only, WD, TCP/IP). Prefer the saved serial;
                // otherwise take the first online non-wireless serial.
                string usb = string.Empty;
                if (!string.IsNullOrWhiteSpace(_config.Device.SelectedDeviceUSB)
                    && deviceList.Any(l => GetOnlineSerial(l).Equals(_config.Device.SelectedDeviceUSB, StringComparison.OrdinalIgnoreCase)))
                {
                    usb = _config.Device.SelectedDeviceUSB;
                }
                else
                {
                    foreach (var entry in deviceList)
                    {
                        var serial = GetOnlineSerial(entry);
                        if (string.IsNullOrWhiteSpace(serial) || IsWirelessSerial(serial))
                            continue;

                        usb = serial;
                        break;
                    }
                }

                if (!string.IsNullOrWhiteSpace(usb))
                {
                    // Capture whether we were waiting for this reconnect before we
                    // clear the flag, so the TCP/IP re-setup below can tell a prompted
                    // reconnect from a cable that was simply always there.
                    bool wasAwaitingUsbReconnect = _wifiNeedsUsbReconnect;

                    _currentDevice = usb;
                    _currentDeviceIsUsb = true;
                    _wifiNeedsUsbReconnect = false;
                    _wifiReconnectPromptShown = false;
                    _wifiReconnectFailurePromptShown = false;
                    _lastWirelessReconnectUtc = DateTimeOffset.MinValue;

                    // TCP/IP only: the user was asked to reconnect USB because the
                    // wireless link dropped, and they just did. Re-run the tcpip setup
                    // once so Wi-Fi is ready next time the cable is pulled. Gated on
                    // wasAwaitingUsbReconnect so it fires only in direct response to
                    // the prompt (one attempt per unplug/replug cycle), never on the
                    // steady-state "USB always plugged" path. WD never reaches this;
                    // it reconnects over mDNS without USB.
                    if (wasAwaitingUsbReconnect
                        && _config.Device.WifiMode == WirelessMode.TcpIp
                        && _config.Device.IsWifiEnabled == true
                        && !string.IsNullOrWhiteSpace(_config.Device.SelectedDeviceWiFi)
                        && _config.Device.SelectedDeviceWiFi != "None")
                    {
                        var newWifi = await SetupWirelessFromUsbAsync(usb).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(newWifi)
                            && !string.Equals(newWifi, _config.Device.SelectedDeviceWiFi, StringComparison.OrdinalIgnoreCase))
                        {
                            _config.Device.SelectedDeviceWiFi = newWifi;
                            MusicConfigManager.Save(_config);
                            await _dispatcher.InvokeAsync(() => (Application.Current as App)?.UpdateConfig(_config));
                        }

                        // USB stays the active link regardless of the setup outcome.
                        _currentDevice = usb;
                        _currentDeviceIsUsb = true;
                        _wifiNeedsUsbReconnect = false;
                    }

                    return;
                }

                // -- No cable. What we try next is dictated entirely by the mode. --
                var now = DateTimeOffset.UtcNow;
                bool mayReconnect = now - _lastWirelessReconnectUtc >= WirelessReconnectInterval;

                switch (_config.Device.WifiMode)
                {
                    case WirelessMode.UsbOnly:
                        // No wireless of any kind. Without a cable there is no device,
                        // and we never ask for a reconnect (that is TCP/IP only).
                        Disconnect();
                        _currentDeviceIsUsb = false;
                        _wifiNeedsUsbReconnect = false;
                        _lastWirelessReconnectUtc = DateTimeOffset.MinValue;
                        return;

                    case WirelessMode.WirelessDebugging:
                        {
                            // Already up over WD? Use it without an mDNS round-trip. This
                            // is the cheap path that runs every poll off the probe above.
                            var live = FindConnectedWirelessSerial(deviceList);
                            if (!string.IsNullOrWhiteSpace(live))
                            {
                                _currentDevice = live;
                                _currentDeviceIsUsb = false;
                                _wifiNeedsUsbReconnect = false;
                                _wifiReconnectPromptShown = false;
                                _wifiReconnectFailurePromptShown = false;
                                _lastWirelessReconnectUtc = DateTimeOffset.MinValue;
                                return;
                            }

                            // Not up: throttled mDNS reconnect, the canonical WD path. The
                            // stale last-known connect and USB-assisted recovery are NOT
                            // run per tick; they add a blocking connect and extra adb-
                            // devices calls every poll while the cable is out.
                            if (mayReconnect
                                && _config.Device.IsWifiEnabled == true
                                && !string.IsNullOrWhiteSpace(_config.Device.MdnsServiceName))
                            {
                                _lastWirelessReconnectUtc = now;
                                var ipPort = await WirelessDebuggingHelper.ReconnectViaMdnsAsync(_config.Device.MdnsServiceName).ConfigureAwait(false);
                                if (!string.IsNullOrWhiteSpace(ipPort))
                                {
                                    _currentDevice = ipPort;
                                    _currentDeviceIsUsb = false;
                                    _wifiNeedsUsbReconnect = false;
                                    _wifiReconnectPromptShown = false;
                                    _wifiReconnectFailurePromptShown = false;
                                    _lastWirelessReconnectUtc = DateTimeOffset.MinValue;

                                    if (!string.Equals(_config.Device.SelectedDeviceWiFi, ipPort, StringComparison.OrdinalIgnoreCase))
                                    {
                                        _config.Device.SelectedDeviceWiFi = ipPort;
                                        MusicConfigManager.Save(_config);
                                        await _dispatcher.InvokeAsync(() => (Application.Current as App)?.UpdateConfig(_config));
                                    }
                                    return;
                                }
                            }

                            // Nothing found (or throttled). In WD a lost link is just
                            // disconnected; we never ask for a USB reconnect.
                            Disconnect();
                            _currentDeviceIsUsb = false;
                            _wifiNeedsUsbReconnect = false;
                            return;
                        }

                    case WirelessMode.TcpIp:
                        {
                            bool wifiConfigured = !string.IsNullOrWhiteSpace(_config.Device.SelectedDeviceWiFi)
                                && _config.Device.SelectedDeviceWiFi != "None";

                            // Already up over TCP/IP? The live serial is the ip:port. Cheap
                            // path off the probe above, runs every poll.
                            var live = FindConnectedWirelessSerial(deviceList);
                            if (!string.IsNullOrWhiteSpace(live))
                            {
                                _currentDevice = live;
                                _currentDeviceIsUsb = false;
                                _wifiNeedsUsbReconnect = false;
                                _wifiReconnectPromptShown = false;
                                _wifiReconnectFailurePromptShown = false;
                                _lastWirelessReconnectUtc = DateTimeOffset.MinValue;
                                return;
                            }

                            // Not up: throttled single connect to the saved fixed port
                            // (option B). Not run per tick; a connect to an unreachable
                            // endpoint blocks for the TCP timeout.
                            if (mayReconnect && wifiConfigured && _config.Device.IsWifiEnabled == true)
                            {
                                _lastWirelessReconnectUtc = now;
                                if (await WirelessDebuggingHelper.TryConnectLastKnownAsync(_config.Device.SelectedDeviceWiFi).ConfigureAwait(false))
                                {
                                    _currentDevice = _config.Device.SelectedDeviceWiFi;
                                    _currentDeviceIsUsb = false;
                                    _wifiNeedsUsbReconnect = false;
                                    _wifiReconnectPromptShown = false;
                                    _wifiReconnectFailurePromptShown = false;
                                    _lastWirelessReconnectUtc = DateTimeOffset.MinValue;
                                    return;
                                }
                            }

                            // No live link. This is the only mode that asks the user to
                            // reconnect USB so Wi-Fi can be set up again; the reconnect
                            // itself is handled by the USB branch above.
                            Disconnect();
                            _currentDeviceIsUsb = false;
                            _wifiNeedsUsbReconnect = wifiConfigured && _config.Device.IsWifiEnabled == true;
                            if (_wifiNeedsUsbReconnect && !_wifiReconnectPromptShown)
                            {
                                _wifiReconnectPromptShown = true;
                                (Application.Current as App)?.ShowToast(
                                    "Wireless connection lost. Reconnect your phone via USB to re-setup wireless.",
                                    ToastLevel.Warning);
                            }
                            return;
                        }
                }
            }
            catch (Exception ex)
            {
                Debugger.show("DetectDeviceAsync failed: " + ex.Message);
            }
        }

        // Called from the WM_DEVICECHANGE handler when Windows reports a change to
        // the device tree. This is the Wi-Fi->USB promotion path: while a wireless
        // link is healthy the tick loop never runs `adb devices`, so a cable being
        // plugged in would otherwise go unnoticed. The cost is one `adb devices`
        // call per (throttled) hardware change. If the change was some unrelated
        // USB device, no USB Android serial is found and the method simply returns.
        public async Task CheckForUsbPromotionAsync()
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastUsbPromotionProbeUtc < UsbPromotionProbeThrottle)
                return;
            _lastUsbPromotionProbeUtc = now;

            // Give adb time to enumerate the just-plugged device before we probe;
            // otherwise `adb devices` runs before the USB serial appears and the
            // promotion silently no-ops. Unnoticeable to the user.
            await Task.Delay(UsbPromotionProbeDelay).ConfigureAwait(false);

            // Wait briefly for any in-flight tick to finish rather than bailing, so
            // the freshly plugged device isn't missed due to lock contention.
            if (!await _updateLock.WaitAsync(UsbPromotionLockWaitMs).ConfigureAwait(false))
                return;

            try
            {
                await TryPromoteToUsbAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debugger.show("CheckForUsbPromotionAsync failed: " + ex.Message);
            }
            finally
            {
                _updateLock.Release();
            }
        }

        // Core USB-preference logic. The caller MUST already hold _updateLock (the
        // tick loop calls this directly; the public wrapper above acquires the lock
        // first). Promotes to a connected USB device when we're currently on a
        // wireless link or have no device. No-op if already on USB or no USB found.
        private async Task TryPromoteToUsbAsync()
        {
            if (_currentDeviceIsUsb)
                return;

            var devices = await AdbHelper.RunAdbCaptureAsync("devices").ConfigureAwait(false);
            var deviceList = devices.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            string usbSerial = string.Empty;
            if (!string.IsNullOrWhiteSpace(_config.Device.SelectedDeviceUSB)
                && deviceList.Any(l => GetOnlineSerial(l).Equals(_config.Device.SelectedDeviceUSB, StringComparison.OrdinalIgnoreCase)))
            {
                usbSerial = _config.Device.SelectedDeviceUSB;
            }
            else
            {
                foreach (var entry in deviceList)
                {
                    var serial = GetOnlineSerial(entry);
                    if (string.IsNullOrWhiteSpace(serial) || IsWirelessSerial(serial))
                        continue;

                    usbSerial = serial;
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(usbSerial))
                return;

            if (string.Equals(_currentDevice, usbSerial, StringComparison.OrdinalIgnoreCase))
            {
                // Already pointed at this USB serial; just make sure the flags agree.
                _currentDeviceIsUsb = true;
                return;
            }

            Debugger.show($"[CONNECTION] Preferring USB; switching from '{(string.IsNullOrEmpty(_currentDevice) ? "none" : _currentDevice)}' to USB '{usbSerial}'.");
            _currentDevice = usbSerial;
            _currentDeviceIsUsb = true;
            _wifiNeedsUsbReconnect = false;
            _deviceQueryCameBackEmpty = false;
        }

        private async Task RecoverWirelessConnectionAsync()
        {
            if (_isRecoveringWifi) return;
            _isRecoveringWifi = true;

            try
            {
                if (_config.Device.WifiMode == WirelessMode.WirelessDebugging)
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
                (Application.Current as App)?.ShowToast("Wireless connection failed. Please reconnect your phone via USB to re-setup wireless.", ToastLevel.Warning);
                _wifiReconnectPromptShown = true;
            }

            var usbDevice = await GetUsbDeviceForRecoveryAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(usbDevice))
                return;

            var newWifi = await SetupWirelessFromUsbAsync(usbDevice).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(newWifi))
                return;

            _config.Device.SelectedDeviceWiFi = newWifi;
            MusicConfigManager.Save(_config);
            await _dispatcher.InvokeAsync(() =>
            {
                (Application.Current as App)?.UpdateConfig(_config);
            });
            _wifiReconnectPromptShown = false;

            (Application.Current as App)?.ShowToast($"Wireless device has been re-setup and saved as {newWifi}.\n\nIf you want to continue using USB, disconnect and reconnect the cable now (USB may stay unavailable right after Wi-Fi setup).", ToastLevel.Warning);
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
            if (!string.IsNullOrWhiteSpace(_config.Device.MdnsServiceName))
            {
                var ipPort = await WirelessDebuggingHelper.ReconnectViaMdnsAsync(_config.Device.MdnsServiceName).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(ipPort))
                {
                    _config.Device.SelectedDeviceWiFi = ipPort;
                    _currentDevice = ipPort;
                    _currentDeviceIsUsb = false;
                    _wifiNeedsUsbReconnect = false;
                    _wifiReconnectPromptShown = false;
                    _wifiReconnectFailurePromptShown = false;
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
            if (!string.IsNullOrWhiteSpace(_config.Device.SelectedDeviceWiFi)
                && _config.Device.SelectedDeviceWiFi != "None")
            {
                if (await WirelessDebuggingHelper.TryConnectLastKnownAsync(_config.Device.SelectedDeviceWiFi).ConfigureAwait(false))
                {
                    _currentDevice = _config.Device.SelectedDeviceWiFi;
                    _currentDeviceIsUsb = false;
                    _wifiNeedsUsbReconnect = false;
                    _wifiReconnectPromptShown = false;
                    _wifiReconnectFailurePromptShown = false;
                    return;
                }
            }

            // Step 3: USB-assisted recovery. Prompt the user, get a USB
            // device, retry mDNS once, and as a last resort try the last
            // known port at the phone's current wlan0 IP.
            if (!_wifiReconnectFailurePromptShown)
            {
                _wifiReconnectFailurePromptShown = true;
                (Application.Current as App)?.ShowToast("Wireless connection failed. Make sure Wireless Debugging is still enabled on your phone\n"
                        + "(Settings, Developer options, Wireless debugging)", ToastLevel.Warning);
            }

            var usbDevice = await GetUsbDeviceForRecoveryAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(usbDevice))
            {
                _currentDevice = string.Empty;
                _currentDeviceIsUsb = false;
                _wifiNeedsUsbReconnect = false;
                return;
            }

            // Retry mDNS now that we have USB context. Some networks only
            // surface the service after the phone has been actively used.
            if (!string.IsNullOrWhiteSpace(_config.Device.MdnsServiceName))
            {
                var ipPort = await WirelessDebuggingHelper.ReconnectViaMdnsAsync(_config.Device.MdnsServiceName).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(ipPort))
                {
                    _config.Device.SelectedDeviceWiFi = ipPort;
                    _currentDevice = ipPort;
                    _currentDeviceIsUsb = false;
                    _wifiNeedsUsbReconnect = false;
                    _wifiReconnectPromptShown = false;
                    _wifiReconnectFailurePromptShown = false;
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
            var freshIp = await DeviceQuery.GetDeviceWifiIpAsync(usbDevice).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(freshIp) && !string.IsNullOrWhiteSpace(_config.Device.SelectedDeviceWiFi))
            {
                var lastPort = ExtractWifiPort(_config.Device.SelectedDeviceWiFi);
                var candidate = $"{freshIp}:{lastPort}";
                if (await WirelessDebuggingHelper.TryConnectLastKnownAsync(candidate).ConfigureAwait(false))
                {
                    _config.Device.SelectedDeviceWiFi = candidate;
                    _currentDevice = candidate;
                    _currentDeviceIsUsb = false;
                    _wifiNeedsUsbReconnect = false;
                    _wifiReconnectPromptShown = false;
                    _wifiReconnectFailurePromptShown = false;
                    MusicConfigManager.Save(_config);
                    await _dispatcher.InvokeAsync(() =>
                    {
                        (Application.Current as App)?.UpdateConfig(_config);
                    });
                    return;
                }
            }

            _currentDevice = string.Empty;
            _currentDeviceIsUsb = false;
            _wifiNeedsUsbReconnect = false;
        }

        private async Task<string> GetUsbDeviceForRecoveryAsync()
        {
            var devices = await AdbHelper.RunAdbCaptureAsync("devices").ConfigureAwait(false);
            var deviceList = devices.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            bool IsDeviceConnected(string id) => deviceList.Any(l => l.StartsWith(id) && l.EndsWith("device"));

            if (!string.IsNullOrWhiteSpace(_config.Device.SelectedDeviceUSB) && IsDeviceConnected(_config.Device.SelectedDeviceUSB))
                return _config.Device.SelectedDeviceUSB;

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
            int port = ExtractWifiPort(_config.Device.SelectedDeviceWiFi);
            var ip = await DeviceQuery.GetDeviceWifiIpAsync(usbDevice).ConfigureAwait(false);
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

        /// <summary>
        /// Parses title, artist and album from the scrambled media_session metadata string by
        /// reading the exact field lengths from `dumpsys notification` for the active package via awk.
        /// The awk script runs on the phone and emits three key=value lines (title_len, artist_len,
        /// album_len). C# then does the substring extraction using those lengths. No sensitive
        /// notification content crosses the wire. Returns success=false if lengths cannot be read
        /// so the caller can fall back to a simple split.
        /// </summary>
        private async Task<(string? title, string? artist, string? album, bool success)> TryParseMediaMetadataAsync(
            string scrambledMetadata, string packageName)
        {
            try
            {
                Debugger.show($"[NOTIFPARSER] Fetching notification lengths for package: {packageName}");
                // Server-side awk: scans dumpsys notification for the NotificationRecord block
                // belonging to the target package, extracts only the [length=N] values for
                // android.title, android.text (artist), and android.subText (album), and exits
                // immediately after the first match. No string values are read or transmitted.
                var awkNotif =
                    "/NotificationRecord\\(/ { in_block=0; title_len=0; text_len=0; sub_len=0 } " +
                    "/pkg=/ && $0 ~ pkg { in_block=1 } " +
                    "in_block && /android\\.title=String/ { line=$0; sub(/.*\\[length=/, \"\", line); sub(/\\].*/,  \"\", line); title_len=line+0 } " +
                    "in_block && /android\\.text=String/ { line=$0; sub(/.*\\[length=/, \"\", line); sub(/\\].*/,   \"\", line); text_len=line+0 } " +
                    "in_block && /android\\.subText=String/ { line=$0; sub(/.*\\[length=/, \"\", line); sub(/\\].*/, \"\", line); sub_len=line+0 } " +
                    "in_block && title_len && text_len { " +
                    "print \"title_len=\" title_len; print \"artist_len=\" text_len; print \"album_len=\" sub_len; exit }";

                string notifOutput = await AdbHelper.RunAdbCaptureAsync(
                    $"-s {_currentDevice} shell dumpsys notification | awk -v pkg='{packageName}' '{awkNotif}'"
                ).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(notifOutput))
                {
                    Debugger.show("[NOTIFPARSER] No notification data available");
                    return (null, null, null, false);
                }

                int titleLen = 0;
                int artistLen = 0;
                int albumLen = 0;

                foreach (var rawLine in notifOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (rawLine.StartsWith("title_len=", StringComparison.Ordinal))
                        int.TryParse(rawLine.Substring("title_len=".Length).Trim(), out titleLen);
                    else if (rawLine.StartsWith("artist_len=", StringComparison.Ordinal))
                        int.TryParse(rawLine.Substring("artist_len=".Length).Trim(), out artistLen);
                    else if (rawLine.StartsWith("album_len=", StringComparison.Ordinal))
                        int.TryParse(rawLine.Substring("album_len=".Length).Trim(), out albumLen);
                }

                if (titleLen == 0 || artistLen == 0)
                {
                    Debugger.show($"[NOTIFPARSER] Missing required lengths (title={titleLen}, text={artistLen}, subText={albumLen})");
                    return (null, null, null, false);
                }

                // Separator between fields in the scrambled string is ", " (2 chars).
                const int sep = 2;
                int needed = titleLen + sep + artistLen;
                if (albumLen > 0) needed += sep + albumLen;

                if (scrambledMetadata.Length < needed)
                {
                    Debugger.show($"[NOTIFPARSER] Scrambled string ({scrambledMetadata.Length}) shorter than expected ({needed})");
                    return (null, null, null, false);
                }

                string title = scrambledMetadata.Substring(0, titleLen);
                string artist = scrambledMetadata.Substring(titleLen + sep, artistLen);
                string album = albumLen > 0
                    ? scrambledMetadata.Substring(titleLen + sep + artistLen + sep, albumLen)
                    : string.Empty;

                Debugger.show($"[NOTIFPARSER] \u2713 Parsed via lengths (T={titleLen}, A={artistLen}, Al={albumLen}): Title='{title}', Artist='{artist}', Album='{album}'");
                return (title, artist, album, true);
            }
            catch (Exception ex)
            {
                Debugger.show($"[NOTIFPARSER] \u2717 Exception: {ex.Message}");
                return (null, null, null, false);
            }
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
                _deviceQueryCameBackEmpty = false;

                if (string.IsNullOrEmpty(_currentDevice)) return false;

                // Server-side awk script: runs entirely on the phone and emits only the four
                // fields we need for the first active, eligible session. This replaces the old
                // grep -A 2 approach which still sent several lines per session block across
                // the wire and left all parsing to C#. The awk script handles both Android 13
                // bare-number state format (state=3) and Android 16+ named format (state=PLAYING(3)).
                // Output format:
                //   package=<pkg>
                //   description=<scrambled>
                //   state=<number>
                //   position=<ms>
                // Empty output means no active eligible session.
                var pkgList = string.Join("|", (_config.Apps.EligibleApps ?? new List<EligibleAppConfig>())
                    .Where(a => !string.IsNullOrWhiteSpace(a.PackageName) && a.PresenceMode != PresenceMode.Off)
                    .Select(a => a.PackageName.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase));

                if (string.IsNullOrWhiteSpace(pkgList)) return false;

                var awkMediaSession =
                    "/queueTitle=/ { in_block=1; pkg=\"\"; active=0; desc=\"\"; state=\"\"; pos=\"\" } " +
                    "in_block && /package=/ { line=$0; sub(/.*package=/, \"\", line); sub(/ .*/, \"\", line); pkg=line } " +
                    "in_block && /active=true/ { active=1 } " +
                    "in_block && /description=/ { line=$0; sub(/.*description=/, \"\", line); desc=line } " +
                    "in_block && /state=PlaybackState/ { line=$0; " +
                    "if (line ~ /state=[A-Z]+\\([0-9]/) { sub(/.*state=[A-Z]*\\(/, \"\", line); sub(/\\).*/, \"\", line) } " +
                    "else { sub(/.*state=/, \"\", line); sub(/,.*/, \"\", line) }; " +
                    "if (line ~ /^[0-9]/) state=line; line=$0; sub(/.*, position=/, \"\", line); sub(/,.*/, \"\", line); pos=line } " +
                    "in_block && active && pkg && desc && state && (pkg ~ pkgs) { " +
                    "if (state+0 == 3) { best_pkg=pkg; best_desc=desc; best_state=state; best_pos=pos; found_playing=1 } " +
                    "else if (!found_playing && !best_pkg) { best_pkg=pkg; best_desc=desc; best_state=state; best_pos=pos } " +
                    "in_block=0 } " +
                    "END { if (best_pkg) { print \"package=\" best_pkg; print \"description=\" best_desc; print \"state=\" best_state; print \"position=\" best_pos } }";

                string output = await AdbHelper.RunAdbCaptureAsync(
                    $"-s {_currentDevice} shell dumpsys media_session | awk -v pkgs='{pkgList}' '{awkMediaSession}'"
                );
                if (string.IsNullOrWhiteSpace(output))
                {
                    // Empty output means the device gave us nothing: either it's
                    // idle with no eligible session, or the connection dropped.
                    // TickAsync reconciles with a single `adb devices` to tell
                    // which, and to promote a wireless link in if USB was pulled.
                    _deviceQueryCameBackEmpty = true;
                    return false;
                }

                // Parse the flat awk output: four key=value lines.
                string pkg = string.Empty;
                string scrambledData = string.Empty;
                int awkState = 0;
                long awkPosition = 0;

                foreach (var rawLine in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (rawLine.StartsWith("package=", StringComparison.Ordinal))
                        pkg = rawLine.Substring("package=".Length).Trim();
                    else if (rawLine.StartsWith("description=", StringComparison.Ordinal))
                        scrambledData = rawLine.Substring("description=".Length).Trim();
                    else if (rawLine.StartsWith("state=", StringComparison.Ordinal))
                        int.TryParse(rawLine.Substring("state=".Length).Trim(), out awkState);
                    else if (rawLine.StartsWith("position=", StringComparison.Ordinal))
                        long.TryParse(rawLine.Substring("position=".Length).Trim(), out awkPosition);
                }

                if (string.IsNullOrEmpty(pkg) || string.IsNullOrEmpty(scrambledData))
                {
                    _mediaController.ClearDisplay();
                    NotifyNowPlaying(null, null, null);
                    NotifyLyricsPlayback(null, null, null, false, false, 0);
                    NotifyMediaPlayerState(null, null, null, null, false, 0, 0);
                    return false;
                }

                // Look up cover search and SMTC flags for the matched package.
                var eligibleApps = (_config.Apps.EligibleApps ?? new List<EligibleAppConfig>())
                    .Where(a => !string.IsNullOrWhiteSpace(a.PackageName))
                    .GroupBy(a => a.PackageName.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        g => g.Key,
                        g => new EligibleAppConfig
                        {
                            PackageName = g.Key,
                            PresenceMode = g.Max(x => (int)x.PresenceMode) switch
                            {
                                2 => PresenceMode.Full,
                                1 => PresenceMode.Half,
                                _ => PresenceMode.Off
                            },
                            EnableCoverSearch = g.Any(x => x.EnableCoverSearch),
                            UseSubsonic = g.Any(x => x.UseSubsonic)
                        },
                        StringComparer.OrdinalIgnoreCase);

                bool enableCoverSearchForApp = false;
                bool useSubsonicForApp = false;
                bool enableSmtcForApp = false;
                if (eligibleApps.TryGetValue(pkg, out var matchedApp))
                {
                    enableCoverSearchForApp = matchedApp.EnableCoverSearch;
                    useSubsonicForApp = matchedApp.UseSubsonic;
                    enableSmtcForApp = matchedApp.PresenceMode == PresenceMode.Full;
                }

                CurrentAppUseSubsonic = useSubsonicForApp;
                CurrentAppCoverSearch = enableCoverSearchForApp;

                // Parse title/artist/album. If the scrambled string is identical to last tick
                // we reuse the cached parse result so we don't re-run the notification parser
                // (which would fire another adb call). We must NOT skip the rest of the method:
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
                    // A track change counts as activity: a churning album stays on the
                    // fast interval, while a single long or looping track does not.
                    _lastActivityUtc = DateTimeOffset.UtcNow;
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
                {
                    _mediaController.ClearDisplay();
                    NotifyNowPlaying(null, null, null);
                    NotifyLyricsPlayback(null, null, null, false, false, 0);
                    NotifyMediaPlayerState(null, null, null, null, false, 0, 0);
                    return false;
                }

                // State and position come directly from the awk output; no further regex needed.
                bool isPlaying = awkState == 3;
                long adbPositionMs = awkPosition;

                if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(artist))
                {
                    NotifyNowPlaying(artist, title, album);
                    NotifyLyricsPlayback(artist, title, album, isPlaying, useSubsonicForApp, Math.Max(0, adbPositionMs));

                    if (!isPlaying)
                    {
                        var signature = $"{title}\n{artist}\n{album}";
                        if (_pausedSince == null || !string.Equals(_pausedSignature, signature, StringComparison.Ordinal))
                        {
                            _pausedSince = DateTimeOffset.UtcNow;
                            _pausedSignature = signature;
                        }

                        if (_config.MediaPlayer.SmtcPauseClearDelayMinutes > 0 && _pausedSince.HasValue)
                        {
                            var elapsed = DateTimeOffset.UtcNow - _pausedSince.Value;
                            if (elapsed.TotalMinutes >= _config.MediaPlayer.SmtcPauseClearDelayMinutes)
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
                        else if (_config.MediaPlayer.SmtcPauseClearDelayMinutes == 0)
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

                    await _mediaController.UpdateMediaControlsAsync(title, artist, album, isPlaying, enableCoverSearchForApp, useSubsonicForApp, enableSmtcForApp, adbPositionMs, _timer.Interval).ConfigureAwait(false);
                    NotifyMediaPlayerState(_mediaController.CurrentTitle, _mediaController.CurrentArtist, _mediaController.CurrentAlbum, _mediaController.CurrentCoverPath, isPlaying, _mediaController.CurrentPositionMs, _mediaController.CurrentDurationMs);
                    return isPlaying;
                }

                _mediaController.ClearDisplay();
                NotifyNowPlaying(null, null, null);
                NotifyLyricsPlayback(null, null, null, false, false, 0);
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

        private void NotifyLyricsPlayback(string? artist, string? title, string? album, bool isPlaying, bool useSubsonic, long positionMs)
        {
            try
            {
                LyricsPlaybackChanged?.Invoke(artist, title, album, isPlaying, useSubsonic, positionMs);
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
                bool isWirelessDebugging = _config.Device.WifiMode == WirelessMode.WirelessDebugging;

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

        // Called by the media controller for transport commands (SMTC or the player
        // window) and by the app for volume changes. Marks activity and, if we're
        // currently slowed, hops back to the dispatcher to restore the base interval
        // and refresh immediately. Safe to call from any thread.
        public void NotifyUserInteraction()
        {
            _lastActivityUtc = DateTimeOffset.UtcNow;

            if (!_config.Polling.AdaptiveEnabled || !_adaptiveActive)
                return;

            _adaptiveActive = false; // set synchronously so a burst doesn't queue many
            _dispatcher.BeginInvoke(new Action(ResumeBasePolling));
        }

        private void ResumeBasePolling()
        {
            var baseInterval = GetInterval(_config.Polling.Interval);
            if (baseInterval.TotalSeconds <= 0)
                return;

            if (_timer.Interval != baseInterval)
                SetPollInterval(baseInterval);

            _ = TickAsync();
        }

        // Steps the poll interval down a fixed ladder the longer nothing happens, and
        // never below the user's configured interval. Runs on the dispatcher thread.
        private void ApplyAdaptivePolling(bool isPlaying)
        {
            if (!_config.Polling.AdaptiveEnabled)
                return;

            var baseInterval = GetInterval(_config.Polling.Interval);
            if (baseInterval.TotalSeconds <= 0)
                return; // polling disabled entirely; nothing to scale

            // Never slow down while disconnected: keep retrying at the base rate.
            if (string.IsNullOrEmpty(_currentDevice))
            {
                _lastActivityUtc = DateTimeOffset.UtcNow;
                SetAdaptiveInterval(baseInterval, baseInterval);
                return;
            }

            // N = user threshold in minutes. Playing gets 2N for the first drop;
            // subsequent steps use ceil(N/2), ceil(N/4), ceil(N/8).
            double n = Math.Max(1, _config.Polling.AdaptiveThresholdMinutes);
            double step1 = isPlaying ? n * 2 : n;
            double step2 = step1 + Math.Ceiling(n / 2.0);
            double step3 = step2 + Math.Ceiling(n / 4.0);
            double step4 = step3 + Math.Ceiling(n / 8.0);

            double elapsedMinutes = (DateTimeOffset.UtcNow - _lastActivityUtc).TotalMinutes;

            TimeSpan target;
            if (elapsedMinutes < step1)
                target = baseInterval;
            else if (elapsedMinutes < step2)
                target = AdaptiveStage1;
            else if (elapsedMinutes < step3)
                target = AdaptiveStage2;
            else if (elapsedMinutes < step4)
                target = AdaptiveStage3;
            else
                target = AdaptiveStage4;

            if (target < baseInterval)
                target = baseInterval; // never poll faster than the user asked

            SetAdaptiveInterval(target, baseInterval);
        }

        private void SetAdaptiveInterval(TimeSpan target, TimeSpan baseInterval)
        {
            if (_timer.Interval != target)
            {
                if (target > baseInterval)
                {
                    Debugger.show($"[ADAPTIVE] Poll interval slowed: {_timer.Interval.TotalSeconds:0}s -> {target.TotalSeconds:0}s (inactive for {(DateTimeOffset.UtcNow - _lastActivityUtc).TotalMinutes:0.0} min).");
                    if (_config.Polling.AdaptiveAlertEnabled)
                        (Application.Current as App)?.ShowToast($"Poll rate slowed to {target.TotalSeconds:0}s due to inactivity.", ToastLevel.Info);
                    // Extend the shell session idle timeout when we reach the 30s stage
                    // so the persistent session doesn't expire between polls.
                    if (target == AdaptiveStage4)
                        AdbHelper.SessionIdleTimeout = TimeSpan.FromSeconds(40);
                }
                else
                {
                    Debugger.show($"[ADAPTIVE] Poll interval restored to base: {target.TotalSeconds:0}s.");
                    AdbHelper.SessionIdleTimeout = TimeSpan.FromSeconds(20);
                }
                SetPollInterval(target);
            }

            _adaptiveActive = target > baseInterval;
        }

        // Single choke point for poll-interval changes: keeps the debug log's
        // separator threshold in step with however fast we're actually polling
        // (base or adaptive), so routine ticks never get separator lines.
        private void SetPollInterval(TimeSpan interval)
        {
            _timer.Interval = interval;
            Debugger.SeparatorGap = TimeSpan.FromSeconds(Math.Max(5, interval.TotalSeconds + 2));
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