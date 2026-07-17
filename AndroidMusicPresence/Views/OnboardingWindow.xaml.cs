using System;
using System.Windows;
using System.Windows.Input;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// Onboarding wizard window. All step state and logic live in
    /// <see cref="OnboardingViewModel"/> (split across its partial files). This code-behind
    /// only provides the three things the ViewModel cannot do on its own:
    ///
    ///   IOnboardingInteraction : the pairing dialog, the name prompt, and the message boxes.
    ///   PickRemoteFolder        : opening the RemoteFolderPicker dialog.
    ///   StartHotkeyRecording    : capturing a held key combination at the window level.
    ///
    /// It sets the DataContext, hands those seams to the VM, exposes UpdatedConfig, and closes
    /// the dialog when the VM asks. The constructor and UpdatedConfig are unchanged, so
    /// App.xaml.cs needs no edits.
    /// </summary>
    public partial class OnboardingWindow : Window, IOnboardingInteraction
    {
        private readonly OnboardingViewModel _vm;

        private readonly HotkeyRecorder _hotkeyRecorder = new HotkeyRecorder();

        public MusicConfig UpdatedConfig => _vm.UpdatedConfig;

        public OnboardingWindow(MusicConfig currentConfig)
        {
            InitializeComponent();

            _vm = new OnboardingViewModel(currentConfig);
            DataContext = _vm;

            // Hand the view seams to the ViewModel.
            _vm.Interaction = this;
            _vm.PickRemoteFolder = ShowRemoteFolderPicker;
            _vm.StartHotkeyRecording = StartRecordingHotkey;

            _vm.RequestClose += OnRequestClose;
        }

        private void OnRequestClose(bool result)
        {
            DialogResult = result;
            Close();
        }

        // ── IOnboardingInteraction (dialogs and message boxes) ────────────────

        public WifiPairResult? ShowWifiPair()
        {
            var dlg = new WifiPairDialog();
            if (IsLoaded && IsVisible)
                dlg.Owner = this;

            if (dlg.ShowDialog() != true)
                return null;

            return new WifiPairResult(dlg.ServiceName ?? string.Empty, dlg.PairAddress ?? string.Empty);
        }

        public string? AskDeviceName()
        {
            var dlg = new NameInputDialogue("Enter a name for this device:", "Device Name");
            if (IsLoaded && IsVisible)
                dlg.Owner = this;

            return dlg.ShowDialog() == true ? dlg.InputText : null;
        }

        public void ShowInfo(string message, string title)
            => (Application.Current as App)?.ShowToast(message, ToastLevel.Info);

        public void ShowWarning(string message, string title)
            => (Application.Current as App)?.ShowToast(message, ToastLevel.Warning);

        public bool ConfirmYesNo(string message, string title)
            => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        // ── Remote folder picker seam ─────────────────────────────────────────

        private string? ShowRemoteFolderPicker(string device)
        {
            var picker = RemoteFolderPicker.Create(device, this);
            return picker.ShowDialog() == true ? picker.SelectedFolder : null;
        }

        // ── Hotkey key capture (view concern) ─────────────────────────────────

        private void StartRecordingHotkey(Action<int[]?> onRecorded)
        {
            _hotkeyRecorder.Start(this, onRecorded);
        }
    }
}