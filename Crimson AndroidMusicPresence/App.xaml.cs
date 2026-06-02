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

namespace musicpresense
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
        private const float ScrcpyVolumeStep = 0.05f;
        private const string AppUserModelId = "Android Music Presence Link";

        private static readonly string version = GetAppVersion();

        internal static string CurrentVersion => version;




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
            ApplyTheme(Config.UseDarkMode);

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
            _lyricsOverlayManager = new LyricsOverlayManager(Dispatcher, Config, () => _presenceService?.CurrentDevice ?? string.Empty);
            _trayIconManager = new TrayIconManager(ShowSettingsWindow, ToggleScrcpyNoAudio, ShutdownApplication, Config.UseDarkMode);
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

                if (!string.IsNullOrWhiteSpace(oldDevice))
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
                    if (!string.IsNullOrWhiteSpace(newDevice))
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
                await AdbHelper.RunAdbAsync($"-s {newDevice} shell input keyevent 164").ConfigureAwait(true);

                if (wasPlaying)
                    await AdbHelper.RunAdbAsync($"-s {newDevice} shell input keyevent 126").ConfigureAwait(true);
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
            Window? owner = calledFromMediaPlayer ? (Window?)_mediaPlayerWindow : _settingsWindow;
            // Only assign Owner if the window has a live HWND; a window that was
            // created but never shown (e.g. hidden on startup) throws otherwise.
            if (owner != null && !owner.IsLoaded)
                owner = null;

            var window = new AudioCustomQualityWindow(Config, showPresets);
            if (owner != null)
                window.Owner = owner;

            if (window.ShowDialog() == true && window.ResultConfig.HasValue)
            {
                var (codec, bitrate, bufferMs, flacLevel) = window.ResultConfig.Value;
                AudioQualityPresets.ApplyCustomToConfig(Config, codec, bitrate, bufferMs, flacLevel);
                MusicConfigManager.Save(Config);

                bool wasRunning = _scrcpyProcess != null && !_scrcpyProcess.HasExited;
                UpdateConfig(Config);

                if (wasRunning)
                    _ = RestartScrcpyForPresetAsync();
            }
        }

        private async Task RestartScrcpyForPresetAsync()
        {
            try
            {
                var device = _presenceService?.CurrentDevice;

                if (!string.IsNullOrWhiteSpace(device))
                    await AdbHelper.RunAdbAsync($"-s {device} shell input keyevent 164").ConfigureAwait(true);

                await StopScrcpyAsync().ConfigureAwait(true);
                await Task.Delay(150).ConfigureAwait(true);
                StartScrcpyNoAudio();
                await Task.Delay(1000).ConfigureAwait(true);

                if (!string.IsNullOrWhiteSpace(device))
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
            Config = config;
            ApplyStartupRegistration(config.StartWithWindows);
            Debugger.IsEnabled = Config.DebugMode;
            AdbHelper.AdbPath = Config.Paths.Adb;
            ApplyTheme(config.UseDarkMode);
            _presenceService?.UpdateConfig(config);
            _lyricsOverlayManager?.UpdateConfig(config);
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
        }

        private void OnLyricsPlaybackChanged(string? artist, string? title, string? album, bool isPlaying, long positionMs)
        {
            _lyricsOverlayManager?.OnPlaybackChanged(artist, title, album, isPlaying, positionMs);
        }

        private void OnMediaPlayerStateChanged(string? title, string? artist, string? album, string? coverPath, bool isPlaying, long positionMs, long durationMs)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;

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

                if (App.Config.NextSongMode != NextSongMode.Off)
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
                    (prev, target, max) => SetPhoneVolumeAsync(prev, target, max));
                _mediaPlayerWindow.Closing += MediaPlayerWindow_Closing;
                _mediaPlayerWindow.InitNextSongPanels(() => RescanNextSongLibraryAsync());

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

        internal void ApplyTheme(bool useDarkMode)
        {
            Resources["ThemeBackgroundBrush"] = CreateBrush(useDarkMode ? "#1E1E1E" : "#F7F7F7");
            Resources["ThemeForegroundBrush"] = CreateBrush(useDarkMode ? "#EAEAEA" : "#1A1A1A");
            Resources["ThemeControlBackgroundBrush"] = CreateBrush(useDarkMode ? "#2B2B2B" : "#FFFFFF");
            Resources["ThemeControlForegroundBrush"] = CreateBrush(useDarkMode ? "#EAEAEA" : "#1A1A1A");
            Resources["ThemeControlBorderBrush"] = CreateBrush(useDarkMode ? "#3C3C3C" : "#C8C8C8");
            Resources["ThemeAccentBrush"] = CreateBrush(useDarkMode ? "#3E7BFF" : "#2D6CDF");
            Resources["ThemeAccentHoverBrush"] = CreateBrush(useDarkMode ? "#5A8BFF" : "#3E7BFF");
            Resources["ThemeAccentPressedBrush"] = CreateBrush(useDarkMode ? "#275ED6" : "#1F5DD1");
            _trayIconManager?.SetDarkMode(useDarkMode);

            // Push the theme change into the media player window so the idle
            // background, icon brush, and text colors all flip immediately.
            _mediaPlayerWindow?.NotifyThemeChanged();

            // If the dev marker file is present, always re-apply dev accent colors
            // so UpdateConfig can't clobber them.
            if (File.Exists(Path.Combine(AppPaths.BaseDirectory, "devmode_snail.txt")))
                ApplyDevTheme();
        }

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
                MessageBox.Show("No device connected!", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(Config.Paths.Scrcpy) || !File.Exists(Config.Paths.Scrcpy))
            {
                _audioLinkDesired = false;
                MessageBox.Show("scrcpy.exe not found!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                var process = LaunchScrcpyProcess(device);
                if (process == null)
                {
                    _audioLinkDesired = false;
                    MessageBox.Show("scrcpy failed to start.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
            }
            catch (Exception ex)
            {
                _audioLinkDesired = false;
                MessageBox.Show($"scrcpy launch failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
                    Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show($"Failed to stop scrcpy: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    });
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
                    if (Config.UpdateIntervalMode > UpdateIntervalMode.Fast)
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
            }
            catch (Exception ex)
            {
                Debugger.show("Audio-link recovery: launch failed: " + ex.Message);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _isExiting = true;
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

                // FullArt: fire and forget cover fetches for both neighbours.
                _ = FetchAndPushNeighbourCoversAsync(window, result, device);
            }
            catch (Exception ex)
            {
                Debugger.show("[NEXTSONG] UpdateNextSongNeighboursAsync failed: " + ex.Message);
            }
        }

        private async Task FetchAndPushNeighbourCoversAsync(MediaPlayerWindow window, NextSongManager.NeighbourResult result, string device)
        {
            try
            {
                var cacheManager = _presenceService?.GetCoverCacheManager();
                if (cacheManager == null)
                {
                    await Dispatcher.InvokeAsync(() =>
                        window.UpdateNeighbours(result, NextSongMode.FullArt, null, null));
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
                    window.UpdateNeighbours(result, NextSongMode.FullArt, prevCover, nextCover);
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