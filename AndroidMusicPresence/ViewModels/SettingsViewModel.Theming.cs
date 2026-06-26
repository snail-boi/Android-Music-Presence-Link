using System;
using System.Windows.Media;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// Theming section of the settings window. The user customizes three high-level colors
    /// (background, accent/button, text) independently for light mode and dark mode; every
    /// other brush (surface, border, accent hover/pressed) is derived from these in
    /// <see cref="App.ApplyTheme(bool)"/>. An empty field means "use the built-in default",
    /// which is what every existing config and fresh install gets until a color is picked.
    ///
    /// Edits preview live: each setter raises PropertyChanged, and MainWindow re-applies the
    /// theme with the in-progress (unsaved) overrides. The values are only persisted on Save,
    /// like every other setting in this window.
    /// </summary>
    internal sealed partial class SettingsViewModel
    {
        // Built-in defaults, shown in the swatches when a field is left blank. Kept in sync
        // with the base palette in App.ApplyThemeCore.
        private const string DefaultLightBackground = "#F7F7F7";
        private const string DefaultLightAccent = "#2D6CDF";
        private const string DefaultLightForeground = "#1A1A1A";
        private const string DefaultDarkBackground = "#1E1E1E";
        private const string DefaultDarkAccent = "#3E7BFF";
        private const string DefaultDarkForeground = "#EAEAEA";

        // ── Light mode ───────────────────────────────────────────────────────

        private string _lightBackgroundColor = string.Empty;
        public string LightBackgroundColor
        {
            get => _lightBackgroundColor;
            set { if (Set(ref _lightBackgroundColor, value)) RaisePropertyChanged(nameof(LightBackgroundPreview)); }
        }

        private string _lightAccentColor = string.Empty;
        public string LightAccentColor
        {
            get => _lightAccentColor;
            set { if (Set(ref _lightAccentColor, value)) RaisePropertyChanged(nameof(LightAccentPreview)); }
        }

        private string _lightForegroundColor = string.Empty;
        public string LightForegroundColor
        {
            get => _lightForegroundColor;
            set { if (Set(ref _lightForegroundColor, value)) RaisePropertyChanged(nameof(LightForegroundPreview)); }
        }

        public Brush LightBackgroundPreview => PreviewBrush(_lightBackgroundColor, DefaultLightBackground);
        public Brush LightAccentPreview => PreviewBrush(_lightAccentColor, DefaultLightAccent);
        public Brush LightForegroundPreview => PreviewBrush(_lightForegroundColor, DefaultLightForeground);

        // ── Dark mode ────────────────────────────────────────────────────────

        private string _darkBackgroundColor = string.Empty;
        public string DarkBackgroundColor
        {
            get => _darkBackgroundColor;
            set { if (Set(ref _darkBackgroundColor, value)) RaisePropertyChanged(nameof(DarkBackgroundPreview)); }
        }

        private string _darkAccentColor = string.Empty;
        public string DarkAccentColor
        {
            get => _darkAccentColor;
            set { if (Set(ref _darkAccentColor, value)) RaisePropertyChanged(nameof(DarkAccentPreview)); }
        }

        private string _darkForegroundColor = string.Empty;
        public string DarkForegroundColor
        {
            get => _darkForegroundColor;
            set { if (Set(ref _darkForegroundColor, value)) RaisePropertyChanged(nameof(DarkForegroundPreview)); }
        }

        public Brush DarkBackgroundPreview => PreviewBrush(_darkBackgroundColor, DefaultDarkBackground);
        public Brush DarkAccentPreview => PreviewBrush(_darkAccentColor, DefaultDarkAccent);
        public Brush DarkForegroundPreview => PreviewBrush(_darkForegroundColor, DefaultDarkForeground);

        // ── Pick (color dialog) commands ─────────────────────────────────────

        private RelayCommand? _pickLightBackgroundCommand;
        public RelayCommand PickLightBackgroundCommand => _pickLightBackgroundCommand ??=
            new RelayCommand(() => LightBackgroundColor = PickColor(LightBackgroundColor, DefaultLightBackground));

        private RelayCommand? _pickLightAccentCommand;
        public RelayCommand PickLightAccentCommand => _pickLightAccentCommand ??=
            new RelayCommand(() => LightAccentColor = PickColor(LightAccentColor, DefaultLightAccent));

        private RelayCommand? _pickLightForegroundCommand;
        public RelayCommand PickLightForegroundCommand => _pickLightForegroundCommand ??=
            new RelayCommand(() => LightForegroundColor = PickColor(LightForegroundColor, DefaultLightForeground));

        private RelayCommand? _pickDarkBackgroundCommand;
        public RelayCommand PickDarkBackgroundCommand => _pickDarkBackgroundCommand ??=
            new RelayCommand(() => DarkBackgroundColor = PickColor(DarkBackgroundColor, DefaultDarkBackground));

        private RelayCommand? _pickDarkAccentCommand;
        public RelayCommand PickDarkAccentCommand => _pickDarkAccentCommand ??=
            new RelayCommand(() => DarkAccentColor = PickColor(DarkAccentColor, DefaultDarkAccent));

        private RelayCommand? _pickDarkForegroundCommand;
        public RelayCommand PickDarkForegroundCommand => _pickDarkForegroundCommand ??=
            new RelayCommand(() => DarkForegroundColor = PickColor(DarkForegroundColor, DefaultDarkForeground));

        // ── Reset commands (back to built-in defaults for that mode) ─────────

        private RelayCommand? _resetLightThemeCommand;
        public RelayCommand ResetLightThemeCommand => _resetLightThemeCommand ??= new RelayCommand(() =>
        {
            LightBackgroundColor = string.Empty;
            LightAccentColor = string.Empty;
            LightForegroundColor = string.Empty;
        });

        private RelayCommand? _resetDarkThemeCommand;
        public RelayCommand ResetDarkThemeCommand => _resetDarkThemeCommand ??= new RelayCommand(() =>
        {
            DarkBackgroundColor = string.Empty;
            DarkAccentColor = string.Empty;
            DarkForegroundColor = string.Empty;
        });

        // ── Load / apply / live-preview snapshots ────────────────────────────

        partial void LoadThemingFromConfig()
        {
            var light = _config.LightTheme ?? new ThemeOverrides();
            var dark = _config.DarkTheme ?? new ThemeOverrides();

            _lightBackgroundColor = light.Background ?? string.Empty;
            _lightAccentColor = light.Accent ?? string.Empty;
            _lightForegroundColor = light.Foreground ?? string.Empty;

            _darkBackgroundColor = dark.Background ?? string.Empty;
            _darkAccentColor = dark.Accent ?? string.Empty;
            _darkForegroundColor = dark.Foreground ?? string.Empty;
        }

        partial void ApplyThemingToConfig(MusicConfig config)
        {
            config.LightTheme = BuildLightOverrides();
            config.DarkTheme = BuildDarkOverrides();
        }

        /// <summary>Current (possibly unsaved) light-mode overrides, for live preview.</summary>
        public ThemeOverrides BuildLightOverrides() => new ThemeOverrides
        {
            Background = (LightBackgroundColor ?? string.Empty).Trim(),
            Accent = (LightAccentColor ?? string.Empty).Trim(),
            Foreground = (LightForegroundColor ?? string.Empty).Trim()
        };

        /// <summary>Current (possibly unsaved) dark-mode overrides, for live preview.</summary>
        public ThemeOverrides BuildDarkOverrides() => new ThemeOverrides
        {
            Background = (DarkBackgroundColor ?? string.Empty).Trim(),
            Accent = (DarkAccentColor ?? string.Empty).Trim(),
            Foreground = (DarkForegroundColor ?? string.Empty).Trim()
        };

        // ── Helpers ──────────────────────────────────────────────────────────

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

        private static Brush PreviewBrush(string? value, string fallback)
        {
            var color = TryParseMediaColor(value, out var c) ? c
                      : TryParseMediaColor(fallback, out var f) ? f
                      : Colors.Transparent;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
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
