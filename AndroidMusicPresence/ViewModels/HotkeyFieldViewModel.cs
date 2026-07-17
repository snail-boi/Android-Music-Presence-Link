using System;
using System.Linq;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// One hotkey row: the combo text, its Record command, and the inline no-modifier
    /// confirmation. When a recorded combo contains no modifier key, the row swaps its
    /// textbox and Record button for a keep/discard prompt (IsConfirming); answering No
    /// restores the previous combo. Inline instead of a toast because toasts can be
    /// disabled. Shared by the settings window and onboarding.
    /// </summary>
    internal sealed class HotkeyFieldViewModel : ViewModelBase
    {
        internal const string WaitingForKeyPress = "waiting for key press...";

        // Resolved at click time because the parent VM's recording delegate is injected
        // by the window after construction.
        private readonly Func<Action<Action<int[]?>>?> _getStartRecording;

        private string _previousText = string.Empty;
        private string _pendingText = string.Empty;

        public HotkeyFieldViewModel(Func<Action<Action<int[]?>>?> getStartRecording)
        {
            _getStartRecording = getStartRecording;
            RecordCommand = new RelayCommand(Record);
            ConfirmYesCommand = new RelayCommand(() => ResolveConfirmation(keep: true));
            ConfirmNoCommand = new RelayCommand(() => ResolveConfirmation(keep: false));
        }

        private string _text = string.Empty;
        public string Text { get => _text; set => Set(ref _text, value); }

        private bool _isConfirming;
        public bool IsConfirming
        {
            get => _isConfirming;
            private set
            {
                if (Set(ref _isConfirming, value))
                    RaisePropertyChanged(nameof(IsEditorVisible));
            }
        }

        public bool IsEditorVisible => !_isConfirming;

        private string _confirmationMessage = string.Empty;
        public string ConfirmationMessage { get => _confirmationMessage; private set => Set(ref _confirmationMessage, value); }

        public RelayCommand RecordCommand { get; }
        public RelayCommand ConfirmYesCommand { get; }
        public RelayCommand ConfirmNoCommand { get; }

        /// <summary>Loads a combo from config, dismissing any pending confirmation.</summary>
        public void SetFromConfig(string? text)
        {
            IsConfirming = false;
            Text = text ?? string.Empty;
        }

        private void Record()
        {
            var start = _getStartRecording();
            if (start == null || IsConfirming)
                return;

            _previousText = Text;
            Text = WaitingForKeyPress;

            start(keys =>
            {
                if (keys == null)
                {
                    // Cancelled (e.g. window lost focus): restore what was there.
                    Text = _previousText;
                    return;
                }

                if (keys.Length == 0)
                {
                    // Esc while recording: disable this hotkey (empty combo).
                    Text = string.Empty;
                    return;
                }

                var combo = HotkeyHelper.ComboToDisplayName(keys);
                Text = combo;

                if (!keys.Any(HotkeyHelper.IsModifier))
                {
                    _pendingText = combo;
                    ConfirmationMessage = $"{combo} has no modifier key, it may trigger accidentally while typing. Keep it?";
                    IsConfirming = true;
                }
            });
        }

        private void ResolveConfirmation(bool keep)
        {
            if (!IsConfirming)
                return;

            IsConfirming = false;
            Text = keep ? _pendingText : _previousText;
        }
    }
}
