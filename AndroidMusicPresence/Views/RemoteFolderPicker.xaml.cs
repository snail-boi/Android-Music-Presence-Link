using System.Windows;
using System.Windows.Controls;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// Remote folder browser. The tree, the lazy loading, and the selection now live in
    /// <see cref="RemoteFolderPickerViewModel"/> and <see cref="RemoteFolderNode"/>. This
    /// code-behind keeps the Create factory (which deals with window ownership, a view
    /// concern), builds the VM, and closes the dialog when asked.
    ///
    /// Path sync is done here rather than through complex XAML bindings:
    ///   - Tree selection -> PathBox.Text via SelectedItemChanged
    ///   - PathBox edits  -> VM.SelectedFolder via TextChanged
    /// This avoids the circular update problem and keeps both in sync reliably.
    /// </summary>
    public partial class RemoteFolderPicker : Window
    {
        private readonly RemoteFolderPickerViewModel _vm;
        private bool _updatingPath;     // guards against circular PathBox <-> VM updates

        public string SelectedFolder => _vm.SelectedFolder;

        public static RemoteFolderPicker Create(string device, DependencyObject? ownerSource = null)
        {
            var picker = new RemoteFolderPicker(device);
            var owner = ownerSource as Window
                        ?? (ownerSource != null ? Window.GetWindow(ownerSource) : null)
                        ?? Application.Current?.MainWindow;

            // Only assign Owner if the window has a live HWND. A window that was
            // constructed but never shown (e.g. hidden on startup in media-player mode)
            // throws InvalidOperationException when Owner is set.
            if (owner != null && owner.IsLoaded)
                picker.Owner = owner;

            return picker;
        }

        public RemoteFolderPicker(string device)
        {
            InitializeComponent();

            _vm = new RemoteFolderPickerViewModel(device);
            DataContext = _vm;
            _vm.RequestClose += OnRequestClose;
        }

        private void OnRequestClose(bool result)
        {
            DialogResult = result;
            Close();
        }

        // Tree selection -> sync to PathBox without re-triggering TextChanged
        private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is not RemoteFolderNode node) return;

            _updatingPath = true;
            PathBox.Text = node.Path;
            _vm.SelectedFolder = node.Path;
            _updatingPath = false;
        }

        // PathBox edit -> sync to VM (tree selection not updated since we don't navigate it)
        private void PathBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_updatingPath) return;
            _vm.SelectedFolder = PathBox.Text;
        }
    }
}