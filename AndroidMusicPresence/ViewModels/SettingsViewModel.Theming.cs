using System;
using System.Collections.Generic;
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
                {
                    _selectedTheme.PropertyChanged -= SelectedTheme_PropertyChanged;
                    _selectedTheme.IsActiveTheme = false;
                }

                _selectedTheme = value;

                if (_selectedTheme != null)
                {
                    _selectedTheme.IsActiveTheme = true;
                    _selectedTheme.PropertyChanged += SelectedTheme_PropertyChanged;
                }

                RaisePropertyChanged();
                RaisePropertyChanged(nameof(ActiveThemeName));
                RaisePropertyChanged(nameof(SelectedThemeName));
                RaisePropertyChanged(nameof(IsCustomSelected));
                RaisePropertyChanged(nameof(IsBuiltInSelected));
                NotifyThemePreview();
            }
        }

        // Random-theme-at-startup toggle.
        private bool _randomThemeAtStartup;
        public bool RandomThemeAtStartup { get => _randomThemeAtStartup; set => Set(ref _randomThemeAtStartup, value); }

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
            // Transient UI state doesn't affect the applied theme — no preview re-apply.
            if (e.PropertyName == nameof(ThemeListItem.IsActiveTheme) ||
                e.PropertyName == nameof(ThemeListItem.IsConfirmingRemove) ||
                e.PropertyName == nameof(ThemeListItem.IsEditing))
                return;

            if (e.PropertyName == nameof(ThemeListItem.Name))
            {
                RaisePropertyChanged(nameof(ActiveThemeName));
                RaisePropertyChanged(nameof(SelectedThemeName));
            }
            // Any change to the active theme (color or name) re-applies the live preview.
            NotifyThemePreview();
        }

        // ── Category tabs + paging ────────────────────────────────────────────
        // Themes are browsed in three categories, each with its own 5-per-page pager:
        //   Default  — built-in themes still in rotation
        //   Custom   — user themes still in rotation
        //   Disabled — any theme removed from the random/cycle rotation
        private const int ThemesPerPage = 5;
        private const string TabDefault = "Default";
        private const string TabCustom = "Custom";
        private const string TabDisabled = "Disabled";

        private string _themeTab = TabDefault;
        private int _themePageCount = 1;
        private readonly Dictionary<string, int> _themePages = new Dictionary<string, int>
        {
            [TabDefault] = 0,
            [TabCustom] = 0,
            [TabDisabled] = 0
        };

        /// <summary>The slice of themes shown on the current tab's current page.</summary>
        public ObservableCollection<ThemeListItem> ThemePageItems { get; } = new ObservableCollection<ThemeListItem>();

        public bool IsDefaultThemeTab { get => _themeTab == TabDefault; set { if (value) SetThemeTab(TabDefault); } }
        public bool IsCustomThemeTab { get => _themeTab == TabCustom; set { if (value) SetThemeTab(TabCustom); } }
        public bool IsDisabledThemeTab { get => _themeTab == TabDisabled; set { if (value) SetThemeTab(TabDisabled); } }

        public string ThemePageLabel => $"{_themePages[_themeTab] + 1} / {_themePageCount}";
        public bool CanPrevThemePage => _themePages[_themeTab] > 0;
        public bool CanNextThemePage => _themePages[_themeTab] < _themePageCount - 1;
        public bool ThemePagerVisible => _themePageCount > 1;
        public bool ThemePageEmpty => ThemePageItems.Count == 0;

        public string ThemePageEmptyHint => _themeTab switch
        {
            TabCustom => "No custom themes yet — press New theme to create one.",
            TabDisabled => "No disabled themes.",
            _ => "All built-in themes are disabled."
        };

        private void SetThemeTab(string tab)
        {
            if (_themeTab == tab)
                return;
            _themeTab = tab;
            ClearRemoveConfirms();
            RaisePropertyChanged(nameof(IsDefaultThemeTab));
            RaisePropertyChanged(nameof(IsCustomThemeTab));
            RaisePropertyChanged(nameof(IsDisabledThemeTab));
            RefreshThemePage();
        }

        private List<ThemeListItem> CurrentCategoryThemes() => _themeTab switch
        {
            TabCustom => Themes.Where(t => !t.IsBuiltIn && t.InRotation).ToList(),
            TabDisabled => Themes.Where(t => !t.InRotation).ToList(),
            _ => Themes.Where(t => t.IsBuiltIn && t.InRotation).ToList()
        };

        private void RefreshThemePage()
        {
            var list = CurrentCategoryThemes();
            _themePageCount = Math.Max(1, (list.Count + ThemesPerPage - 1) / ThemesPerPage);
            int page = Math.Clamp(_themePages[_themeTab], 0, _themePageCount - 1);
            _themePages[_themeTab] = page;

            ThemePageItems.Clear();
            foreach (var t in list.Skip(page * ThemesPerPage).Take(ThemesPerPage))
                ThemePageItems.Add(t);

            // If the theme being edited scrolled out of view (tab switch, paging, disable,
            // remove), close the editor rather than leaving it open with no Save button.
            if (IsThemeEditorOpen && !ThemePageItems.Any(t => t.IsEditing))
                CloseThemeEditor();

            RaisePropertyChanged(nameof(ThemePageLabel));
            RaisePropertyChanged(nameof(CanPrevThemePage));
            RaisePropertyChanged(nameof(CanNextThemePage));
            RaisePropertyChanged(nameof(ThemePagerVisible));
            RaisePropertyChanged(nameof(ThemePageEmpty));
            RaisePropertyChanged(nameof(ThemePageEmptyHint));
        }

        private void ClearRemoveConfirms()
        {
            foreach (var t in Themes)
                t.IsConfirmingRemove = false;
        }

        // ── Edit mode ─────────────────────────────────────────────────────────
        // The name/color editor is hidden until a row's Edit button opens it; the same
        // button reads Save while open and closes the editor again. Edits apply to the
        // item immediately (live preview) and persist with the window's Save like before.
        private bool _isThemeEditorOpen;
        public bool IsThemeEditorOpen
        {
            get => _isThemeEditorOpen;
            private set => Set(ref _isThemeEditorOpen, value);
        }

        private void CloseThemeEditor()
        {
            foreach (var t in Themes)
                t.IsEditing = false;
            IsThemeEditorOpen = false;
        }

        private void OpenThemeEditor(ThemeListItem item)
        {
            ClearRemoveConfirms();
            CloseThemeEditor();
            SelectedTheme = item; // editing also applies the theme, so edits preview live
            item.IsEditing = true;
            IsThemeEditorOpen = true;
        }

        private RelayCommand<ThemeListItem>? _toggleEditThemeCommand;
        public RelayCommand<ThemeListItem> ToggleEditThemeCommand => _toggleEditThemeCommand ??= new RelayCommand<ThemeListItem>(item =>
        {
            if (item == null || item.IsBuiltIn)
                return;
            if (item.IsEditing)
                CloseThemeEditor(); // Save: edits are already on the item, just close
            else
                OpenThemeEditor(item);
        });

        private RelayCommand? _prevThemePageCommand;
        public RelayCommand PrevThemePageCommand => _prevThemePageCommand ??= new RelayCommand(() =>
        {
            if (!CanPrevThemePage)
                return;
            _themePages[_themeTab]--;
            ClearRemoveConfirms();
            RefreshThemePage();
        });

        private RelayCommand? _nextThemePageCommand;
        public RelayCommand NextThemePageCommand => _nextThemePageCommand ??= new RelayCommand(() =>
        {
            if (!CanNextThemePage)
                return;
            _themePages[_themeTab]++;
            ClearRemoveConfirms();
            RefreshThemePage();
        });

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

        // Clicking a row applies that theme (live preview; persisted on Save).
        private RelayCommand<ThemeListItem>? _selectThemeItemCommand;
        public RelayCommand<ThemeListItem> SelectThemeItemCommand => _selectThemeItemCommand ??= new RelayCommand<ThemeListItem>(item =>
        {
            if (item == null)
                return;
            ClearRemoveConfirms();
            if (!item.IsEditing)
                CloseThemeEditor();
            SelectedTheme = item;
        });

        // Remove is a two-step confirm: the first click flips the row's buttons to
        // "Are you sure?" (Yes/No); only Yes actually deletes. Built-ins can't be removed.
        private RelayCommand<ThemeListItem>? _requestRemoveThemeCommand;
        public RelayCommand<ThemeListItem> RequestRemoveThemeCommand => _requestRemoveThemeCommand ??= new RelayCommand<ThemeListItem>(item =>
        {
            if (item == null || item.IsBuiltIn)
                return;
            ClearRemoveConfirms();
            item.IsConfirmingRemove = true;
        });

        private RelayCommand<ThemeListItem>? _cancelRemoveThemeCommand;
        public RelayCommand<ThemeListItem> CancelRemoveThemeCommand => _cancelRemoveThemeCommand ??= new RelayCommand<ThemeListItem>(item =>
        {
            if (item != null)
                item.IsConfirmingRemove = false;
        });

        private RelayCommand<ThemeListItem>? _confirmRemoveThemeCommand;
        public RelayCommand<ThemeListItem> ConfirmRemoveThemeCommand => _confirmRemoveThemeCommand ??= new RelayCommand<ThemeListItem>(item =>
        {
            if (item == null || item.IsBuiltIn)
                return;

            int idx = Themes.IndexOf(item);
            Themes.Remove(item);

            if (ReferenceEquals(_selectedTheme, item))
            {
                int next = Math.Clamp(idx - 1, 0, Math.Max(0, Themes.Count - 1));
                SelectedTheme = Themes.Count > 0 ? Themes[next] : null;
            }
            RefreshThemePage();
        });

        // Disable moves a theme to the Disabled category: it's skipped by cycling and the
        // random-at-startup pick. The last in-rotation theme can't be disabled (cycling
        // needs a target). Enable moves it back to its home category.
        private RelayCommand<ThemeListItem>? _toggleThemeDisabledCommand;
        public RelayCommand<ThemeListItem> ToggleThemeDisabledCommand => _toggleThemeDisabledCommand ??= new RelayCommand<ThemeListItem>(item =>
        {
            if (item == null)
                return;

            if (item.InRotation && Themes.Count(t => t.InRotation) <= 1)
            {
                Interaction?.ShowWarning("At least one theme must stay in rotation.", "Themes");
                return;
            }

            item.InRotation = !item.InRotation;
            ClearRemoveConfirms();
            RefreshThemePage();
        });

        // New theme: creates an editable copy of the current theme, selects it (which pops
        // the name/color editors open) and jumps the pager to it on the Custom tab.
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

            var customs = Themes.Where(t => !t.IsBuiltIn && t.InRotation).ToList();
            _themePages[TabCustom] = Math.Max(0, customs.IndexOf(item)) / ThemesPerPage;
            SetThemeTab(TabCustom);
            RefreshThemePage();
            OpenThemeEditor(item);
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

            var disabled = new HashSet<string>(
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

            // Reset the browser to the first page of the tab holding the active theme.
            _themePages[TabDefault] = _themePages[TabCustom] = _themePages[TabDisabled] = 0;
            _themeTab = active == null ? TabDefault
                      : !active.InRotation ? TabDisabled
                      : active.IsBuiltIn ? TabDefault
                      : TabCustom;
            RaisePropertyChanged(nameof(IsDefaultThemeTab));
            RaisePropertyChanged(nameof(IsCustomThemeTab));
            RaisePropertyChanged(nameof(IsDisabledThemeTab));
            RefreshThemePage();
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
