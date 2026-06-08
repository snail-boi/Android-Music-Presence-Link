using System.ComponentModel;
using System.Windows;

namespace musicpresense
{
    /// <summary>
    /// Wireless debugging pairing dialog. All the work (QR generation, the mDNS listening
    /// loop, packet parsing, manual pairing, mode switching) now lives in
    /// <see cref="WifiPairViewModel"/>. This code-behind handles only view concerns:
    /// starting the loop when the window loads, cancelling it when the window closes,
    /// focusing the address box when the user switches to manual mode, and closing the
    /// dialog (with the right DialogResult) when the VM asks.
    ///
    /// The close request can come from the background mDNS thread, so it is marshaled to the
    /// UI thread here. The constructor, ServiceName, and PairAddress are unchanged, so
    /// MainWindow_Wifi and OnboardingWindow need no edits.
    /// </summary>
    public partial class WifiPairDialog : Window
    {
        private readonly WifiPairViewModel _vm;

        public string ServiceName => _vm.ServiceName;
        public string PairAddress => _vm.PairAddress;

        public WifiPairDialog()
        {
            InitializeComponent();

            _vm = new WifiPairViewModel();
            DataContext = _vm;

            _vm.RequestClose += OnRequestClose;
            _vm.PropertyChanged += OnVmPropertyChanged;

            Loaded += (_, _) => _vm.Start();
            Closing += (_, _) => _vm.Cancel();
        }

        private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // When the user switches to manual mode, move focus to the address box.
            // Deferred, so the panel is visible by the time we focus it.
            if (e.PropertyName == nameof(WifiPairViewModel.IsQrMode) && !_vm.IsQrMode)
                Dispatcher.InvokeAsync(() => TxtPairAddress.Focus());
        }

        private void OnRequestClose(bool result)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => OnRequestClose(result));
                return;
            }

            DialogResult = result;
            Close();
        }
    }
}
