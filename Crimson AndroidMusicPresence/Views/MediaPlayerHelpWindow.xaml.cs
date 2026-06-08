using System.Windows;

namespace musicpresense
{
    /// <summary>
    /// Themed help dialog. It is pure static content with a single Close button, so it does
    /// not get a ViewModel: there is no state to bind and no logic to run. The button closes
    /// the dialog by itself through IsCancel/IsDefault (this window is always shown with
    /// ShowDialog), so there is no Click handler either. Theme brushes come from
    /// App.Resources via DynamicResource, so dark/light mode works with no wiring here.
    /// </summary>
    public partial class MediaPlayerHelpWindow : Window
    {
        public MediaPlayerHelpWindow()
        {
            InitializeComponent();
        }
    }
}
