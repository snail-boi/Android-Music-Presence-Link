using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Threading.Tasks;

namespace musicpresense
{
    public partial class OnboardingWindow : Window
    {
        private readonly MusicConfig _workingConfig;
        private readonly ObservableCollection<string> _remoteRoots = new();
        private readonly ObservableCollection<AppPackageItem> _appPackages = new();
        private readonly string[] _stepTitles =
        {
            "Welcome",
            "Enable wireless debugging",
            "Connect your phone",
            "Music and lyrics folders",
            "Allowed apps",
            "Hotkeys",
            "Startup options"
        };

        private readonly string[] _stepSubtitles =
        {
            "Welcome to Android Music Presence. Let's get you set up.",
            "Your phone needs Wireless Debugging turned on so we can talk to it.",
            "Plug in your phone, then click Auto Detect and we'll figure out the rest.",
            "Tell us where your music lives so we can fetch cover art and lyrics.",
            "Choose which Android apps may share what they're playing.",
            "Set the keyboard shortcuts for volume, lyrics and more.",
            "Decide how the app launches. You can change everything later in Settings."
        };

        private readonly ObservableCollection<SidebarStepItem> _sidebarSteps = new();

        private int _currentStep;
        private bool _isAutoGathering;
        private bool _isLoadingApps;
        private bool _isRecordingHotkey;
        private Action<int>? _onHotkeyRecorded;

        public MusicConfig UpdatedConfig => _workingConfig;

        public OnboardingWindow(MusicConfig currentConfig)
        {
            InitializeComponent();

            _workingConfig = CloneConfig(currentConfig);

            LstRemoteRoots.ItemsSource = _remoteRoots;
            LstAllowedApps.ItemsSource = _appPackages;
            LstSidebarSteps.ItemsSource = _sidebarSteps;

            foreach (var root in GetNormalizedRemoteRoots(_workingConfig))
            {
                _remoteRoots.Add(root);
            }

            TxtUsbSerial.Text = _workingConfig.SelectedDeviceUSB;
            SetWifiDisplayFromConfig();
            TxtDeviceName.Text = _workingConfig.SelectedDeviceName;
            SelectWifiModeFromConfig();
            UpdatePairButtonVisibility();
            TxtLyricsFolderOverride.Text = _workingConfig.LyricsSearchFolderOverride ?? string.Empty;

            ChkOpenInTaskbar.IsChecked = _workingConfig.OpenInTaskbar;
            ChkStartWithWindows.IsChecked = _workingConfig.StartWithWindows;

            // Default-view radio: pick whichever the saved config indicates, defaulting
            // to Settings view for fresh installs (ShowMediaPlayerWindow == false).
            if (_workingConfig.ShowMediaPlayerWindow)
            {
                RbViewMediaPlayer.IsChecked = true;
            }
            else
            {
                RbViewSettings.IsChecked = true;
            }

            TxtHotkeyVolumeUp.Text = VirtualKeyToDisplayName(_workingConfig.HotkeyVolumeUpKey);
            TxtHotkeyVolumeDown.Text = VirtualKeyToDisplayName(_workingConfig.HotkeyVolumeDownKey);
            TxtHotkeyToggleScrcpy.Text = VirtualKeyToDisplayName(_workingConfig.HotkeyToggleScrcpyKey);
            TxtHotkeyToggleLyricsOverlay.Text = VirtualKeyToDisplayName(_workingConfig.HotkeyToggleLyricsOverlayKey);
            TxtHotkeyCopyTrackInfo.Text = VirtualKeyToDisplayName(_workingConfig.HotkeyCopyTrackInfoKey);
            TxtHotkeyAudioQuality.Text = VirtualKeyToDisplayName(_workingConfig.HotkeyAudioQualityKey);
            TxtHotkeyAudioQuality.Text = VirtualKeyToDisplayName(_workingConfig.HotkeyAudioQualityKey);

            SelectModifier(_workingConfig.HotkeyModifier);
            _ = LoadInstalledAppsAsync();
            UpdateStepUI();
        }

        private void BtnWifiModeToggle_Click(object sender, RoutedEventArgs e)
        {
            var nextMode = GetSelectedWifiMode() == WirelessMode.WirelessDebugging
                ? WirelessMode.TcpIp
                : WirelessMode.WirelessDebugging;

            SetWifiMode(nextMode, updateConfig: true);
        }

        private void SetWifiMode(WirelessMode mode, bool updateConfig)
        {
            if (BtnWifiModeToggle != null)
            {
                BtnWifiModeToggle.Tag = mode.ToString();
                BtnWifiModeToggle.Content = mode == WirelessMode.WirelessDebugging
                    ? "Wi-Fi mode: Wireless Debugging"
                    : "Wi-Fi mode: adb tcpip";
            }

            if (updateConfig)
            {
                _workingConfig.WifiMode = mode;
            }

            UpdatePairButtonVisibility();
        }

