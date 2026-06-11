using System;
using System.Windows;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// Manage Apps window. All state and logic now live in
    /// <see cref="AppsManagerViewModel"/>. This code-behind only builds the VM, sets it as
    /// the DataContext, kicks off the initial load when the window is shown, and closes the
    /// dialog when the VM asks.
    ///
    /// "Which device is connected" is the one thing the VM should not reach into App for,
    /// so it is passed in as a delegate from here, where touching App is appropriate.
    ///
    /// The constructor signature is unchanged, so MainWindow_Apps needs no edits. Cancel is
    /// handled by IsCancel="True" in the XAML (click and Esc both close with no result),
    /// exactly as before.
    /// </summary>
    public partial class AppsManagerWindow : Window
    {
        private readonly AppsManagerViewModel _vm;

        public AppsManagerWindow(MusicConfig config, Action<MusicConfig> onSaved)
        {
            InitializeComponent();

            _vm = new AppsManagerViewModel(
                config,
                onSaved,
                () => (Application.Current as App)?.GetCurrentDevice() ?? string.Empty);

            DataContext = _vm;
            _vm.RequestClose += OnRequestClose;

            Loaded += (_, _) => _ = _vm.LoadAppsAsync();
        }

        private void OnRequestClose(bool result)
        {
            DialogResult = result;
            Close();
        }
    }
}
