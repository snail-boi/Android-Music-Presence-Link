using System;
using System.Collections.Generic;

namespace AndroidMusicPresenceLink
{
    /// <summary>One selectable hotkey modifier (Alt/Ctrl/Shift) with its Win32 modifier value.</summary>
    internal sealed class HotkeyModifierOption
    {
        public string Name { get; }
        public int Value { get; }

        public HotkeyModifierOption(string name, int value)
        {
            Name = name;
            Value = value;
        }
    }

    /// <summary>
    /// Hotkeys step plus the small Startup step (folded in here rather than a sixth file).
    ///
    /// Each hotkey is shown as a display string. Recording a key is a keyboard-capture job that
    /// belongs to the window, so the view supplies it through the injected StartHotkeyRecording
    /// delegate: the VM hands over a callback that applies the captured virtual-key to the right
    /// hotkey, and the view performs the actual capture and invokes it.
    /// </summary>
    internal sealed partial class OnboardingViewModel
    {
        // Set by the view. Begins key capture; when a key is pressed the view calls back the
        // supplied action with the virtual-key code.
        public Action<Action<int>>? StartHotkeyRecording { get; set; }

        // ── Hotkey display strings ──────────────────────────────────────────────

        private string _hotkeyVolumeUpText = string.Empty;
        public string HotkeyVolumeUpText { get => _hotkeyVolumeUpText; set => Set(ref _hotkeyVolumeUpText, value); }

        private string _hotkeyVolumeDownText = string.Empty;
        public string HotkeyVolumeDownText { get => _hotkeyVolumeDownText; set => Set(ref _hotkeyVolumeDownText, value); }

        private string _hotkeyToggleScrcpyText = string.Empty;
        public string HotkeyToggleScrcpyText { get => _hotkeyToggleScrcpyText; set => Set(ref _hotkeyToggleScrcpyText, value); }

        private string _hotkeyToggleLyricsOverlayText = string.Empty;
        public string HotkeyToggleLyricsOverlayText { get => _hotkeyToggleLyricsOverlayText; set => Set(ref _hotkeyToggleLyricsOverlayText, value); }

        private string _hotkeyCopyTrackInfoText = string.Empty;
        public string HotkeyCopyTrackInfoText { get => _hotkeyCopyTrackInfoText; set => Set(ref _hotkeyCopyTrackInfoText, value); }

        private string _hotkeyAudioQualityText = string.Empty;
        public string HotkeyAudioQualityText { get => _hotkeyAudioQualityText; set => Set(ref _hotkeyAudioQualityText, value); }

        // ── Modifier ────────────────────────────────────────────────────────────

        public IReadOnlyList<HotkeyModifierOption> ModifierOptions { get; } = new[]
        {
            new HotkeyModifierOption("Alt", 1),
            new HotkeyModifierOption("Ctrl", 2),
            new HotkeyModifierOption("Shift", 4),
        };

        private int _selectedModifierValue = 1;
        public int SelectedModifierValue
        {
            get => _selectedModifierValue;
            set => Set(ref _selectedModifierValue, value);
        }

        // ── Record commands ─────────────────────────────────────────────────────

        public RelayCommand RecordVolumeUpCommand { get; private set; } = null!;
        public RelayCommand RecordVolumeDownCommand { get; private set; } = null!;
        public RelayCommand RecordToggleScrcpyCommand { get; private set; } = null!;
        public RelayCommand RecordToggleLyricsOverlayCommand { get; private set; } = null!;
        public RelayCommand RecordCopyTrackInfoCommand { get; private set; } = null!;
        public RelayCommand RecordAudioQualityCommand { get; private set; } = null!;

