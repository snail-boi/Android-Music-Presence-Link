using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// ViewModel for RemoteFolderPicker. Owns the tree (a single "Internal Storage" root),
    /// tracks the currently selected folder, and provides the ADB directory listing that
    /// each node calls when it is expanded.
    ///
    /// SelectedFolder has a public setter so the path TextBox can write into it directly
    /// via two-way binding. The tree also writes it via OnNodeSelected.
    /// </summary>
    public sealed class RemoteFolderPickerViewModel : ViewModelBase
    {
        private const string InitialPath = "/storage/emulated/0";

        private readonly string _device;

        // Raised when the dialog should close. The bool becomes DialogResult.
        public event Action<bool>? RequestClose;

        // Bound to TreeView.ItemsSource.
        public ObservableCollection<RemoteFolderNode> Roots { get; } = new();

        private string _selectedFolder = string.Empty;
        public string SelectedFolder
        {
            get => _selectedFolder;
            set => Set(ref _selectedFolder, value);
            // WPF re-evaluates CanExecute via CommandManager.RequerySuggested automatically.
        }

        public RelayCommand OkCommand { get; }
        public RelayCommand CancelCommand { get; }

        public RemoteFolderPickerViewModel(string device)
        {
            _device = device;

            OkCommand = new RelayCommand(
                () => RequestClose?.Invoke(true),
                () => !string.IsNullOrWhiteSpace(SelectedFolder));

            CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));

            var root = new RemoteFolderNode("Internal Storage", InitialPath, GetRemoteDirectoriesAsync, OnNodeSelected);
            Roots.Add(root);

            // Pre-expand root so the first level is visible immediately.
            root.IsExpanded = true;
        }

        private void OnNodeSelected(RemoteFolderNode node)
        {
            SelectedFolder = node.Path;
        }

        private async System.Threading.Tasks.Task<List<string>> GetRemoteDirectoriesAsync(string path)
        {
            string cmd = $"shell ls -d \"{path}\"/*/";
            string output = await AdbHelper.RunAdbCaptureAsync($"-s {_device} {cmd}").ConfigureAwait(true);

            var dirs = new List<string>();
            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("ls:", StringComparison.OrdinalIgnoreCase)) continue;

                string name = trimmed.TrimEnd('/');
                if (!string.IsNullOrEmpty(name))
                    dirs.Add(System.IO.Path.GetFileName(name));
            }
            return dirs;
        }
    }
}