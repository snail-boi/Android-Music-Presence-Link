using System;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// Hotkeys group: six <see cref="HotkeyFieldViewModel"/> rows, each holding a combo
    /// string like "CTRL+ALT+C" (up to 5 keys). Recording is a window keyboard-capture
    /// job, so the view supplies it through StartHotkeyRecording; the row VMs handle the
    /// waiting placeholder and the inline no-modifier confirmation themselves.
    /// </summary>
    internal sealed partial class SettingsViewModel
    {
        // Set by the window. Begins key capture; the view calls back with the held
        // virtual-key codes in press order, or null when the recording was cancelled.
        public Action<Action<int[]?>>? StartHotkeyRecording { get; set; }

        public HotkeyFieldViewModel VolumeUpHotkey { get; private set; } = null!;
        public HotkeyFieldViewModel VolumeDownHotkey { get; private set; } = null!;
        public HotkeyFieldViewModel ToggleScrcpyHotkey { get; private set; } = null!;
        public HotkeyFieldViewModel ToggleLyricsOverlayHotkey { get; private set; } = null!;
        public HotkeyFieldViewModel CopyTrackInfoHotkey { get; private set; } = null!;
        public HotkeyFieldViewModel AudioQualityHotkey { get; private set; } = null!;
        public HotkeyFieldViewModel PlayPauseHotkey { get; private set; } = null!;
        public HotkeyFieldViewModel NextTrackHotkey { get; private set; } = null!;
        public HotkeyFieldViewModel PreviousTrackHotkey { get; private set; } = null!;

        // False = SMTC mode (Windows' own media keys drive the app, no custom keys).
        // True = Direct mode (bind custom play/pause/next/previous keys, sent to the phone).
        private bool _mediaKeysDirectMode;
        public bool MediaKeysDirectMode
        {
            get => _mediaKeysDirectMode;
            set => Set(ref _mediaKeysDirectMode, value);
        }

        partial void InitHotkeys()
        {
            VolumeUpHotkey = new HotkeyFieldViewModel(() => StartHotkeyRecording);
            VolumeDownHotkey = new HotkeyFieldViewModel(() => StartHotkeyRecording);
            ToggleScrcpyHotkey = new HotkeyFieldViewModel(() => StartHotkeyRecording);
            ToggleLyricsOverlayHotkey = new HotkeyFieldViewModel(() => StartHotkeyRecording);
            CopyTrackInfoHotkey = new HotkeyFieldViewModel(() => StartHotkeyRecording);
            AudioQualityHotkey = new HotkeyFieldViewModel(() => StartHotkeyRecording);
            PlayPauseHotkey = new HotkeyFieldViewModel(() => StartHotkeyRecording);
            NextTrackHotkey = new HotkeyFieldViewModel(() => StartHotkeyRecording);
            PreviousTrackHotkey = new HotkeyFieldViewModel(() => StartHotkeyRecording);

            LoadHotkeysFromConfig();
        }

        partial void LoadHotkeysFromConfig()
        {
            VolumeUpHotkey.SetFromConfig(_config.Hotkeys.VolumeUpKeys);
            VolumeDownHotkey.SetFromConfig(_config.Hotkeys.VolumeDownKeys);
            ToggleScrcpyHotkey.SetFromConfig(_config.Hotkeys.ToggleScrcpyKeys);
            ToggleLyricsOverlayHotkey.SetFromConfig(_config.Hotkeys.ToggleLyricsOverlayKeys);
            CopyTrackInfoHotkey.SetFromConfig(_config.Hotkeys.CopyTrackInfoKeys);
            AudioQualityHotkey.SetFromConfig(_config.Hotkeys.AudioQualityKeys);
            PlayPauseHotkey.SetFromConfig(_config.Hotkeys.PlayPauseKeys);
            NextTrackHotkey.SetFromConfig(_config.Hotkeys.NextTrackKeys);
            PreviousTrackHotkey.SetFromConfig(_config.Hotkeys.PreviousTrackKeys);
            MediaKeysDirectMode = _config.Hotkeys.MediaKeysDirectMode;
        }

        partial void ApplyHotkeysToConfig(MusicConfig config)
        {
            config.Hotkeys.VolumeUpKeys = NormalizeComboText(VolumeUpHotkey.Text, _config.Hotkeys.VolumeUpKeys);
            config.Hotkeys.VolumeDownKeys = NormalizeComboText(VolumeDownHotkey.Text, _config.Hotkeys.VolumeDownKeys);
            config.Hotkeys.ToggleScrcpyKeys = NormalizeComboText(ToggleScrcpyHotkey.Text, _config.Hotkeys.ToggleScrcpyKeys);
            config.Hotkeys.ToggleLyricsOverlayKeys = NormalizeComboText(ToggleLyricsOverlayHotkey.Text, _config.Hotkeys.ToggleLyricsOverlayKeys);
            config.Hotkeys.CopyTrackInfoKeys = NormalizeComboText(CopyTrackInfoHotkey.Text, _config.Hotkeys.CopyTrackInfoKeys);
            config.Hotkeys.AudioQualityKeys = NormalizeComboText(AudioQualityHotkey.Text, _config.Hotkeys.AudioQualityKeys);
            config.Hotkeys.PlayPauseKeys = NormalizeComboText(PlayPauseHotkey.Text, _config.Hotkeys.PlayPauseKeys);
            config.Hotkeys.NextTrackKeys = NormalizeComboText(NextTrackHotkey.Text, _config.Hotkeys.NextTrackKeys);
            config.Hotkeys.PreviousTrackKeys = NormalizeComboText(PreviousTrackHotkey.Text, _config.Hotkeys.PreviousTrackKeys);
            config.Hotkeys.MediaKeysDirectMode = MediaKeysDirectMode;
        }

        // Re-parses whatever the field holds. An empty field is an intentionally disabled
        // hotkey and stays empty; the transient "waiting..." placeholder or any unparseable
        // text falls back to the previously saved combo so it isn't clobbered.
        private static string NormalizeComboText(string text, string previousCombo)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;               // disabled
            if (text == HotkeyFieldViewModel.WaitingForKeyPress) return previousCombo ?? string.Empty;

            var fallback = HotkeyHelper.ParseCombo(previousCombo, Array.Empty<int>());
            var keys = HotkeyHelper.ParseCombo(text.Trim(), fallback);
            return keys.Length == 0 ? (previousCombo ?? string.Empty) : HotkeyHelper.ComboToDisplayName(keys);
        }
    }
}