        private void UpdateStepUI()
        {
            TxtStepTitle.Text = _stepTitles[_currentStep];
            TxtStepSubtitle.Text = _stepSubtitles[_currentStep];
            TxtStepCounter.Text = $"Step {_currentStep + 1} of {_stepTitles.Length}";

            PanelWelcome.Visibility = _currentStep == 0 ? Visibility.Visible : Visibility.Collapsed;
            PanelUsbDebugging.Visibility = _currentStep == 1 ? Visibility.Visible : Visibility.Collapsed;
            PanelDevice.Visibility = _currentStep == 2 ? Visibility.Visible : Visibility.Collapsed;
            PanelFolders.Visibility = _currentStep == 3 ? Visibility.Visible : Visibility.Collapsed;
            PanelApps.Visibility = _currentStep == 4 ? Visibility.Visible : Visibility.Collapsed;
            PanelHotkeys.Visibility = _currentStep == 5 ? Visibility.Visible : Visibility.Collapsed;
            PanelStartup.Visibility = _currentStep == 6 ? Visibility.Visible : Visibility.Collapsed;

            BtnBack.IsEnabled = _currentStep > 0;
            BtnNext.Visibility = _currentStep < _stepTitles.Length - 1 ? Visibility.Visible : Visibility.Collapsed;
            BtnFinish.Visibility = _currentStep == _stepTitles.Length - 1 ? Visibility.Visible : Visibility.Collapsed;

            // Skip doesn't make sense on the welcome step. On the last step it acts as Skip & Finish.
            if (_currentStep == 0)
            {
                BtnSkip.Visibility = Visibility.Collapsed;
            }
            else
            {
                BtnSkip.Visibility = Visibility.Visible;
                BtnSkip.Content = _currentStep == _stepTitles.Length - 1 ? "Skip & Finish" : "Skip";
            }

            BtnNext.Content = _currentStep == 0 ? "Get Started  \u25B6" : "Next  \u25B6";

            RebuildSidebar();
        }

        private void RebuildSidebar()
        {
            _sidebarSteps.Clear();
            for (int i = 0; i < _stepTitles.Length; i++)
            {
                bool isCurrent = i == _currentStep;
                bool isDone = i < _currentStep;

                _sidebarSteps.Add(new SidebarStepItem
                {
                    Title = _stepTitles[i],
                    NumberText = isDone ? "\u2713" : (i + 1).ToString(),
                    NumberBackground = isCurrent ? "#FFFFFF" : (isDone ? "#66FFFFFF" : "#33FFFFFF"),
                    NumberForeground = isCurrent ? "#2D6CDF" : "#FFFFFF",
                    RowBackground = isCurrent ? "#33FFFFFF" : "#00000000",
                    TitleOpacity = isCurrent ? 1.0 : (isDone ? 0.85 : 0.7),
                    TitleWeight = isCurrent ? "SemiBold" : "Normal"
                });
            }
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            SaveCurrentStepValues();
            if (_currentStep > 0)
            {
                _currentStep--;
                UpdateStepUI();
            }
        }

        private void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            SaveCurrentStepValues();
            if (_currentStep < _stepTitles.Length - 1)
            {
                _currentStep++;
                UpdateStepUI();
            }
        }

        private void BtnSkip_Click(object sender, RoutedEventArgs e)
        {
            if (_currentStep == _stepTitles.Length - 1)
            {
                BtnFinish_Click(sender, e);
                return;
            }

            BtnNext_Click(sender, e);
        }

