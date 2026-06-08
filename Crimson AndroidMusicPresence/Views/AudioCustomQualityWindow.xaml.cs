using System.Windows;

namespace musicpresense
{
    /// <summary>
    /// Audio quality window with two modes:
    ///
    /// Hotkey mode (showPresets = true): shows a preset combobox at the top so the
    /// user can pick a preset OR dial in custom values. This is for non-UI users who
    /// trigger the window via the global hotkey without the media player open.
    ///
    /// Media player mode (showPresets = false): shows only the custom codec/bitrate/
    /// buffer/FLAC fields.
    ///
    /// All state and logic now live in <see cref="AudioCustomQualityViewModel"/>. This
    /// code-behind only sets the DataContext and reacts to the two things a ViewModel
    /// cannot do on its own: close the dialog (DialogResult is a Window concept) and
    /// show a validation MessageBox plus focus the offending field.
    ///
    /// On confirm, <see cref="ResultConfig"/> is populated and <see cref="DialogResult"/>
    /// is true. The public surface is unchanged, so App needs no edits.
    /// </summary>
    public partial class AudioCustomQualityWindow : Window
    {
        private readonly AudioCustomQualityViewModel _vm;

        // Read back by App after ShowDialog(). Null if cancelled.
        public (string Codec, string Bitrate, int BufferMs, int FlacLevel)? ResultConfig => _vm.ResultConfig;

        public AudioCustomQualityWindow(MusicConfig current, bool showPresets = false)
        {
            InitializeComponent();

            _vm = new AudioCustomQualityViewModel(current, showPresets);
            DataContext = _vm;

            _vm.RequestClose += OnRequestClose;
            _vm.ValidationRequested += OnValidationRequested;
        }

        private void OnRequestClose(bool result)
        {
            DialogResult = result;
            Close();
        }

        private void OnValidationRequested(AudioCustomQualityViewModel.ValidationRequest request)
        {
            MessageBox.Show(request.Message, request.Title, MessageBoxButton.OK, MessageBoxImage.Warning);

            switch (request.Focus)
            {
                case AudioCustomQualityViewModel.FocusTarget.Bitrate:
                    TxtBitrate.Focus();
                    break;
                case AudioCustomQualityViewModel.FocusTarget.Buffer:
                    TxtBuffer.Focus();
                    break;
                case AudioCustomQualityViewModel.FocusTarget.FlacLevel:
                    TxtFlacLevel.Focus();
                    break;
            }
        }
    }
}
