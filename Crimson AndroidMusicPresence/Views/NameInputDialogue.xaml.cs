using System.Windows;

namespace musicpresense
{
    /// <summary>
    /// Small text-input dialog. The title and prompt are passed in; the typed value comes
    /// back through <see cref="InputText"/> after a true DialogResult. All of that now
    /// lives in <see cref="NameInputDialogueViewModel"/>. Focus is handed to the text box
    /// by FocusManager in the XAML, so the constructor no longer touches controls.
    ///
    /// The constructor signature and InputText are unchanged, so the callers in
    /// MainWindow_DeviceSetup, MainWindow_Wifi, and OnboardingWindow need no edits.
    /// </summary>
    public partial class NameInputDialogue : Window
    {
        private readonly NameInputDialogueViewModel _vm;

        public string InputText => _vm.InputText;

        public NameInputDialogue(string title, string prompt)
        {
            InitializeComponent();

            _vm = new NameInputDialogueViewModel(title, prompt);
            DataContext = _vm;
            _vm.RequestClose += OnRequestClose;
        }

        private void OnRequestClose(bool result)
        {
            DialogResult = result;
            Close();
        }
    }
}
