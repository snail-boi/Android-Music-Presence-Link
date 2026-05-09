using System.Threading.Tasks;
using System.Windows;
using musicpresense;

namespace musicpresense
{
    public partial class WifiPairDialog : Window
    {
        /// <summary>The mDNS service name returned by adb after a successful pair.</summary>
        public string ServiceName { get; private set; } = string.Empty;

        /// <summary>The ip:pair_port the user typed in, kept for diagnostic logging.</summary>
        public string PairAddress { get; private set; } = string.Empty;

        public WifiPairDialog()
        {
            InitializeComponent();
            TxtPairAddress.Focus();
        }

        private async void BtnPair_Click(object sender, RoutedEventArgs e)
        {
            var addr = TxtPairAddress.Text.Trim();
            var code = TxtPairCode.Text.Trim();

            if (string.IsNullOrWhiteSpace(addr) || !addr.Contains(':'))
            {
                TxtStatus.Text = "Pairing address must be in format ip:port.";
                return;
            }
            if (string.IsNullOrWhiteSpace(code))
            {
                TxtStatus.Text = "Enter the 6-digit pairing code from your phone.";
                return;
            }

            BtnPair.IsEnabled = false;
            TxtStatus.Foreground = System.Windows.Media.Brushes.Gray;
            TxtStatus.Text = "Pairing...";

            var result = await WirelessDebuggingHelper.PairAsync(addr, code).ConfigureAwait(true);

            if (!result.Success)
            {
                BtnPair.IsEnabled = true;
                TxtStatus.Foreground = System.Windows.Media.Brushes.OrangeRed;
                TxtStatus.Text = "Pairing failed. Make sure the phone screen is still showing the code, "
                                 + "the IP/port match exactly, and your PC is on the same Wi-Fi network. "
                                 + (string.IsNullOrWhiteSpace(result.Output) ? "" : "Details: " + result.Output.Trim());
                return;
            }

            ServiceName = result.ServiceName;
            PairAddress = addr;
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}