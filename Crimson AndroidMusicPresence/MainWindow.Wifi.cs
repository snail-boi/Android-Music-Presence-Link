using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace musicpresense
{
    public partial class MainWindow
    {
        private WirelessMode GetSelectedWifiMode()
        {
            if (Tag is string tag
                && Enum.TryParse<WirelessMode>(tag, out var mode))
            {
                return mode;
            }

            if (_config != null)
                return _config.WifiMode;

            return WirelessMode.TcpIp;
        }

        private void SelectWifiModeFromConfig()
        {
            SetWifiMode(_config?.WifiMode ?? WirelessMode.TcpIp, updateConfig: false);
        }

        private void UpdatePairButtonVisibility()
        {
            UpdateWifiModeUi();
        }

        // Centralized show/hide for the wifi-related rows. Called whenever
        // mode changes or config is reloaded.
        //
        // TcpIp mode:
        //   - Wi-Fi (ip:port) row: visible
        //   - mDNS service row:    hidden (irrelevant)
        //   - Pair button row:     hidden
        //
        // WirelessDebugging mode:
        //   - Wi-Fi (ip:port) row: hidden (managed by pair flow internally)
        //   - mDNS service row:    visible only if a service name is known
        //   - Pair button row:     visible. Label says "Pair phone..." if
        //                          unpaired, "Re-pair phone..." if a service
        //                          name is already stored.
        private void UpdateWifiFieldVisibility()
        {
            var mode = GetSelectedWifiMode();
            bool isWd = mode == WirelessMode.WirelessDebugging;

            if (LblWifiAddress != null)
                LblWifiAddress.Visibility = isWd ? Visibility.Collapsed : Visibility.Visible;
            if (TxtWifi != null)
                TxtWifi.Visibility = isWd ? Visibility.Collapsed : Visibility.Visible;

            bool hasMdns = !string.IsNullOrWhiteSpace(_config?.WifiMdnsServiceName);
            if (LblMdnsService != null)
                LblMdnsService.Visibility = (isWd && hasMdns) ? Visibility.Visible : Visibility.Collapsed;
            if (TxtMdnsService != null)
                TxtMdnsService.Visibility = (isWd && hasMdns) ? Visibility.Visible : Visibility.Collapsed;

            if (BtnAutoGather != null)
            {
                BtnAutoGather.Content = isWd ? (hasMdns ? "Re-pair phone" : "Pair phone") : "Auto-detect USB";
            }
        }

        private void BtnWifiModeToggle_Click(object sender, RoutedEventArgs e)
        {
            var nextMode = GetSelectedWifiMode() == WirelessMode.WirelessDebugging
                ? WirelessMode.TcpIp
                : WirelessMode.WirelessDebugging;

            SetWifiMode(nextMode, updateConfig: true);
        }

        private async void BtnDeviceAction_Click(object sender, RoutedEventArgs e)
        {
            if (GetSelectedWifiMode() == WirelessMode.WirelessDebugging)
            {
                await RunWirelessPairingAsync();
                return;
            }

            await AutoGatherDeviceInfoAsync();
        }

        private void SetWifiMode(WirelessMode mode, bool updateConfig)
        {
            Tag = mode.ToString();

            if (BtnWifiModeToggle != null)
            {
                BtnWifiModeToggle.Content = mode == WirelessMode.WirelessDebugging
                    ? "mode: Wireless Debugging"
                    : "mode: adb tcpip";
            }

            if (updateConfig && _config != null)
                _config.WifiMode = mode;

            UpdateWifiFieldVisibility();
        }

        private void UpdateWifiModeUi()
        {
            SetWifiMode(GetSelectedWifiMode(), updateConfig: false);
        }

        private async void BtnPairWireless_Click(object sender, RoutedEventArgs e)
        {
            await RunWirelessPairingAsync();
        }

        private async Task RunWirelessPairingAsync()
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
                ipPort = await WirelessDebuggingHelper.ReconnectViaMdnsAsync(dlg.ServiceName);
            }

            if (!string.IsNullOrWhiteSpace(dlg.ServiceName))
            {
                _config.WifiMdnsServiceName = dlg.ServiceName;
                if (TxtMdnsService != null)
                    TxtMdnsService.Text = dlg.ServiceName;
            }
            if (!string.IsNullOrWhiteSpace(ipPort))
            {
                _config.SelectedDeviceWiFi = ipPort;
                TxtWifi.Text = ipPort;
                _config.IsWifiEnabled = true;
            }

            // Prefer querying ro.serialno directly from the paired device (via ipPort)
            // so we always get the real hardware serial, not an ADB transport name
            // (mDNS serials like "adb-XXXXXXXX" would otherwise slip through the
            // GetConnectedUsbDeviceAsync filter and land in the USB serial field).
            string usbSerial = string.Empty;
            if (!string.IsNullOrWhiteSpace(ipPort))
            {
                usbSerial = await DeviceQuery.GetDeviceSerialAsync(ipPort);
            }
            if (string.IsNullOrWhiteSpace(usbSerial))
            {
                var adbSerial = await DeviceQuery.GetConnectedUsbDeviceAsync();
                if (!string.IsNullOrWhiteSpace(adbSerial))
                    usbSerial = await DeviceQuery.GetDeviceSerialAsync(adbSerial);
            }
            if (!string.IsNullOrWhiteSpace(usbSerial))
            {
                TxtUsbSerial.Text = usbSerial;
                _config.SelectedDeviceUSB = usbSerial;
            }

            var nameDialog = new NameInputDialogue("Enter a name for this device:", "Device Name");
            if (IsLoaded && IsVisible)
            {
                nameDialog.Owner = this;
            }

            if (nameDialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(nameDialog.InputText))
            {
                TxtDeviceName.Text = nameDialog.InputText.Trim();
                _config.SelectedDeviceName = TxtDeviceName.Text.Trim();
            }

            SaveConfigFromUi(false);

            if (string.IsNullOrWhiteSpace(ipPort))
            {
                MessageBox.Show(
                    "Pairing succeeded but I could not auto-discover the device on the network. "
                    + "Make sure Wireless Debugging is still enabled on the phone, then click Save. "
                    + "The app will retry on the next reconnect.",
                    "Pairing Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(
                    $"Paired and connected at {ipPort}.",
                    "Pairing Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            // mDNS row should now appear and the button should say "Re-pair...".
            UpdateWifiFieldVisibility();
        }
    }
}