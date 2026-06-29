using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;


// version names:
// v1.0: the lost version
// v1.0.1 : initial public release
// v1.0.2 : hotkeys update
// v1.0.3 : the subfolder traumu update
// v1.0.4 : settingsmode shenanigans update
// v1.0.5 : the UI update 1
// v1.0.6 : app elligibility update
// v1.0.7 : the quality presets update
// v1.0.8 : caching and tracking update
// v1.0.9 : the forgotten update
// v1.0.10 : cover data collection hell update
// v1.0.11 : the cover fixer update
// v1.1.0 : the media player window update + a some unboarding on the side
// v1.1.1 : mediaplayer button heaven update
// v1.2.0 : the connection update
// v1.3.0 : paths and updates update
// v1.3.1 : the hotfix update
// v1.3.2 : random bulshit go update
// v1.4.0 : lost in installer hell update
// v1.4.1 : the dependency and installers update
// v1.4.2 : the QR code update
// v1.4.3 : random bulshit go 2 electric boogaloo update
// v1.4.4 : elligable windows & audio link aditions update
// v1.4.5 : the customizability update
// v1.5.0 : the big boy update aka the MVVM refactor update aka the rewrite update aka nexts and previous's update
// v1.6.0 : the embedded lyrics and metadata editing update with some adaptive poll rates on the side


namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        internal static MusicConfig Config { get; private set; } = new MusicConfig();

        private TrayIconManager? _trayIconManager;
        private MusicPresenceService? _presenceService;
        private MainWindow? _settingsWindow;
        private Process? _scrcpyProcess;
        private LyricsOverlayManager? _lyricsOverlayManager;
        private MediaPlayerWindow? _mediaPlayerWindow;
        private NextSongManager _nextSongManager = new NextSongManager();
        private HwndSource? _hotkeySource;
        private NotificationToastManager? _toastManager;
        private const string StartupRunValueName = "AndroidMusicPresenceLink";

        private const int HotkeyIdVolumeUp = 1;
        private const int HotkeyIdVolumeDown = 2;
        private const int HotkeyIdToggleScrcpy = 3;
        private const int HotkeyIdToggleLyricsOverlay = 4;
        private const int HotkeyIdCopyTrackInfo = 5;
        private const int HotkeyIdAudioQuality = 6;
        private const int ModShift = 0x0004;
        private const int VkVolumeUp = 0xAF;
        private const int VkVolumeDown = 0xAE;
        private const int WmHotkey = 0x0312;
        private const int WmDeviceChange = 0x0219;
        private const int DbtDevnodesChanged = 0x0007;
        private const int DbtDeviceArrival = 0x8000;
        private const float ScrcpyVolumeStep = 0.05f;
        private const string AppUserModelId = "Android Music Presence Link";

        private static readonly string version = GetAppVersion();

        internal static string CurrentVersion => version;

        internal void ShowToast(string message, ToastLevel level = ToastLevel.Info)
            => _toastManager?.Show(message, level);




        private bool _isScrcpyRunning;
        private bool _isExiting;
        // Device id (USB serial or "ip:port") that _scrcpyProcess was started against.
        // Used to detect when the active connection changes mid-session and the audio
        // link needs to be migrated to the new transport.
        private string? _scrcpyDeviceId;
        // Whether _scrcpyProcess was started over USB (vs Wi-Fi). Cached so we don't
        // re-derive it from _scrcpyDeviceId everywhere.
        private bool _scrcpyDeviceIsUsb;
        // Set while a transport switch is in progress so reentrant tray-state events
        // don't kick off overlapping switches. Also prevents the "scrcpy died" path
        // from interpreting an intentional shutdown of the old process as a real exit.
        private bool _scrcpySwitchInProgress;
        // True when the user has requested the audio link be active. Persists across
        // transport flips so that when scrcpy dies during a USB cable yank (or any
        // other unexpected exit) we can automatically reconnect on the new transport
        // instead of leaving audio off forever. Cleared only by an explicit user stop
        // or app exit.
        private bool _audioQualityWindowOpen;
        private bool _metadataEditWindowOpen;
        private bool _saveTrackOpen;
        private bool _audioLinkDesired;
        // Cancels any pending recovery delay when scrcpy has died and we're waiting
        // for a device to come back so we can restart the audio link. Tied to
        // _audioLinkDesired: cancelled when the user explicitly stops or app exits.
        private CancellationTokenSource? _audioLinkRecoveryCts;
        // How long to wait after an unexpected scrcpy exit for a device to (re)appear
        // before giving up on automatic reconnection.
        private const int AudioLinkRecoveryWindowMs = 8000;
        private TrayIconState _lastTrayState = TrayIconState.NoDevice;
        private TrayIconState? _lastLoggedTrayState;
        private string? _lastToastedTransport;
        private string? _lastNowPlayingArtist;
        private string? _lastNowPlayingTitle;
        private string? _lastNowPlayingAlbum;
        private string? _lastMediaPlayerTitle;
        private string? _lastMediaPlayerArtist;
        private string? _lastMediaPlayerAlbum;
        private string? _lastMediaPlayerCoverPath;
        private bool _lastMediaPlayerIsPlaying;
        private long _lastMediaPlayerPositionMs;
        private long _lastMediaPlayerDurationMs;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);



            Config = MusicConfigManager.Load();
            ApplyStartupRegistration(Config.StartWithWindows);
            Debugger.IsEnabled = Config.DebugMode;
            AdbHelper.AdbPath = Config.Paths.Adb;
            if (Config.RandomThemeAtStartup)
            {
                var randomTheme = ThemeCatalog.RandomEnabledThemeName(Config);
                if (!string.IsNullOrEmpty(randomTheme))
                    Config.ActiveThemeName = randomTheme;
            }
            ApplyActiveTheme(Config);

            _toastManager = new NotificationToastManager(Dispatcher)
            {
                GetConfig = () => Config,
                IsMediaPlayerOpen = () => _mediaPlayerWindow != null
            };

            _settingsWindow = new MainWindow();
            if (Config.OpenInTaskbar)
            {
                _settingsWindow.Hide();
            }
            else if (!Config.ShowMediaPlayerWindow)
            {
                _settingsWindow.Show();
                _settingsWindow.Activate();
            }

            if (!Config.OnboardingCompleted)
            {
                ShowOnboarding(forceRun: false);
            }

            _presenceService = new MusicPresenceService(Dispatcher, Config);
            _lyricsOverlayManager = new LyricsOverlayManager(Dispatcher, Config, () => _presenceService?.CurrentDevice ?? string.Empty, () => (_presenceService?.CurrentRemoteFilePath, _presenceService?.CurrentRemoteFileToken));
            _trayIconManager = new TrayIconManager(ShowSettingsWindow, ToggleScrcpyNoAudio, ShutdownApplication, Config.UseDarkMode);
            SessionEnding += OnSessionEnding;
            _presenceService.TrayStateChanged += OnTrayStateChanged;
            _presenceService.NowPlayingChanged += OnNowPlayingChanged;
            _presenceService.LyricsPlaybackChanged += OnLyricsPlaybackChanged;
            _presenceService.MediaPlayerStateChanged += OnMediaPlayerStateChanged;
            _presenceService.Start();
            UpdateTrayAudioSettings();

            if (Config.ShowMediaPlayerWindow && !Config.OpenInTaskbar)
            {
                ShowMediaPlayerWindowNow();
            }
            else
            {
                UpdateSettingsWindowModeButton();
            }

            InitializeHotkeys();

            _ = Updater.CheckForUpdateAsync(version, showPrompt: Config.OpenInTaskbar, allowRemindLater: Config.OpenInTaskbar, ignoredVersion: Config.IgnoredUpdateVersion, onDismissed: ignoredVersion =>
            {
                Config.IgnoredUpdateVersion = ignoredVersion;
                MusicConfigManager.Save(Config);
            });
        }

        private static string GetAppVersion()
        {
            return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
        }

        private void OnNowPlayingChanged(string? artist, string? title, string? album)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;

            Dispatcher.BeginInvoke(() =>
            {
                _lastNowPlayingArtist = artist;
                _lastNowPlayingTitle = title;
                _lastNowPlayingAlbum = album;
                _trayIconManager?.SetNowPlaying(artist, title, album);
            });
        }

        private void OnTrayStateChanged(TrayIconState state)
        {
            _lastTrayState = state;

            if (_lastLoggedTrayState != state)
            {
                Debugger.show($"[CONNECTION] Connection state changed: {state}.");
                _lastLoggedTrayState = state;
            }

            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;

            Dispatcher.BeginInvoke(() =>
            {
                ApplyTrayState();
            });
        }

        private void ApplyTrayState()
        {
            var state = _lastTrayState;

            if (_isScrcpyRunning)
            {
                state = state switch
                {
                    TrayIconState.ActiveUsb => TrayIconState.ActiveUsbScrcpy,
                    TrayIconState.InactiveUsb => TrayIconState.InactiveUsbScrcpy,
                    TrayIconState.ActiveWifi => TrayIconState.ActiveWifiScrcpy,
                    TrayIconState.InactiveWifi => TrayIconState.InactiveWifiScrcpy,
                    TrayIconState.ActiveWifiDebug => TrayIconState.ActiveWifiDebugScrcpy,
                    TrayIconState.InactiveWifiDebug => TrayIconState.InactiveWifiDebugScrcpy,
                    _ => state
                };
            }

            _trayIconManager?.SetState(state);
            ApplyConnectionStateToMediaPlayer();
            _mediaPlayerWindow?.SetAudioLinkState(_isScrcpyRunning);

            // Toast when the transport category changes (USB / TCP-IP / WD / no device).
            // Scrcpy variants and Active/Inactive variants within the same transport are
            // intentionally ignored so toggling the audio link doesn't re-announce the
            // connection and spurious no-device blips during transport switches don't spam.
            string transport = _lastTrayState switch
            {
                TrayIconState.ActiveUsb or TrayIconState.InactiveUsb or
                TrayIconState.ActiveUsbScrcpy or TrayIconState.InactiveUsbScrcpy => "usb",
                TrayIconState.ActiveWifi or TrayIconState.InactiveWifi or
                TrayIconState.ActiveWifiScrcpy or TrayIconState.InactiveWifiScrcpy => "wifi",
                TrayIconState.ActiveWifiDebug or TrayIconState.InactiveWifiDebug or
                TrayIconState.ActiveWifiDebugScrcpy or TrayIconState.InactiveWifiDebugScrcpy => "wd",
                TrayIconState.NeedsUsbReconnect => "reconnect",
                _ => "none"
            };
            if (transport != _lastToastedTransport)
            {
                string? prevTransport = _lastToastedTransport;
                _lastToastedTransport = transport;
                string? msg = transport switch
                {
                    "usb" => "Connected via USB",
                    "wifi" => "Connected via TCP/IP",
                    "wd" => "Connected via Wireless Debugging",
                    "reconnect" => "Wi-Fi port lost, reconnect USB to restore",
                    "none" => prevTransport != null ? "Device disconnected" : null,
                    _ => null
                };
                if (msg != null)
                {
                    var lvl = transport == "none" || transport == "reconnect" ? ToastLevel.Warning : ToastLevel.Info;
                    ShowToast(msg, lvl);
                }
            }

            // If the audio link is active and the device's transport (USB vs Wi-Fi)
            // changed since we started scrcpy, migrate the audio link to the new
            // transport. We do this from ApplyTrayState because tray state changes
            // are exactly the moments the connection medium flips.
            CheckAudioLinkTransport();
        }

        /// <summary>
        /// Detects when the device id bound to the running scrcpy process no longer
        /// matches the live device id from the presence service (i.e. transport changed
        /// from USB to Wi-Fi or vice versa) and triggers an appropriate handover.
        /// Safe to call repeatedly; reentrant calls are guarded by _scrcpySwitchInProgress.
        ///
        /// When the device is empty (transient, e.g. USB just yanked, Wi-Fi not yet
        /// enumerated) we do nothing: scrcpy will either survive the blip or die on
        /// its own, and ScrcpyProcessExited handles the post-death recovery via
        /// _audioLinkDesired.
        /// </summary>
        private void CheckAudioLinkTransport()
        {
            if (_isExiting) return;
            if (_scrcpySwitchInProgress) return;
            if (_scrcpyProcess == null || _scrcpyProcess.HasExited) return;
            if (string.IsNullOrEmpty(_scrcpyDeviceId)) return;
            // Auto-switching requires a fast enough update cycle to be reliable.
            if (Config.UpdateIntervalMode > UpdateIntervalMode.Fast) return;
            if (!Config.AudioLinkConnectionAutoRestart) return;

            var liveDevice = _presenceService?.CurrentDevice ?? string.Empty;

            // Same device id, nothing to do.
            if (string.Equals(liveDevice, _scrcpyDeviceId, StringComparison.OrdinalIgnoreCase))
                return;

            // Device empty: don't stop, don't switch, just wait. If scrcpy dies during
            // this gap, ScrcpyProcessExited will arm recovery.
            if (string.IsNullOrWhiteSpace(liveDevice))
            {
                Debugger.show($"Audio-link: device transiently empty (was {_scrcpyDeviceId}); waiting.");
                return;
            }

            bool liveIsUsb = !liveDevice.Contains(':');

            // Always use a hard switch regardless of transport direction.
            // Seamless (concurrent) handover is not viable for audio-only scrcpy sessions:
            // Android does not allow two concurrent --audio-source=playback captures from
            // the same package, so the new session never actually starts while the old one
            // is alive. Hard switch (stop old, brief pause, start new) is reliable.
            Debugger.show($"Audio-link transport change: {_scrcpyDeviceId} ({(_scrcpyDeviceIsUsb ? "USB" : "WiFi")}) -> {liveDevice} ({(liveIsUsb ? "USB" : "WiFi")}), hard switch");

            _scrcpySwitchInProgress = true;
            _ = PerformAudioLinkSwitchAsync(liveDevice, liveIsUsb);
        }

        /// <summary>
        /// Migrates the running audio link to a new device by stopping the current
        /// scrcpy process and starting a fresh one bound to <paramref name="newDevice"/>.
        /// A seamless (concurrent) handover is not used because Android does not allow
        /// two simultaneous --audio-source=playback captures from the same package, so
        /// the new session would never start while the old one is alive.
        /// </summary>
        private async Task PerformAudioLinkSwitchAsync(string newDevice, bool newIsUsb)
        {
            try
            {
                bool wasPlaying = _lastMediaPlayerIsPlaying;
                var oldDevice = _scrcpyDeviceId;

                if (Config.AudioLinkBleedless && !string.IsNullOrWhiteSpace(oldDevice))
                    await AdbHelper.RunAdbAsync($"-s {oldDevice} shell input keyevent 164").ConfigureAwait(true);

                await StopScrcpyAsync().ConfigureAwait(true);
                if (!_audioLinkDesired || _isExiting) return;

                // Give the OS time to release the audio session before we reacquire it.
                // USB audio capture on Android takes longer to initialize than Wi-Fi,
                // so use a generous delay.
                await Task.Delay(400).ConfigureAwait(true);
                if (!_audioLinkDesired || _isExiting) return;

                var process = LaunchScrcpyProcess(newDevice);
                if (process == null)
                {
                    Debugger.show("Audio-link switch: LaunchScrcpyProcess returned null.");
                    _trayIconManager?.SetScrcpyRunning(false);
                    UpdateTrayAudioSettings();
                    ApplyTrayState();
                    if (Config.AudioLinkBleedless && !string.IsNullOrWhiteSpace(newDevice))
                        await AdbHelper.RunAdbAsync($"-s {newDevice} shell input keyevent 164").ConfigureAwait(true);
                    return;
                }

                _scrcpyProcess = process;
                _scrcpyDeviceId = newDevice;
                _scrcpyDeviceIsUsb = newIsUsb;
                _scrcpyProcess.EnableRaisingEvents = true;
                _scrcpyProcess.Exited += ScrcpyProcessExited;
                _isScrcpyRunning = true;
                _trayIconManager?.SetScrcpyRunning(true);
                UpdateTrayAudioSettings();
                ApplyTrayState();

                await Task.Delay(1000).ConfigureAwait(true);
                if (Config.AudioLinkBleedless)
                {
                    await AdbHelper.RunAdbAsync($"-s {newDevice} shell input keyevent 164").ConfigureAwait(true);

                    if (wasPlaying)
                        await AdbHelper.RunAdbAsync($"-s {newDevice} shell input keyevent 126").ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                Debugger.show("PerformAudioLinkSwitchAsync failed: " + ex.Message);
            }
            finally
            {
                _scrcpySwitchInProgress = false;

                // After releasing the guard, re-check in case the device flipped again
                // while we were busy.
                if (!_isExiting)
                {
                    CheckAudioLinkTransport();
                }
            }
        }

        // Invoked by the media-player window when the user toggles the audio-link button.
        // Mirrors the tray menu's ToggleScrcpyNoAudio behaviour.
        private void SetAudioLinkFromMediaPlayer(bool enable)
        {
            try
            {
                if (enable)
                {
                    _audioLinkDesired = true;
                    _audioLinkRecoveryAttempts = 0;
                    if (_scrcpyProcess == null || _scrcpyProcess.HasExited)
                    {
                        StartScrcpyNoAudio();
                    }
                }
                else
                {
                    _audioLinkDesired = false;
                    _audioLinkRecoveryAttempts = 0;
                    CancelAudioLinkRecovery();
                    if (_scrcpyProcess != null && !_scrcpyProcess.HasExited)
                    {
                        _ = StopScrcpyAsync();
                        ShowToast("Audio link stopped");
                    }
                }
            }
            catch (Exception ex)
            {
                Debugger.show("SetAudioLinkFromMediaPlayer failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Invoked by the media-player window when the user picks a new audio quality preset.
        /// Applies the preset to the live config, persists it, propagates via UpdateConfig,
        /// and restarts scrcpy if it's currently running so the new args take effect.
        /// </summary>
        private void ApplyAudioQualityPresetFromMediaPlayer(AudioQualityPresets.Preset preset)
        {
            if (preset == null) return;

            try
            {
                AudioQualityPresets.ApplyToConfig(Config, preset);
                MusicConfigManager.Save(Config);

                bool wasRunning = _scrcpyProcess != null && !_scrcpyProcess.HasExited;

                // UpdateConfig pushes everywhere (settings UI, tray, media player label).
                UpdateConfig(Config);

                if (wasRunning)
                {
                    // Restart scrcpy so the new codec/bitrate/buffer take effect.
                    // StopScrcpyAsync runs asynchronously; chain Start once it's gone.
                    if (Config.AudioLinkQualityAutoRestart)
                        _ = RestartScrcpyForPresetAsync();
                }
            }
            catch (Exception ex)
            {
                Debugger.show("ApplyAudioQualityPresetFromMediaPlayer failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Called by the global hotkey. Always opens the quality window with the full
        /// preset picker, regardless of whether the media player is open.
        /// </summary>
        private void OpenAudioQualityFromHotkey()
        {
            OpenAudioQualityWindow(showPresets: true, calledFromMediaPlayer: false);
        }

        private void OpenAudioQualityWindow(bool showPresets, bool calledFromMediaPlayer)
        {
            if (_audioQualityWindowOpen)
                return;

            Window? owner = calledFromMediaPlayer ? (Window?)_mediaPlayerWindow : _settingsWindow;
            // Only assign Owner if the window has a live HWND; a window that was
            // created but never shown (e.g. hidden on startup) throws otherwise.
            if (owner != null && !owner.IsLoaded)
                owner = null;

            var window = new AudioCustomQualityWindow(Config, showPresets);
            if (owner != null)
                window.Owner = owner;

            _audioQualityWindowOpen = true;
            try
            {
                if (window.ShowDialog() == true && window.ResultConfig.HasValue)
                {
                    var (codec, bitrate, bufferMs, flacLevel) = window.ResultConfig.Value;
                    AudioQualityPresets.ApplyCustomToConfig(Config, codec, bitrate, bufferMs, flacLevel);
                    MusicConfigManager.Save(Config);

                    bool wasRunning = _scrcpyProcess != null && !_scrcpyProcess.HasExited;
                    UpdateConfig(Config);

                    if (wasRunning)
                    {
                        if (Config.AudioLinkQualityAutoRestart)
                            _ = RestartScrcpyForPresetAsync();
                    }
                }
            }
            finally
            {
                _audioQualityWindowOpen = false;
            }
        }

        private async Task RestartScrcpyForPresetAsync()
        {
            try
            {
                var device = _presenceService?.CurrentDevice;

                if (Config.AudioLinkBleedless && !string.IsNullOrWhiteSpace(device))
                    await AdbHelper.RunAdbAsync($"-s {device} shell input keyevent 164").ConfigureAwait(true);

                await StopScrcpyAsync().ConfigureAwait(true);
                await Task.Delay(150).ConfigureAwait(true);
                StartScrcpyNoAudio();
                await Task.Delay(1000).ConfigureAwait(true);

                if (Config.AudioLinkBleedless && !string.IsNullOrWhiteSpace(device))
                    await AdbHelper.RunAdbAsync($"-s {device} shell input keyevent 164").ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Debugger.show("RestartScrcpyForPresetAsync failed: " + ex.Message);
            }
        }

        // Pushes the current tray state to the media-player window's connection-info pill.
        // Color spec (per UX):
        //   USB                 -> green
        //   TCP/IP              -> cyan
        //   Wireless Debugging  -> blue
        //   No connection       -> red
        //   Wi-Fi port lost     -> red (same as no-connection; both signal "broken")
        // Tray icon and tray-menu colors are kept in sync with this in TrayIconManager.
        private void ApplyConnectionStateToMediaPlayer()
        {
            var window = _mediaPlayerWindow;
            if (window == null) return;

            var (label, detail, color) = MapTrayStateToMediaPlayerStatus(_lastTrayState, _isScrcpyRunning);
            window.SetConnectionStatus(label, detail, color);
        }

        private static (string label, string detail, Color color) MapTrayStateToMediaPlayerStatus(TrayIconState state, bool scrcpyRunning)
        {
            // USB green
            var usbColor = Color.FromRgb(0x34, 0xC9, 0x54);
            // TCP/IP cyan
            var tcpipColor = Color.FromRgb(0x00, 0xBC, 0xD4);
            // Wireless Debugging blue
            var wdColor = Color.FromRgb(0x00, 0x7A, 0xFF);
            // No connection / port lost red
            var redColor = Color.FromRgb(0xFF, 0x3B, 0x30);

            string scrcpySuffix = scrcpyRunning ? " · audio link active" : "";

            switch (state)
            {
                case TrayIconState.ActiveUsb:
                case TrayIconState.InactiveUsb:
                case TrayIconState.ActiveUsbScrcpy:
                case TrayIconState.InactiveUsbScrcpy:
                    return ("USB connected", "Device reachable over USB" + scrcpySuffix, usbColor);

                case TrayIconState.ActiveWifi:
                case TrayIconState.InactiveWifi:
                case TrayIconState.ActiveWifiScrcpy:
                case TrayIconState.InactiveWifiScrcpy:
                    return ("TCP/IP connected", "Device reachable over Wi-Fi (tcpip)" + scrcpySuffix, tcpipColor);

                case TrayIconState.ActiveWifiDebug:
                case TrayIconState.InactiveWifiDebug:
                case TrayIconState.ActiveWifiDebugScrcpy:
                case TrayIconState.InactiveWifiDebugScrcpy:
                    return ("Wireless Debugging", "Device reachable over Wireless Debugging" + scrcpySuffix, wdColor);

                case TrayIconState.NeedsUsbReconnect:
                    return ("Wi-Fi port lost", "Reconnect USB to restore the Wi-Fi bridge", redColor);

                default:
                    return ("Not connected", "No device detected", redColor);
            }
        }

        internal void UpdateConfig(MusicConfig config)
        {
            bool audioChanged = !string.Equals(Config.ScrcpyAudioCodec, config.ScrcpyAudioCodec, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Config.ScrcpyAudioBitrate ?? string.Empty, config.ScrcpyAudioBitrate ?? string.Empty, StringComparison.Ordinal)
                || Config.ScrcpyAudioBuffer != config.ScrcpyAudioBuffer
                || Config.ScrcpyFlacCompressionLevel != config.ScrcpyFlacCompressionLevel;

            bool noCoverChanged = !string.Equals(
                Config.Paths?.NoCoverIconPath ?? string.Empty,
                config.Paths?.NoCoverIconPath ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);

            Config = config;
            ApplyStartupRegistration(config.StartWithWindows);
            Debugger.IsEnabled = Config.DebugMode;
            AdbHelper.AdbPath = Config.Paths.Adb;
            ApplyActiveTheme(config);
            _presenceService?.UpdateConfig(config);
            _lyricsOverlayManager?.UpdateConfig(config);
            _toastManager?.UpdateConfig(config);
            _settingsWindow?.SyncRuntimeConfig(config);
            _trayIconManager?.SetDarkMode(config.UseDarkMode);
            UpdateTrayAudioSettings();
            EnsureMediaPlayerWindowState();
            // Push the latest preset label to the media player's quick-quality button.
            _mediaPlayerWindow?.RefreshAudioQualityButton();
            UpdateSettingsWindowModeButton();
            // Reinitialize hotkeys to reflect updated configuration
            try
            {
                DisposeHotkeys();
                InitializeHotkeys();
            }
            catch { }

            // Restart the audio link if it is running and the codec/bitrate/buffer changed,
            // so the new settings take effect immediately without needing a manual restart.
            if (audioChanged && _scrcpyProcess != null && !_scrcpyProcess.HasExited && Config.AudioLinkQualityAutoRestart)
                _ = RestartScrcpyForPresetAsync();

            // When the no-cover icon changes, force the next tick to re-push the image and
            // immediately refresh the media player window cover without waiting for a tick.
            if (noCoverChanged && _presenceService != null)
            {
                _presenceService.ResetCoverSearch();
                var newCoverPath = _presenceService.CurrentCoverPath;
                if (_mediaPlayerWindow != null)
                {
                    _lastMediaPlayerCoverPath = newCoverPath;
                    _mediaPlayerWindow.UpdateTrack(_lastMediaPlayerTitle, _lastMediaPlayerArtist, _lastMediaPlayerAlbum, newCoverPath, _lastMediaPlayerIsPlaying);
                }
            }
        }

        private void OnLyricsPlaybackChanged(string? artist, string? title, string? album, bool isPlaying, long positionMs)
        {
            _lyricsOverlayManager?.OnPlaybackChanged(artist, title, album, isPlaying, positionMs);
        }

        private void OnMediaPlayerStateChanged(string? title, string? artist, string? album, string? coverPath, bool isPlaying, long positionMs, long durationMs)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;

            bool trackChanged = title != _lastMediaPlayerTitle || artist != _lastMediaPlayerArtist;

            _lastMediaPlayerTitle = title;
            _lastMediaPlayerArtist = artist;
            _lastMediaPlayerAlbum = album;
            _lastMediaPlayerCoverPath = coverPath;
            _lastMediaPlayerIsPlaying = isPlaying;
            _lastMediaPlayerPositionMs = positionMs;
            _lastMediaPlayerDurationMs = durationMs;

            Dispatcher.BeginInvoke(() =>
            {
                if (_mediaPlayerWindow == null || !_mediaPlayerWindow.IsVisible)
                    return;

                _mediaPlayerWindow.UpdateTrack(title, artist, album, coverPath, isPlaying);
                _mediaPlayerWindow.UpdateProgress(positionMs, durationMs);

                if (App.Config.NextSongMode != NextSongMode.Off && trackChanged)
                    _ = UpdateNextSongNeighboursAsync(title, artist);
            });
        }

        private void EnsureMediaPlayerWindowState()
        {
            if (!Config.ShowMediaPlayerWindow && _mediaPlayerWindow != null)
            {
                CloseMediaPlayerWindow();
            }
        }

        internal bool IsMediaPlayerModeActive()
        {
            return _mediaPlayerWindow != null && _mediaPlayerWindow.IsVisible;
        }

        internal async Task EditCurrentTrackMetadataAsync()
        {
            var device = _presenceService?.CurrentDevice ?? string.Empty;
            if (string.IsNullOrWhiteSpace(device))
            {
                ShowToast("No device is connected.", ToastLevel.Warning);
                return;
            }

            var remotePath = _presenceService?.CurrentRemoteFilePath;
            if (string.IsNullOrWhiteSpace(remotePath))
            {
                ShowToast("No track file has been resolved yet.", ToastLevel.Warning);
                return;
            }

            string ffmpeg = Config.Paths.FfmpegPath;
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AMPL_TagEdit");

            var meta = await MetadataEditService.ReadAsync(device, remotePath, ffmpeg, tempDir);
            if (meta == null)
            {
                ShowToast("Could not read this track's tags.", ToastLevel.Warning);
                return;
            }

            meta.RetainDateModified = Config?.RetainDateModifiedOnTagEdit ?? true;

            string ext = (System.IO.Path.GetExtension(remotePath) ?? string.Empty).ToLowerInvariant();
            // WAV can't embed lyrics, so it's always .lrc. Otherwise use the saved preference,
            // and stick with .lrc if the existing lyrics already came from one.
            meta.SaveLyricsAsLrc = ext == ".wav"
                ? true
                : (meta.LyricsFromLrc || (Config?.SaveLyricsAsLrcInFolder ?? false));

            try
            {
                if (_metadataEditWindowOpen)
                    return;

                var window = new MetadataEditWindow(meta, System.IO.Path.GetFileName(remotePath))
                {
                    Owner = _mediaPlayerWindow
                };

                _metadataEditWindowOpen = true;
                try
                {
                    if (window.ShowDialog() == true && window.Result != null)
                    {
                        if (Config != null)
                        {
                            Config.RetainDateModifiedOnTagEdit = window.Result.RetainDateModified;
                            // Don't let WAV's forced-on value poison the default for other formats.
                            if (ext != ".wav")
                                Config.SaveLyricsAsLrcInFolder = window.Result.SaveLyricsAsLrc;
                            MusicConfigManager.Save(Config);
                        }

                        var (ok, message) = await MetadataEditService.WriteAsync(device, remotePath, window.Result, ffmpeg, tempDir, window.Result.RetainDateModified);
                        ShowToast(message, ok ? ToastLevel.Info : ToastLevel.Warning);

                        if (ok)
                        {
                            // We just wrote this file and know its new lyrics, so update the cache
                            // directly (no re-pull needed) and nudge the resolver to re-read the
                            // current track so the change shows without switching songs.
                            var dk = LyricsCache.DeviceKey(Config?.SelectedDeviceName, device);
                            var fk = LyricsCache.FileKey(dk, remotePath);
                            var newLyrics = window.Result.Lyrics;
                            if (!string.IsNullOrWhiteSpace(newLyrics))
                                LyricsCache.Save(fk, LyricsCache.Source.Embed, newLyrics);
                            else
                                LyricsCache.Invalidate(fk);

                            _lyricsOverlayManager?.MarkCurrentTrackDirty();
                        }
                    }
                }
                finally
                {
                    _metadataEditWindowOpen = false;
                }
            }
            finally
            {
                // The pulled copy is reused by WriteAsync, so it is only safe to delete now.
                TryDeleteTagEditTemp(meta.LocalSourcePath);
                TryDeleteTagEditTemp(meta.CoverPreviewPath);
            }
        }

        private static void TryDeleteTagEditTemp(string? path)
        {
            try { if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path)) System.IO.File.Delete(path); }
            catch { }
        }

        internal async Task SaveCurrentTrackAsync()
        {
            if (_saveTrackOpen)
                return;

            var device = _presenceService?.CurrentDevice ?? string.Empty;
            if (string.IsNullOrWhiteSpace(device))
            {
                ShowToast("No device is connected.", ToastLevel.Warning);
                return;
            }

            var remotePath = _presenceService?.CurrentRemoteFilePath;
            if (string.IsNullOrWhiteSpace(remotePath))
            {
                ShowToast("No track file has been resolved yet.", ToastLevel.Warning);
                return;
            }

            string fileName = System.IO.Path.GetFileName(remotePath);
            string ext = System.IO.Path.GetExtension(fileName);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".mp3";

            _saveTrackOpen = true;
            try
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Save song",
                    FileName = fileName,
                    Filter = $"Audio Files|*{ext}|All Files|*.*",
                    DefaultExt = ext
                };

                if (dialog.ShowDialog(_mediaPlayerWindow) != true)
                    return;

                string dest = dialog.FileName;

                ShowToast("Pulling file from device...", ToastLevel.Info);
                await AdbHelper.RunAdbAsync($"-s {device} pull \"{remotePath}\" \"{dest}\"").ConfigureAwait(true);

                if (System.IO.File.Exists(dest) && new System.IO.FileInfo(dest).Length > 0)
                    ShowToast("Saved: " + System.IO.Path.GetFileName(dest), ToastLevel.Info);
                else
                    ShowToast("Pull failed or file is empty.", ToastLevel.Warning);
            }
            catch (Exception ex)
            {
                ShowToast("Failed to save song: " + ex.Message, ToastLevel.Warning);
            }
            finally
            {
                _saveTrackOpen = false;
            }
        }

        internal void ShowMediaPlayerWindowNow()
        {
            Debugger.show("[MEDIAPLAYER] Opening media player window.");

            if (_mediaPlayerWindow == null)
            {
                _mediaPlayerWindow = new MediaPlayerWindow(
                    () => _presenceService?.PauseCurrentAsync() ?? Task.CompletedTask,
                    () => _presenceService?.NextCurrentAsync() ?? Task.CompletedTask,
                    () => _presenceService?.PreviousCurrentAsync() ?? Task.CompletedTask,
                    () => _lyricsOverlayManager?.ToggleVisibility(),
                    IsScrcpyAudioSessionAvailable,
                    TryGetScrcpyVolume,
                    TrySetScrcpyVolume,
                    StepVolumeOnce,
                    SetAudioLinkFromMediaPlayer,
                    seconds => _presenceService?.SeekRelativeCurrentAsync(seconds) ?? Task.CompletedTask,
                    _lyricsOverlayManager,
                    () => Config,
                    ApplyAudioQualityPresetFromMediaPlayer,
                    () => OpenAudioQualityWindow(showPresets: false, calledFromMediaPlayer: true),
                    GetPhoneVolumeAsync,
                    (prev, target, max) => SetPhoneVolumeAsync(prev, target, max),
                    EditCurrentTrackMetadataAsync,
                    SaveCurrentTrackAsync);
                _mediaPlayerWindow.Closing += MediaPlayerWindow_Closing;
                _mediaPlayerWindow.InitNextSongPanels(() => RescanNextSongLibraryAsync(), () => _presenceService?.NextCurrentAsync() ?? Task.CompletedTask, () => _presenceService?.PreviousCurrentAsync() ?? Task.CompletedTask);

                // Wire toast manager to the new media player window.
                if (_toastManager != null)
                {
                    _toastManager.AddToMediaPlayer = el => _mediaPlayerWindow?.AddToast(el);
                    _toastManager.RemoveFromMediaPlayer = el => _mediaPlayerWindow?.RemoveToast(el);
                }

                // Push current connection + scrcpy state into the freshly created window.
                ApplyConnectionStateToMediaPlayer();
                _mediaPlayerWindow.SetAudioLinkState(_isScrcpyRunning);
            }

            var config = Config;
            _mediaPlayerWindow.Width = config.MediaPlayerWindowWidth;
            _mediaPlayerWindow.Height = config.MediaPlayerWindowHeight;
            _mediaPlayerWindow.Top = config.MediaPlayerWindowTop;
            _mediaPlayerWindow.Left = config.MediaPlayerWindowLeft;
            _mediaPlayerWindow.WindowState = config.MediaPlayerWindowState;

            if (_settingsWindow != null)
            {
                if (_settingsWindow.Content is FrameworkElement rootContent)
                {
                    EnsureEmbeddedSettingsStyles(rootContent, _settingsWindow.Resources);
                    _settingsWindow.Content = null;
                    _mediaPlayerWindow.SetSettingsContent(rootContent);
                }

                if (_settingsWindow.IsVisible)
                {
                    Debugger.show("[SETTINGS] Hiding settings window while media player is open.");
                    _settingsWindow.Hide();
                }
            }

            if (!_mediaPlayerWindow.IsVisible)
            {
                _mediaPlayerWindow.Show();
            }

            if (_mediaPlayerWindow.WindowState == WindowState.Minimized)
            {
                _mediaPlayerWindow.WindowState = WindowState.Normal;
            }

            _mediaPlayerWindow.Activate();
            _mediaPlayerWindow.UpdateTrack(_lastMediaPlayerTitle, _lastMediaPlayerArtist, _lastMediaPlayerAlbum, _lastMediaPlayerCoverPath, _lastMediaPlayerIsPlaying);
            _mediaPlayerWindow.UpdateProgress(_lastMediaPlayerPositionMs, _lastMediaPlayerDurationMs);
            UpdateSettingsWindowModeButton();
        }

        internal void GoBackToSettingsWindow()
        {
            CloseMediaPlayerWindow(forceShowSettings: true);
        }

        private static void EnsureEmbeddedSettingsStyles(FrameworkElement rootContent, ResourceDictionary windowResources)
        {
            if (rootContent.Resources == null)
                return;

            bool alreadyMerged = rootContent.Resources.MergedDictionaries
                .Any(d => ReferenceEquals(d, windowResources));

            if (!alreadyMerged)
            {
                rootContent.Resources.MergedDictionaries.Add(windowResources);
            }
        }

        private void CloseMediaPlayerWindow(bool forceShowSettings = false)
        {
            if (_mediaPlayerWindow == null)
                return;

            Debugger.show("[MEDIAPLAYER] Closing media player window.");
            var window = _mediaPlayerWindow;
            _mediaPlayerWindow = null;

            // Detach inline toast callbacks so toasts from now on go to headless mode.
            if (_toastManager != null)
            {
                _toastManager.AddToMediaPlayer = null;
                _toastManager.RemoveFromMediaPlayer = null;
            }

            window.Closing -= MediaPlayerWindow_Closing;
            var hostedContent = window.TakeSettingsContent();
            if (_settingsWindow != null && hostedContent != null)
            {
                _settingsWindow.Content = hostedContent;
            }

            window.Close();

            if (_settingsWindow != null
                && !Dispatcher.HasShutdownStarted
                && !Dispatcher.HasShutdownFinished
                && (forceShowSettings || !Config.OpenInTaskbar))
            {
                Debugger.show("[SETTINGS] Showing settings window after media player closed.");
                _settingsWindow.Show();
                _settingsWindow.Activate();
            }

            UpdateSettingsWindowModeButton();
        }

        private void MediaPlayerWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_isExiting)
            {
                Debugger.show("[MEDIAPLAYER] Closing media player window.");
            }

            if (_isExiting)
                return;

            // Check for unsaved changes in the hosted settings content before letting
            // the media player window close. This mirrors the prompt in MainWindow_Closing.
            if (_settingsWindow != null && _settingsWindow.HasUnsavedChanges())
            {
                var result = System.Windows.MessageBox.Show(
                    "there are unsaved changes, do you wish to save them?",
                    "Unsaved changes",
                    System.Windows.MessageBoxButton.YesNoCancel,
                    System.Windows.MessageBoxImage.Warning);

                if (result == System.Windows.MessageBoxResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }

                if (result == System.Windows.MessageBoxResult.Yes)
                    _settingsWindow.Save(false);
                else
                    _settingsWindow.RevertUnsavedChanges();
            }

            var window = sender as MediaPlayerWindow;
            if (window != null)
            {
                var hostedContent = window.TakeSettingsContent();
                if (_settingsWindow != null && hostedContent != null)
                {
                    _settingsWindow.Content = hostedContent;
                }
            }

            _mediaPlayerWindow = null;

            if (_settingsWindow != null)
            {
                _settingsWindow.SyncRuntimeConfig(Config);
            }

            UpdateSettingsWindowModeButton();
        }

        private void UpdateSettingsWindowModeButton()
        {
            _settingsWindow?.UpdateMediaPlayerModeButton(IsMediaPlayerModeActive());
        }

        private static void ApplyStartupRegistration(bool enable)
        {
            try
            {
                using var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                if (runKey == null)
                    return;

                if (!enable)
                {
                    runKey.DeleteValue(StartupRunValueName, false);
                    return;
                }

                var exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    exePath = Assembly.GetEntryAssembly()?.Location;
                }

                if (string.IsNullOrWhiteSpace(exePath))
                    return;

                runKey.SetValue(StartupRunValueName, $"\"{exePath}\"");
            }
            catch
            {
            }
        }

        internal void ShowOnboarding(bool forceRun)
        {
            if (!forceRun && Config.OnboardingCompleted)
                return;

            var onboardingWindow = new OnboardingWindow(Config);

            if (_settingsWindow != null)
            {
                var ownerHandle = new WindowInteropHelper(_settingsWindow).Handle;
                if (ownerHandle != IntPtr.Zero)
                {
                    onboardingWindow.Owner = _settingsWindow;
                }
            }

            var result = onboardingWindow.ShowDialog();
            if (result == true)
            {
                Config = onboardingWindow.UpdatedConfig;
                MusicConfigManager.Save(Config);
                UpdateConfig(Config);

                // Honor the view choice picked in onboarding: open the media player
                // window if the user just selected it, or fall back to the settings
                // window otherwise.
                if (Config.ShowMediaPlayerWindow)
                {
                    ShowMediaPlayerWindowNow();
                }
                else if (_mediaPlayerWindow != null)
                {
                    GoBackToSettingsWindow();
                }
            }
        }

        /// <summary>
        /// Resolves and applies the config's active theme profile, and keeps the legacy
        /// <see cref="MusicConfig.UseDarkMode"/> flag in sync with the active theme's
        /// darkness so the tray icon and media-player icon logic stay correct.
        /// </summary>
        internal void ApplyActiveTheme(MusicConfig config)
        {
            var profile = ThemeCatalog.ResolveActive(config);
            config.UseDarkMode = ThemeCatalog.IsDark(profile);
            ApplyThemeProfile(profile);
        }

        /// <summary>
        /// Live-preview entry point used by the settings window while the user selects,
        /// cycles, or edits a theme. Applies the in-progress (unsaved) profile and updates
        /// the running UseDarkMode flag so icons follow immediately; values are only
        /// persisted on Save like every other setting.
        /// </summary>
        internal void ApplyThemePreview(ThemeProfile profile)
        {
            if (Config != null)
                Config.UseDarkMode = ThemeCatalog.IsDark(profile);
            ApplyThemeProfile(profile);
        }

        /// <summary>
        /// Applies a single theme profile to the live brushes. The built-in Default Light
        /// and Default Dark profiles render with their pristine hand-tuned palettes (their
        /// surface/border/accent shades are not pure derivations of the three headline
        /// colors); High Contrast and every custom profile derive their dependent brushes
        /// from the three colors via <see cref="ApplyThemeCore"/>.
        /// </summary>
        internal void ApplyThemeProfile(ThemeProfile profile)
        {
            if (ThemeCatalog.IsPristineDefaultLight(profile))
            {
                ApplyThemeCore(false, null);
                return;
            }
            if (ThemeCatalog.IsPristineDefaultDark(profile))
            {
                ApplyThemeCore(true, null);
                return;
            }

            var overrides = new ThemeOverrides
            {
                Background = profile.Background ?? string.Empty,
                Accent = profile.Accent ?? string.Empty,
                Foreground = profile.Foreground ?? string.Empty
            };
            ApplyThemeCore(ThemeCatalog.IsDark(profile), overrides);
        }

        private void ApplyThemeCore(bool useDarkMode, ThemeOverrides? overrides)
        {
            // Built-in palette for the active mode. User overrides layer on top of these,
            // and any color the user leaves blank keeps its default below.
            var background = ParseColorOr(useDarkMode ? "#1E1E1E" : "#F7F7F7", null);
            var foreground = ParseColorOr(useDarkMode ? "#EAEAEA" : "#1A1A1A", null);
            var controlBackground = ParseColorOr(useDarkMode ? "#2B2B2B" : "#FFFFFF", null);
            var controlBorder = ParseColorOr(useDarkMode ? "#3C3C3C" : "#C8C8C8", null);
            var accent = ParseColorOr(useDarkMode ? "#3E7BFF" : "#2D6CDF", null);
            var accentHover = ParseColorOr(useDarkMode ? "#5A8BFF" : "#3E7BFF", null);
            var accentPressed = ParseColorOr(useDarkMode ? "#275ED6" : "#1F5DD1", null);

            if (overrides != null)
            {
                // Background: also re-derive the surface (control) and border colors and,
                // unless the user picked an explicit text color, an auto-contrasting one
                // so a dark custom background never leaves dark default text unreadable.
                if (TryParseColor(overrides.Background, out var bg))
                {
                    bool bgIsDark = Luminance(bg) < 0.5;
                    background = bg;
                    controlBackground = bgIsDark ? Lighten(bg, 0.06) : Darken(bg, 0.045);
                    controlBorder = bgIsDark ? Lighten(bg, 0.16) : Darken(bg, 0.14);
                    if (!TryParseColor(overrides.Foreground, out _))
                        foreground = bgIsDark ? ParseColorOr("#EAEAEA", null) : ParseColorOr("#1A1A1A", null);
                }

                if (TryParseColor(overrides.Foreground, out var fg))
                    foreground = fg;

                // Accent: derive hover (lighter) and pressed (darker) from the picked color.
                if (TryParseColor(overrides.Accent, out var ac))
                {
                    accent = ac;
                    accentHover = Lighten(ac, 0.13);
                    accentPressed = Darken(ac, 0.13);
                }
            }

            Resources["ThemeBackgroundBrush"] = CreateBrush(background);
            Resources["ThemeForegroundBrush"] = CreateBrush(foreground);
            Resources["ThemeControlBackgroundBrush"] = CreateBrush(controlBackground);
            Resources["ThemeControlForegroundBrush"] = CreateBrush(foreground);
            Resources["ThemeControlBorderBrush"] = CreateBrush(controlBorder);
            Resources["ThemeAccentBrush"] = CreateBrush(accent);
            Resources["ThemeAccentHoverBrush"] = CreateBrush(accentHover);
            Resources["ThemeAccentPressedBrush"] = CreateBrush(accentPressed);
            _trayIconManager?.SetDarkMode(useDarkMode);

            // Push the theme change into the media player window so the idle
            // background, icon brush, and text colors all flip immediately.
            _mediaPlayerWindow?.NotifyThemeChanged();

            // If the dev marker file is present, always re-apply dev accent colors
            // so UpdateConfig can't clobber them.
            if (File.Exists(Path.Combine(AppPaths.BaseDirectory, "devmode_snail.txt")))
                ApplyDevTheme();
        }

        // ── Color helpers used by the theming engine ─────────────────────────

        private static bool TryParseColor(string? hex, out Color color)
        {
            color = Colors.Black;
            if (string.IsNullOrWhiteSpace(hex))
                return false;
            try
            {
                if (ColorConverter.ConvertFromString(hex.Trim()) is Color c)
                {
                    color = c;
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static Color ParseColorOr(string hex, Color? fallback)
            => TryParseColor(hex, out var c) ? c : (fallback ?? Colors.Black);

        private static Color Lighten(Color c, double amount) => Mix(c, Colors.White, amount);
        private static Color Darken(Color c, double amount) => Mix(c, Colors.Black, amount);

        private static Color Mix(Color a, Color b, double t)
        {
            t = Math.Clamp(t, 0.0, 1.0);
            return Color.FromRgb(
                (byte)Math.Round(a.R + (b.R - a.R) * t),
                (byte)Math.Round(a.G + (b.G - a.G) * t),
                (byte)Math.Round(a.B + (b.B - a.B) * t));
        }

        // Perceived brightness 0 (black) .. 1 (white), Rec. 709 weights.
        private static double Luminance(Color c)
            => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

        internal void ApplyDevTheme()
        {
            Resources["ThemeAccentBrush"] = CreateBrush("#CC0000");
            Resources["ThemeAccentHoverBrush"] = CreateBrush("#FF2222");
            Resources["ThemeAccentPressedBrush"] = CreateBrush("#990000");
            _trayIconManager?.SetDarkMode(false);
            _mediaPlayerWindow?.NotifyThemeChanged();
        }

        private static SolidColorBrush CreateBrush(string color)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }

        private static SolidColorBrush CreateBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private void ShowSettingsWindow()
        {
            if (_mediaPlayerWindow != null && _mediaPlayerWindow.IsVisible)
            {
                ShowMediaPlayerWindowNow();
                return;
            }

            // Honor the user's saved view-style preference. If they last chose the
            // media player view, open it again, even if the window has since been closed.
            if (Config.ShowMediaPlayerWindow)
            {
                ShowMediaPlayerWindowNow();
                return;
            }

            if (_settingsWindow == null)
            {
                _settingsWindow = new MainWindow();
            }

            if (!_settingsWindow.IsVisible)
            {
                Debugger.show("[SETTINGS] Opening settings window.");
                _settingsWindow.Show();
            }

            if (_settingsWindow.WindowState == WindowState.Minimized)
            {
                _settingsWindow.WindowState = WindowState.Normal;
            }

            _settingsWindow.Activate();
        }

        private void ShutdownApplication()
        {
            _settingsWindow?.AllowClose();
            Shutdown();
        }
        private void ToggleScrcpyNoAudio()
        {
            if (_scrcpyProcess != null && !_scrcpyProcess.HasExited)
            {
                _audioLinkDesired = false;
                _audioLinkRecoveryAttempts = 0;
                CancelAudioLinkRecovery();
                _ = StopScrcpyAsync();
                ShowToast("Audio link stopped");
            }
            else
            {
                _audioLinkDesired = true;
                _audioLinkRecoveryAttempts = 0;
                StartScrcpyNoAudio();
            }
        }

        private void StartScrcpyNoAudio()
        {
            var device = _presenceService?.CurrentDevice;
            if (string.IsNullOrWhiteSpace(device))
            {
                _audioLinkDesired = false;
                (Application.Current as App)?.ShowToast("No device connected!", ToastLevel.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(Config.Paths.Scrcpy) || !File.Exists(Config.Paths.Scrcpy))
            {
                _audioLinkDesired = false;
                (Application.Current as App)?.ShowToast("scrcpy.exe not found!", ToastLevel.Error);
                return;
            }

            try
            {
                var process = LaunchScrcpyProcess(device);
                if (process == null)
                {
                    _audioLinkDesired = false;
                    (Application.Current as App)?.ShowToast("scrcpy failed to start.", ToastLevel.Error);
                    _isScrcpyRunning = false;
                    _trayIconManager?.SetScrcpyRunning(false);
                    UpdateTrayAudioSettings();
                    ApplyTrayState();
                    return;
                }

                _scrcpyProcess = process;
                _scrcpyDeviceId = device;
                _scrcpyDeviceIsUsb = !device.Contains(':');
                _scrcpyProcess.EnableRaisingEvents = true;
                _scrcpyProcess.Exited += ScrcpyProcessExited;
                _isScrcpyRunning = true;
                _trayIconManager?.SetScrcpyRunning(true);
                UpdateTrayAudioSettings();
                ApplyTrayState();
                ShowToast("Audio link started");
            }
            catch (Exception ex)
            {
                _audioLinkDesired = false;
                (Application.Current as App)?.ShowToast($"scrcpy launch failed: {ex.Message}", ToastLevel.Error);
                _isScrcpyRunning = false;
                _trayIconManager?.SetScrcpyRunning(false);
                UpdateTrayAudioSettings();
                ApplyTrayState();
            }
        }

        /// <summary>
        /// Builds the scrcpy argument string for a given device id. Pure (no side effects),
        /// shared between the user-initiated start and the transport-switch path so both
        /// paths produce identical args modulo the -s target.
        /// </summary>
        private string BuildScrcpyArgs(string device)
        {
            var codec = string.IsNullOrWhiteSpace(Config.ScrcpyAudioCodec) ? "raw" : Config.ScrcpyAudioCodec.Trim();
            var buffer = Config.ScrcpyAudioBuffer > 0 ? Config.ScrcpyAudioBuffer : 50;

            var argParts = new List<string>
            {
                $"-s {device}",
                "--no-video",
                "--no-window",
                "--audio-source=playback",
                $"--audio-codec={codec}",
                $"--audio-buffer={buffer}"
            };

            if (!codec.Equals("raw", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(Config.ScrcpyAudioBitrate))
            {
                var bitrateText = Config.ScrcpyAudioBitrate.Trim();
                if (bitrateText.EndsWith("K", StringComparison.OrdinalIgnoreCase))
                {
                    bitrateText = bitrateText[..^1];
                }

                if (int.TryParse(bitrateText, out var bitrateValue) && bitrateValue > 0)
                {
                    argParts.Add($"--audio-bit-rate={bitrateValue}K");
                }
            }

            if (codec.Equals("flac", StringComparison.OrdinalIgnoreCase))
            {
                argParts.Add($"--audio-codec-options=flac-compression-level={Math.Clamp(Config.ScrcpyFlacCompressionLevel, 1, 8)}");
            }

            return string.Join(" ", argParts);
        }

        /// <summary>
        /// Launches a scrcpy process bound to the given device id. Returns the started
        /// Process (with EnableRaisingEvents NOT yet set and no Exited handler attached),
        /// or null on failure. Caller is responsible for wiring up state and lifetime.
        /// No UI side effects so this can be used during background transport switches.
        /// </summary>
        private Process? LaunchScrcpyProcess(string device)
        {
            if (_isExiting || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return null;

            if (string.IsNullOrWhiteSpace(Config.Paths.Scrcpy) || !File.Exists(Config.Paths.Scrcpy))
            {
                Debugger.show("LaunchScrcpyProcess: scrcpy.exe not found at " + (Config.Paths.Scrcpy ?? "<null>"));
                return null;
            }

            var args = BuildScrcpyArgs(device);
            Debugger.show($"Starting scrcpy (device={device}) with args: {args}");

            var psi = new ProcessStartInfo
            {
                FileName = Config.Paths.Scrcpy,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            return Process.Start(psi);
        }

        private async Task StopScrcpyAsync()
        {
            var process = _scrcpyProcess;
            _scrcpyProcess = null;
            _scrcpyDeviceId = null;
            _scrcpyDeviceIsUsb = false;
            _isScrcpyRunning = false;
            _trayIconManager?.SetScrcpyRunning(false);
            UpdateTrayAudioSettings();
            ApplyTrayState();

            if (process == null)
                return;

            try
            {
                process.Exited -= ScrcpyProcessExited;
                process.EnableRaisingEvents = false;

                await Task.Run(() =>
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                        process.WaitForExit(2000);
                    }
                });
            }
            catch (Exception ex)
            {
                if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                {
                    (Application.Current as App)?.ShowToast($"Failed to stop scrcpy: {ex.Message}", ToastLevel.Error);
                }
            }
            finally
            {
                process.Dispose();
            }
        }

        private void ScrcpyProcessExited(object? sender, EventArgs e)
        {
            // Guard against stale Exited events from a process we already replaced during
            // a transport switch. PerformAudioLinkSwitchAsync detaches the handler before
            // stopping the old process, so anything reaching here should be the currently
            // tracked _scrcpyProcess. The ReferenceEquals check is a safety net.
            if (sender is Process exited && !ReferenceEquals(exited, _scrcpyProcess))
            {
                // Stale event from a process we already replaced; just dispose it.
                try { exited.Dispose(); } catch { }
                return;
            }

            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                _scrcpyProcess?.Dispose();
                _scrcpyProcess = null;
                _scrcpyDeviceId = null;
                _scrcpyDeviceIsUsb = false;
                _isScrcpyRunning = false;
                return;
            }

            Dispatcher.BeginInvoke(() =>
            {
                bool wasDesired = _audioLinkDesired;
                bool wasUsb = _scrcpyDeviceIsUsb;
                string? oldDevice = _scrcpyDeviceId;

                _scrcpyProcess?.Dispose();
                _scrcpyProcess = null;
                _scrcpyDeviceId = null;
                _scrcpyDeviceIsUsb = false;
                _isScrcpyRunning = false;
                _trayIconManager?.SetScrcpyRunning(false);
                UpdateTrayAudioSettings();
                ApplyTrayState();

                // If the user still wants the audio link active and the process died
                // on its own (USB cable yanked, network blip, scrcpy crashed), kick
                // off recovery: poll briefly for a usable device, then restart on it.
                // We don't immediately know which transport will come up, so we let
                // the recovery wait for the device to be present and start fresh.
                if (wasDesired && !_isExiting && !_scrcpySwitchInProgress)
                {
                    if (!Config.AudioLinkConnectionAutoRestart)
                    {
                        Debugger.show($"Audio-link: scrcpy exited unexpectedly (was on {(wasUsb ? "USB" : "WiFi")} as {oldDevice ?? "<null>"}), connection auto-restart disabled.");
                    }
                    else if (Config.UpdateIntervalMode > UpdateIntervalMode.Fast)
                    {
                        Debugger.show($"Audio-link: scrcpy exited unexpectedly (was on {(wasUsb ? "USB" : "WiFi")} as {oldDevice ?? "<null>"}), auto-recovery disabled at this update interval.");
                    }
                    else
                    {
                        Debugger.show($"Audio-link: scrcpy exited unexpectedly (was on {(wasUsb ? "USB" : "WiFi")} as {oldDevice ?? "<null>"}), arming recovery.");
                        ArmAudioLinkRecovery();
                    }
                }
            });
        }

        // Counts how many times scrcpy has been (re)started by the recovery path
        // since the last successful stable session. Reset when the user explicitly
        // starts/stops. Prevents infinite restart storms when Wi-Fi is unavailable
        // after a USB disconnect.
        private int _audioLinkRecoveryAttempts;
        private const int AudioLinkRecoveryMaxAttempts = 40;

        /// <summary>
        /// Starts polling for a usable device to restart the audio link on. Used after
        /// scrcpy exits unexpectedly (e.g. USB cable yank) when the user still wants
        /// audio link active. Coalesces with any in-flight recovery.
        /// </summary>
        private void ArmAudioLinkRecovery()
        {
            _audioLinkRecoveryAttempts++;
            if (_audioLinkRecoveryAttempts > AudioLinkRecoveryMaxAttempts)
            {
                Debugger.show($"Audio-link recovery: reached {AudioLinkRecoveryMaxAttempts} attempts, giving up. Toggle audio link to retry.");
                ShowToast("Audio link lost, reconnect manually", ToastLevel.Warning);
                _audioLinkDesired = false;
                _audioLinkRecoveryAttempts = 0;
                return;
            }

            Debugger.show($"Audio-link recovery: attempt {_audioLinkRecoveryAttempts}/{AudioLinkRecoveryMaxAttempts}.");
            CancelAudioLinkRecovery();
            var cts = new CancellationTokenSource();
            _audioLinkRecoveryCts = cts;
            _ = AudioLinkRecoveryLoopAsync(cts.Token);
        }

        private void CancelAudioLinkRecovery()
        {
            var cts = _audioLinkRecoveryCts;
            _audioLinkRecoveryCts = null;
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
                try { cts.Dispose(); } catch { }
            }
        }

        private async Task AudioLinkRecoveryLoopAsync(CancellationToken token)
        {
            var deadline = Environment.TickCount + AudioLinkRecoveryWindowMs;
            const int pollIntervalMs = 300;

            try
            {
                while (Environment.TickCount < deadline)
                {
                    if (token.IsCancellationRequested) return;
                    if (_isExiting) return;
                    if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                    if (!_audioLinkDesired) return;
                    // Someone else (e.g. user, switch path) already restarted scrcpy.
                    if (_scrcpyProcess != null && !_scrcpyProcess.HasExited) return;

                    var device = _presenceService?.CurrentDevice ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(device))
                    {
                        Debugger.show($"Audio-link recovery: device available ({device}), restarting scrcpy.");
                        // Use the silent helper so we don't pop a MessageBox if anything
                        // races with the user disconnecting again.
                        StartAudioLinkSilently(device);
                        return;
                    }

                    await Task.Delay(pollIntervalMs, token).ConfigureAwait(true);
                }

                Debugger.show("Audio-link recovery: window expired without device, giving up.");
            }
            catch (OperationCanceledException)
            {
                // Cancelled because user explicitly stopped, exit, or another recovery armed.
            }
            catch (Exception ex)
            {
                Debugger.show("Audio-link recovery loop failed: " + ex.Message);
            }
            finally
            {
                if (ReferenceEquals(_audioLinkRecoveryCts?.Token, token) || _audioLinkRecoveryCts == null)
                {
                    _audioLinkRecoveryCts = null;
                }
            }
        }

        /// <summary>
        /// Starts a fresh scrcpy bound to the given device without the user-facing
        /// MessageBox popups. Used by the recovery path so transient races don't
        /// surface error dialogs.
        /// </summary>
        private void StartAudioLinkSilently(string device)
        {
            if (string.IsNullOrWhiteSpace(Config.Paths.Scrcpy) || !File.Exists(Config.Paths.Scrcpy))
            {
                Debugger.show("Audio-link recovery: scrcpy.exe not found, abandoning.");
                _audioLinkDesired = false;
                return;
            }

            try
            {
                var process = LaunchScrcpyProcess(device);
                if (process == null)
                {
                    Debugger.show("Audio-link recovery: LaunchScrcpyProcess returned null, abandoning.");
                    return;
                }

                _scrcpyProcess = process;
                _scrcpyDeviceId = device;
                _scrcpyDeviceIsUsb = !device.Contains(':');
                _scrcpyProcess.EnableRaisingEvents = true;
                _scrcpyProcess.Exited += ScrcpyProcessExited;
                _isScrcpyRunning = true;
                _trayIconManager?.SetScrcpyRunning(true);
                UpdateTrayAudioSettings();
                ApplyTrayState();

                // Don't announce success yet. A relaunch during a USB/Wi-Fi flap can die
                // again within a second or two, so we only consider the link reconnected
                // once this process has survived a short grace period.
                _ = ConfirmAudioLinkReconnectAsync(process);
            }
            catch (Exception ex)
            {
                Debugger.show("Audio-link recovery: launch failed: " + ex.Message);
            }
        }

        // How long a recovery-relaunched scrcpy must stay alive before we treat the audio
        // link as genuinely reconnected (and announce it). Dying relaunches never reach
        // this point, so a flapping transition stays silent.
        private const int AudioLinkReconnectGraceMs = 2500;

        /// <summary>
        /// Waits a short grace period after a recovery relaunch and, if the process is
        /// still the live audio-link process, announces a successful reconnect and clears
        /// the recovery attempt budget. Stays silent if the process died in the meantime
        /// (the flap case) or was replaced by a transport switch.
        /// </summary>
        private async Task ConfirmAudioLinkReconnectAsync(Process process)
        {
            try
            {
                await Task.Delay(AudioLinkReconnectGraceMs).ConfigureAwait(true);
            }
            catch
            {
                return;
            }

            if (_isExiting) return;
            if (!_audioLinkDesired) return;
            if (!ReferenceEquals(_scrcpyProcess, process)) return;
            if (process.HasExited) return;

            // Held long enough to count as a real reconnect: reset the budget so a future
            // genuine drop gets a fresh set of attempts, and announce it once.
            _audioLinkRecoveryAttempts = 0;
            ShowToast("Audio link reconnected");
        }

        private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            // Windows is shutting down or the user is logging off.
            // Set the exit flag immediately so no new scrcpy processes are launched
            // while Windows is tearing down the session.
            _isExiting = true;
            CancelAudioLinkRecovery();
            StopScrcpyOnExit();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _isExiting = true;
            CancelAudioLinkRecovery();
            StopScrcpyOnExit();
            CloseMediaPlayerWindow();
            _trayIconManager?.Dispose();
            _presenceService?.Dispose();
            _lyricsOverlayManager?.Dispose();
            DisposeHotkeys();
            AdbHelper.StopServer();
            base.OnExit(e);
        }

        private void StopScrcpyOnExit()
        {
            _audioLinkDesired = false;
            CancelAudioLinkRecovery();

            var process = _scrcpyProcess;
            _scrcpyProcess = null;
            _scrcpyDeviceId = null;
            _scrcpyDeviceIsUsb = false;
            _isScrcpyRunning = false;
            _trayIconManager?.SetScrcpyRunning(false);
            UpdateTrayAudioSettings();

            if (process == null)
                return;

            try
            {
                process.Exited -= ScrcpyProcessExited;
                process.EnableRaisingEvents = false;

                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        private void InitializeHotkeys()
        {
            var parameters = new HwndSourceParameters("HotkeySink")
            {
                Width = 0,
                Height = 0,
                WindowStyle = unchecked((int)0x80000000)
            };

            _hotkeySource = new HwndSource(parameters);
            _hotkeySource.AddHook(HotkeyHook);

            // Register Shift + configured keys. Use try/catch to avoid crashing if registration fails.
            try { RegisterHotKey(_hotkeySource.Handle, HotkeyIdVolumeUp, Config.HotkeyModifier, Config.HotkeyVolumeUpKey); } catch { }
            try { RegisterHotKey(_hotkeySource.Handle, HotkeyIdVolumeDown, Config.HotkeyModifier, Config.HotkeyVolumeDownKey); } catch { }
            try { RegisterHotKey(_hotkeySource.Handle, HotkeyIdToggleScrcpy, Config.HotkeyModifier, Config.HotkeyToggleScrcpyKey); } catch { }
            try { RegisterHotKey(_hotkeySource.Handle, HotkeyIdToggleLyricsOverlay, Config.HotkeyModifier, Config.HotkeyToggleLyricsOverlayKey); } catch { }
            try { RegisterHotKey(_hotkeySource.Handle, HotkeyIdCopyTrackInfo, Config.HotkeyModifier, Config.HotkeyCopyTrackInfoKey); } catch { }
            try { RegisterHotKey(_hotkeySource.Handle, HotkeyIdAudioQuality, Config.HotkeyModifier, Config.HotkeyAudioQualityKey); } catch { }
            Debugger.show($"[HOTKEY] Hotkeys initialized with modifier 0x{Config.HotkeyModifier:X}.");
        }

        private void UpdateTrayAudioSettings()
        {
            var codec = string.IsNullOrWhiteSpace(Config.ScrcpyAudioCodec) ? "raw" : Config.ScrcpyAudioCodec.Trim();
            var bitrate = Config.ScrcpyAudioBitrate ?? string.Empty;
            var buffer = Config.ScrcpyAudioBuffer > 0 ? Config.ScrcpyAudioBuffer : 50;
            _trayIconManager?.SetAudioSettings(codec, bitrate, buffer);
            _trayIconManager?.SetScrcpyRunning(_isScrcpyRunning);
        }

        private void DisposeHotkeys()
        {
            if (_hotkeySource != null)
            {
                UnregisterHotKey(_hotkeySource.Handle, HotkeyIdVolumeUp);
                UnregisterHotKey(_hotkeySource.Handle, HotkeyIdVolumeDown);
                UnregisterHotKey(_hotkeySource.Handle, HotkeyIdToggleScrcpy);
                UnregisterHotKey(_hotkeySource.Handle, HotkeyIdToggleLyricsOverlay);
                UnregisterHotKey(_hotkeySource.Handle, HotkeyIdCopyTrackInfo);
                UnregisterHotKey(_hotkeySource.Handle, HotkeyIdAudioQuality);
                _hotkeySource.RemoveHook(HotkeyHook);
                _hotkeySource.Dispose();
                _hotkeySource = null;
            }
        }

        private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmHotkey)
            {
                int id = wParam.ToInt32();
                switch (id)
                {
                    case HotkeyIdVolumeUp:
                        Debugger.show("[HOTKEY] Global hotkey used: volume up.");
                        HandleVolumeHotkey(up: true);
                        handled = true;
                        break;
                    case HotkeyIdVolumeDown:
                        Debugger.show("[HOTKEY] Global hotkey used: volume down.");
                        HandleVolumeHotkey(up: false);
                        handled = true;
                        break;
                    case HotkeyIdToggleScrcpy:
                        Debugger.show("[HOTKEY] Global hotkey used: toggle scrcpy.");
                        ToggleScrcpyNoAudio();
                        handled = true;
                        break;
                    case HotkeyIdToggleLyricsOverlay:
                        Debugger.show("[HOTKEY] Global hotkey used: toggle lyrics overlay.");
                        _lyricsOverlayManager?.ToggleVisibility();
                        ShowToast(_lyricsOverlayManager?.IsOverlayVisible == true ? "Lyrics overlay on" : "Lyrics overlay off");
                        handled = true;
                        break;
                    case HotkeyIdCopyTrackInfo:
                        Debugger.show("[HOTKEY] Global hotkey used: copy track info.");
                        handled = TryCopyCurrentTrackInfoToClipboard();
                        break;
                    case HotkeyIdAudioQuality:
                        Debugger.show("[HOTKEY] Global hotkey used: audio quality.");
                        OpenAudioQualityFromHotkey();
                        handled = true;
                        break;
                }
            }
            else if (msg == WmDeviceChange)
            {
                // A change to the USB device tree. DBT_DEVNODES_CHANGED fires for
                // any plug/unplug without us registering for notifications. Ask the
                // service to look for a USB device to promote to; if the change was
                // unrelated it just finds nothing and returns. We don't mark the
                // message handled so the system can keep broadcasting it.
                int evt = wParam.ToInt32();
                if (evt == DbtDevnodesChanged || evt == DbtDeviceArrival)
                {
                    var svc = _presenceService;
                    if (svc != null)
                        _ = svc.CheckForUsbPromotionAsync();
                }
            }

            return IntPtr.Zero;
        }

        private void HandleVolumeHotkey(bool up)
        {
            // If scrcpy is active and we can adjust its session volume, do that.
            if (TryAdjustScrcpyVolume(up ? ScrcpyVolumeStep : -ScrcpyVolumeStep))
                return;

            // Otherwise fall back to sending an ADB volume keyevent to the device.
            _ = SendAdbVolumeKeyAsync(up);
        }

        private bool TryAdjustScrcpyVolume(float delta)
        {
            var process = _scrcpyProcess;
            if (process == null || process.HasExited)
                return false;

            return ScrcpyVolumeController.TryAdjustVolume(process.Id, delta);
        }

        // ---- Volume helpers exposed to MediaPlayerWindow ----

        internal string GetCurrentDevice() => _presenceService?.CurrentDevice ?? string.Empty;

        /// <summary>
        /// True when scrcpy is running AND its audio session is reachable, i.e.
        /// we can read/write its absolute volume right now.
        /// </summary>
        private bool IsScrcpyAudioSessionAvailable()
        {
            var process = _scrcpyProcess;
            if (process == null || process.HasExited)
                return false;

            return ScrcpyVolumeController.TryGetVolume(process.Id, out _);
        }

        private float? TryGetScrcpyVolume()
        {
            var process = _scrcpyProcess;
            if (process == null || process.HasExited)
                return null;

            return ScrcpyVolumeController.TryGetVolume(process.Id, out var v) ? v : (float?)null;
        }

        private void TrySetScrcpyVolume(float volume)
        {
            var process = _scrcpyProcess;
            if (process == null || process.HasExited)
                return;

            ScrcpyVolumeController.TrySetVolume(process.Id, volume);
        }

        /// <summary>
        /// Single +/- step matching the hotkey behavior: scrcpy session if active,
        /// ADB keyevent fallback otherwise. Inlined (rather than calling
        /// HandleVolumeHotkey) so the +/- buttons don't depend on the hotkey
        /// method's presence.
        /// </summary>
        private void StepVolumeOnce(bool up)
        {
            if (TryAdjustScrcpyVolume(up ? ScrcpyVolumeStep : -ScrcpyVolumeStep))
                return;

            _ = SendAdbVolumeKeyAsync(up);
        }

        private async Task SendAdbVolumeKeyAsync(bool up)
        {
            try
            {
                var device = _presenceService?.CurrentDevice;
                if (string.IsNullOrWhiteSpace(device))
                    return;

                // 24 = KEYCODE_VOLUME_UP, 25 = KEYCODE_VOLUME_DOWN
                var keycode = up ? 24 : 25;
                _presenceService?.NotifyUserInteraction();
                await AdbHelper.RunAdbAsync($"-s {device} shell input keyevent {keycode}").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debugger.show($"ADB volume key failed: {ex.Message}");
            }
        }

        private async Task<(int current, int max)> GetPhoneVolumeAsync()
        {
            try
            {
                var device = _presenceService?.CurrentDevice;
                if (string.IsNullOrWhiteSpace(device))
                    return (-1, 15);

                var output = await AdbHelper.RunAdbCaptureAsync(
                    $"-s {device} shell sh -c \"dumpsys audio | grep -A 6 'VOLUME GROUP AUDIO_STREAM_MUSIC'\"")
                    .ConfigureAwait(false);

                Debugger.show($"[VOLUME] dumpsys output: {output.Replace("\n", "|").Replace("\r", "")}");
                if (string.IsNullOrWhiteSpace(output))
                    return (-1, 15);

                int max = 15;
                string? currentLine = null;
                string? activeDevice = null;

                foreach (var rawLine in output.Split('\n'))
                {
                    var line = rawLine.Trim();

                    if (line.StartsWith("Max:", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(line.Substring(4).Trim(), out var m))
                            max = m;
                    }
                    else if (line.StartsWith("Current:", StringComparison.OrdinalIgnoreCase))
                    {
                        currentLine = line;
                    }
                    else if (line.StartsWith("Devices:", StringComparison.OrdinalIgnoreCase))
                    {
                        activeDevice = line.Substring(8).Trim();
                    }
                }

                if (activeDevice != null && currentLine != null)
                {
                    var needle = $"({activeDevice}):";
                    var idx = currentLine.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        var after = currentLine.Substring(idx + needle.Length).TrimStart();
                        var comma = after.IndexOf(',');
                        var numStr = comma >= 0 ? after.Substring(0, comma) : after;
                        if (int.TryParse(numStr.Trim(), out var v))
                            return (v, max);
                    }
                }

                return (-1, max);
            }
            catch (Exception ex)
            {
                Debugger.show($"GetPhoneVolumeAsync failed: {ex.Message}");
                return (-1, 15);
            }
        }

        private async Task SetPhoneVolumeAsync(int previousIndex, int targetIndex, int max)
        {
            try
            {
                var device = _presenceService?.CurrentDevice;
                if (string.IsNullOrWhiteSpace(device))
                    return;

                int delta = Math.Clamp(targetIndex, 0, max) - Math.Clamp(previousIndex, 0, max);
                if (delta == 0)
                    return;

                int keycode = delta > 0 ? 24 : 25;
                var keys = string.Join(" ", Enumerable.Repeat(keycode, Math.Abs(delta)));
                Debugger.show($"[VOLUME] Sending {Math.Abs(delta)}x keyevent {keycode} (prev={previousIndex} target={targetIndex})");
                _presenceService?.NotifyUserInteraction();
                await AdbHelper.RunAdbAsync($"-s {device} shell input keyevent {keys}").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debugger.show($"SetPhoneVolumeAsync failed: {ex.Message}");
            }
        }

        private bool TryCopyCurrentTrackInfoToClipboard()
        {
            var artist = _lastNowPlayingArtist ?? string.Empty;
            var title = _lastNowPlayingTitle ?? string.Empty;
            var album = _lastNowPlayingAlbum ?? string.Empty;

            if (string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(album))
                return false;

            var template = string.IsNullOrWhiteSpace(Config.CopyTrackInfoTemplate)
                ? "{artist} - {title}"
                : Config.CopyTrackInfoTemplate;

            var text = template
                .Replace("{artist}", artist, StringComparison.OrdinalIgnoreCase)
                .Replace("{title}", title, StringComparison.OrdinalIgnoreCase)
                .Replace("{album}", album, StringComparison.OrdinalIgnoreCase)
                .Trim();

            if (string.IsNullOrWhiteSpace(text))
                return false;

            try
            {
                Dispatcher.Invoke(() => Clipboard.SetText(text));
                ShowToast("Copied: " + text);
                return true;
            }
            catch
            {
                return false;
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // ── Next / Previous song ──────────────────────────────────────────────

        private async Task UpdateNextSongNeighboursAsync(string? title, string? artist)
        {
            try
            {
                var mode = Config.NextSongMode;
                if (mode == NextSongMode.Off) return;

                var window = _mediaPlayerWindow;
                if (window == null || !window.IsVisible) return;

                var device = _presenceService?.CurrentDevice ?? string.Empty;
                var roots = Config.MusicRemoteRoots ?? new System.Collections.Generic.List<string>();

                // First enable and no list: trigger a scan automatically.
                if (!_nextSongManager.IsListPresent)
                {
                    if (string.IsNullOrWhiteSpace(device) || roots.Count == 0)
                    {
                        window.UpdateNeighbours(new NextSongManager.NeighbourResult(null, null, null, null, false), mode, null, null);
                        return;
                    }

                    await _nextSongManager.ScanAsync(device, roots, Config.NextSongSortMode).ConfigureAwait(false);
                }

                var result = await _nextSongManager.FindNeighboursAsync(title, artist).ConfigureAwait(false);

                if (!result.Found)
                {
                    await Dispatcher.InvokeAsync(() => window.UpdateNeighbours(result, mode, null, null));
                    return;
                }

                if (mode == NextSongMode.TextOnly)
                {
                    await Dispatcher.InvokeAsync(() => window.UpdateNeighbours(result, mode, null, null));
                    return;
                }

                // FullArt and Kirsten: fire and forget cover fetches for both neighbours.
                _ = FetchAndPushNeighbourCoversAsync(window, result, device, mode);
            }
            catch (Exception ex)
            {
                Debugger.show("[NEXTSONG] UpdateNextSongNeighboursAsync failed: " + ex.Message);
            }
        }

        internal Task RefreshNextSongNeighboursAsync()
        {
            if (_mediaPlayerWindow == null || !_mediaPlayerWindow.IsVisible || Config.NextSongMode == NextSongMode.Off)
                return Task.CompletedTask;

            return UpdateNextSongNeighboursAsync(_lastMediaPlayerTitle, _lastMediaPlayerArtist);
        }

        private async Task FetchAndPushNeighbourCoversAsync(MediaPlayerWindow window, NextSongManager.NeighbourResult result, string device, NextSongMode mode)
        {
            try
            {
                var cacheManager = _presenceService?.GetCoverCacheManager();
                if (cacheManager == null)
                {
                    await Dispatcher.InvokeAsync(() =>
                        window.UpdateNeighbours(result, mode, null, null));
                    return;
                }

                string? prevCover = null;
                string? nextCover = null;

                if (!string.IsNullOrWhiteSpace(result.PrevPath))
                {
                    try
                    {
                        var r = await cacheManager.GetImagePathForNowPlayingAsync(device, result.PrevPath, Config.SelectedDeviceName).ConfigureAwait(false);
                        prevCover = r.ImagePath;
                    }
                    catch { }
                }

                if (!string.IsNullOrWhiteSpace(result.NextPath))
                {
                    try
                    {
                        var r = await cacheManager.GetImagePathForNowPlayingAsync(device, result.NextPath, Config.SelectedDeviceName).ConfigureAwait(false);
                        nextCover = r.ImagePath;
                    }
                    catch { }
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    if (_mediaPlayerWindow == null || !_mediaPlayerWindow.IsVisible) return;
                    window.UpdateNeighbours(result, mode, prevCover, nextCover);
                });
            }
            catch (Exception ex)
            {
                Debugger.show("[NEXTSONG] FetchAndPushNeighbourCoversAsync failed: " + ex.Message);
            }
        }

        internal void RescanNextSongLibraryAsync(Action? onComplete = null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var device = _presenceService?.CurrentDevice ?? string.Empty;
                    var roots = Config.MusicRemoteRoots ?? new System.Collections.Generic.List<string>();

                    if (string.IsNullOrWhiteSpace(device) || roots.Count == 0)
                    {
                        Debugger.show("[NEXTSONG] Rescan skipped: no device or no roots configured.");
                        onComplete?.Invoke();
                        return;
                    }

                    _nextSongManager.InvalidateCache();
                    await _nextSongManager.ScanAsync(device, roots, Config.NextSongSortMode).ConfigureAwait(false);

                    // Re-run neighbour lookup with the last known track.
                    await Dispatcher.InvokeAsync(async () =>
                    {
                        if (_mediaPlayerWindow != null && _mediaPlayerWindow.IsVisible && Config.NextSongMode != NextSongMode.Off)
                            await UpdateNextSongNeighboursAsync(_lastMediaPlayerTitle, _lastMediaPlayerArtist);
                    });
                }
                catch (Exception ex)
                {
                    Debugger.show("[NEXTSONG] RescanNextSongLibraryAsync failed: " + ex.Message);
                }
                finally
                {
                    onComplete?.Invoke();
                }
            });
        }

        internal void ResortNextSongListAsync()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _nextSongManager.ResortAsync(Config.NextSongSortMode).ConfigureAwait(false);

                    await Dispatcher.InvokeAsync(async () =>
                    {
                        if (_mediaPlayerWindow != null && _mediaPlayerWindow.IsVisible && Config.NextSongMode != NextSongMode.Off)
                            await UpdateNextSongNeighboursAsync(_lastMediaPlayerTitle, _lastMediaPlayerArtist);
                    });
                }
                catch (Exception ex)
                {
                    Debugger.show("[NEXTSONG] ResortNextSongListAsync failed: " + ex.Message);
                }
            });
        }

    }
}