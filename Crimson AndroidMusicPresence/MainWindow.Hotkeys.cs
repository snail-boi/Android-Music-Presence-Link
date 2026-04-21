using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace musicpresense
{
    public partial class MainWindow
    {
        private bool _isRecordingHotkey = false;
        private Action<int>? _onHotkeyRecorded;

        private void BtnRecordHotkeyVolumeUp_Click(object sender, RoutedEventArgs e)
        {
            StartRecordingHotkey(k =>
            {
                Dispatcher.Invoke(() => TxtHotkeyVolumeUp.Text = VirtualKeyToDisplayName(k));
                _config.HotkeyVolumeUpKey = k;
            });
        }

        private void BtnRecordHotkeyVolumeDown_Click(object sender, RoutedEventArgs e)
        {
            StartRecordingHotkey(k =>
            {
                Dispatcher.Invoke(() => TxtHotkeyVolumeDown.Text = VirtualKeyToDisplayName(k));
                _config.HotkeyVolumeDownKey = k;
            });
        }

        private void BtnRecordHotkeyToggleScrcpy_Click(object sender, RoutedEventArgs e)
        {
            StartRecordingHotkey(k =>
            {
                Dispatcher.Invoke(() => TxtHotkeyToggleScrcpy.Text = VirtualKeyToDisplayName(k));
                _config.HotkeyToggleScrcpyKey = k;
            });
        }

        private void BtnRecordHotkeyToggleLyricsOverlay_Click(object sender, RoutedEventArgs e)
        {
            StartRecordingHotkey(k =>
            {
                Dispatcher.Invoke(() => TxtHotkeyToggleLyricsOverlay.Text = VirtualKeyToDisplayName(k));
                _config.HotkeyToggleLyricsOverlayKey = k;
            });
        }

        private void BtnRecordHotkeyCopyTrackInfo_Click(object sender, RoutedEventArgs e)
        {
            StartRecordingHotkey(k =>
            {
                Dispatcher.Invoke(() => TxtHotkeyCopyTrackInfo.Text = VirtualKeyToDisplayName(k));
                _config.HotkeyCopyTrackInfoKey = k;
            });
        }

        private void CmbHotkeyModifier_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            try
            {
                if (CmbHotkeyModifier.SelectedItem is System.Windows.Controls.ComboBoxItem item && item.Tag != null)
                {
                    if (int.TryParse(item.Tag.ToString()?.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out var mod))
                    {
                        _config.HotkeyModifier = mod;
                    }
                }
            }
            catch { }
        }

        private void StartRecordingHotkey(Action<int> onRecorded)
        {
            if (_isRecordingHotkey)
                return;

            _isRecordingHotkey = true;
            _onHotkeyRecorded = onRecorded;

            Title = "Press a key to record hotkey (Esc to cancel)...";
            Focus();
            // capture keyboard events at window level
            this.PreviewKeyDown += Recording_PreviewKeyDown;
            this.Deactivated += Recording_Deactivated;
        }

        private void StopRecordingHotkey()
        {
            if (!_isRecordingHotkey)
                return;

            _isRecordingHotkey = false;
            _onHotkeyRecorded = null;
            Title = "Music Presence Settings";
            this.PreviewKeyDown -= Recording_PreviewKeyDown;
            this.Deactivated -= Recording_Deactivated;
        }

        private void Recording_Deactivated(object? sender, EventArgs e)
        {
            // stop recording if window loses focus
            StopRecordingHotkey();
        }

        private void Recording_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                if (!_isRecordingHotkey) return;

                e.Handled = true;

                if (e.Key == Key.Escape)
                {
                    StopRecordingHotkey();
                    return;
                }

                int vk = KeyToVirtualKey(e);

                _onHotkeyRecorded?.Invoke(vk);
                StopRecordingHotkey();
            }
            catch
            {
                StopRecordingHotkey();
            }
        }

        private static int KeyToVirtualKey(KeyEventArgs e)
        {
            // Get Key code: consider system keys and convert to KeyInterop virtual key
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            int vk = KeyInterop.VirtualKeyFromKey(key);
            return vk & 0xFF;
        }

        private static string VirtualKeyToDisplayName(int vk)
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
                { 0x20, "SPACE" }
            };

            if (map.TryGetValue(vk, out var name))
                return name;

            return $"VK_0x{vk:X2}";
        }

        private static int ParseVirtualKey(string input, int fallback)
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
                { "SPACE", 0x20 }
            };

            if (map.TryGetValue(normalized, out var mapped)) return mapped;
            if (map.TryGetValue(up, out mapped)) return mapped;

            return fallback;
        }
    }
}
