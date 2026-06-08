using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace musicpresense
{
    /// <summary>
    /// One folder in the remote folder tree. Children are loaded lazily: a real node starts
    /// with a single empty placeholder child so the TreeView draws an expand arrow, and the
    /// first time the node is expanded it replaces that placeholder with the actual
    /// subfolders fetched over ADB.
    ///
    /// IsExpanded and IsSelected are bound two-way to the TreeViewItem (see the
    /// ItemContainerStyle in the XAML), which is how a TreeView talks to a ViewModel:
    /// expanding triggers the load, selecting reports the path back up to the picker.
    /// </summary>
    public sealed class RemoteFolderNode : ViewModelBase
    {
        private readonly Func<string, Task<List<string>>>? _loadSubdirs;
        private readonly Action<RemoteFolderNode>? _onSelected;
        private bool _childrenLoaded;

        public string Name { get; }
        public string Path { get; }
        public ObservableCollection<RemoteFolderNode> Children { get; } = new();

        // A real folder node. The placeholder child gives it an expand arrow up front.
        public RemoteFolderNode(string name, string path,
            Func<string, Task<List<string>>> loadSubdirs, Action<RemoteFolderNode> onSelected)
        {
            Name = name;
            Path = path;
            _loadSubdirs = loadSubdirs;
            _onSelected = onSelected;
            Children.Add(new RemoteFolderNode());
        }

        // Empty placeholder child, replaced by the real folders on first expand.
        private RemoteFolderNode()
        {
            Name = string.Empty;
            Path = string.Empty;
        }

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (!Set(ref _isExpanded, value)) return;
                if (value && !_childrenLoaded && _loadSubdirs != null)
                    _ = LoadChildrenAsync();
            }
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (!Set(ref _isSelected, value)) return;
                if (value)
                    _onSelected?.Invoke(this);
            }
        }

        private async Task LoadChildrenAsync()
        {
            _childrenLoaded = true; // set first, so a fast collapse/expand does not load twice

            var subdirs = await _loadSubdirs!(Path).ConfigureAwait(true);

            Children.Clear(); // drop the placeholder
            foreach (var dir in subdirs)
                Children.Add(new RemoteFolderNode(dir, Path + "/" + dir, _loadSubdirs!, _onSelected!));
        }
    }
}
