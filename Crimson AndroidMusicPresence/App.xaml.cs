using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
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
        private HwndSource? _hotkeySource;
        private const string StartupRunValueName = "AndroidMusicPresenceLink";

        private const int HotkeyIdVolumeUp = 1;
        private const int HotkeyIdVolumeDown = 2;
        private const int HotkeyIdToggleScrcpy = 3;
        private const int HotkeyIdToggleLyricsOverlay = 4;
        private const int HotkeyIdCopyTrackInfo = 5;
        private const int ModShift = 0x0004;
        private const int VkVolumeUp = 0xAF;
        private const int VkVolumeDown = 0xAE;
        private const int WmHotkey = 0x0312;
        private const float ScrcpyVolumeStep = 0.05f;

        private static readonly string version = "1.0.12.0";

        private bool _isScrcpyRunning;
        private bool _isExiting;
        private TrayIconState _lastTrayState = TrayIconState.NoDevice;
        private string? _lastNowPlayingArtist;
        private string? _lastNowPlayingTitle;
        private string? _lastNowPlayingAlbum;
        private string? _lastMediaPlayerTitle;
        private string? _lastMediaPlayerArtist;
        private string? _lastMediaPlayerAlbum;
        private string? _lastMediaPlayerCoverPath;
        private bool _lastMediaPlayerIsPlaying;

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
            else
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

            if (Config.ShowMediaPlayerWindow)
            {
                ShowMediaPlayerWindowNow();
            }
            else
            {
                UpdateSettingsWindowModeButton();
            }

            InitializeHotkeys();

            _ = Updater.CheckForUpdateAsync(version);
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
                    _ => state
                };
            }

            _trayIconManager?.SetState(state);
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

        private void OnMediaPlayerStateChanged(string? title, string? artist, string? album, string? coverPath, bool isPlaying)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;

            _lastMediaPlayerTitle = title;
            _lastMediaPlayerArtist = artist;
            _lastMediaPlayerAlbum = album;
            _lastMediaPlayerCoverPath = coverPath;
            _lastMediaPlayerIsPlaying = isPlaying;

            Dispatcher.BeginInvoke(() =>
            {
                if (_mediaPlayerWindow == null || !_mediaPlayerWindow.IsVisible)
                    return;

                _mediaPlayerWindow.UpdateTrack(title, artist, album, coverPath, isPlaying);
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
            if (_mediaPlayerWindow == null)
            {
                _mediaPlayerWindow = new MediaPlayerWindow(
                    () => _presenceService?.PauseCurrentAsync() ?? Task.CompletedTask,
                    () => _presenceService?.NextCurrentAsync() ?? Task.CompletedTask,
                    () => _presenceService?.PreviousCurrentAsync() ?? Task.CompletedTask);
                _mediaPlayerWindow.Closing += MediaPlayerWindow_Closing;
            }

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
                _settingsWindow.Show();
                _settingsWindow.Activate();
            }

            UpdateSettingsWindowModeButton();
        }

        private void MediaPlayerWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
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
            Config.ShowMediaPlayerWindow = false;
            MusicConfigManager.Save(Config);

            if (_settingsWindow != null)
            {
                _settingsWindow.SyncRuntimeConfig(Config);
            }

            if (_settingsWindow != null && !Config.OpenInTaskbar && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                _settingsWindow.Show();
                _settingsWindow.Activate();
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

            if (_settingsWindow == null)
            {
                _settingsWindow = new MainWindow();
            }

            if (!_settingsWindow.IsVisible)
            {
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
                _ = StopScrcpyAsync();
            }
            else
            {
                StartScrcpyNoAudio();
            }
        }

        private void StartScrcpyNoAudio()
        {
            var device = _presenceService?.CurrentDevice;
            if (string.IsNullOrWhiteSpace(device))
            {
                MessageBox.Show("No device connected!", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(Config.Paths.Scrcpy) || !File.Exists(Config.Paths.Scrcpy))
            {
                MessageBox.Show("scrcpy.exe not found!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

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

            var args = string.Join(" ", argParts);

            Debugger.show($"Starting scrcpy with args: {args}");

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Config.Paths.Scrcpy,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                _scrcpyProcess = Process.Start(psi);
                if (_scrcpyProcess == null)
                {
                    MessageBox.Show("scrcpy failed to start.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    _isScrcpyRunning = false;
                    _trayIconManager?.SetScrcpyRunning(false);
                    UpdateTrayAudioSettings();
                    ApplyTrayState();
                    return;
                }

                _scrcpyProcess.EnableRaisingEvents = true;
                _scrcpyProcess.Exited += ScrcpyProcessExited;
                _isScrcpyRunning = true;
                _trayIconManager?.SetScrcpyRunning(true);
                UpdateTrayAudioSettings();
                ApplyTrayState();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"scrcpy launch failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                _isScrcpyRunning = false;
                _trayIconManager?.SetScrcpyRunning(false);
                UpdateTrayAudioSettings();
                ApplyTrayState();
            }
        }

        private async Task StopScrcpyAsync()
        {
            var process = _scrcpyProcess;
            _scrcpyProcess = null;
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
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                _scrcpyProcess?.Dispose();
                _scrcpyProcess = null;
                _isScrcpyRunning = false;
                return;
            }

            Dispatcher.BeginInvoke(() =>
            {
                _scrcpyProcess?.Dispose();
                _scrcpyProcess = null;
                _isScrcpyRunning = false;
                _trayIconManager?.SetScrcpyRunning(false);
                UpdateTrayAudioSettings();
                ApplyTrayState();
            });
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
            var process = _scrcpyProcess;
            _scrcpyProcess = null;
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
                        handled = TryAdjustScrcpyVolume(ScrcpyVolumeStep);
                        break;
                    case HotkeyIdVolumeDown:
                        handled = TryAdjustScrcpyVolume(-ScrcpyVolumeStep);
                        break;
                    case HotkeyIdToggleScrcpy:
                        ToggleScrcpyNoAudio();
                        handled = true;
                        break;
                    case HotkeyIdToggleLyricsOverlay:
                        _lyricsOverlayManager?.ToggleVisibility();
                        handled = true;
                        break;
                    case HotkeyIdCopyTrackInfo:
                        handled = TryCopyCurrentTrackInfoToClipboard();
                        break;
                }
            }

            return IntPtr.Zero;
        }

        private bool TryAdjustScrcpyVolume(float delta)
        {
            var process = _scrcpyProcess;
            if (process == null || process.HasExited)
                return false;

            return ScrcpyVolumeController.TryAdjustVolume(process.Id, delta);
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
    }

}
