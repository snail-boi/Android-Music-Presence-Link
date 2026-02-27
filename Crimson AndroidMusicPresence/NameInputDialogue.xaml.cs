using System.Windows;

namespace Crimson_AndroidMusicPresence
{
    public partial class NameInputDialogue : Window
    {
        public string InputText { get; private set; }
            
        public NameInputDialogue(string title, string prompt)
        {
            InitializeComponent();
            Title = title;
            PromptText.Text = prompt;
            TxtInput.Focus();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            InputText = TxtInput.Text.Trim();
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}