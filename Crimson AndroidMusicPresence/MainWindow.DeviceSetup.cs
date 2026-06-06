using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;

namespace musicpresense
{
	public partial class MainWindow
	{
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
           if (GetSelectedWifiMode() == WirelessMode.WirelessDebugging)
			{
                await RunWirelessPairingAsync();
				return;
			}

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
				MessageBox.Show("Please connect your device via USB first.", "USB Required", MessageBoxButton.OK, MessageBoxImage.Warning);
				return;
			}

			TxtUsbSerial.Text = usbSerial;
			var port = 0;
			var ip = "none";

          if (MessageBox.Show("do you want to enable WiFi", "May be incompatible with certain networks", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.No)
			{
				_config.IsWifiEnabled = false;
			}
			else
			{
				_config.IsWifiEnabled = true;
				port = await DeviceQuery.GetWifiPortAsync(usbSerial);
				ip = await DeviceQuery.GetDeviceWifiIpAsync(usbSerial);

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

			var nameDialog = new NameInputDialogue("Enter a name for this device:", "Device Name");
			if (IsLoaded && IsVisible)
			{
				nameDialog.Owner = this;
			}

			if (nameDialog.ShowDialog() != true)
			{
				return;
			}
            var deviceName = nameDialog.InputText;

			if (!string.IsNullOrWhiteSpace(deviceName))
			{
				TxtDeviceName.Text = deviceName.Trim();
			}

			SaveConfigFromUi(false);
		}

	}
}
