using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Media;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// Theming section of the settings window. Themes are now full named profiles instead of
    /// a light/dark toggle. Three built-in profiles (Default Light, Default Dark, High
    /// Contrast) are always present and read-only; the user can create, edit, rename and
    /// delete their own profiles, each holding three colors (background, buttons/accent,
    /// text). One profile is "active" — that is the one applied to the app and the one whose
    /// colors the editor shows.
    ///
    /// Edits preview live: selecting/cycling a theme or editing the active custom theme's
    /// colors raises <see cref="ThemePreviewToken"/>, which MainWindow listens for and uses
    /// to re-apply the in-progress profile. Nothing is persisted until Save, like every
    /// other setting in this window.
    /// </summary>
    internal sealed partial class SettingsViewModel
    {
        // All selectable themes in display/cycle order: the three built-ins first, then the
        // user's custom profiles. Bound to the theme selector.
        public ObservableCollection<ThemeListItem> Themes { get; } = new ObservableCollection<ThemeListItem>();

        private ThemeListItem? _selectedTheme;
        public ThemeListItem? SelectedTheme
        {
            get => _selectedTheme;
            set
            {
                if (ReferenceEquals(_selectedTheme, value))
                    return;

                if (_selectedTheme != null)
                    _selectedTheme.PropertyChanged -= SelectedTheme_PropertyChanged;

                _selectedTheme = value;

                if (_selectedTheme != null)
                    _selectedTheme.PropertyChanged += SelectedTheme_PropertyChanged;

                RaisePropertyChanged();
                RaisePropertyChanged(nameof(ActiveThemeName));
                RaisePropertyChanged(nameof(SelectedThemeName));
                RaisePropertyChanged(nameof(IsCustomSelected));
                RaisePropertyChanged(nameof(IsBuiltInSelected));
                RaisePropertyChanged(nameof(RotationToggleLabel));
                NotifyThemePreview();
            }
        }

        // Random-theme-at-startup toggle.
        private bool _randomThemeAtStartup;
        public bool RandomThemeAtStartup { get => _randomThemeAtStartup; set => Set(ref _randomThemeAtStartup, value); }

        // Label for the built-in theme's rotation toggle button.
        public string RotationToggleLabel => (_selectedTheme?.InRotation ?? true) ? "Disable" : "Enable";

        // Name of the active theme — shown on the header cycle button.
        public string ActiveThemeName => _selectedTheme?.Name ?? string.Empty;

        public bool IsCustomSelected => _selectedTheme != null && !_selectedTheme.IsBuiltIn;
        public bool IsBuiltInSelected => _selectedTheme == null || _selectedTheme.IsBuiltIn;

        // Editable name for the active theme. Built-ins are rejected; custom names are trimmed,
        // capped at the limit, defaulted when blank, and de-duplicated. Validation lives here so
        // save stays a pure serialize (no mutation during the unsaved-changes check).
        public string SelectedThemeName
        {
            get => _selectedTheme?.Name ?? string.Empty;
            set
            {
                var item = _selectedTheme;
                if (item == null || item.IsBuiltIn)
                    return;

                var v = (value ?? string.Empty).Trim();
                if (v.Length > ThemeConfig.NameMaxLength)
                    v = v.Substring(0, ThemeConfig.NameMaxLength).Trim();
                if (string.IsNullOrEmpty(v))
                    v = "Custom Theme";
                v = MakeUniqueName(v, item);

                if (!string.Equals(item.Name, v, StringComparison.Ordinal))
                    item.Name = v; // raises Name change -> ActiveThemeName refresh + preview
                RaisePropertyChanged();
            }
        }

        // Swatch/editor previews bind to SelectedTheme.* directly via a hex->brush converter,
        // so no per-channel preview brushes are needed here.

        // Bumped purely to notify MainWindow that the live preview should be re-applied.
        public object? ThemePreviewToken => null;
        private void NotifyThemePreview() => RaisePropertyChanged(nameof(ThemePreviewToken));

        private void SelectedTheme_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ThemeListItem.Name))
            {
                RaisePropertyChanged(nameof(ActiveThemeName));
                RaisePropertyChanged(nameof(SelectedThemeName));
            }
            // Any change to the active theme (color or name) re-applies the live preview.
            NotifyThemePreview();
        }

        // ── Cycle / select / add / delete ─────────────────────────────────────

        private RelayCommand? _cycleThemeCommand;
        public RelayCommand CycleThemeCommand => _cycleThemeCommand ??= new RelayCommand(() =>
        {
            // Cycle only through in-rotation themes; disabled themes are skipped. Starting
            // from the current selection means a disabled active theme still advances to the
            // next enabled one.
            var rotation = Themes.Where(t => t.InRotation).ToList();
            if (rotation.Count == 0)
                return;

            int start = _selectedTheme != null ? Themes.IndexOf(_selectedTheme) : -1;
            for (int step = 1; step <= Themes.Count; step++)
            {
                var candidate = Themes[((start + step) % Themes.Count + Themes.Count) % Themes.Count];
                if (candidate.InRotation)
                {
                    SelectedTheme = candidate;
                    return;
                }
            }
        });

        // Built-in themes can't be deleted, but can be removed from / added back to the cycle
        // rotation. The last in-rotation theme can't be disabled (cycling needs a target).
        private RelayCommand? _toggleRotationCommand;
        public RelayCommand ToggleRotationCommand => _toggleRotationCommand ??= new RelayCommand(() =>
        {
            var item = _selectedTheme;
            if (item == null)
                return;

            if (item.InRotation && Themes.Count(t => t.InRotation) <= 1)
            {
                Interaction?.ShowWarning("At least one theme must stay in rotation.", "Themes");
                return;
            }

            item.InRotation = !item.InRotation;
            RaisePropertyChanged(nameof(RotationToggleLabel));
        });

        private RelayCommand? _addThemeCommand;
        public RelayCommand AddThemeCommand => _addThemeCommand ??= new RelayCommand(() =>
        {
            var src = _selectedTheme;
            var fallback = BuiltInThemes.DefaultDark;
            var item = new ThemeListItem(new ThemeProfile
            {
                Name = MakeUniqueName("Custom Theme", null),
                Background = src?.Background ?? fallback.Background,
                Accent = src?.Accent ?? fallback.Accent,
                Foreground = src?.Foreground ?? fallback.Foreground
            }, isBuiltIn: false);

            Themes.Add(item);
            SelectedTheme = item;
        });

        private RelayCommand? _deleteThemeCommand;
        public RelayCommand DeleteThemeCommand => _deleteThemeCommand ??= new RelayCommand(() =>
        {
            var item = _selectedTheme;
            if (item == null || item.IsBuiltIn)
                return;

            int idx = Themes.IndexOf(item);
            Themes.Remove(item);

            int next = Math.Clamp(idx - 1, 0, Math.Max(0, Themes.Count - 1));
            SelectedTheme = Themes.Count > 0 ? Themes[next] : null;
        });

        // ── Pick (color dialog) commands — edit the active custom theme ────────

        private RelayCommand? _pickBackgroundCommand;
        public RelayCommand PickBackgroundCommand => _pickBackgroundCommand ??= new RelayCommand(() =>
        {
            if (_selectedTheme == null || _selectedTheme.IsBuiltIn) return;
            _selectedTheme.Background = PickColor(_selectedTheme.Background, BuiltInThemes.DefaultDark.Background);
        });

        private RelayCommand? _pickAccentCommand;
        public RelayCommand PickAccentCommand => _pickAccentCommand ??= new RelayCommand(() =>
        {
            if (_selectedTheme == null || _selectedTheme.IsBuiltIn) return;
            _selectedTheme.Accent = PickColor(_selectedTheme.Accent, BuiltInThemes.DefaultDark.Accent);
        });

        private RelayCommand? _pickForegroundCommand;
        public RelayCommand PickForegroundCommand => _pickForegroundCommand ??= new RelayCommand(() =>
        {
            if (_selectedTheme == null || _selectedTheme.IsBuiltIn) return;
            _selectedTheme.Foreground = PickColor(_selectedTheme.Foreground, BuiltInThemes.DefaultDark.Foreground);
        });

        // ── Load / apply / preview ────────────────────────────────────────────

        partial void LoadThemingFromConfig()
        {
            if (_selectedTheme != null)
                _selectedTheme.PropertyChanged -= SelectedTheme_PropertyChanged;
            _selectedTheme = null;

            var disabled = new System.Collections.Generic.HashSet<string>(
                _config.Theme.DisabledProfiles ?? Enumerable.Empty<string>(), StringComparer.Ordinal);

            Themes.Clear();
            foreach (var b in BuiltInThemes.All)
                Themes.Add(new ThemeListItem(b, isBuiltIn: true) { InRotation = !disabled.Contains(b.Name) });
            foreach (var c in _config.Theme.CustomProfiles ?? Enumerable.Empty<ThemeProfile>())
                Themes.Add(new ThemeListItem(c, isBuiltIn: false) { InRotation = !disabled.Contains(c.Name) });

            _randomThemeAtStartup = _config.Theme.RandomAtStartup;

            var active = Themes.FirstOrDefault(t => string.Equals(t.Name, _config.Theme.ActiveProfile, StringComparison.Ordinal))
                         ?? Themes.FirstOrDefault();
            SelectedTheme = active;
        }

        partial void ApplyThemingToConfig(MusicConfig config)
        {
            config.Theme.CustomProfiles = Themes.Where(t => !t.IsBuiltIn).Select(t => t.ToProfile()).ToList();
            config.Theme.ActiveProfile = _selectedTheme?.Name ?? BuiltInThemes.DefaultDark.Name;
            config.Theme.DisabledProfiles = Themes.Where(t => !t.InRotation).Select(t => t.Name).ToList();
            config.Theme.RandomAtStartup = RandomThemeAtStartup;
            // Keep the legacy flag aligned with the active theme so the tray icon and the
            // media-player icon logic (which read UseDarkMode) stay correct.
            var activeProfile = _selectedTheme?.ToProfile() ?? BuiltInThemes.DefaultDark;
            config.Theme.UseDarkMode = ThemeCatalog.IsDark(activeProfile);
        }

        /// <summary>The active profile reflecting current (possibly unsaved) edits, for live preview.</summary>
        public ThemeProfile BuildActiveThemeProfile()
            => _selectedTheme?.ToProfile() ?? BuiltInThemes.DefaultDark;

        // ── Helpers ──────────────────────────────────────────────────────────

        // Returns a name not already used by any theme (case-insensitive), excluding the item
        // being renamed. Appends " 2", " 3", ... when needed, trimming to fit the length cap.
        private string MakeUniqueName(string candidate, ThemeListItem? exclude)
        {
            bool Taken(string name) => Themes.Any(t => !ReferenceEquals(t, exclude)
                && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

            if (!Taken(candidate))
                return candidate;

            for (int i = 2; ; i++)
            {
                var suffix = " " + i;
                var baseName = candidate;
                int max = ThemeConfig.NameMaxLength - suffix.Length;
                if (max > 0 && baseName.Length > max)
                    baseName = baseName.Substring(0, max).Trim();
                var attempt = baseName + suffix;
                if (!Taken(attempt))
                    return attempt;
            }
        }

        // Opens the native color dialog seeded with the current (or default) color and
        // returns the chosen color as "#RRGGBB", or the original value if cancelled.
        private static string PickColor(string? current, string fallback)
        {
            var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };

            var seed = TryParseMediaColor(current, out var c) ? c
                     : TryParseMediaColor(fallback, out var f) ? f
                     : Colors.Gray;
            dlg.Color = System.Drawing.Color.FromArgb(seed.R, seed.G, seed.B);

            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return current ?? string.Empty;

            var picked = dlg.Color;
            return $"#{picked.R:X2}{picked.G:X2}{picked.B:X2}";
        }

        private static bool TryParseMediaColor(string? hex, out Color color)
        {
            color = Colors.Black;
            if (string.IsNullOrWhiteSpace(hex))
                return false;
            try
            {
                if (ColorConverter.ConvertFromString(hex.Trim()) is Color c)
                {
                    color = c;
                    return true;
                }
            }
            catch { }
            return false;
        }
    }
}