        private void BtnFinish_Click(object sender, RoutedEventArgs e)
        {
            SaveCurrentStepValues();
            EnsureEligibleAppsFallback();
            _workingConfig.OnboardingCompleted = true;
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void SaveCurrentStepValues()
        {
            _workingConfig.SelectedDeviceUSB = TxtUsbSerial.Text.Trim();
            _workingConfig.SelectedDeviceWiFi = TxtWifi.Text.Trim();
            _workingConfig.SelectedDeviceName = TxtDeviceName.Text.Trim();
            _workingConfig.WifiMode = GetSelectedWifiMode();
            // WifiMdnsServiceName is set by BtnPairWireless_Click on successful pair.

            _workingConfig.MusicRemoteRoots = _remoteRoots
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            _workingConfig.MusicRemoteRoot = _workingConfig.MusicRemoteRoots.FirstOrDefault() ?? string.Empty;

            _workingConfig.LyricsSearchFolderOverride = TxtLyricsFolderOverride.Text.Trim();

            _workingConfig.OpenInTaskbar = ChkOpenInTaskbar.IsChecked == true;
            _workingConfig.StartWithWindows = ChkStartWithWindows.IsChecked == true;
            // Default view choice: media player view if explicitly selected, otherwise settings.
            _workingConfig.ShowMediaPlayerWindow = RbViewMediaPlayer.IsChecked == true;

            _workingConfig.HotkeyVolumeUpKey = ParseVirtualKey(TxtHotkeyVolumeUp.Text.Trim(), _workingConfig.HotkeyVolumeUpKey);
            _workingConfig.HotkeyVolumeDownKey = ParseVirtualKey(TxtHotkeyVolumeDown.Text.Trim(), _workingConfig.HotkeyVolumeDownKey);
            _workingConfig.HotkeyToggleScrcpyKey = ParseVirtualKey(TxtHotkeyToggleScrcpy.Text.Trim(), _workingConfig.HotkeyToggleScrcpyKey);
            _workingConfig.HotkeyToggleLyricsOverlayKey = ParseVirtualKey(TxtHotkeyToggleLyricsOverlay.Text.Trim(), _workingConfig.HotkeyToggleLyricsOverlayKey);
            _workingConfig.HotkeyCopyTrackInfoKey = ParseVirtualKey(TxtHotkeyCopyTrackInfo.Text.Trim(), _workingConfig.HotkeyCopyTrackInfoKey);
            _workingConfig.HotkeyAudioQualityKey = ParseVirtualKey(TxtHotkeyAudioQuality.Text.Trim(), _workingConfig.HotkeyAudioQualityKey);

            try
            {
                if (CmbHotkeyModifier.SelectedItem is ComboBoxItem cbi && cbi.Tag != null)
                {
                    if (int.TryParse(cbi.Tag.ToString()?.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out var mod))
                    {
                        _workingConfig.HotkeyModifier = mod;
                    }
                }
            }
            catch { }

            if (_appPackages.Count > 0)
            {
                _workingConfig.EligibleApps = _appPackages
                    .Select(item => new EligibleAppConfig
                    {
                        PackageName = item.PackageName,
                        PresenceMode = item.PresenceMode,
                        IsEnabled = item.PresenceMode != PresenceMode.Off,
                        EnableCoverSearch = item.PresenceMode != PresenceMode.Off && item.EnableCoverSearch
                    })
                    .Where(item => !string.IsNullOrWhiteSpace(item.PackageName))
                    .GroupBy(item => item.PackageName, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new EligibleAppConfig
                    {
                        PackageName = g.Key,
                        PresenceMode = g.Max(x => (int)x.PresenceMode) switch { 2 => PresenceMode.Full, 1 => PresenceMode.Half, _ => PresenceMode.Off },
                        IsEnabled = g.Any(x => x.PresenceMode != PresenceMode.Off || x.IsEnabled),
                        EnableCoverSearch = g.Any(x => x.EnableCoverSearch)
                    })
                    .ToList();

                _workingConfig.AllowedApps = _workingConfig.EligibleApps
                    .Where(a => a.PresenceMode != PresenceMode.Off || a.IsEnabled)
                    .Select(a => a.PackageName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        // -------------------------------------------------------------------
        // Wireless mode helpers (TcpIp vs WirelessDebugging)
        // -------------------------------------------------------------------
        private WirelessMode GetSelectedWifiMode()
        {
            if (BtnWifiModeToggle?.Tag is string tag
                && Enum.TryParse<WirelessMode>(tag, out var mode))
            {
                return mode;
            }

            return _workingConfig?.WifiMode ?? WirelessMode.TcpIp;
        }

        private void SelectWifiModeFromConfig()
        {
            SetWifiMode(_workingConfig?.WifiMode ?? WirelessMode.TcpIp, updateConfig: false);
        }

        private void UpdatePairButtonVisibility()
        {
            if (BtnAutoGather != null)
            {
                BtnAutoGather.Content = GetSelectedWifiMode() == WirelessMode.WirelessDebugging
                    ? "Pair phone"
                    : "Auto Detect USB";
            }

            UpdateWifiFieldPresentation();
        }

        private void UpdateWifiFieldPresentation()
        {
            bool isWd = GetSelectedWifiMode() == WirelessMode.WirelessDebugging;

            if (FindName("LblWifiAddress") is TextBlock wifiLabel)
                wifiLabel.Text = isWd ? "mDNS" : "Wi-Fi Address";

            if (FindName("LblWifiAddressHelp") is TextBlock wifiHelp)
                wifiHelp.Text = isWd
                    ? "mDNS service name discovered by pairing." 
                    : "Optional, format ip:port.";

            if (TxtWifi != null && !isWd)
            {
                TxtWifi.Text = _workingConfig.SelectedDeviceWiFi;
            }

            if (TxtWifi != null && isWd)
            {
                TxtWifi.Text = _workingConfig.WifiMdnsServiceName ?? string.Empty;
            }
        }

        private void SetWifiDisplayFromConfig()
        {
            if (TxtWifi == null) return;

            if (GetSelectedWifiMode() == WirelessMode.WirelessDebugging)
            {
                TxtWifi.Text = _workingConfig.WifiMdnsServiceName ?? string.Empty;
            }
            else
            {
                TxtWifi.Text = _workingConfig.SelectedDeviceWiFi;
            }
        }

        private void CmbWifiMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdatePairButtonVisibility();
        }

        private async void BtnAutoGatherOrPair_Click(object sender, RoutedEventArgs e)
        {
            if (GetSelectedWifiMode() == WirelessMode.WirelessDebugging)
            {
                await BtnPairWireless_ClickAsync();
                return;
            }

            BtnAutoGather_Click(sender, e);
        }

        private async Task BtnPairWireless_ClickAsync()
        {
            var dlg = new WifiPairDialog();
            if (IsLoaded && IsVisible)
                dlg.Owner = this;
            if (dlg.ShowDialog() != true) return;

            string ipPort = string.Empty;
            if (!string.IsNullOrWhiteSpace(dlg.ServiceName))
            {
                ipPort = await ReconnectViaMdnsWithRetryAsync(dlg.ServiceName);
            }

            if (!string.IsNullOrWhiteSpace(dlg.ServiceName))
            {
                _workingConfig.WifiMdnsServiceName = dlg.ServiceName;
                TxtWifi.Text = dlg.ServiceName;
            }
            if (!string.IsNullOrWhiteSpace(ipPort))
            {
                _workingConfig.SelectedDeviceWiFi = ipPort;
                _workingConfig.IsWifiEnabled = true;
            }

            string usbSerial = string.Empty;
            if (!string.IsNullOrWhiteSpace(dlg.ServiceName))
            {
                usbSerial = await GetWirelessDebuggingSerialAsync(dlg.ServiceName, ipPort).ConfigureAwait(true);
            }

            if (!string.IsNullOrWhiteSpace(usbSerial))
            {
                TxtUsbSerial.Text = usbSerial;
                _workingConfig.SelectedDeviceUSB = usbSerial;
            }

            var nameDialog = new NameInputDialogue("Enter a name for this device:", "Device Name");
            if (IsLoaded && IsVisible)
            {
                nameDialog.Owner = this;
            }

            if (nameDialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(nameDialog.InputText))
            {
                TxtDeviceName.Text = nameDialog.InputText.Trim();
                _workingConfig.SelectedDeviceName = TxtDeviceName.Text.Trim();
            }

            MusicConfigManager.Save(_workingConfig);
            (Application.Current as App)?.UpdateConfig(_workingConfig);
        }

        private async void BtnPairWireless_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new WifiPairDialog();
            if (IsLoaded && IsVisible)
                dlg.Owner = this;
            if (dlg.ShowDialog() != true) return;

            // Pairing succeeded. Run an mDNS lookup to capture the current
            // ip:port (the connection port differs from the pair port).
            string ipPort = string.Empty;
            if (!string.IsNullOrWhiteSpace(dlg.ServiceName))
            {
                ipPort = await ReconnectViaMdnsWithRetryAsync(dlg.ServiceName);
            }

            if (!string.IsNullOrWhiteSpace(dlg.ServiceName))
            {
                _workingConfig.WifiMdnsServiceName = dlg.ServiceName;
                TxtWifi.Text = dlg.ServiceName;
            }
            if (!string.IsNullOrWhiteSpace(ipPort))
            {
                _workingConfig.SelectedDeviceWiFi = ipPort;
                _workingConfig.IsWifiEnabled = true;
            }

            // Prefer the Wireless Debugging connection itself to read the real
            // hardware serial, rather than trying to infer it from whatever USB
            // device happens to be attached right now.
            string usbSerial = string.Empty;
            if (!string.IsNullOrWhiteSpace(dlg.ServiceName))
            {
                usbSerial = await GetWirelessDebuggingSerialAsync(dlg.ServiceName, ipPort).ConfigureAwait(true);
            }

            if (!string.IsNullOrWhiteSpace(usbSerial))
            {
                TxtUsbSerial.Text = usbSerial;
                _workingConfig.SelectedDeviceUSB = usbSerial;
            }

            var nameDialog = new NameInputDialogue("Enter a name for this device:", "Device Name");
            if (IsLoaded && IsVisible)
            {
                nameDialog.Owner = this;
            }

            if (nameDialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(nameDialog.InputText))
            {
                TxtDeviceName.Text = nameDialog.InputText.Trim();
                _workingConfig.SelectedDeviceName = TxtDeviceName.Text.Trim();
            }

            MusicConfigManager.Save(_workingConfig);
            (Application.Current as App)?.UpdateConfig(_workingConfig);

            MessageBox.Show(
                string.IsNullOrWhiteSpace(ipPort)
                    ? "Pairing succeeded."
                    : $"Paired and connected at {ipPort}.",
                "Pairing Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private static async Task<string> ReconnectViaMdnsWithRetryAsync(string serviceName)
        {
            if (string.IsNullOrWhiteSpace(serviceName))
                return string.Empty;

            for (int attempt = 0; attempt < 8; attempt++)
            {
                var ipPort = await WirelessDebuggingHelper.ReconnectViaMdnsAsync(serviceName).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(ipPort))
                    return ipPort;

                await Task.Delay(500).ConfigureAwait(false);
            }

            return string.Empty;
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
         try
            {
                await AdbHelper.RunAdbAsync("disconnect");
            }
            catch
            {
                // Ignore disconnect failures and continue with USB detection.
            }

            var usbSerial = await GetConnectedUsbDeviceAsync();
            if (string.IsNullOrWhiteSpace(usbSerial))
            {
                MessageBox.Show("Please connect your device via USB first.", "USB Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            TxtUsbSerial.Text = usbSerial;
            var port = 0;
            var ip = "none";

            // In WirelessDebugging mode, the wifi address comes from the pair
            // flow (ip:random_port discovered via mDNS), not from
            // service.adb.tcp.port. Don't overwrite it here.
            if (_workingConfig.WifiMode == WirelessMode.WirelessDebugging)
            {
                MessageBox.Show(
                    "Auto-detect skipped Wi-Fi setup because Wireless Debugging mode is selected. "
                    + "Use the 'Pair phone' button to set up wireless.",
                    "Wireless Debugging Mode",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else if (MessageBox.Show("do you want to enable WiFi", "May be incompatible with certain networks", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.No)
            {
                _workingConfig.IsWifiEnabled = false;
            }
            else
            {
                _workingConfig.IsWifiEnabled = true;
                port = await GetWifiPortAsync(usbSerial);
                ip = await GetDeviceWifiIpAsync(usbSerial);
            }

            if (_workingConfig.WifiMode != WirelessMode.WirelessDebugging)
            {
                if (!string.IsNullOrWhiteSpace(ip))
                {
                    TxtWifi.Text = _workingConfig.IsWifiEnabled ? $"{ip}:{port}" : string.Empty;
                }
                else
                {
                    MessageBox.Show("Could not read the device Wi-Fi IP address.", "Wi-Fi Info", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            var nameDialog = new NameInputDialogue("Enter a name for this device:", "Device Name")
            {
                Owner = this
            };

            if (nameDialog.ShowDialog() != true)
                return;

            var deviceName = nameDialog.InputText;
            if (!string.IsNullOrWhiteSpace(deviceName))
            {
                TxtDeviceName.Text = deviceName.Trim();
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

            var picker = RemoteFolderPicker.Create(device, this);

            if (picker.ShowDialog() == true)
            {
                var selectedFolder = picker.SelectedFolder?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(selectedFolder))
                    return;

                if (_remoteRoots.Any(p => string.Equals(p, selectedFolder, StringComparison.OrdinalIgnoreCase)))
                    return;

                _remoteRoots.Add(selectedFolder);
            }
        }

        private void BtnRemoveRemoteRoot_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button)
                return;

            if (button.Tag is not string root || string.IsNullOrWhiteSpace(root))
                return;

            var existing = _remoteRoots.FirstOrDefault(p => string.Equals(p, root, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                _remoteRoots.Remove(existing);
            }
        }

        private async void BtnBrowseLyricsFolder_Click(object sender, RoutedEventArgs e)
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
                var selectedFolder = picker.SelectedFolder?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(selectedFolder))
                {
                    TxtLyricsFolderOverride.Text = selectedFolder;
                }
            }
        }

        private void BtnRefreshApps_Click(object sender, RoutedEventArgs e)
        {
            _ = LoadInstalledAppsAsync();
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

                var eligible = (_workingConfig.EligibleApps ?? new List<EligibleAppConfig>())
                    .Where(a => !string.IsNullOrWhiteSpace(a.PackageName))
                    .GroupBy(a => a.PackageName.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        g => g.Key,
                        g => new EligibleAppConfig
                        {
                            PackageName = g.Key,
                            PresenceMode = g.Max(x => (int)x.PresenceMode) switch { 2 => PresenceMode.Full, 1 => PresenceMode.Half, _ => PresenceMode.Off },
                            IsEnabled = g.Any(x => x.IsEnabled),
                            EnableCoverSearch = g.Any(x => x.EnableCoverSearch)
                        },
                        StringComparer.OrdinalIgnoreCase);

                foreach (var cfgPkg in eligible.Keys)
                {
                    if (!packages.Contains(cfgPkg, StringComparer.OrdinalIgnoreCase))
                    {
                        packages.Add(cfgPkg);
                    }
                }

                packages = packages.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p).ToList();

                await Dispatcher.InvokeAsync(() =>
                {
                    _appPackages.Clear();
                    foreach (var pkg in packages)
                    {
                        if (eligible.TryGetValue(pkg, out var appConfig))
                        {
                            _appPackages.Add(new AppPackageItem(pkg, appConfig.PresenceMode, appConfig.EnableCoverSearch));
                        }
                        else
                        {
                            _appPackages.Add(new AppPackageItem(pkg, PresenceMode.Off, false));
                        }
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
            var devices = await AdbHelper.RunAdbCaptureAsync("devices").ConfigureAwait(false);
            var deviceList = devices.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            bool IsDeviceConnected(string id) => deviceList.Any(l => l.StartsWith(id) && l.EndsWith("device"));

            bool IsWirelessSerial(string serial)
            {
                if (string.IsNullOrWhiteSpace(serial))
                    return false;

                return serial.Contains(':')
                    || serial.StartsWith("adb-", StringComparison.OrdinalIgnoreCase)
                    || serial.IndexOf("_adb-tls", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            string FindConnectedWirelessSerial()
            {
                foreach (var entry in deviceList)
                {
                    if (!entry.EndsWith("device", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var serial = entry.Split('\t', ' ').FirstOrDefault();
                    if (IsWirelessSerial(serial))
                        return serial ?? string.Empty;
                }

                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(_workingConfig.SelectedDeviceUSB) && IsDeviceConnected(_workingConfig.SelectedDeviceUSB))
            {
                return _workingConfig.SelectedDeviceUSB;
            }

            if (_workingConfig.WifiMode == WirelessMode.WirelessDebugging && !string.IsNullOrWhiteSpace(_workingConfig.WifiMdnsServiceName))
            {
                var ipPort = await WirelessDebuggingHelper.ReconnectViaMdnsAsync(_workingConfig.WifiMdnsServiceName).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(ipPort))
                {
                    devices = await AdbHelper.RunAdbCaptureAsync("devices").ConfigureAwait(false);
                    deviceList = devices.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                    var liveWireless = FindConnectedWirelessSerial();
                    if (!string.IsNullOrWhiteSpace(liveWireless))
                        return liveWireless;

                    if (IsDeviceConnected(ipPort))
                        return ipPort;
                }

                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(_workingConfig.SelectedDeviceWiFi) && _workingConfig.SelectedDeviceWiFi != "None")
            {
                if (!IsDeviceConnected(_workingConfig.SelectedDeviceWiFi))
                {
                    await AdbHelper.RunAdbCaptureAsync($"connect {_workingConfig.SelectedDeviceWiFi}").ConfigureAwait(false);
                    devices = await AdbHelper.RunAdbCaptureAsync("devices").ConfigureAwait(false);
                    deviceList = devices.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                }

                if (IsDeviceConnected(_workingConfig.SelectedDeviceWiFi))
                    return _workingConfig.SelectedDeviceWiFi;
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

        private static async Task<string> GetWirelessDebuggingSerialAsync(string serviceName, string ipPort)
        {
            if (string.IsNullOrWhiteSpace(serviceName) && string.IsNullOrWhiteSpace(ipPort))
                return string.Empty;

            for (int attempt = 0; attempt < 8; attempt++)
            {
                if (!string.IsNullOrWhiteSpace(serviceName))
                {
                    var connectedIpPort = await WirelessDebuggingHelper.ReconnectViaMdnsAsync(serviceName).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(connectedIpPort))
                    {
                        var devices = await AdbHelper.RunAdbCaptureAsync("devices").ConfigureAwait(false);
                        var deviceList = devices.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                        foreach (var entry in deviceList)
                        {
                            if (!entry.EndsWith("device", StringComparison.OrdinalIgnoreCase))
                                continue;

                            var serial = entry.Split('\t', ' ').FirstOrDefault();
                            if (string.IsNullOrWhiteSpace(serial))
                                continue;

                            if (serial.Contains(':') || serial.StartsWith("adb-", StringComparison.OrdinalIgnoreCase) || serial.IndexOf("_adb-tls", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                var liveSerial = await GetDeviceSerialAsync(serial).ConfigureAwait(false);
                                if (!string.IsNullOrWhiteSpace(liveSerial))
                                    return liveSerial;
                            }
                        }

                        var serialFromIpPort = await GetDeviceSerialAsync(connectedIpPort).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(serialFromIpPort))
                            return serialFromIpPort;
                    }
                }

                if (!string.IsNullOrWhiteSpace(ipPort))
                {
                    var serial = await GetDeviceSerialAsync(ipPort).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(serial))
                        return serial;
                }

                await Task.Delay(500).ConfigureAwait(false);
            }

            return string.Empty;
        }

        private static async Task<string> GetDeviceSerialAsync(string device)
        {
            if (string.IsNullOrWhiteSpace(device))
                return string.Empty;

            var serial = await AdbHelper.RunAdbCaptureAsync($"-s {device} shell getprop ro.serialno");
            serial = serial.Trim();
            if (!string.IsNullOrWhiteSpace(serial))
                return serial;

            serial = await AdbHelper.RunAdbCaptureAsync($"-s {device} shell getprop ro.boot.serialno");
            return serial.Trim();
        }

        private static async Task<string> GetDeviceSerialWithRetryAsync(string device)
        {
            if (string.IsNullOrWhiteSpace(device))
                return string.Empty;

            for (int attempt = 0; attempt < 8; attempt++)
            {
                var serial = await GetDeviceSerialAsync(device).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(serial))
                    return serial;

                await Task.Delay(500).ConfigureAwait(false);
            }

            return string.Empty;
        }

        private static List<string> GetNormalizedRemoteRoots(MusicConfig config)
        {
            var roots = (config.MusicRemoteRoots ?? new List<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (roots.Count == 0 && !string.IsNullOrWhiteSpace(config.MusicRemoteRoot))
            {
                roots.Add(config.MusicRemoteRoot.Trim());
            }

            return roots;
        }

        private void EnsureEligibleAppsFallback()
        {
            if (!_workingConfig.EligibleApps.Any(a => a.PresenceMode != PresenceMode.Off || a.IsEnabled))
            {
                _workingConfig.EligibleApps.Add(new EligibleAppConfig
                {
                    PackageName = "in.krosbits.musicolet",
                    PresenceMode = PresenceMode.Full,
                    IsEnabled = true,
                    EnableCoverSearch = true
                });
            }

            _workingConfig.AllowedApps = _workingConfig.EligibleApps
                .Where(a => a.PresenceMode != PresenceMode.Off || a.IsEnabled)
                .Select(a => a.PackageName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void SelectModifier(int modifier)
        {
            foreach (var item in CmbHotkeyModifier.Items)
            {
                if (item is ComboBoxItem cbi && cbi.Tag != null)
                {
                    if (int.TryParse(cbi.Tag.ToString()?.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out var mod) && mod == modifier)
                    {
                        CmbHotkeyModifier.SelectedItem = cbi;
                        return;
                    }
                }
            }

            CmbHotkeyModifier.SelectedIndex = 0;
        }

        private void BtnRecordHotkeyVolumeUp_Click(object sender, RoutedEventArgs e)
        {
            StartRecordingHotkey(k => TxtHotkeyVolumeUp.Text = VirtualKeyToDisplayName(k));
        }

        private void BtnRecordHotkeyVolumeDown_Click(object sender, RoutedEventArgs e)
        {
            StartRecordingHotkey(k => TxtHotkeyVolumeDown.Text = VirtualKeyToDisplayName(k));
        }

        private void BtnRecordHotkeyToggleScrcpy_Click(object sender, RoutedEventArgs e)
        {
            StartRecordingHotkey(k => TxtHotkeyToggleScrcpy.Text = VirtualKeyToDisplayName(k));
        }

        private void BtnRecordHotkeyToggleLyricsOverlay_Click(object sender, RoutedEventArgs e)
        {
            StartRecordingHotkey(k => TxtHotkeyToggleLyricsOverlay.Text = VirtualKeyToDisplayName(k));
        }

        private void BtnRecordHotkeyCopyTrackInfo_Click(object sender, RoutedEventArgs e)
        {
            StartRecordingHotkey(k => TxtHotkeyCopyTrackInfo.Text = VirtualKeyToDisplayName(k));
        }

        private void BtnRecordHotkeyAudioQuality_Click(object sender, RoutedEventArgs e)
        {
            StartRecordingHotkey(k => TxtHotkeyAudioQuality.Text = VirtualKeyToDisplayName(k));
        }

        private void BtnPresenceMode_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is AppPackageItem item)
                item.CyclePresenceMode();
        }

        private void BtnCover_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is AppPackageItem item)
                item.ToggleCover();
        }

        private void StartRecordingHotkey(Action<int> onRecorded)
        {
            if (_isRecordingHotkey)
                return;

            _isRecordingHotkey = true;
            _onHotkeyRecorded = onRecorded;
            Title = "Press a key to record hotkey (Esc to cancel)...";
            Focus();
            PreviewKeyDown += Recording_PreviewKeyDown;
            Deactivated += Recording_Deactivated;
        }

        private void StopRecordingHotkey()
        {
            if (!_isRecordingHotkey)
                return;

            _isRecordingHotkey = false;
            _onHotkeyRecorded = null;
            Title = "Welcome to Android Music Presence";
            PreviewKeyDown -= Recording_PreviewKeyDown;
            Deactivated -= Recording_Deactivated;
        }

        private void Recording_Deactivated(object? sender, EventArgs e)
        {
            StopRecordingHotkey();
        }

        private void Recording_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                if (!_isRecordingHotkey) return;

                e.Handled = true;

                if (e.Key == Key.Escape)
                {
                    StopRecordingHotkey();
                    return;
                }

                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                var vk = KeyInterop.VirtualKeyFromKey(key) & 0xFF;
                _onHotkeyRecorded?.Invoke(vk);
                StopRecordingHotkey();
            }
            catch
            {
                StopRecordingHotkey();
            }
        }

        private static string VirtualKeyToDisplayName(int vk)
        {
            if (vk >= 0x41 && vk <= 0x5A)
                return ((char)vk).ToString();

            if (vk >= 0x30 && vk <= 0x39)
                return ((char)vk).ToString();

            if (vk >= 0x70 && vk <= 0x87)
                return "F" + (vk - 0x6F).ToString();

            var map = new Dictionary<int, string>
            {
                { 0xAF, "VOLUME_UP" },
                { 0xAE, "VOLUME_DOWN" },
                { 0xAD, "VOLUME_MUTE" },
                { 0xB3, "MEDIA_PLAY_PAUSE" },
                { 0x1B, "ESC" },
                { 0x0D, "ENTER" },
                { 0x20, "SPACE" }
            };

            if (map.TryGetValue(vk, out var name))
                return name;

            return $"VK_0x{vk:X2}";
        }

        private static int ParseVirtualKey(string input, int fallback)
        {
            if (string.IsNullOrWhiteSpace(input)) return fallback;
            input = input.Trim();

            if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(input.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out var v))
                    return v & 0xFF;
                return fallback;
            }

            if (input.StartsWith("VK_0X", StringComparison.OrdinalIgnoreCase) || input.StartsWith("VK_0x", StringComparison.OrdinalIgnoreCase))
            {
                var part = input.Substring(5);
                if (int.TryParse(part, System.Globalization.NumberStyles.HexNumber, null, out var v2))
                    return v2 & 0xFF;
                return fallback;
            }

            if (int.TryParse(input, out var d))
                return d & 0xFF;

            var up = input.ToUpperInvariant();
            if (up.Length == 1)
                return (int)up[0];

            if (up.StartsWith("F") && int.TryParse(up.Substring(1), out var fn))
            {
                if (fn >= 1 && fn <= 24)
                    return 0x6F + fn;
            }

            var normalized = up.Replace("VK_", "").Replace(" ", "_").Replace("-", "_");
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "VOLUME_UP", 0xAF },
                { "VOLUME_DOWN", 0xAE },
                { "VOLUME_MUTE", 0xAD },
                { "MEDIA_PLAY_PAUSE", 0xB3 },
                { "ESC", 0x1B },
                { "ENTER", 0x0D },
                { "RETURN", 0x0D },
                { "SPACE", 0x20 }
            };

            if (map.TryGetValue(normalized, out var mapped)) return mapped;
            if (map.TryGetValue(up, out mapped)) return mapped;

            return fallback;
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
                WifiMode = source.WifiMode,
                WifiMdnsServiceName = source.WifiMdnsServiceName,
                MusicRemoteRoot = source.MusicRemoteRoot,
                MusicRemoteRoots = source.MusicRemoteRoots?.ToList() ?? new List<string>(),
                UpdateIntervalMode = source.UpdateIntervalMode,
                IgnoredUpdateVersion = source.IgnoredUpdateVersion,
                DebugMode = source.DebugMode,
                UseDarkMode = source.UseDarkMode,
                OpenInTaskbar = source.OpenInTaskbar,
                StartWithWindows = source.StartWithWindows,
                ShowMediaPlayerWindow = source.ShowMediaPlayerWindow,
                MediaPlayerWindowWidth = source.MediaPlayerWindowWidth,
                MediaPlayerWindowHeight = source.MediaPlayerWindowHeight,
                MediaPlayerWindowTop = source.MediaPlayerWindowTop,
                MediaPlayerWindowLeft = source.MediaPlayerWindowLeft,
                MediaPlayerWindowState = source.MediaPlayerWindowState,
                ScrcpyAudioCodec = source.ScrcpyAudioCodec,
                ScrcpyAudioBitrate = source.ScrcpyAudioBitrate ?? string.Empty,
                ScrcpyAudioBuffer = source.ScrcpyAudioBuffer,
                ScrcpyFlacCompressionLevel = source.ScrcpyFlacCompressionLevel,
                ScrcpyAvailableAudioCodecs = source.ScrcpyAvailableAudioCodecs?.ToList() ?? new List<string>(),
                AudioQualityPresetName = source.AudioQualityPresetName ?? string.Empty,
                SmtcPauseClearDelayMinutes = source.SmtcPauseClearDelayMinutes,
                IsWifiEnabled = source.IsWifiEnabled,
                OnboardingCompleted = source.OnboardingCompleted,
                CachClearInMB = source.CachClearInMB,
                AllowedApps = source.AllowedApps?.ToList() ?? new List<string>(),
                EligibleApps = source.EligibleApps?.Select(a => new EligibleAppConfig
                {
                    PackageName = a.PackageName,
                    IsEnabled = a.IsEnabled,
                    EnableCoverSearch = a.EnableCoverSearch,
                    PresenceMode = a.PresenceMode
                }).ToList() ?? new List<EligibleAppConfig>(),
                HotkeyVolumeUpKey = source.HotkeyVolumeUpKey,
                HotkeyVolumeDownKey = source.HotkeyVolumeDownKey,
                HotkeyToggleScrcpyKey = source.HotkeyToggleScrcpyKey,
                HotkeyToggleLyricsOverlayKey = source.HotkeyToggleLyricsOverlayKey,
                HotkeyCopyTrackInfoKey = source.HotkeyCopyTrackInfoKey,
                LyricsSearchFolderOverride = source.LyricsSearchFolderOverride ?? string.Empty,
                CoverArtFileNamePatterns = source.CoverArtFileNamePatterns ?? string.Empty,
                CopyTrackInfoTemplate = source.CopyTrackInfoTemplate ?? string.Empty,
                HotkeyModifier = source.HotkeyModifier
            };
        }

        private sealed class AppPackageItem : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler? PropertyChanged;

            public string PackageName { get; }
            public string DisplayName => MainWindow.FormatPackageName(PackageName);

            private PresenceMode _presenceMode;
            public PresenceMode PresenceMode
            {
                get => _presenceMode;
                set
                {
                    if (_presenceMode == value) return;
                    _presenceMode = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PresenceMode)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PresenceModeLabel)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PresenceModeColor)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PresenceModeBrush)));
                }
            }

            private bool _enableCoverSearch;
            public bool EnableCoverSearch
            {
                get => _enableCoverSearch;
                set
                {
                    if (_enableCoverSearch == value) return;
                    _enableCoverSearch = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EnableCoverSearch)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverLabel)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverColor)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverBrush)));
                }
            }

            public string PresenceModeLabel => _presenceMode switch
            {
                PresenceMode.Full => "Full",
                PresenceMode.Half => "Half",
                _ => "Off"
            };

            public string PresenceModeColor => _presenceMode switch
            {
                PresenceMode.Full => "#34C954",
                PresenceMode.Half => "#3E7BFF",
                _ => "#FF3B30"
            };

            public System.Windows.Media.Brush PresenceModeBrush => new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(PresenceModeColor));

            public string CoverLabel => _enableCoverSearch ? "On" : "Off";
            public string CoverColor => _enableCoverSearch ? "#34C954" : "#FF3B30";
            public System.Windows.Media.Brush CoverBrush => new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(CoverColor));

            public AppPackageItem(string packageName, PresenceMode presenceMode, bool enableCoverSearch)
            {
                PackageName = packageName;
                _presenceMode = presenceMode;
                _enableCoverSearch = enableCoverSearch;
            }

            public void CyclePresenceMode()
            {
                PresenceMode = PresenceMode switch
                {
                    PresenceMode.Full => PresenceMode.Half,
                    PresenceMode.Half => PresenceMode.Off,
                    _ => PresenceMode.Full
                };
            }

            public void ToggleCover()
            {
                EnableCoverSearch = !EnableCoverSearch;
            }
        }

        private sealed class SidebarStepItem
        {
            public string Title { get; set; } = string.Empty;
            public string NumberText { get; set; } = string.Empty;
            public string NumberBackground { get; set; } = "#33FFFFFF";
            public string NumberForeground { get; set; } = "#FFFFFF";
            public string RowBackground { get; set; } = "#00000000";
            public double TitleOpacity { get; set; } = 0.7;
            public string TitleWeight { get; set; } = "Normal";
        }
    }
}