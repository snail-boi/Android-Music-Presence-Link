using Crimson_AndroidMusicPresence;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace musicpresense
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private MusicConfig _config;
        private MusicConfig _savedConfig;
        private bool _isInitializing = true;
        private bool _allowClose;
        private readonly ObservableCollection<AppPackageItem> _appPackages = new();
        private bool _isLoadingApps;
        private readonly ObservableCollection<string> _audioCodecs = new();
        private bool _isLoadingCodecs;
        private bool _isAutoGathering;
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            Config.Load();

            Width = Config.Current.WindowWidth;
            Height = Config.Current.WindowHeight;
            Top = Config.Current.WindowTop;
            Left = Config.Current.WindowLeft;
            WindowState = Config.Current.WindowState;
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);

            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;

            Config.Current.WindowState = WindowState;
            Config.Current.WindowWidth = RestoreBounds.Width;
            Config.Current.WindowHeight = RestoreBounds.Height;
            Config.Current.WindowTop = RestoreBounds.Top;
            Config.Current.WindowLeft = RestoreBounds.Left;

            Config.Save();
        }

        public MainWindow()
        {
            InitializeComponent();

            _config = App.Config;
            _savedConfig = CloneConfig(_config);
            InitializeUpdateIntervalUI();
            InitializeAudioCodecUI();
            ApplyConfigToUI();

            LstAllowedApps.ItemsSource = _appPackages;
            LstAudioCodecs.ItemsSource = _audioCodecs;

            BtnSave.Click += BtnSave_Click;
            BtnRefreshApps.Click += BtnRefreshApps_Click;
            BtnListCodecs.Click += BtnListCodecs_Click;
            BtnAutoGather.Click += BtnAutoGather_Click;
            BtnPickRemoteRoot.Click += BtnPickRemoteRoot_Click;
            BtnClearCoverCache.Click += BtnClearCoverCache_Click;
            LstAudioCodecs.SelectionChanged += LstAudioCodecs_SelectionChanged;
            ChkDarkMode.Checked += ChkDarkMode_CheckedChanged;
            ChkDarkMode.Unchecked += ChkDarkMode_CheckedChanged;
            BtnToggleTheme.Click += BtnToggleTheme_Click;
            Closing += MainWindow_Closing;
            Loaded += MainWindow_Loaded;
            _isInitializing = false;
        }

        private void BtnClearCoverCache_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var manager = new CoverCacheManager(_config.Paths.FfmpegPath, _config.Paths.CoverCachePath);
                manager.ClearCache();
                MessageBox.Show("Cover cache cleared.", "Cover Cache", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to clear cover cache: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnPickRemoteRoot_Click(object sender, RoutedEventArgs e)
        {
            var device = await GetCurrentDeviceForAppsAsync();
            if (string.IsNullOrWhiteSpace(device))
            {
                MessageBox.Show("No device connected.", "Device Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var picker = new RemoteFolderPicker(device)
            {
                Owner = this
            };

            if (picker.ShowDialog() == true)
            {
                TxtRemoteRoot.Text = picker.SelectedFolder;
            }
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

            if (HasUnsavedChanges())
            {
                var result = MessageBox.Show(
                    "there are unsaved changes, do you wish to save them?",
                    "Unsaved changes",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }

                if (result == MessageBoxResult.Yes)
                {
                    SaveConfigFromUi(true);
                }
                else if (result == MessageBoxResult.No)
                {
                    RevertUnsavedChanges();
                }
            }

            e.Cancel = true;
            Hide();
        }

        private async void BtnAutoGather_Click(object sender, RoutedEventArgs e)
        {
            if (_isAutoGathering)
                return;

            _isAutoGathering = true;
            BtnAutoGather.IsEnabled = false;

            try
            {
                await AutoGatherDeviceInfoAsync();
            }
            finally
            {
                _isAutoGathering = false;
                BtnAutoGather.IsEnabled = true;
            }
        }

        private async Task AutoGatherDeviceInfoAsync()
        {
            var usbSerial = await GetConnectedUsbDeviceAsync();
            if (string.IsNullOrWhiteSpace(usbSerial))
            {
                MessageBox.Show("Please connect your device via USB first.", "USB Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            TxtUsbSerial.Text = usbSerial;
            var port = 0;
            var ip = "none";
            if (MessageBox.Show("do you want to enable WiFi","May be incompatible with certain networks",MessageBoxButton.YesNo,MessageBoxImage.Question) == MessageBoxResult.No)
            {
                _config.IsWifiEnabled = false;
            }
            else
            {
                _config.IsWifiEnabled = true;
                port = await GetWifiPortAsync(usbSerial);
                ip = await GetDeviceWifiIpAsync(usbSerial);

            }

            if (!string.IsNullOrWhiteSpace(ip))
            {
                if (_config.IsWifiEnabled == true)
                {
                    TxtWifi.Text = $"{ip}:{port}";
                }
                else
                {
                    TxtWifi.Text = "";
                }

            }
            else
            {
                MessageBox.Show("Could not read the device Wi-Fi IP address.", "Wi-Fi Info", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            var deviceNamePrompt = string.IsNullOrWhiteSpace(TxtDeviceName.Text) ? "" : TxtDeviceName.Text.Trim();

            var nameDialog = new NameInputDialogue("Enter a name for this device:", "Device Name")
            {
                Owner = this
            };

            if (nameDialog.ShowDialog() != true)
            {
                return;
            }
            var deviceName = nameDialog.InputText; ;


            if (!string.IsNullOrWhiteSpace(deviceName))
            {
                TxtDeviceName.Text = deviceName.Trim();
            }
        }

        private static async Task<string> GetConnectedUsbDeviceAsync()
        {
            var devices = await AdbHelper.RunAdbCaptureAsync("devices");
            var deviceList = devices.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var entry in deviceList)
            {
                if (!entry.EndsWith("device", StringComparison.OrdinalIgnoreCase))
                    continue;

                var serial = entry.Split('\t', ' ').FirstOrDefault();
                if (string.IsNullOrWhiteSpace(serial))
                    continue;

                if (!serial.Contains(':'))
                    return serial;
            }

            return string.Empty;
        }

        private static async Task<int> GetWifiPortAsync(string usbSerial)
        {
            var output = await AdbHelper.RunAdbCaptureAsync($"-s {usbSerial} shell getprop service.adb.tcp.port");
            if (int.TryParse(output.Trim(), out var port) && port > 0)
                return port;

            return 5555;
        }

        private static async Task<string> GetDeviceWifiIpAsync(string usbDevice)
        {
            var ipOutput = await AdbHelper.RunAdbCaptureAsync($"-s {usbDevice} shell ip -f inet addr show wlan0");
            var match = Regex.Match(ipOutput, @"inet\s+(?<ip>\d+\.\d+\.\d+\.\d+)");
            if (match.Success)
                return match.Groups["ip"].Value;

            var routeOutput = await AdbHelper.RunAdbCaptureAsync($"-s {usbDevice} shell ip route");
            match = Regex.Match(routeOutput, @"src\s+(?<ip>\d+\.\d+\.\d+\.\d+)");
            return match.Success ? match.Groups["ip"].Value : string.Empty;
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

        private void InitializeAudioCodecUI()
        {
            _audioCodecs.Clear();
            if (_config.ScrcpyAvailableAudioCodecs != null && _config.ScrcpyAvailableAudioCodecs.Count > 0)
            {
                foreach (var codec in _config.ScrcpyAvailableAudioCodecs.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    _audioCodecs.Add(codec.ToLowerInvariant());
                }
            }

            if (!_audioCodecs.Any(c => c.Equals("raw", StringComparison.OrdinalIgnoreCase)))
            {
                _audioCodecs.Insert(0, "raw");
            }
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
            ChkDarkMode.IsChecked = _config.UseDarkMode;
            ChkOpenInTaskbar.IsChecked = _config.OpenInTaskbar;
            UpdateThemeToggleText(_config.UseDarkMode);

            TxtAudioBitrate.Text = _config.ScrcpyAudioBitrate ?? string.Empty;
            TxtAudioBuffer.Text = _config.ScrcpyAudioBuffer > 0 ? _config.ScrcpyAudioBuffer.ToString() : "50";
            TxtFlacCompressionLevel.Text = _config.ScrcpyFlacCompressionLevel.ToString();
            TxtPauseClearDelayMinutes.Text = _config.SmtcPauseClearDelayMinutes.ToString();

            SelectCodecFromConfig();
            UpdateCodecDependentFields();

            // Populate hotkey fields with hex representation
            try { TxtHotkeyVolumeUp.Text = VirtualKeyToDisplayName(_config.HotkeyVolumeUpKey); } catch { TxtHotkeyVolumeUp.Text = string.Empty; }
            try { TxtHotkeyVolumeDown.Text = VirtualKeyToDisplayName(_config.HotkeyVolumeDownKey); } catch { TxtHotkeyVolumeDown.Text = string.Empty; }
            try { TxtHotkeyToggleScrcpy.Text = VirtualKeyToDisplayName(_config.HotkeyToggleScrcpyKey); } catch { TxtHotkeyToggleScrcpy.Text = string.Empty; }

            // Set modifier combobox to current config
            try
            {
                foreach (var item in CmbHotkeyModifier.Items)
                {
                    if (item is System.Windows.Controls.ComboBoxItem cbi && cbi.Tag != null)
                    {
                        if (int.TryParse(cbi.Tag.ToString()?.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out var mod) && mod == _config.HotkeyModifier)
                        {
                            CmbHotkeyModifier.SelectedItem = cbi;
                            break;
                        }
                    }
                }
            }
            catch { }
        }

        private void ChkDarkMode_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (_isInitializing)
                return;

            var useDarkMode = ChkDarkMode.IsChecked == true;
            (Application.Current as App)?.ApplyTheme(useDarkMode);
            UpdateThemeToggleText(useDarkMode);
        }

        private void BtnToggleTheme_Click(object sender, RoutedEventArgs e)
        {
            ChkDarkMode.IsChecked = !(ChkDarkMode.IsChecked == true);
        }

        private void UpdateThemeToggleText(bool useDarkMode)
        {
            if (BtnToggleTheme == null)
                return;

            BtnToggleTheme.Content = useDarkMode ? "Switch to Light" : "Switch to Dark";
        }

        private void SelectCodecFromConfig()
        {
            var codec = string.IsNullOrWhiteSpace(_config.ScrcpyAudioCodec) ? "raw" : _config.ScrcpyAudioCodec.Trim();
            if (!_audioCodecs.Any(c => c.Equals(codec, StringComparison.OrdinalIgnoreCase)))
            {
                codec = "raw";
            }

            var selected = _audioCodecs.FirstOrDefault(c => c.Equals(codec, StringComparison.OrdinalIgnoreCase));
            LstAudioCodecs.SelectedItem = selected ?? _audioCodecs.FirstOrDefault();
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
            SaveConfigFromUi(true);
        }

        private void SaveConfigFromUi(bool showConfirmation)
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
            _config.UseDarkMode = ChkDarkMode.IsChecked == true;
            _config.OpenInTaskbar = ChkOpenInTaskbar.IsChecked == true;

            var selectedCodec = LstAudioCodecs.SelectedItem as string ?? "raw";
            _config.ScrcpyAudioCodec = selectedCodec;

            if (selectedCodec.Equals("raw", StringComparison.OrdinalIgnoreCase))
            {
                _config.ScrcpyAudioBitrate = string.Empty;
            }
            else
            {
                var bitrateText = TxtAudioBitrate.Text.Trim();
                if (string.IsNullOrEmpty(bitrateText))
                {
                    _config.ScrcpyAudioBitrate = string.Empty;
                }
                else if (int.TryParse(bitrateText, out var bitrateValue))
                {
                    if (bitrateValue < 1)
                        bitrateValue = 1;

                    if (bitrateValue > 10000)
                    {
                        var message = BuildBitrateWarningMessage(selectedCodec, bitrateValue);
                        var response = MessageBox.Show(message, "High bitrate warning", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                        if (response == MessageBoxResult.No)
                        {
                            bitrateValue = GetTypicalBitrate(selectedCodec);
                            TxtAudioBitrate.Text = bitrateValue.ToString();
                        }
                    }

                    _config.ScrcpyAudioBitrate = bitrateValue > 0 ? bitrateValue.ToString() : string.Empty;
                }
                else
                {
                    _config.ScrcpyAudioBitrate = string.Empty;
                }
            }

            if (int.TryParse(TxtAudioBuffer.Text.Trim(), out var bufferValue) && bufferValue > 0)
            {
                if (bufferValue > 2000)
                {
                    var response = MessageBox.Show(
                        "The audio buffer is above 2000 ms, which can introduce a noticeable delay. Continue with this value?",
                        "Large audio buffer",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (response == MessageBoxResult.No)
                    {
                        bufferValue = 2000;
                        TxtAudioBuffer.Text = bufferValue.ToString();
                    }
                }

                _config.ScrcpyAudioBuffer = Math.Max(1, bufferValue);
            }
            else
            {
                _config.ScrcpyAudioBuffer = 50;
            }

            if (int.TryParse(TxtFlacCompressionLevel.Text.Trim(), out var flacLevel))
            {
                var clampedFlac = Math.Clamp(flacLevel, 1, 8);
                if (clampedFlac != flacLevel)
                {
                    TxtFlacCompressionLevel.Text = clampedFlac.ToString();
                }

                _config.ScrcpyFlacCompressionLevel = clampedFlac;
            }
            else
            {
                _config.ScrcpyFlacCompressionLevel = 5;
            }

            if (int.TryParse(TxtPauseClearDelayMinutes.Text.Trim(), out var pauseDelay))
            {
                _config.SmtcPauseClearDelayMinutes = Math.Max(0, pauseDelay);
            }
            else
            {
                _config.SmtcPauseClearDelayMinutes = 3;
            }

            // Parse and store hotkey settings (allows hex 0x.., decimal, single letters or common names)
            _config.HotkeyVolumeUpKey = ParseVirtualKey(TxtHotkeyVolumeUp.Text.Trim(), _config.HotkeyVolumeUpKey);
            _config.HotkeyVolumeDownKey = ParseVirtualKey(TxtHotkeyVolumeDown.Text.Trim(), _config.HotkeyVolumeDownKey);
            _config.HotkeyToggleScrcpyKey = ParseVirtualKey(TxtHotkeyToggleScrcpy.Text.Trim(), _config.HotkeyToggleScrcpyKey);

            // Modifier: use selected combobox item
            try
            {
                if (CmbHotkeyModifier.SelectedItem is System.Windows.Controls.ComboBoxItem cbi && cbi.Tag != null)
                {
                    if (int.TryParse(cbi.Tag.ToString()?.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out var mod))
                    {
                        _config.HotkeyModifier = mod;
                    }
                }
            }
            catch { }

            MusicConfigManager.Save(_config);
            (Application.Current as App)?.UpdateConfig(_config);
            _savedConfig = CloneConfig(_config);

            if (showConfirmation)
            {
                MessageBox.Show("Music presence settings saved.", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private static int GetTypicalBitrate(string codec)
        {
            return codec.ToLowerInvariant() switch
            {
                "opus" => 160,
                "aac" => 256,
                "flac" => 1000,
                "raw" => 0,
                _ => 320
            };
        }

        private static string BuildBitrateWarningMessage(string codec, int bitrateValue)
        {
            var guidance = codec.ToLowerInvariant() switch
            {
                "opus" => "Opus is typically transparent around 96-160 kbps for stereo music.",
                "aac" => "AAC is typically transparent around 128-256 kbps for stereo music.",
                "flac" => "FLAC is lossless; bitrate depends on content and is often 700-1100 kbps.",
                "raw" => "RAW is uncompressed PCM; bitrate depends on sample rate and channels.",
                _ => "Most encoders reach high quality well below 10000 kbps."
            };

            return $"The selected bitrate ({bitrateValue} kbps) is extremely high and usually unnecessary.\n\n" +
                   $"Encoder info: {guidance}\n\n" +
                   "Do you want to keep this value?";
        }

        private void BtnListCodecs_Click(object sender, RoutedEventArgs e)
        {
            _ = LoadScrcpyCodecsAsync();
        }

        private void LstAudioCodecs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateCodecDependentFields();
        }

        private void UpdateCodecDependentFields()
        {
            var codec = LstAudioCodecs.SelectedItem as string ?? "raw";
            bool isRaw = codec.Equals("raw", StringComparison.OrdinalIgnoreCase);
            PanelAudioBitrate.Visibility = isRaw ? Visibility.Collapsed : Visibility.Visible;
            PanelFlacCompression.Visibility = codec.Equals("flac", StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void UpdateSavedSnapshot()
        {
            _savedConfig = CloneConfig(_config);
        }

        private void RevertUnsavedChanges()
        {
            _config = CloneConfig(_savedConfig);
            (Application.Current as App)?.UpdateConfig(_config);
            InitializeAudioCodecUI();
            ApplyConfigToUI();
        }

        private bool HasUnsavedChanges()
        {
            var currentConfig = BuildConfigFromUi();
            return !AreConfigsEqual(currentConfig, _savedConfig);
        }

        private MusicConfig BuildConfigFromUi()
        {
            var config = CloneConfig(_config);

            config.SelectedDeviceUSB = TxtUsbSerial.Text.Trim();
            config.SelectedDeviceWiFi = TxtWifi.Text.Trim();
            config.SelectedDeviceName = TxtDeviceName.Text.Trim();
            config.MusicRemoteRoot = TxtRemoteRoot.Text.Trim();

            if (_appPackages.Count > 0)
            {
                config.AllowedApps = _appPackages
                    .Where(item => item.IsSelected)
                    .Select(item => item.PackageName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            else
            {
                config.AllowedApps = _config.AllowedApps?.ToList() ?? new List<string>();
            }

            if (config.AllowedApps.Count == 0)
            {
                config.AllowedApps.Add("in.krosbits.musicolet");
            }

            if (!_isInitializing && CmbUpdateInterval.SelectedIndex >= 0)
            {
                config.UpdateIntervalMode = (UpdateIntervalMode)(CmbUpdateInterval.SelectedIndex + 1);
            }

            config.DebugMode = ChkDebugMode.IsChecked == true;
            config.UseDarkMode = ChkDarkMode.IsChecked == true;
            config.OpenInTaskbar = ChkOpenInTaskbar.IsChecked == true;

            var selectedCodec = LstAudioCodecs.SelectedItem as string ?? "raw";
            config.ScrcpyAudioCodec = selectedCodec;

            if (selectedCodec.Equals("raw", StringComparison.OrdinalIgnoreCase))
            {
                config.ScrcpyAudioBitrate = string.Empty;
            }
            else
            {
                var bitrateText = TxtAudioBitrate.Text.Trim();
                if (string.IsNullOrEmpty(bitrateText))
                {
                    config.ScrcpyAudioBitrate = string.Empty;
                }
                else if (int.TryParse(bitrateText, out var bitrateValue))
                {
                    if (bitrateValue < 1)
                        bitrateValue = 1;

                    config.ScrcpyAudioBitrate = bitrateValue > 0 ? bitrateValue.ToString() : string.Empty;
                }
                else
                {
                    config.ScrcpyAudioBitrate = string.Empty;
                }
            }

            if (int.TryParse(TxtAudioBuffer.Text.Trim(), out var bufferValue) && bufferValue > 0)
            {
                config.ScrcpyAudioBuffer = Math.Max(1, bufferValue);
            }
            else
            {
                config.ScrcpyAudioBuffer = 50;
            }

            if (int.TryParse(TxtFlacCompressionLevel.Text.Trim(), out var flacLevel))
            {
                config.ScrcpyFlacCompressionLevel = Math.Clamp(flacLevel, 1, 8);
            }
            else
            {
                config.ScrcpyFlacCompressionLevel = 5;
            }

            if (int.TryParse(TxtPauseClearDelayMinutes.Text.Trim(), out var pauseDelay))
            {
                config.SmtcPauseClearDelayMinutes = Math.Max(0, pauseDelay);
            }
            else
            {
                config.SmtcPauseClearDelayMinutes = 3;
            }

            config.HotkeyVolumeUpKey = ParseVirtualKey(TxtHotkeyVolumeUp.Text.Trim(), _config.HotkeyVolumeUpKey);
            config.HotkeyVolumeDownKey = ParseVirtualKey(TxtHotkeyVolumeDown.Text.Trim(), _config.HotkeyVolumeDownKey);
            config.HotkeyToggleScrcpyKey = ParseVirtualKey(TxtHotkeyToggleScrcpy.Text.Trim(), _config.HotkeyToggleScrcpyKey);

            try
            {
                if (CmbHotkeyModifier.SelectedItem is System.Windows.Controls.ComboBoxItem cbi && cbi.Tag != null)
                {
                    if (int.TryParse(cbi.Tag.ToString()?.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out var mod))
                    {
                        config.HotkeyModifier = mod;
                    }
                }
            }
            catch { }

            return config;
        }

        private static bool AreConfigsEqual(MusicConfig left, MusicConfig right)
        {
            if (left == null || right == null) return false;

            bool PathsEqual(PathsConfig? a, PathsConfig? b)
            {
                if (a == null || b == null) return a == b;
                return string.Equals(a.Adb, b.Adb, StringComparison.Ordinal)
                    && string.Equals(a.FfmpegPath, b.FfmpegPath, StringComparison.Ordinal)
                    && string.Equals(a.Scrcpy, b.Scrcpy, StringComparison.Ordinal)
                    && string.Equals(a.CoverCachePath, b.CoverCachePath, StringComparison.Ordinal);
            }

            if (!PathsEqual(left.Paths, right.Paths)) return false;
            if (!string.Equals(left.SelectedDeviceUSB, right.SelectedDeviceUSB, StringComparison.Ordinal)) return false;
            if (!string.Equals(left.SelectedDeviceWiFi, right.SelectedDeviceWiFi, StringComparison.Ordinal)) return false;
            if (!string.Equals(left.SelectedDeviceName, right.SelectedDeviceName, StringComparison.Ordinal)) return false;
            if (!string.Equals(left.MusicRemoteRoot, right.MusicRemoteRoot, StringComparison.Ordinal)) return false;
            if (left.UpdateIntervalMode != right.UpdateIntervalMode) return false;
            if (left.DebugMode != right.DebugMode) return false;
            if (left.UseDarkMode != right.UseDarkMode) return false;
            if (left.OpenInTaskbar != right.OpenInTaskbar) return false;
            if (!string.Equals(left.ScrcpyAudioCodec, right.ScrcpyAudioCodec, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(left.ScrcpyAudioBitrate ?? string.Empty, right.ScrcpyAudioBitrate ?? string.Empty, StringComparison.Ordinal)) return false;
            if (left.ScrcpyAudioBuffer != right.ScrcpyAudioBuffer) return false;
            if (left.ScrcpyFlacCompressionLevel != right.ScrcpyFlacCompressionLevel) return false;
            if (left.SmtcPauseClearDelayMinutes != right.SmtcPauseClearDelayMinutes) return false;
            if (left.HotkeyVolumeUpKey != right.HotkeyVolumeUpKey) return false;
            if (left.HotkeyVolumeDownKey != right.HotkeyVolumeDownKey) return false;
            if (left.HotkeyToggleScrcpyKey != right.HotkeyToggleScrcpyKey) return false;
            if (left.HotkeyModifier != right.HotkeyModifier) return false;

            var allowedLeft = new HashSet<string>(left.AllowedApps ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var allowedRight = new HashSet<string>(right.AllowedApps ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            if (!allowedLeft.SetEquals(allowedRight)) return false;

            var codecsLeft = new HashSet<string>(left.ScrcpyAvailableAudioCodecs ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var codecsRight = new HashSet<string>(right.ScrcpyAvailableAudioCodecs ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            if (!codecsLeft.SetEquals(codecsRight)) return false;

            return true;
        }

        private static MusicConfig CloneConfig(MusicConfig source)
        {
            var paths = source.Paths ?? new PathsConfig();
            return new MusicConfig
            {
                Paths = new PathsConfig
                {
                    Adb = paths.Adb,
                    FfmpegPath = paths.FfmpegPath,
                    Scrcpy = paths.Scrcpy,
                    CoverCachePath = paths.CoverCachePath
                },
                SelectedDeviceUSB = source.SelectedDeviceUSB,
                SelectedDeviceWiFi = source.SelectedDeviceWiFi,
                SelectedDeviceName = source.SelectedDeviceName,
                MusicRemoteRoot = source.MusicRemoteRoot,
                AllowedApps = source.AllowedApps?.ToList() ?? new List<string>(),
                UpdateIntervalMode = source.UpdateIntervalMode,
                DebugMode = source.DebugMode,
                UseDarkMode = source.UseDarkMode,
                OpenInTaskbar = source.OpenInTaskbar,
                ScrcpyAudioCodec = source.ScrcpyAudioCodec,
                ScrcpyAudioBitrate = source.ScrcpyAudioBitrate ?? string.Empty,
                ScrcpyAudioBuffer = source.ScrcpyAudioBuffer,
                ScrcpyFlacCompressionLevel = source.ScrcpyFlacCompressionLevel,
                ScrcpyAvailableAudioCodecs = source.ScrcpyAvailableAudioCodecs?.ToList() ?? new List<string>(),
                SmtcPauseClearDelayMinutes = source.SmtcPauseClearDelayMinutes,
                HotkeyVolumeUpKey = source.HotkeyVolumeUpKey,
                HotkeyVolumeDownKey = source.HotkeyVolumeDownKey,
                HotkeyToggleScrcpyKey = source.HotkeyToggleScrcpyKey,
                HotkeyModifier = source.HotkeyModifier
            };
        }

        private async Task LoadScrcpyCodecsAsync()
        {
            if (_isLoadingCodecs)
                return;

            _isLoadingCodecs = true;
            TxtCodecStatus.Text = "Loading...";

            try
            {
                if (string.IsNullOrWhiteSpace(_config.Paths.Scrcpy) || !File.Exists(_config.Paths.Scrcpy))
                {
                    TxtCodecStatus.Text = "scrcpy.exe not found.";
                    return;
                }

                var device = await GetCurrentDeviceForAppsAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(device))
                {
                    await Dispatcher.InvokeAsync(() => TxtCodecStatus.Text = "No device connected.");
                    return;
                }

                Debugger.show("Listing scrcpy encoders...");
                var output = await Task.Run(() => RunScrcpyListEncoders(_config.Paths.Scrcpy, device)).ConfigureAwait(false);
                Debugger.show(string.IsNullOrWhiteSpace(output) ? "scrcpy encoder list returned no output." : "scrcpy encoder list received.");
                var codecs = ParseScrcpyAudioCodecs(output);

                await Dispatcher.InvokeAsync(() =>
                {
                    _audioCodecs.Clear();
                    foreach (var codec in codecs)
                    {
                        _audioCodecs.Add(codec);
                    }

                    _config.ScrcpyAvailableAudioCodecs = codecs.ToList();
                    MusicConfigManager.Save(_config);
                    UpdateSavedSnapshot();

                    SelectCodecFromConfig();
                    UpdateCodecDependentFields();
                    TxtCodecStatus.Text = $"{_audioCodecs.Count} codecs";
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    TxtCodecStatus.Text = "Failed to list codecs.";
                    MessageBox.Show($"Failed to list scrcpy codecs: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
            finally
            {
                _isLoadingCodecs = false;
            }
        }

        private static string RunScrcpyListEncoders(string scrcpyPath, string device)
        {
            var psi = new ProcessStartInfo
            {
                FileName = scrcpyPath,
                Arguments = $"-s {device} --list-encoders",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            try
            {
                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    return string.Empty;
                }

                string output = proc.StandardOutput.ReadToEnd();
                string error = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                return output + Environment.NewLine + error;
            }
            catch (Exception ex)
            {
                Debugger.show("scrcpy list encoders failed: " + ex.Message);
                return string.Empty;
            }
        }

        private static List<string> ParseScrcpyAudioCodecs(string output)
        {
            var codecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "raw"
            };

            if (!string.IsNullOrWhiteSpace(output))
            {
                foreach (var line in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!line.Contains("--audio-codec=", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var match = Regex.Match(line, "--audio-codec=([a-z0-9]+)", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        codecs.Add(match.Groups[1].Value.ToLowerInvariant());
                    }
                }
            }

            Debugger.show($"Parsed scrcpy audio codecs: {string.Join(", ", codecs.OrderBy(c => c))}");

            return codecs
                .OrderBy(c => c.Equals("raw", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(c => c)
                .ToList();
        }

        private void Expander_Expanded(object sender, RoutedEventArgs e)
        {
            if (sender is not Expander expander)
                return;

            if (expander.Content is not FrameworkElement content)
                return;

            content.RenderTransformOrigin = new Point(0.5, 0);
            if (content.RenderTransform is not ScaleTransform scaleTransform)
            {
                scaleTransform = new ScaleTransform(1, 0.9);
                content.RenderTransform = scaleTransform;
            }

            content.Opacity = 0;
            scaleTransform.ScaleY = 0.9;

            var duration = TimeSpan.FromMilliseconds(200);
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

            var scaleAnimation = new DoubleAnimation(0.9, 1, duration) { EasingFunction = easing };
            var opacityAnimation = new DoubleAnimation(0, 1, duration) { EasingFunction = easing };

            scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation);
            content.BeginAnimation(OpacityProperty, opacityAnimation);
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