        private void InitHotkeys()
        {
            RecordVolumeUpCommand = new RelayCommand(() =>
                StartHotkeyRecording?.Invoke(vk => HotkeyVolumeUpText = HotkeyHelper.VirtualKeyToDisplayName(vk)));
            RecordVolumeDownCommand = new RelayCommand(() =>
                StartHotkeyRecording?.Invoke(vk => HotkeyVolumeDownText = HotkeyHelper.VirtualKeyToDisplayName(vk)));
            RecordToggleScrcpyCommand = new RelayCommand(() =>
                StartHotkeyRecording?.Invoke(vk => HotkeyToggleScrcpyText = HotkeyHelper.VirtualKeyToDisplayName(vk)));
            RecordToggleLyricsOverlayCommand = new RelayCommand(() =>
                StartHotkeyRecording?.Invoke(vk => HotkeyToggleLyricsOverlayText = HotkeyHelper.VirtualKeyToDisplayName(vk)));
            RecordCopyTrackInfoCommand = new RelayCommand(() =>
                StartHotkeyRecording?.Invoke(vk => HotkeyCopyTrackInfoText = HotkeyHelper.VirtualKeyToDisplayName(vk)));
            RecordAudioQualityCommand = new RelayCommand(() =>
                StartHotkeyRecording?.Invoke(vk => HotkeyAudioQualityText = HotkeyHelper.VirtualKeyToDisplayName(vk)));

            _hotkeyVolumeUpText = HotkeyHelper.VirtualKeyToDisplayName(_workingConfig.HotkeyVolumeUpKey);
            _hotkeyVolumeDownText = HotkeyHelper.VirtualKeyToDisplayName(_workingConfig.HotkeyVolumeDownKey);
            _hotkeyToggleScrcpyText = HotkeyHelper.VirtualKeyToDisplayName(_workingConfig.HotkeyToggleScrcpyKey);
            _hotkeyToggleLyricsOverlayText = HotkeyHelper.VirtualKeyToDisplayName(_workingConfig.HotkeyToggleLyricsOverlayKey);
            _hotkeyCopyTrackInfoText = HotkeyHelper.VirtualKeyToDisplayName(_workingConfig.HotkeyCopyTrackInfoKey);
            _hotkeyAudioQualityText = HotkeyHelper.VirtualKeyToDisplayName(_workingConfig.HotkeyAudioQualityKey);

            int mod = _workingConfig.HotkeyModifier;
            _selectedModifierValue = (mod == 1 || mod == 2 || mod == 4) ? mod : 1;
        }

        private void CommitHotkeysToConfig()
        {
            _workingConfig.HotkeyVolumeUpKey = HotkeyHelper.ParseVirtualKey(HotkeyVolumeUpText.Trim(), _workingConfig.HotkeyVolumeUpKey);
            _workingConfig.HotkeyVolumeDownKey = HotkeyHelper.ParseVirtualKey(HotkeyVolumeDownText.Trim(), _workingConfig.HotkeyVolumeDownKey);
            _workingConfig.HotkeyToggleScrcpyKey = HotkeyHelper.ParseVirtualKey(HotkeyToggleScrcpyText.Trim(), _workingConfig.HotkeyToggleScrcpyKey);
            _workingConfig.HotkeyToggleLyricsOverlayKey = HotkeyHelper.ParseVirtualKey(HotkeyToggleLyricsOverlayText.Trim(), _workingConfig.HotkeyToggleLyricsOverlayKey);
            _workingConfig.HotkeyCopyTrackInfoKey = HotkeyHelper.ParseVirtualKey(HotkeyCopyTrackInfoText.Trim(), _workingConfig.HotkeyCopyTrackInfoKey);
            _workingConfig.HotkeyAudioQualityKey = HotkeyHelper.ParseVirtualKey(HotkeyAudioQualityText.Trim(), _workingConfig.HotkeyAudioQualityKey);

            _workingConfig.HotkeyModifier = SelectedModifierValue;
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
            _openInTaskbar = _workingConfig.OpenInTaskbar;
            _startWithWindows = _workingConfig.StartWithWindows;
            _showMediaPlayerView = _workingConfig.ShowMediaPlayerWindow;
        }

        private void CommitStartupToConfig()
        {
            _workingConfig.OpenInTaskbar = OpenInTaskbar;
            _workingConfig.StartWithWindows = StartWithWindows;
            _workingConfig.ShowMediaPlayerWindow = ShowMediaPlayerView;
        }
    }
}
