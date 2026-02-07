using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace musicpresense
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private MusicConfig _config;
        private bool _isInitializing = true;
        private bool _allowClose;
        private readonly ObservableCollection<AppPackageItem> _appPackages = new();
        private bool _isLoadingApps;

        public MainWindow()
        {
            InitializeComponent();

            _config = App.Config;
            InitializeUpdateIntervalUI();
            ApplyConfigToUI();

            LstAllowedApps.ItemsSource = _appPackages;

            BtnSave.Click += BtnSave_Click;
            BtnRefreshApps.Click += BtnRefreshApps_Click;
            Closing += MainWindow_Closing;
            Loaded += MainWindow_Loaded;
            _isInitializing = false;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _ = LoadInstalledAppsAsync();
        }

        private void BtnRefreshApps_Click(object sender, RoutedEventArgs e)
        {
            _ = LoadInstalledAppsAsync();
        }

        internal void AllowClose() => _allowClose = true;

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            if (_allowClose) return;
            e.Cancel = true;
            Hide();
        }

        private void InitializeUpdateIntervalUI()
        {
            CmbUpdateInterval.ItemsSource = new[]
            {
                "Extreme (1s)",
                "Fast (5s)",
                "Medium (15s)",
                "Slow (30s)",
                "No automatic update"
            };
        }

        private void ApplyConfigToUI()
        {
            TxtUsbSerial.Text = _config.SelectedDeviceUSB;
            TxtWifi.Text = _config.SelectedDeviceWiFi;
            TxtDeviceName.Text = _config.SelectedDeviceName;
            TxtRemoteRoot.Text = _config.MusicRemoteRoot;

            ApplyAllowedAppsSelection();

            int mode = (int)_config.UpdateIntervalMode;
            if (mode < 1 || mode > 5) mode = 3;
            CmbUpdateInterval.SelectedIndex = mode - 1;

            ChkDebugMode.IsChecked = _config.DebugMode;
        }

        private void ApplyAllowedAppsSelection()
        {
            if (_appPackages.Count == 0 || _config.AllowedApps == null)
                return;

            var allowed = new HashSet<string>(_config.AllowedApps, StringComparer.OrdinalIgnoreCase);
            foreach (var item in _appPackages)
            {
                item.IsSelected = allowed.Contains(item.PackageName);
            }
        }

        private async Task LoadInstalledAppsAsync()
        {
            if (_isLoadingApps) return;
            _isLoadingApps = true;
            TxtAppsStatus.Text = "Loading...";

            try
            {
                var device = await GetCurrentDeviceForAppsAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(device))
                {
                    await Dispatcher.InvokeAsync(() => TxtAppsStatus.Text = "No device connected.");
                    return;
                }

                var output = await AdbHelper.RunAdbCaptureAsync($"-s {device} shell pm list packages").ConfigureAwait(false);
                var packages = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(l => l.StartsWith("package:"))
                    .Select(l => l.Substring(8))
                    .OrderBy(l => l)
                    .ToList();

                var allowed = new HashSet<string>(_config.AllowedApps ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

                await Dispatcher.InvokeAsync(() =>
                {
                    _appPackages.Clear();
                    foreach (var pkg in packages)
                    {
                        _appPackages.Add(new AppPackageItem(pkg, allowed.Contains(pkg)));
                    }

                    TxtAppsStatus.Text = $"{_appPackages.Count} apps";
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    TxtAppsStatus.Text = "Failed to load apps.";
                    MessageBox.Show($"Failed to load installed apps: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
            finally
            {
                _isLoadingApps = false;
            }
        }

        private async Task<string> GetCurrentDeviceForAppsAsync()
        {
            string device = string.Empty;

            var devices = await AdbHelper.RunAdbCaptureAsync("devices").ConfigureAwait(false);
            var deviceList = devices.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            bool IsDeviceConnected(string id) => deviceList.Any(l => l.StartsWith(id) && l.EndsWith("device"));

            if (!string.IsNullOrWhiteSpace(_config.SelectedDeviceUSB) && IsDeviceConnected(_config.SelectedDeviceUSB))
            {
                device = _config.SelectedDeviceUSB;
            }
            else if (!string.IsNullOrWhiteSpace(_config.SelectedDeviceWiFi) && _config.SelectedDeviceWiFi != "None")
            {
                if (!IsDeviceConnected(_config.SelectedDeviceWiFi))
                {
                    await AdbHelper.RunAdbCaptureAsync($"connect {_config.SelectedDeviceWiFi}").ConfigureAwait(false);
                    devices = await AdbHelper.RunAdbCaptureAsync("devices").ConfigureAwait(false);
                    deviceList = devices.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                }

                if (IsDeviceConnected(_config.SelectedDeviceWiFi))
                {
                    device = _config.SelectedDeviceWiFi;
                }
            }

            return device;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            _config.SelectedDeviceUSB = TxtUsbSerial.Text.Trim();
            _config.SelectedDeviceWiFi = TxtWifi.Text.Trim();
            _config.SelectedDeviceName = TxtDeviceName.Text.Trim();
            _config.MusicRemoteRoot = TxtRemoteRoot.Text.Trim();

            _config.AllowedApps = _appPackages
                .Where(item => item.IsSelected)
                .Select(item => item.PackageName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (_config.AllowedApps.Count == 0)
            {
                _config.AllowedApps.Add("in.krosbits.musicolet");
            }

            if (!_isInitializing && CmbUpdateInterval.SelectedIndex >= 0)
            {
                _config.UpdateIntervalMode = (UpdateIntervalMode)(CmbUpdateInterval.SelectedIndex + 1);
            }

            _config.DebugMode = ChkDebugMode.IsChecked == true;

            MusicConfigManager.Save(_config);
            (Application.Current as App)?.UpdateConfig(_config);

            MessageBox.Show("Music presence settings saved.", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private sealed class AppPackageItem : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler? PropertyChanged;

            public string PackageName { get; }

            private bool _isSelected;
            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected == value) return;
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }

            public AppPackageItem(string packageName, bool isSelected)
            {
                PackageName = packageName;
                _isSelected = isSelected;
            }
        }
    }
}