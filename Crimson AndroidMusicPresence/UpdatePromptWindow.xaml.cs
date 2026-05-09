using System.Windows;

namespace musicpresense
{
    public enum UpdatePromptChoice
    {
        Install,
        RemindLater,
        Ignore
    }

    public partial class UpdatePromptWindow : Window
    {
        public UpdatePromptChoice Choice { get; private set; } = UpdatePromptChoice.RemindLater;

        public UpdatePromptWindow(string latestVersion, string patchNotes, bool allowRemindLater)
        {
            InitializeComponent();
            TxtSummary.Text = $"A new version {latestVersion} is available.";
            TxtNotes.Text = string.IsNullOrWhiteSpace(patchNotes) ? "No patch notes available." : patchNotes.Trim();
            TxtPrompt.Text = "Do you want to download and install it now?";
            BtnLater.Visibility = allowRemindLater ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnYes_Click(object sender, RoutedEventArgs e)
        {
            Choice = UpdatePromptChoice.Install;
            DialogResult = true;
            Close();
        }

        private void BtnLater_Click(object sender, RoutedEventArgs e)
        {
            Choice = UpdatePromptChoice.RemindLater;
            DialogResult = null;
            Close();
        }

        private void BtnNo_Click(object sender, RoutedEventArgs e)
        {
            Choice = UpdatePromptChoice.Ignore;
            DialogResult = false;
            Close();
        }
    }
}
