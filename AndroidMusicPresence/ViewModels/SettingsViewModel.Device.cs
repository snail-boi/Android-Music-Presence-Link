using System;
using System.Threading.Tasks;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// Device step: USB serial, Wi-Fi address (ip:port), mDNS service name, device name, and the
    /// Wi-Fi mode. The old code kept the mode in Window.Tag; here it is a real property.
    ///
    /// Field visibility mirrors the old UpdateWifiFieldVisibility:
    ///   TcpIp:             Wi-Fi (ip:port) row visible, mDNS row hidden.
    ///   WirelessDebugging: Wi-Fi row hidden, mDNS row visible only once a service name is known.
    /// The pair / auto-detect flows persist immediately via Save(false), as before.
    /// </summary>
    internal sealed partial class SettingsViewModel
    {
        public RelayCommand WifiModeToggleCommand { get; private set; } = null!;
        public RelayCommand AutoGatherOrPairCommand { get; private set; } = null!;
        public RelayCommand ResetDeviceCommand { get; private set; } = null!;

        private bool _isBusy;
        public bool IsNotBusy => !_isBusy;   // bound to the auto-detect/pair button IsEnabled

        private string _usbSerial = string.Empty;
        public string UsbSerial { get => _usbSerial; set => Set(ref _usbSerial, value); }

        private string _wifiAddress = string.Empty;
        public string WifiAddress { get => _wifiAddress; set => Set(ref _wifiAddress, value); }

        private string _deviceName = string.Empty;
        public string DeviceName { get => _deviceName; set => Set(ref _deviceName, value); }

        private string _mdnsService = string.Empty;
        public string MdnsService
        {
            get => _mdnsService;
            set
            {
                if (!Set(ref _mdnsService, value)) return;
                RaisePropertyChanged(nameof(AutoOrPairButtonText));
                RaisePropertyChanged(nameof(MdnsVisible));
            }
        }

        private WirelessMode _wifiMode;
        public WirelessMode WifiMode
        {
            get => _wifiMode;
            set
            {
                if (!Set(ref _wifiMode, value)) return;
                _config.WifiMode = value;
                RaisePropertyChanged(nameof(WifiModeButtonText));
                RaisePropertyChanged(nameof(AutoOrPairButtonText));
                RaisePropertyChanged(nameof(WifiAddressVisible));
                RaisePropertyChanged(nameof(MdnsVisible));
            }
        }

        private bool HasMdns => !string.IsNullOrWhiteSpace(MdnsService);

        public string WifiModeButtonText => WifiMode switch
        {
            WirelessMode.WirelessDebugging => "mode: Wireless Debugging",
            WirelessMode.UsbOnly => "mode: USB only",
            _ => "mode: adb tcpip"
        };

        public string AutoOrPairButtonText => WifiMode switch
        {
            WirelessMode.WirelessDebugging => HasMdns ? "Re-pair phone" : "Pair phone",
            WirelessMode.UsbOnly => "Auto-detect USB",
            _ => "Auto-detect USB"
        };

        public bool WifiAddressVisible => WifiMode == WirelessMode.TcpIp;
        public bool MdnsVisible => WifiMode == WirelessMode.WirelessDebugging && HasMdns;

        partial void InitDevice()
        {
            WifiModeToggleCommand = new RelayCommand(ToggleWifiMode);
            AutoGatherOrPairCommand = new RelayCommand(async () => await AutoGatherOrPairAsync());
            ResetDeviceCommand = new RelayCommand(async () => await ResetDeviceAsync());

            LoadDeviceFromConfig();
        }

        partial void LoadDeviceFromConfig()
        {
            _usbSerial = _config.SelectedDeviceUSB;
            _wifiAddress = _config.SelectedDeviceWiFi;
            _deviceName = _config.SelectedDeviceName;
            _mdnsService = _config.WifiMdnsServiceName ?? string.Empty;
            _wifiMode = _config.WifiMode;
        }

        partial void ApplyDeviceToConfig(MusicConfig config)
        {
            config.SelectedDeviceUSB = UsbSerial.Trim();
            config.SelectedDeviceWiFi = WifiAddress.Trim();
            config.SelectedDeviceName = DeviceName.Trim();
            config.WifiMode = WifiMode;
            // WifiMdnsServiceName is already on the cloned config (set by the pair flow).

            // Mode cleanup so stale fields don't linger and the dirty diff reflects post-save state.
            if (config.WifiMode == WirelessMode.TcpIp)
            {
                config.WifiMdnsServiceName = string.Empty;
            }
            else if (config.WifiMode == WirelessMode.UsbOnly)
            {
                config.WifiMdnsServiceName = string.Empty;
                config.SelectedDeviceWiFi = string.Empty;
                config.IsWifiEnabled = false;
            }
            else if (string.IsNullOrWhiteSpace(config.WifiMdnsServiceName))
            {
                config.SelectedDeviceWiFi = string.Empty;
            }
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

            string ipPort = string.Empty;
            if (!string.IsNullOrWhiteSpace(result.ServiceName))
                ipPort = await WirelessDebuggingHelper.ReconnectViaMdnsAsync(result.ServiceName);

            if (!string.IsNullOrWhiteSpace(result.ServiceName))
            {
                _config.WifiMdnsServiceName = result.ServiceName;
                MdnsService = result.ServiceName;
            }
            if (!string.IsNullOrWhiteSpace(ipPort))
            {
                _config.SelectedDeviceWiFi = ipPort;
                WifiAddress = ipPort;
                _config.IsWifiEnabled = true;
            }

            // Prefer ro.serialno over the ADB transport name so the real hardware serial lands
            // in the USB field rather than an "adb-XXXX" mDNS name.
            string usbSerial = string.Empty;
            if (!string.IsNullOrWhiteSpace(ipPort))
                usbSerial = await DeviceQuery.GetDeviceSerialAsync(ipPort);
            if (string.IsNullOrWhiteSpace(usbSerial))
            {
                var adbSerial = await DeviceQuery.GetConnectedUsbDeviceAsync();
                if (!string.IsNullOrWhiteSpace(adbSerial))
                    usbSerial = await DeviceQuery.GetDeviceSerialAsync(adbSerial);
            }
            if (!string.IsNullOrWhiteSpace(usbSerial))
            {
                UsbSerial = usbSerial;
                _config.SelectedDeviceUSB = usbSerial;
            }

            var name = Interaction!.AskDeviceName();
            if (!string.IsNullOrWhiteSpace(name))
            {
                DeviceName = name.Trim();
                _config.SelectedDeviceName = DeviceName;
            }

            Save(false);

            if (string.IsNullOrWhiteSpace(ipPort))
            {
                Interaction!.ShowInfo(
                    "Pairing succeeded but I could not auto-discover the device on the network. "
                    + "Make sure Wireless Debugging is still enabled on the phone, then click Save. "
                    + "The app will retry on the next reconnect.",
                    "Pairing Complete");
            }
            else
            {
                Interaction!.ShowInfo($"Paired and connected at {ipPort}.", "Pairing Complete");
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

            var usbSerial = await DeviceQuery.GetConnectedUsbDeviceAsync();
            if (string.IsNullOrWhiteSpace(usbSerial))
            {
                Interaction!.ShowWarning("Please connect your device via USB first.", "USB Required");
                return;
            }

            UsbSerial = usbSerial;

            if (WifiMode == WirelessMode.UsbOnly)
            {
                // USB-only mode: skip all Wi-Fi setup.
                _config.IsWifiEnabled = false;
            }
            else
            {
                // TcpIp mode: wifi is always the point, enable it automatically.
                _config.IsWifiEnabled = true;
                var port = await DeviceQuery.GetWifiPortAsync(usbSerial);
                var ip = await DeviceQuery.GetDeviceWifiIpAsync(usbSerial);

                if (!string.IsNullOrWhiteSpace(ip))
                {
                    WifiAddress = $"{ip}:{port}";
                }
                else
                {
                    Interaction!.ShowWarning("Could not read the device Wi-Fi IP address.", "Wi-Fi Info");
                }
            }

            var name = Interaction!.AskDeviceName();
            if (name == null)
                return;

            if (!string.IsNullOrWhiteSpace(name))
                DeviceName = name.Trim();

            Save(false);
        }

        private async Task ResetDeviceAsync()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_config.SelectedDeviceWiFi))
                {
                    await AdbHelper.RunAdbAsync("disconnect");
                    Debugger.show("[RESET] SUCCESFULLY RESET");
                }
            }
            catch
            {
                Debugger.show("[RESET] RESET FAILED");
            }

            UsbSerial = string.Empty;
            WifiAddress = string.Empty;
            MdnsService = string.Empty;
            DeviceName = string.Empty;

            _config.SelectedDeviceUSB = string.Empty;
            _config.SelectedDeviceWiFi = string.Empty;
            _config.SelectedDeviceName = string.Empty;
            _config.WifiMdnsServiceName = string.Empty;
            _config.IsWifiEnabled = false;
        }
    }
}