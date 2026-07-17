using System;
using System.Collections.Generic;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// Hotkeys step plus the small Startup step (folded in here rather than a sixth file).
    ///
    /// Each hotkey is a <see cref="HotkeyFieldViewModel"/> row holding a combo string like
    /// "CTRL+ALT+C" (up to 5 keys). Recording is a keyboard-capture job that belongs to the
    /// window, so the view supplies it through the injected StartHotkeyRecording delegate;
    /// the row VMs handle the waiting placeholder and inline no-modifier confirmation.
    /// </summary>
    internal sealed partial class OnboardingViewModel
    {
        // Set by the view. Begins key capture; the view calls back with the held virtual-key
        // codes in press order, or null when the recording was cancelled.
        public Action<Action<int[]?>>? StartHotkeyRecording { get; set; }

        public HotkeyFieldViewModel VolumeUpHotkey { get; private set; } = null!;
        public HotkeyFieldViewModel VolumeDownHotkey { get; private set; } = null!;
        public HotkeyFieldViewModel ToggleScrcpyHotkey { get; private set; } = null!;
        public HotkeyFieldViewModel ToggleLyricsOverlayHotkey { get; private set; } = null!;
        public HotkeyFieldViewModel CopyTrackInfoHotkey { get; private set; } = null!;
        public HotkeyFieldViewModel AudioQualityHotkey { get; private set; } = null!;

        private void InitHotkeys()
        {
            VolumeUpHotkey = new HotkeyFieldViewModel(() => StartHotkeyRecording);
            VolumeDownHotkey = new HotkeyFieldViewModel(() => StartHotkeyRecording);
            ToggleScrcpyHotkey = new HotkeyFieldViewModel(() => StartHotkeyRecording);
            ToggleLyricsOverlayHotkey = new HotkeyFieldViewModel(() => StartHotkeyRecording);
            CopyTrackInfoHotkey = new HotkeyFieldViewModel(() => StartHotkeyRecording);
            AudioQualityHotkey = new HotkeyFieldViewModel(() => StartHotkeyRecording);

            VolumeUpHotkey.SetFromConfig(_workingConfig.Hotkeys.VolumeUpKeys);
            VolumeDownHotkey.SetFromConfig(_workingConfig.Hotkeys.VolumeDownKeys);
            ToggleScrcpyHotkey.SetFromConfig(_workingConfig.Hotkeys.ToggleScrcpyKeys);
            ToggleLyricsOverlayHotkey.SetFromConfig(_workingConfig.Hotkeys.ToggleLyricsOverlayKeys);
            CopyTrackInfoHotkey.SetFromConfig(_workingConfig.Hotkeys.CopyTrackInfoKeys);
            AudioQualityHotkey.SetFromConfig(_workingConfig.Hotkeys.AudioQualityKeys);
        }

        private void CommitHotkeysToConfig()
        {
            _workingConfig.Hotkeys.VolumeUpKeys = NormalizeComboText(VolumeUpHotkey.Text, _workingConfig.Hotkeys.VolumeUpKeys);
            _workingConfig.Hotkeys.VolumeDownKeys = NormalizeComboText(VolumeDownHotkey.Text, _workingConfig.Hotkeys.VolumeDownKeys);
            _workingConfig.Hotkeys.ToggleScrcpyKeys = NormalizeComboText(ToggleScrcpyHotkey.Text, _workingConfig.Hotkeys.ToggleScrcpyKeys);
            _workingConfig.Hotkeys.ToggleLyricsOverlayKeys = NormalizeComboText(ToggleLyricsOverlayHotkey.Text, _workingConfig.Hotkeys.ToggleLyricsOverlayKeys);
            _workingConfig.Hotkeys.CopyTrackInfoKeys = NormalizeComboText(CopyTrackInfoHotkey.Text, _workingConfig.Hotkeys.CopyTrackInfoKeys);
            _workingConfig.Hotkeys.AudioQualityKeys = NormalizeComboText(AudioQualityHotkey.Text, _workingConfig.Hotkeys.AudioQualityKeys);
            // These combos are now authoritative, so lock in the migration flag: a hotkey the
            // user disabled here (empty) must not be re-seeded from the legacy defaults on load.
            _workingConfig.Hotkeys.ComboHotkeysMigrated = true;
        }

        // Re-parses whatever the field holds. An empty field is an intentionally disabled
        // hotkey and stays empty; the transient "waiting..." placeholder or unparseable text
        // falls back to the previously saved combo so it isn't clobbered.
        private static string NormalizeComboText(string text, string previousCombo)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;               // disabled
            if (text == HotkeyFieldViewModel.WaitingForKeyPress) return previousCombo ?? string.Empty;

            var fallback = HotkeyHelper.ParseCombo(previousCombo, Array.Empty<int>());
            var keys = HotkeyHelper.ParseCombo(text.Trim(), fallback);
            return keys.Length == 0 ? (previousCombo ?? string.Empty) : HotkeyHelper.ComboToDisplayName(keys);
        }

        // ── Startup step ──────────────────────────────────────────────────────

        private bool _openInTaskbar;
        public bool OpenInTaskbar { get => _openInTaskbar; set => Set(ref _openInTaskbar, value); }

        private bool _startWithWindows;
        public bool StartWithWindows { get => _startWithWindows; set => Set(ref _startWithWindows, value); }

        private bool _showMediaPlayerView;
        public bool ShowMediaPlayerView
        {
            get => _showMediaPlayerView;
            set
            {
                if (Set(ref _showMediaPlayerView, value))
                    RaisePropertyChanged(nameof(ShowSettingsView));
            }
        }

        // The two default-view radios are mutually exclusive, so this is just the inverse.
        public bool ShowSettingsView
        {
            get => !_showMediaPlayerView;
            set
            {
                if (value == !_showMediaPlayerView) return;
                ShowMediaPlayerView = !value;
            }
        }

        private void InitStartup()
        {
            _openInTaskbar = _workingConfig.AppSettings.OpenInTaskbar;
            _startWithWindows = _workingConfig.AppSettings.StartWithWindows;
            // Default to media player view for new installs; existing users keep their saved preference.
            _showMediaPlayerView = _workingConfig.AppSettings.OnboardingCompleted ? _workingConfig.MediaPlayer.ShowWindow : true;
        }

        private void CommitStartupToConfig()
        {
            _workingConfig.AppSettings.OpenInTaskbar = OpenInTaskbar;
            _workingConfig.AppSettings.StartWithWindows = StartWithWindows;
            _workingConfig.MediaPlayer.ShowWindow = ShowMediaPlayerView;
        }
    }
}