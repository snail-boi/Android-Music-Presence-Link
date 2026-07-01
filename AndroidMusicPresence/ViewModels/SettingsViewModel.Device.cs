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
                _config.Device.WifiMode = value;
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
            _usbSerial = _config.Device.SelectedDeviceUSB;
            _wifiAddress = _config.Device.SelectedDeviceWiFi;
            _deviceName = _config.Device.SelectedDeviceName;
            _mdnsService = _config.Device.MdnsServiceName ?? string.Empty;
            _wifiMode = _config.Device.WifiMode;
        }

        partial void ApplyDeviceToConfig(MusicConfig config)
        {
            config.Device.SelectedDeviceUSB = UsbSerial.Trim();
            config.Device.SelectedDeviceWiFi = WifiAddress.Trim();
            config.Device.SelectedDeviceName = DeviceName.Trim();
            config.Device.WifiMode = WifiMode;
            // WifiMdnsServiceName is already on the cloned config (set by the pair flow).

            // Mode cleanup so stale fields don't linger and the dirty diff reflects post-save state.
            if (config.Device.WifiMode == WirelessMode.TcpIp)
            {
                config.Device.MdnsServiceName = string.Empty;
            }
            else if (config.Device.WifiMode == WirelessMode.UsbOnly)
            {
                config.Device.MdnsServiceName = string.Empty;
                config.Device.SelectedDeviceWiFi = string.Empty;
                config.Device.IsWifiEnabled = false;
            }
            else if (string.IsNullOrWhiteSpace(config.Device.MdnsServiceName))
            {
                config.Device.SelectedDeviceWiFi = string.Empty;
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
                WirelessMode.WirelessDebugging => WirelessMode.UsbOnly,
                WirelessMode.UsbOnly => WirelessMode.TcpIp,
                _ => WirelessMode.WirelessDebugging
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
                _config.Device.MdnsServiceName = result.ServiceName;
                MdnsService = result.ServiceName;
            }
            if (!string.IsNullOrWhiteSpace(ipPort))
            {
                _config.Device.SelectedDeviceWiFi = ipPort;
                WifiAddress = ipPort;
                _config.Device.IsWifiEnabled = true;
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
                _config.Device.SelectedDeviceUSB = usbSerial;
            }

            var name = Interaction!.AskDeviceName();
            if (!string.IsNullOrWhiteSpace(name))
            {
                DeviceName = name.Trim();
                _config.Device.SelectedDeviceName = DeviceName;
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
                _config.Device.IsWifiEnabled = false;
            }
            else
            {
                // TcpIp mode: wifi is always the point, enable it automatically.
                _config.Device.IsWifiEnabled = true;
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
                if (!string.IsNullOrWhiteSpace(_config.Device.SelectedDeviceWiFi))
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

            _config.Device.SelectedDeviceUSB = string.Empty;
            _config.Device.SelectedDeviceWiFi = string.Empty;
            _config.Device.SelectedDeviceName = string.Empty;
            _config.Device.MdnsServiceName = string.Empty;
            _config.Device.IsWifiEnabled = false;
        }
    }
}