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
			var currentMode = GetSelectedWifiMode();
			if (currentMode == WirelessMode.WirelessDebugging)
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
				_config.IsWifiEnabled = false;
			}
			else
			{
				_config.IsWifiEnabled = true;
				port = await GetWifiPortAsync(usbSerial);
				ip = await GetDeviceWifiIpAsync(usbSerial);

			}

			if (currentMode != WirelessMode.WirelessDebugging)
			{
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
			}

			var deviceNamePrompt = string.IsNullOrWhiteSpace(TxtDeviceName.Text) ? "" : TxtDeviceName.Text.Trim();

			var nameDialog = new NameInputDialogue("Enter a name for this device:", "Device Name");
			if (IsLoaded && IsVisible)
			{
				nameDialog.Owner = this;
			}

			if (nameDialog.ShowDialog() != true)
			{
				return;
			}
			var deviceName = nameDialog.InputText; ;


			if (!string.IsNullOrWhiteSpace(deviceName))
			{
				TxtDeviceName.Text = deviceName.Trim();
			}

			SaveConfigFromUi(false);
		}

		private static async Task<string> GetConnectedUsbDeviceAsync()
		{
			for (var attempt = 0; attempt < 8; attempt++)
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

				if (attempt < 7)
				{
					await Task.Delay(500);
				}
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
	}
}
