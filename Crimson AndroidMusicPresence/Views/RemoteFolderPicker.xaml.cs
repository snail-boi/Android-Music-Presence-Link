using System.Windows;

namespace musicpresense
{
    /// <summary>
    /// Remote folder browser. The tree, the lazy loading, and the selection now live in
    /// <see cref="RemoteFolderPickerViewModel"/> and <see cref="RemoteFolderNode"/>. This
    /// code-behind keeps the Create factory (which deals with window ownership, a view
    /// concern), builds the VM, and closes the dialog when asked.
    ///
    /// Create, the constructor, and SelectedFolder are unchanged, so MainWindow_RemoteFolders
    /// and OnboardingWindow need no edits.
    /// </summary>
    public partial class RemoteFolderPicker : Window
    {
        private readonly RemoteFolderPickerViewModel _vm;

        public string SelectedFolder => _vm.SelectedFolder;

        public static RemoteFolderPicker Create(string device, DependencyObject? ownerSource = null)
        {
            var picker = new RemoteFolderPicker(device);
            var owner = ownerSource as Window
                        ?? (ownerSource != null ? Window.GetWindow(ownerSource) : null)
                        ?? Application.Current?.MainWindow;

            if (owner != null)
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
    }
}
