using System;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// ViewModel for NameInputDialogue. Holds the window title, the prompt label, and the
    /// text the user types. OK trims the input and asks the view to close with a true
    /// result; Cancel closes with false. The view reads InputText back afterwards.
    /// </summary>
    public sealed class NameInputDialogueViewModel : ViewModelBase
    {
        // Raised when the dialog should close. The bool becomes DialogResult.
        public event Action<bool>? RequestClose;

        private string _windowTitle = "Input";
        public string WindowTitle
        {
            get => _windowTitle;
            set => Set(ref _windowTitle, value);
        }

        private string _promptText = string.Empty;
        public string PromptText
        {
            get => _promptText;
            set => Set(ref _promptText, value);
        }

        private string _inputText = string.Empty;
        public string InputText
        {
            get => _inputText;
            set => Set(ref _inputText, value);
        }

        public RelayCommand OkCommand { get; }
        public RelayCommand CancelCommand { get; }

        public NameInputDialogueViewModel(string title, string prompt)
        {
            _windowTitle = title;
            _promptText = prompt;

            OkCommand = new RelayCommand(Ok);
            CancelCommand = new RelayCommand(Cancel);
        }

        private void Ok()
        {
            InputText = (InputText ?? string.Empty).Trim();
            RequestClose?.Invoke(true);
        }

        private void Cancel()
        {
            RequestClose?.Invoke(false);
        }
    }
}
