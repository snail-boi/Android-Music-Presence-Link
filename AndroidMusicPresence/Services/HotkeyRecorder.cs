using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// Captures a hotkey combo (up to <see cref="HotkeyHelper.MaxComboKeys"/> keys) from a
    /// window. Keys accumulate while held; releasing any of them finishes the recording with
    /// everything held at once. Esc or window deactivation cancels (callback gets null).
    /// Attachable to any window because the settings content can be re-hosted inside the
    /// media player window, where the settings window's own key events never fire.
    /// </summary>
    internal sealed class HotkeyRecorder
    {
        private Window? _window;
        private Action<int[]?>? _onRecorded;
        private string _savedTitle = string.Empty;
        private readonly List<int> _keys = new List<int>();

        public void Start(Window host, Action<int[]?> onRecorded)
        {
            if (_window != null)
            {
                // The active recording keeps the window events; just tell the new
                // request it was cancelled so its field restores.
                onRecorded(null);
                return;
            }

            _window = host;
            _onRecorded = onRecorded;
            _keys.Clear();
            Debugger.show("[HOTKEY] Started recording hotkey.");

            // Suspend global hotkey matching so already-registered combos (and their
            // swallowed key presses) can still be captured here.
            (Application.Current as App)?.SetHotkeysSuspended(true);

            _savedTitle = host.Title;
            host.Title = "Hold the keys for the hotkey, release to save (Esc to cancel)...";
            host.Focus();
            host.PreviewKeyDown += OnPreviewKeyDown;
            host.PreviewKeyUp += OnPreviewKeyUp;
            host.Deactivated += OnDeactivated;
        }

        private void Finish(int[]? result)
        {
            var window = _window;
            if (window == null)
                return;

            Debugger.show(result == null
                ? "[HOTKEY] Recording cancelled."
                : $"[HOTKEY] Recorded combo: {HotkeyHelper.ComboToDisplayName(result)}.");

            var callback = _onRecorded;
            _window = null;
            _onRecorded = null;
            _keys.Clear();
            window.Title = _savedTitle;
            window.PreviewKeyDown -= OnPreviewKeyDown;
            window.PreviewKeyUp -= OnPreviewKeyUp;
            window.Deactivated -= OnDeactivated;

            (Application.Current as App)?.SetHotkeysSuspended(false);

            callback?.Invoke(result);
        }

        private void OnDeactivated(object? sender, EventArgs e)
        {
            Finish(null);
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                if (_window == null) return;

                e.Handled = true;

                if (e.Key == Key.Escape)
                {
                    Finish(null);
                    return;
                }

                int vk = KeyToVirtualKey(e);
                if (vk == 0)
                    return;

                if (!_keys.Contains(vk))
                    _keys.Add(vk);

                if (_keys.Count >= HotkeyHelper.MaxComboKeys)
                    Finish(_keys.ToArray());
            }
            catch
            {
                Finish(null);
            }
        }

        private void OnPreviewKeyUp(object sender, KeyEventArgs e)
        {
            try
            {
                if (_window == null) return;

                e.Handled = true;

                // Only a release of a key we saw pressed finishes the combo; stray
                // key-ups (e.g. the Enter that clicked the Record button) are ignored.
                int vk = KeyToVirtualKey(e);
                if (_keys.Contains(vk))
                    Finish(_keys.ToArray());
            }
            catch
            {
                Finish(null);
            }
        }

        private static int KeyToVirtualKey(KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            int vk = KeyInterop.VirtualKeyFromKey(key);
            return HotkeyHelper.NormalizeKey(vk & 0xFF);
        }
    }
}
