using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace AndroidMusicPresenceLink
{
    /// <summary>Result of the Wi-Fi pairing dialog handed back to the ViewModel.</summary>
    public sealed record WifiPairResult(string ServiceName, string PairAddress);

    /// <summary>
    /// The view-side things the device step needs: dialogs and message boxes. The code-behind
    /// implements this, so the ViewModel can run the full pairing/detection flow without ever
    /// referencing a Window, a dialog, or MessageBox directly.
    /// </summary>
    internal interface IOnboardingInteraction
    {
        WifiPairResult? ShowWifiPair();
        string? AskDeviceName();
        void ShowInfo(string message, string title);
        void ShowWarning(string message, string title);
        bool ConfirmYesNo(string message, string title);
    }

    /// <summary>
    /// Device step: USB serial, Wi-Fi mode, the connect/pair flow, and device naming.
    ///
    /// WifiAddress is the single overloaded field the old TxtWifi used: it shows the mDNS
    /// service name in Wireless Debugging mode and an ip:port in tcpip mode. Switching modes
    /// repopulates it from the matching saved value, mirroring the old UpdateWifiFieldPresentation.
    /// </summary>
    internal sealed partial class OnboardingViewModel
    {
        // Set by the view right after construction. Supplies dialogs without the VM knowing
        // about windows.
        public IOnboardingInteraction? Interaction { get; set; }

        public RelayCommand WifiModeToggleCommand { get; private set; } = null!;
        public RelayCommand AutoGatherOrPairCommand { get; private set; } = null!;

        private bool _isBusy;
        public bool IsNotBusy => !_isBusy;   // bound to the connect/pair button's IsEnabled

        private string _usbSerial = string.Empty;
        public string UsbSerial
        {
            get => _usbSerial;
            set => Set(ref _usbSerial, value);
        }

        private string _deviceName = string.Empty;
        public string DeviceName
        {
            get => _deviceName;
            set => Set(ref _deviceName, value);
        }

        private string _wifiAddress = string.Empty;
        public string WifiAddress
        {
            get => _wifiAddress;
            set => Set(ref _wifiAddress, value);
        }

        private WirelessMode _wifiMode;
        public WirelessMode WifiMode
        {
            get => _wifiMode;
            set
            {
                if (!Set(ref _wifiMode, value)) return;

                _workingConfig.WifiMode = value;

                RaisePropertyChanged(nameof(WifiModeButtonText));
                RaisePropertyChanged(nameof(AutoOrPairButtonText));
                RaisePropertyChanged(nameof(WifiAddressLabel));
                RaisePropertyChanged(nameof(WifiAddressHelp));
                RaisePropertyChanged(nameof(WifiAddressVisible));

                // Show the value relevant to the new mode, as the old code did on toggle.
                WifiAddress = value == WirelessMode.WirelessDebugging
                    ? (_workingConfig.WifiMdnsServiceName ?? string.Empty)
                    : _workingConfig.SelectedDeviceWiFi;
            }
        }

        // True only for TcpIp mode: hides the field in WirelessDebugging and UsbOnly.
        public bool WifiAddressVisible => WifiMode == WirelessMode.TcpIp;

        public string WifiModeButtonText => WifiMode switch
        {
            WirelessMode.WirelessDebugging => "Wi-Fi mode: Wireless Debugging",
            WirelessMode.UsbOnly => "Wi-Fi mode: USB only",
            _ => "Wi-Fi mode: adb tcpip"
        };

        public string AutoOrPairButtonText => WifiMode switch
        {
            WirelessMode.WirelessDebugging => "Pair phone",
            WirelessMode.UsbOnly => "Auto Detect USB",
            _ => "Auto Detect USB"
        };

        public string WifiAddressLabel => WifiMode == WirelessMode.WirelessDebugging
            ? "mDNS"
            : "Wi-Fi Address";

        public string WifiAddressHelp => WifiMode == WirelessMode.WirelessDebugging
            ? "mDNS service name discovered by pairing."
            : "Optional, format ip:port.";

        private void InitDevice()
        {
            WifiModeToggleCommand = new RelayCommand(ToggleWifiMode);
            AutoGatherOrPairCommand = new RelayCommand(async () => await AutoGatherOrPairAsync());

            _usbSerial = _workingConfig.SelectedDeviceUSB;
            _deviceName = _workingConfig.SelectedDeviceName;
            _wifiMode = _workingConfig.WifiMode;
            _wifiAddress = _wifiMode == WirelessMode.WirelessDebugging
                ? (_workingConfig.WifiMdnsServiceName ?? string.Empty)
                : _workingConfig.SelectedDeviceWiFi;
        }

        private void CommitDeviceToConfig()
        {
            _workingConfig.SelectedDeviceUSB = UsbSerial.Trim();
            _workingConfig.SelectedDeviceWiFi = WifiAddress.Trim();
            _workingConfig.SelectedDeviceName = DeviceName.Trim();
            _workingConfig.WifiMode = WifiMode;
            // WifiMdnsServiceName and IsWifiEnabled are set during the pair / auto-detect flows.
        }

        private void SetBusy(bool busy)
        {
            _isBusy = busy;
            RaisePropertyChanged(nameof(IsNotBusy));
        }

        private void ToggleWifiMode()
        {
            WifiMode = WifiMode switch
            {
                WirelessMode.TcpIp => WirelessMode.WirelessDebugging,
                WirelessMode.WirelessDebugging => WirelessMode.UsbOnly,
                _ => WirelessMode.TcpIp
            };
        }

        private async Task AutoGatherOrPairAsync()
        {
            if (_isBusy || Interaction == null)
                return;

            SetBusy(true);
            try
            {
                if (WifiMode == WirelessMode.WirelessDebugging)
                    await PairWirelessAsync();
                else
                    await AutoGatherDeviceInfoAsync();
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task PairWirelessAsync()
        {
            var result = Interaction!.ShowWifiPair();
            if (result == null) return;

            // Pairing succeeded. Run an mDNS lookup to capture the current ip:port
            // (the connection port differs from the pairing port).
            string ipPort = string.Empty;
            if (!string.IsNullOrWhiteSpace(result.ServiceName))
                ipPort = await ReconnectViaMdnsWithRetryAsync(result.ServiceName);

            if (!string.IsNullOrWhiteSpace(result.ServiceName))
            {
                _workingConfig.WifiMdnsServiceName = result.ServiceName;
                WifiAddress = result.ServiceName;
            }
            if (!string.IsNullOrWhiteSpace(ipPort))
            {
                _workingConfig.SelectedDeviceWiFi = ipPort;
                _workingConfig.IsWifiEnabled = true;
            }

            // Read the real hardware serial over the wireless connection rather than
            // guessing from whatever USB device is attached.
            string usbSerial = string.Empty;
            if (!string.IsNullOrWhiteSpace(result.ServiceName))
                usbSerial = await GetWirelessDebuggingSerialAsync(result.ServiceName, ipPort).ConfigureAwait(true);

            if (!string.IsNullOrWhiteSpace(usbSerial))
            {
                UsbSerial = usbSerial;
                _workingConfig.SelectedDeviceUSB = usbSerial;
            }

            var name = Interaction!.AskDeviceName();
            if (!string.IsNullOrWhiteSpace(name))
            {
                DeviceName = name.Trim();
                _workingConfig.SelectedDeviceName = DeviceName;
            }

            MusicConfigManager.Save(_workingConfig);
            (Application.Current as App)?.UpdateConfig(_workingConfig);
        }

        private async Task AutoGatherDeviceInfoAsync()
        {
            // Do NOT call `adb disconnect` here. Doing so removes the USB device from the
            // ADB device list, which breaks folder detection on the next onboarding step
            // because the working config hasn't been saved yet and DeviceQuery still needs
            // a live USB connection to find the device.

            var usbSerial = await DeviceQuery.GetConnectedUsbDeviceAsync();
            if (string.IsNullOrWhiteSpace(usbSerial))
            {
                Interaction!.ShowWarning("Please connect your device via USB first.", "USB Required");
                return;
            }

            UsbSerial = usbSerial;
            _workingConfig.SelectedDeviceUSB = usbSerial;

            // In Wireless Debugging mode the Wi-Fi address comes from the pair flow
            // (ip:random_port discovered via mDNS), not from service.adb.tcp.port.
            if (_workingConfig.WifiMode == WirelessMode.WirelessDebugging)
            {
                Interaction!.ShowInfo(
                    "Auto-detect skipped Wi-Fi setup because Wireless Debugging mode is selected. "
                    + "Use the 'Pair phone' button to set up wireless.",
                    "Wireless Debugging Mode");
            }
            else if (_workingConfig.WifiMode == WirelessMode.UsbOnly)
            {
                // USB-only mode: skip all Wi-Fi setup.
                _workingConfig.IsWifiEnabled = false;
            }
            else
            {
                // TcpIp mode: wifi is always the point, enable it automatically.
                _workingConfig.IsWifiEnabled = true;
                var port = await DeviceQuery.GetWifiPortAsync(usbSerial);
                var ip = await DeviceQuery.GetDeviceWifiIpAsync(usbSerial);

                if (!string.IsNullOrWhiteSpace(ip))
                {
                    WifiAddress = $"{ip}:{port}";
                    _workingConfig.SelectedDeviceWiFi = WifiAddress;
                }
                else
                {
                    Interaction!.ShowWarning("Could not read the device Wi-Fi IP address.", "Wi-Fi Info");
                }
            }

            var name = Interaction!.AskDeviceName();
            if (!string.IsNullOrWhiteSpace(name))
            {
                DeviceName = name.Trim();
                _workingConfig.SelectedDeviceName = DeviceName;
            }
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
                                var liveSerial = await DeviceQuery.GetDeviceSerialAsync(serial).ConfigureAwait(false);
                                if (!string.IsNullOrWhiteSpace(liveSerial))
                                    return liveSerial;
                            }
                        }

                        var serialFromIpPort = await DeviceQuery.GetDeviceSerialAsync(connectedIpPort).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(serialFromIpPort))
                            return serialFromIpPort;
                    }
                }

                if (!string.IsNullOrWhiteSpace(ipPort))
                {
                    var serial = await DeviceQuery.GetDeviceSerialAsync(ipPort).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(serial))
                        return serial;
                }

                await Task.Delay(500).ConfigureAwait(false);
            }

            return string.Empty;
        }
    }
}