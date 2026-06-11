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
    ///   StartHotkeyRecording    : capturing a single key press at the window level.
    ///
    /// It sets the DataContext, hands those seams to the VM, exposes UpdatedConfig, and closes
    /// the dialog when the VM asks. The constructor and UpdatedConfig are unchanged, so
    /// App.xaml.cs needs no edits.
    /// </summary>
    public partial class OnboardingWindow : Window, IOnboardingInteraction
    {
        private readonly OnboardingViewModel _vm;

        // Hotkey capture state. Recording a key is a window-level keyboard concern, so it
        // stays here rather than in the ViewModel.
        private bool _isRecordingHotkey;
        private Action<int>? _onHotkeyRecorded;

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
            => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

        public void ShowWarning(string message, string title)
            => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

        public bool ConfirmYesNo(string message, string title)
            => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        // ── Remote folder picker seam ─────────────────────────────────────────

        private string? ShowRemoteFolderPicker(string device)
        {
            var picker = RemoteFolderPicker.Create(device, this);
            return picker.ShowDialog() == true ? picker.SelectedFolder : null;
        }

        // ── Hotkey key capture (view concern) ─────────────────────────────────

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
    }
}
