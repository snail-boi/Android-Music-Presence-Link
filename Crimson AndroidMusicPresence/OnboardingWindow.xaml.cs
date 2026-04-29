using Crimson_AndroidMusicPresence;
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
            "Enable USB debugging",
            "Connect your phone",
            "Music and lyrics folders",
            "Allowed apps",
            "Hotkeys",
            "Startup options"
        };

        private readonly string[] _stepSubtitles =
        {
            "Welcome to Android Music Presence. Let's get you set up.",
            "Your phone needs USB debugging turned on so we can talk to it.",
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
            TxtWifi.Text = _workingConfig.SelectedDeviceWiFi;
            TxtDeviceName.Text = _workingConfig.SelectedDeviceName;
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

            SelectModifier(_workingConfig.HotkeyModifier);
            _ = LoadInstalledAppsAsync();
            UpdateStepUI();
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
                        IsEnabled = item.IsSelected,
                        EnableCoverSearch = item.IsSelected && item.IsCoverSearchEnabled
                    })
                    .Where(item => !string.IsNullOrWhiteSpace(item.PackageName))
                    .GroupBy(item => item.PackageName, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new EligibleAppConfig
                    {
                        PackageName = g.Key,
                        IsEnabled = g.Any(x => x.IsEnabled),
                        EnableCoverSearch = g.Any(x => x.EnableCoverSearch)
                    })
                    .ToList();

                _workingConfig.AllowedApps = _workingConfig.EligibleApps
                    .Where(a => a.IsEnabled)
                    .Select(a => a.PackageName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
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
            if (MessageBox.Show("do you want to enable WiFi", "May be incompatible with certain networks", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.No)
            {
                _workingConfig.IsWifiEnabled = false;
            }
            else
            {
                _workingConfig.IsWifiEnabled = true;
                port = await GetWifiPortAsync(usbSerial);
                ip = await GetDeviceWifiIpAsync(usbSerial);
            }

            if (!string.IsNullOrWhiteSpace(ip))
            {
                TxtWifi.Text = _workingConfig.IsWifiEnabled ? $"{ip}:{port}" : string.Empty;
            }
            else
            {
                MessageBox.Show("Could not read the device Wi-Fi IP address.", "Wi-Fi Info", MessageBoxButton.OK, MessageBoxImage.Warning);
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

            var picker = new RemoteFolderPicker(device)
            {
                Owner = this
            };

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
                            _appPackages.Add(new AppPackageItem(pkg, appConfig.IsEnabled, appConfig.EnableCoverSearch));
                        }
                        else
                        {
                            _appPackages.Add(new AppPackageItem(pkg, false, false));
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
            string device = string.Empty;

            var devices = await AdbHelper.RunAdbCaptureAsync("devices").ConfigureAwait(false);
            var deviceList = devices.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            bool IsDeviceConnected(string id) => deviceList.Any(l => l.StartsWith(id) && l.EndsWith("device"));

            if (!string.IsNullOrWhiteSpace(_workingConfig.SelectedDeviceUSB) && IsDeviceConnected(_workingConfig.SelectedDeviceUSB))
            {
                device = _workingConfig.SelectedDeviceUSB;
            }
            else if (!string.IsNullOrWhiteSpace(_workingConfig.SelectedDeviceWiFi) && _workingConfig.SelectedDeviceWiFi != "None")
            {
                if (!IsDeviceConnected(_workingConfig.SelectedDeviceWiFi))
                {
                    await AdbHelper.RunAdbCaptureAsync($"connect {_workingConfig.SelectedDeviceWiFi}").ConfigureAwait(false);
                    devices = await AdbHelper.RunAdbCaptureAsync("devices").ConfigureAwait(false);
                    deviceList = devices.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                }

                if (IsDeviceConnected(_workingConfig.SelectedDeviceWiFi))
                {
                    device = _workingConfig.SelectedDeviceWiFi;
                }
            }

            return device;
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
            if (!_workingConfig.EligibleApps.Any(a => a.IsEnabled))
            {
                _workingConfig.EligibleApps.Add(new EligibleAppConfig
                {
                    PackageName = "in.krosbits.musicolet",
                    IsEnabled = true,
                    EnableCoverSearch = true
                });
            }

            _workingConfig.AllowedApps = _workingConfig.EligibleApps
                .Where(a => a.IsEnabled)
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
                MusicRemoteRoot = source.MusicRemoteRoot,
                MusicRemoteRoots = source.MusicRemoteRoots?.ToList() ?? new List<string>(),
                CachClearInMB = source.CachClearInMB,
                AllowedApps = source.AllowedApps?.ToList() ?? new List<string>(),
                EligibleApps = source.EligibleApps?.Select(a => new EligibleAppConfig
                {
                    PackageName = a.PackageName,
                    IsEnabled = a.IsEnabled,
                    EnableCoverSearch = a.EnableCoverSearch
                }).ToList() ?? new List<EligibleAppConfig>(),
                UpdateIntervalMode = source.UpdateIntervalMode,
                DebugMode = source.DebugMode,
                UseDarkMode = source.UseDarkMode,
                OpenInTaskbar = source.OpenInTaskbar,
                StartWithWindows = source.StartWithWindows,
                ScrcpyAudioCodec = source.ScrcpyAudioCodec,
                ScrcpyAudioBitrate = source.ScrcpyAudioBitrate ?? string.Empty,
                ScrcpyAudioBuffer = source.ScrcpyAudioBuffer,
                ScrcpyFlacCompressionLevel = source.ScrcpyFlacCompressionLevel,
                ScrcpyAvailableAudioCodecs = source.ScrcpyAvailableAudioCodecs?.ToList() ?? new List<string>(),
                AudioQualityPresetName = source.AudioQualityPresetName ?? string.Empty,
                SmtcPauseClearDelayMinutes = source.SmtcPauseClearDelayMinutes,
                HotkeyVolumeUpKey = source.HotkeyVolumeUpKey,
                HotkeyVolumeDownKey = source.HotkeyVolumeDownKey,
                HotkeyToggleScrcpyKey = source.HotkeyToggleScrcpyKey,
                HotkeyToggleLyricsOverlayKey = source.HotkeyToggleLyricsOverlayKey,
                HotkeyCopyTrackInfoKey = source.HotkeyCopyTrackInfoKey,
                LyricsSearchFolderOverride = source.LyricsSearchFolderOverride ?? string.Empty,
                CoverArtFileNamePatterns = source.CoverArtFileNamePatterns ?? string.Empty,
                CopyTrackInfoTemplate = source.CopyTrackInfoTemplate ?? string.Empty,
                IsWifiEnabled = source.IsWifiEnabled,
                OnboardingCompleted = source.OnboardingCompleted,
                HotkeyModifier = source.HotkeyModifier
            };
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

            private bool _isCoverSearchEnabled;
            public bool IsCoverSearchEnabled
            {
                get => _isCoverSearchEnabled;
                set
                {
                    if (_isCoverSearchEnabled == value) return;
                    _isCoverSearchEnabled = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCoverSearchEnabled)));
                }
            }

            public AppPackageItem(string packageName, bool isSelected, bool isCoverSearchEnabled)
            {
                PackageName = packageName;
                _isSelected = isSelected;
                _isCoverSearchEnabled = isCoverSearchEnabled;
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