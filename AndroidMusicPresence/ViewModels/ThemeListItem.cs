namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// Bindable wrapper around a <see cref="ThemeProfile"/> for the theme list/editor in the
    /// settings window. Built-in items are read-only (their name and colors can't be edited);
    /// custom items are fully editable and persist back to <see cref="MusicConfig.Theme.CustomProfiles"/>.
    /// Color changes raise PropertyChanged so swatch previews and the live theme preview update.
    /// </summary>
    internal sealed class ThemeListItem : ViewModelBase
    {
        public bool IsBuiltIn { get; }

        public ThemeListItem(ThemeProfile profile, bool isBuiltIn)
        {
            IsBuiltIn = isBuiltIn;
            _name = profile.Name ?? string.Empty;
            _background = profile.Background ?? string.Empty;
            _accent = profile.Accent ?? string.Empty;
            _foreground = profile.Foreground ?? string.Empty;
        }

        private string _name;
        public string Name { get => _name; set => Set(ref _name, value ?? string.Empty); }

        private string _background;
        public string Background { get => _background; set => Set(ref _background, value ?? string.Empty); }

        private string _accent;
        public string Accent { get => _accent; set => Set(ref _accent, value ?? string.Empty); }

        private string _foreground;
        public string Foreground { get => _foreground; set => Set(ref _foreground, value ?? string.Empty); }

        // Whether this theme is part of the cycle rotation (the top-corner button). Disabled
        // themes stay selectable in the list but are skipped when cycling.
        private bool _inRotation = true;
        public bool InRotation
        {
            get => _inRotation;
            set { if (Set(ref _inRotation, value)) RaisePropertyChanged(nameof(IsDisabled)); }
        }

        public bool IsDisabled => !_inRotation;

        // Transient UI state (never persisted): whether this item is the active/applied
        // theme (drives the accent border + checkmark in the paged list) and whether its
        // Remove button is in the "Are you sure?" confirm state.
        private bool _isActiveTheme;
        public bool IsActiveTheme { get => _isActiveTheme; set => Set(ref _isActiveTheme, value); }

        private bool _isConfirmingRemove;
        public bool IsConfirmingRemove { get => _isConfirmingRemove; set => Set(ref _isConfirmingRemove, value); }

        // Whether this theme's editor panel is open (the row's Edit button reads Save).
        private bool _isEditing;
        public bool IsEditing { get => _isEditing; set => Set(ref _isEditing, value); }

        public ThemeProfile ToProfile() => new ThemeProfile
        {
            Name = _name ?? string.Empty,
            Background = _background ?? string.Empty,
            Accent = _accent ?? string.Empty,
            Foreground = _foreground ?? string.Empty
        };
    }
}
