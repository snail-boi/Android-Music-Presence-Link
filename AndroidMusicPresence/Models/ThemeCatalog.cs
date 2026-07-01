using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// The three built-in theme profiles and a couple of small helpers for converting
    /// legacy overrides into a fully-specified profile. Built-in profiles are defined in
    /// code (not stored in config) so they can be tweaked in future versions and so they
    /// can never be deleted by the user. Each accessor returns a fresh instance to avoid
    /// any accidental shared mutable state.
    /// </summary>
    public static class BuiltInThemes
    {
        public static ThemeProfile DefaultLight => new ThemeProfile
        {
            Name = "Default Light",
            Background = "#F7F7F7",
            Accent = "#2D6CDF",
            Foreground = "#1A1A1A"
        };

        public static ThemeProfile DefaultDark => new ThemeProfile
        {
            Name = "Default Dark",
            Background = "#1E1E1E",
            Accent = "#3E7BFF",
            Foreground = "#EAEAEA"
        };

        public static ThemeProfile HighContrast => new ThemeProfile
        {
            Name = "High Contrast",
            Background = "#000000",
            Accent = "#2D6CDF",
            Foreground = "#FFFFFF"
        };

        // Display/cycle order for the built-ins.
        public static IReadOnlyList<ThemeProfile> All => new[] { DefaultLight, DefaultDark, HighContrast };

        /// <summary>
        /// Builds a fully-specified profile from a partial legacy <see cref="ThemeOverrides"/>.
        /// Unset colors fall back to the matching built-in default; an unset foreground is
        /// auto-contrasted against the effective background so a dark custom background never
        /// keeps unreadable dark text.
        /// </summary>
        public static ThemeProfile ProfileFromOverrides(string name, ThemeOverrides overrides, bool isDark)
        {
            var baseTheme = isDark ? DefaultDark : DefaultLight;

            string background = !string.IsNullOrWhiteSpace(overrides.Background) ? overrides.Background.Trim() : baseTheme.Background;
            string accent = !string.IsNullOrWhiteSpace(overrides.Accent) ? overrides.Accent.Trim() : baseTheme.Accent;
            string foreground = !string.IsNullOrWhiteSpace(overrides.Foreground)
                ? overrides.Foreground.Trim()
                : (ThemeCatalog.IsDarkColor(background) ? "#EAEAEA" : "#1A1A1A");

            return new ThemeProfile
            {
                Name = name,
                Background = background,
                Accent = accent,
                Foreground = foreground
            };
        }
    }

    /// <summary>
    /// Resolution and classification helpers over the combined set of themes (built-ins
    /// plus the user's custom profiles) for a given config.
    /// </summary>
    public static class ThemeCatalog
    {
        /// <summary>All selectable themes in display/cycle order: built-ins then custom.</summary>
        public static List<ThemeProfile> AllThemes(MusicConfig config)
        {
            var list = new List<ThemeProfile>(BuiltInThemes.All);
            if (config.Theme.CustomProfiles != null)
                list.AddRange(config.Theme.CustomProfiles.Where(t => t != null).Select(t => t.Clone()));
            return list;
        }

        /// <summary>
        /// The themes in the cycle rotation: all themes minus the ones the user disabled.
        /// Falls back to the full set if every theme somehow ended up disabled.
        /// </summary>
        public static List<ThemeProfile> EnabledThemes(MusicConfig config)
        {
            var disabled = new HashSet<string>(config.Theme.DisabledProfiles ?? new List<string>(), StringComparer.Ordinal);
            var enabled = AllThemes(config).Where(t => !disabled.Contains(t.Name)).ToList();
            return enabled.Count > 0 ? enabled : AllThemes(config);
        }

        /// <summary>Name of a random in-rotation theme, or empty if there are none.</summary>
        public static string RandomEnabledThemeName(MusicConfig config)
        {
            var enabled = EnabledThemes(config);
            if (enabled.Count == 0)
                return string.Empty;
            return enabled[Random.Shared.Next(enabled.Count)].Name;
        }

        /// <summary>The active profile, falling back to Default Dark if the name is unknown.</summary>
        public static ThemeProfile ResolveActive(MusicConfig config)
        {
            var all = AllThemes(config);
            return all.FirstOrDefault(t => string.Equals(t.Name, config.Theme.ActiveProfile, StringComparison.Ordinal))
                   ?? BuiltInThemes.DefaultDark;
        }

        public static bool IsBuiltIn(string? name)
            => BuiltInThemes.All.Any(t => string.Equals(t.Name, name, StringComparison.Ordinal));

        public static bool IsPristineDefaultLight(ThemeProfile p)
            => string.Equals(p.Name, BuiltInThemes.DefaultLight.Name, StringComparison.Ordinal);

        public static bool IsPristineDefaultDark(ThemeProfile p)
            => string.Equals(p.Name, BuiltInThemes.DefaultDark.Name, StringComparison.Ordinal);

        /// <summary>Whether a profile reads as a dark theme, by background luminance.</summary>
        public static bool IsDark(ThemeProfile p) => IsDarkColor(p.Background);

        public static bool IsDarkColor(string? hex)
        {
            if (TryParseColor(hex, out var c))
                return (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0 < 0.5;
            // Unknown/blank: assume dark (matches the app's default-dark stance).
            return true;
        }

        private static bool TryParseColor(string? hex, out Color color)
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
