using System;
using System.Collections.Generic;

namespace musicpresense
{
    /// <summary>
    /// Hotkeys group: six bound hotkey display strings and the modifier dropdown. Recording a key
    /// is a window keyboard-capture job, so the view supplies it through StartHotkeyRecording: the
    /// VM hands over a callback that writes the captured virtual-key into the right field.
    /// Reuses the HotkeyModifierOption type defined alongside the onboarding ViewModel.
    /// </summary>
    internal sealed partial class SettingsViewModel
    {
        // Set by the window. Begins key capture; the view calls back with the virtual-key code.
        public Action<Action<int>>? StartHotkeyRecording { get; set; }

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

        // Display order matches the dropdown in the XAML: Shift, Ctrl, Alt.
        public IReadOnlyList<HotkeyModifierOption> ModifierOptions { get; } = new[]
        {
            new HotkeyModifierOption("Shift", 4),
            new HotkeyModifierOption("Ctrl", 2),
            new HotkeyModifierOption("Alt", 1),
        };

        private int _selectedModifierValue = 1;
        public int SelectedModifierValue { get => _selectedModifierValue; set => Set(ref _selectedModifierValue, value); }

        public RelayCommand RecordVolumeUpCommand { get; private set; } = null!;
        public RelayCommand RecordVolumeDownCommand { get; private set; } = null!;
        public RelayCommand RecordToggleScrcpyCommand { get; private set; } = null!;
        public RelayCommand RecordToggleLyricsOverlayCommand { get; private set; } = null!;
        public RelayCommand RecordCopyTrackInfoCommand { get; private set; } = null!;
        public RelayCommand RecordAudioQualityCommand { get; private set; } = null!;

        partial void InitHotkeys()
        {
            RecordVolumeUpCommand = new RelayCommand(() =>
                StartHotkeyRecording?.Invoke(k => HotkeyVolumeUpText = HotkeyHelper.VirtualKeyToDisplayName(k)));
            RecordVolumeDownCommand = new RelayCommand(() =>
                StartHotkeyRecording?.Invoke(k => HotkeyVolumeDownText = HotkeyHelper.VirtualKeyToDisplayName(k)));
            RecordToggleScrcpyCommand = new RelayCommand(() =>
                StartHotkeyRecording?.Invoke(k => HotkeyToggleScrcpyText = HotkeyHelper.VirtualKeyToDisplayName(k)));
            RecordToggleLyricsOverlayCommand = new RelayCommand(() =>
                StartHotkeyRecording?.Invoke(k => HotkeyToggleLyricsOverlayText = HotkeyHelper.VirtualKeyToDisplayName(k)));
            RecordCopyTrackInfoCommand = new RelayCommand(() =>
                StartHotkeyRecording?.Invoke(k => HotkeyCopyTrackInfoText = HotkeyHelper.VirtualKeyToDisplayName(k)));
            RecordAudioQualityCommand = new RelayCommand(() =>
                StartHotkeyRecording?.Invoke(k => HotkeyAudioQualityText = HotkeyHelper.VirtualKeyToDisplayName(k)));

            LoadHotkeysFromConfig();
        }

        partial void LoadHotkeysFromConfig()
        {
            _hotkeyVolumeUpText = SafeDisplayName(_config.HotkeyVolumeUpKey);
            _hotkeyVolumeDownText = SafeDisplayName(_config.HotkeyVolumeDownKey);
            _hotkeyToggleScrcpyText = SafeDisplayName(_config.HotkeyToggleScrcpyKey);
            _hotkeyToggleLyricsOverlayText = SafeDisplayName(_config.HotkeyToggleLyricsOverlayKey);
            _hotkeyCopyTrackInfoText = SafeDisplayName(_config.HotkeyCopyTrackInfoKey);
            _hotkeyAudioQualityText = SafeDisplayName(_config.HotkeyAudioQualityKey);

            int mod = _config.HotkeyModifier;
            _selectedModifierValue = (mod == 1 || mod == 2 || mod == 4) ? mod : 1;
        }

        partial void ApplyHotkeysToConfig(MusicConfig config)
        {
            config.HotkeyVolumeUpKey = HotkeyHelper.ParseVirtualKey(HotkeyVolumeUpText.Trim(), _config.HotkeyVolumeUpKey);
            config.HotkeyVolumeDownKey = HotkeyHelper.ParseVirtualKey(HotkeyVolumeDownText.Trim(), _config.HotkeyVolumeDownKey);
            config.HotkeyToggleScrcpyKey = HotkeyHelper.ParseVirtualKey(HotkeyToggleScrcpyText.Trim(), _config.HotkeyToggleScrcpyKey);
            config.HotkeyToggleLyricsOverlayKey = HotkeyHelper.ParseVirtualKey(HotkeyToggleLyricsOverlayText.Trim(), _config.HotkeyToggleLyricsOverlayKey);
            config.HotkeyCopyTrackInfoKey = HotkeyHelper.ParseVirtualKey(HotkeyCopyTrackInfoText.Trim(), _config.HotkeyCopyTrackInfoKey);
            config.HotkeyAudioQualityKey = HotkeyHelper.ParseVirtualKey(HotkeyAudioQualityText.Trim(), _config.HotkeyAudioQualityKey);

            config.HotkeyModifier = SelectedModifierValue;
        }

        private static string SafeDisplayName(int virtualKey)
        {
            try { return HotkeyHelper.VirtualKeyToDisplayName(virtualKey); }
            catch { return string.Empty; }
        }
    }
}
