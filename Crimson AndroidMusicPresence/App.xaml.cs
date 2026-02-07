using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Windows;

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

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            Config = MusicConfigManager.Load();
            Debugger.IsEnabled = Config.DebugMode;
            AdbHelper.AdbPath = Config.Paths.Adb;

            _settingsWindow = new MainWindow();
            _settingsWindow.Hide();

            _presenceService = new MusicPresenceService(Dispatcher, Config);
            _presenceService.Start();

            _trayIconManager = new TrayIconManager(ShowSettingsWindow, ToggleScrcpyNoAudio, ShutdownApplication);
        }

        internal void UpdateConfig(MusicConfig config)
        {
            Config = config;
            Debugger.IsEnabled = Config.DebugMode;
            AdbHelper.AdbPath = Config.Paths.Adb;
            _presenceService?.UpdateConfig(config);
        }

        private void ShowSettingsWindow()
        {
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
                StopScrcpy();
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

            var args = $"-s {device} --no-video --no-window --audio-source=playback --audio-buffer=300";

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
                    _trayIconManager?.SetScrcpyRunning(false);
                    return;
                }

                _scrcpyProcess.EnableRaisingEvents = true;
                _scrcpyProcess.Exited += ScrcpyProcessExited;
                _trayIconManager?.SetScrcpyRunning(true);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"scrcpy launch failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                _trayIconManager?.SetScrcpyRunning(false);
            }
        }

        private async void StopScrcpy()
        {
            var process = _scrcpyProcess;
            _scrcpyProcess = null;
            _trayIconManager?.SetScrcpyRunning(false);

            if (process == null)
                return;

            try
            {
                await Task.Run(() =>
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                        process.WaitForExit();
                    }
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to stop scrcpy: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                process.Dispose();
            }
        }

        private void ScrcpyProcessExited(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                _scrcpyProcess?.Dispose();
                _scrcpyProcess = null;
                _trayIconManager?.SetScrcpyRunning(false);
            });
        }

        protected override void OnExit(ExitEventArgs e)
        {
            StopScrcpy();
            _trayIconManager?.Dispose();
            _presenceService?.Dispose();
            base.OnExit(e);
        }
    }

}
