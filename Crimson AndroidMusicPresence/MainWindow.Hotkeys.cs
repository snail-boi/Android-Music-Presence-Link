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
                Dispatcher.Invoke(() => TxtHotkeyVolumeUp.Text = HotkeyHelper.VirtualKeyToDisplayName(k));
                _config.HotkeyVolumeUpKey = k;
            });
        }

        private void BtnRecordHotkeyVolumeDown_Click(object sender, RoutedEventArgs e)
        {
            StartRecordingHotkey(k =>
            {
                Dispatcher.Invoke(() => TxtHotkeyVolumeDown.Text = HotkeyHelper.VirtualKeyToDisplayName(k));
                _config.HotkeyVolumeDownKey = k;
            });
        }

        private void BtnRecordHotkeyToggleScrcpy_Click(object sender, RoutedEventArgs e)
        {
            StartRecordingHotkey(k =>
            {
                Dispatcher.Invoke(() => TxtHotkeyToggleScrcpy.Text = HotkeyHelper.VirtualKeyToDisplayName(k));
                _config.HotkeyToggleScrcpyKey = k;
            });
        }

        private void BtnRecordHotkeyToggleLyricsOverlay_Click(object sender, RoutedEventArgs e)
        {
            StartRecordingHotkey(k =>
            {
                Dispatcher.Invoke(() => TxtHotkeyToggleLyricsOverlay.Text = HotkeyHelper.VirtualKeyToDisplayName(k));
                _config.HotkeyToggleLyricsOverlayKey = k;
            });
        }

        private void BtnRecordHotkeyCopyTrackInfo_Click(object sender, RoutedEventArgs e)
        {
            StartRecordingHotkey(k =>
            {
                Dispatcher.Invoke(() => TxtHotkeyCopyTrackInfo.Text = HotkeyHelper.VirtualKeyToDisplayName(k));
                _config.HotkeyCopyTrackInfoKey = k;
            });
        }

        private void BtnRecordHotkeyAudioQuality_Click(object sender, RoutedEventArgs e)
        {
            StartRecordingHotkey(k =>
            {
                Dispatcher.Invoke(() => TxtHotkeyAudioQuality.Text = HotkeyHelper.VirtualKeyToDisplayName(k));
                _config.HotkeyAudioQualityKey = k;
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
            Debugger.show("[HOTKEY] Started recording hotkey.");

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

            Debugger.show("[HOTKEY] Stopped recording hotkey.");
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
                    Debugger.show("[HOTKEY] Recording cancelled with Escape.");
                    StopRecordingHotkey();
                    return;
                }

                int vk = KeyToVirtualKey(e);
                Debugger.show($"[HOTKEY] Recorded key 0x{vk:X2}.");

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

    }
}