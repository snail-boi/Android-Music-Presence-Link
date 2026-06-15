using System;
using System.Text.RegularExpressions;
using System.Windows.Controls;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// Inline lyrics editor hosted inside MetadataEditWindow. It edits a draft and reports
    /// the result through events rather than touching the device: Save raises Saved with the
    /// text and the save-as-lrc choice (the window stages it into the metadata edit), Clear
    /// empties the box so the user can save empty to remove lyrics, and Cancel discards.
    /// Sync state is shown live by scanning the text for [mm:ss] stamps.
    /// </summary>
    public partial class LyricsEditorControl : UserControl
    {
        private static readonly Regex TimestampRegex = new Regex(@"\[\d{1,2}:\d{2}", RegexOptions.Compiled);

        public event Action<string, bool>? Saved;
        public event Action? Cancelled;

        public LyricsEditorControl()
        {
            InitializeComponent();
        }

        /// <summary>Seed the editor when it opens. lrcLocked forces and disables the .lrc box (WAV).</summary>
        public void SetData(string? text, bool saveAsLrc, bool lrcLocked)
        {
            Txt.Text = text ?? string.Empty;
            ChkLrc.IsChecked = lrcLocked || saveAsLrc;
            ChkLrc.IsEnabled = !lrcLocked;
            UpdateStatus();
            Txt.Focus();
            Txt.CaretIndex = Txt.Text.Length;
        }

        private void Txt_TextChanged(object sender, TextChangedEventArgs e) => UpdateStatus();

        private void UpdateStatus()
        {
            string t = Txt.Text;
            if (string.IsNullOrWhiteSpace(t))
                LblStatus.Text = "empty";
            else
                LblStatus.Text = TimestampRegex.IsMatch(t) ? "synced (timed)" : "unsynced (plain text)";
        }

        private void Save_Click(object sender, System.Windows.RoutedEventArgs e)
            => Saved?.Invoke(Txt.Text, ChkLrc.IsChecked == true);

        private void Clear_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            Txt.Clear();
            Txt.Focus();
        }

        private void Cancel_Click(object sender, System.Windows.RoutedEventArgs e)
            => Cancelled?.Invoke();
    }
}
