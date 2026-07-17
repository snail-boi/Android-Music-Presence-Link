using System;
using System.Collections.Generic;
using System.Linq;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// Maps between Windows virtual-key codes and the display/storage strings used
    /// for the global hotkey settings. Shared by the main settings window and onboarding.
    /// A hotkey is a combo of up to <see cref="MaxComboKeys"/> keys (modifiers included),
    /// stored and displayed as a "+"-joined string like "CTRL+ALT+C".
    /// </summary>
    internal static class HotkeyHelper
    {
        public const int MaxComboKeys = 5;

        // Generic modifier virtual keys; left/right variants normalize to these.
        private const int VkShift = 0x10;
        private const int VkControl = 0x11;
        private const int VkAlt = 0x12;
        private const int VkWin = 0x5B;

        /// <summary>Collapses left/right modifier variants to the generic virtual key.</summary>
        public static int NormalizeKey(int vk)
        {
            switch (vk)
            {
                case 0xA0: case 0xA1: return VkShift;
                case 0xA2: case 0xA3: return VkControl;
                case 0xA4: case 0xA5: return VkAlt;
                case 0x5C: return VkWin;
                default: return vk;
            }
        }

        public static bool IsModifier(int vk)
        {
            vk = NormalizeKey(vk);
            return vk == VkShift || vk == VkControl || vk == VkAlt || vk == VkWin;
        }

        public static string ComboToDisplayName(IReadOnlyList<int> keys)
            => string.Join("+", keys.Select(VirtualKeyToDisplayName));

        /// <summary>
        /// Parses a combo string like "CTRL+ALT+C". Any unrecognized token invalidates the
        /// whole combo and returns <paramref name="fallback"/>. Keys are normalized,
        /// de-duplicated and capped at <see cref="MaxComboKeys"/>.
        /// </summary>
        public static int[] ParseCombo(string? input, int[] fallback)
        {
            if (string.IsNullOrWhiteSpace(input))
                return fallback;

            var keys = new List<int>();
            foreach (var part in input.Split('+'))
            {
                var token = part.Trim();
                if (token.Length == 0)
                    continue;

                int vk = ParseVirtualKey(token, -1);
                if (vk < 0)
                    return fallback;

                vk = NormalizeKey(vk);
                if (!keys.Contains(vk))
                    keys.Add(vk);
            }

            if (keys.Count == 0)
                return fallback;

            return keys.Take(MaxComboKeys).ToArray();
        }

        /// <summary>
        /// Builds a combo from the legacy config format: one shared Win32 modifier flag
        /// (MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4) plus a single virtual key.
        /// </summary>
        public static int[] ComboFromLegacy(int modifierFlags, int vk)
        {
            var keys = new List<int>();
            if ((modifierFlags & 0x2) != 0) keys.Add(VkControl);
            if ((modifierFlags & 0x1) != 0) keys.Add(VkAlt);
            if ((modifierFlags & 0x4) != 0) keys.Add(VkShift);

            vk = NormalizeKey(vk & 0xFF);
            if (!keys.Contains(vk))
                keys.Add(vk);

            return keys.ToArray();
        }

        public static string VirtualKeyToDisplayName(int vk)
        {
            // Letters
            if (vk >= 0x41 && vk <= 0x5A)
                return ((char)vk).ToString();

            // Digits
            if (vk >= 0x30 && vk <= 0x39)
                return ((char)vk).ToString();

            // Function keys F1-F24
            if (vk >= 0x70 && vk <= 0x87)
                return "F" + (vk - 0x6F).ToString();

            var map = new Dictionary<int, string>
            {
                { 0xAF, "VOLUME_UP" },
                { 0xAE, "VOLUME_DOWN" },
                { 0xAD, "VOLUME_MUTE" },
                { 0xB3, "MEDIA_PLAY_PAUSE" },
                { 0xB0, "MEDIA_NEXT_TRACK" },
                { 0xB1, "MEDIA_PREV_TRACK" },
                { 0xB2, "MEDIA_STOP" },
                { 0x1B, "ESC" },
                { 0x0D, "ENTER" },
                { 0x20, "SPACE" },
                { VkShift, "SHIFT" },
                { VkControl, "CTRL" },
                { VkAlt, "ALT" },
                { VkWin, "WIN" }
            };

            if (map.TryGetValue(vk, out var name))
                return name;

            return $"VK_0x{vk:X2}";
        }

        public static int ParseVirtualKey(string input, int fallback)
        {
            if (string.IsNullOrWhiteSpace(input)) return fallback;
            input = input.Trim();

            // Hex like 0xAF or VK_0xAF
            if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(input.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out var v))
                    return v & 0xFF;
                return fallback;
            }

            if (input.StartsWith("VK_0X", StringComparison.OrdinalIgnoreCase) || input.StartsWith("VK_0x", StringComparison.OrdinalIgnoreCase))
            {
                var part = input.Substring(5);
                if (int.TryParse(part, System.Globalization.NumberStyles.HexNumber, null, out var v2))
                    return v2 & 0xFF;
                return fallback;
            }

            // Single letter/digit character. Checked before the decimal branch so "1"
            // means the 1 key (0x31), not virtual-key 0x01.
            if (input.Length == 1 && char.IsLetterOrDigit(input[0]))
                return char.ToUpperInvariant(input[0]);

            // Decimal
            if (int.TryParse(input, out var d))
                return d & 0xFF;

            var up = input.ToUpperInvariant();

            // Single char
            if (up.Length == 1)
                return (int)up[0];

            // Function key like F1
            if (up.StartsWith("F") && int.TryParse(up.Substring(1), out var fn))
            {
                if (fn >= 1 && fn <= 24)
                    return 0x6F + fn; // F1 = 0x70
            }

            // Named keys
            var normalized = up.Replace("VK_", "").Replace(" ", "_").Replace("-", "_");
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "VOLUME_UP", 0xAF },
                { "VOLUME_DOWN", 0xAE },
                { "VOLUME_MUTE", 0xAD },
                { "MEDIA_PLAY_PAUSE", 0xB3 },
                { "MEDIA_NEXT_TRACK", 0xB0 },
                { "MEDIA_PREV_TRACK", 0xB1 },
                { "MEDIA_STOP", 0xB2 },
                { "ESC", 0x1B },
                { "ENTER", 0x0D },
                { "RETURN", 0x0D },
                { "SPACE", 0x20 },
                { "SHIFT", VkShift },
                { "CTRL", VkControl },
                { "CONTROL", VkControl },
                { "ALT", VkAlt },
                { "WIN", VkWin }
            };

            if (map.TryGetValue(normalized, out var mapped)) return mapped;
            if (map.TryGetValue(up, out mapped)) return mapped;

            return fallback;
        }
    }
}
