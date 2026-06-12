using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace AndroidMusicPresenceLink
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
    ///
    /// IsLoading is true while the ADB call is in flight so the XAML can show a spinner.
    /// HasError is set when the ADB call returns nothing (permission denied, empty dir, etc.)
    /// HasNoChildren is set when the directory loaded successfully but is empty.
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
            Children.Add(new RemoteFolderNode());   // placeholder -> expand arrow
        }

        // Empty placeholder child, replaced by real folders on first expand.
        private RemoteFolderNode()
        {
            Name = string.Empty;
            Path = string.Empty;
        }

        // ── State flags ──────────────────────────────────────────────────────

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            private set => Set(ref _isLoading, value);
        }

        private bool _hasError;
        public bool HasError
        {
            get => _hasError;
            private set => Set(ref _hasError, value);
        }

        private bool _hasNoChildren;
        public bool HasNoChildren
        {
            get => _hasNoChildren;
            private set => Set(ref _hasNoChildren, value);
        }

        // ── Tree bindings ────────────────────────────────────────────────────

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

        // ── Loading ──────────────────────────────────────────────────────────

        private async Task LoadChildrenAsync()
        {
            _childrenLoaded = true;     // guard against re-entrant expand during load
            IsLoading = true;
            HasError = false;
            HasNoChildren = false;

            try
            {
                var subdirs = await _loadSubdirs!(Path).ConfigureAwait(true);

                Children.Clear();       // drop the placeholder (or previous result)

                if (subdirs.Count == 0)
                {
                    HasNoChildren = true;
                }
                else
                {
                    foreach (var dir in subdirs)
                        Children.Add(new RemoteFolderNode(dir, Path + "/" + dir, _loadSubdirs!, _onSelected!));
                }
            }
            catch
            {
                Children.Clear();
                HasError = true;
            }
            finally
            {
                IsLoading = false;
            }
        }
    }
}