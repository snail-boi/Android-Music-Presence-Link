using System.Windows;

namespace musicpresense
{
    /// <summary>
    /// Themed replacement for the old MessageBox-based help dialog. Shows what each
    /// button and control on the media player window does. Theme brushes are picked
    /// up from App.Resources via DynamicResource so dark/light mode works without any
    /// extra wiring here.
    /// </summary>
    public partial class MediaPlayerHelpWindow : Window
    {
        public MediaPlayerHelpWindow()
        {
            InitializeComponent();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
