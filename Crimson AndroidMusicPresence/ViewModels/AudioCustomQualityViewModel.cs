using System;
using System.Collections.Generic;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// One option in the preset dropdown. Preset is null for the "Custom" entry, which
    /// means "do not auto-fill, use whatever is in the fields".
    /// </summary>
    public sealed class PresetOption
    {
        public string Name { get; }
        public AudioQualityPresets.Preset? Preset { get; }

        public PresetOption(string name, AudioQualityPresets.Preset? preset)
        {
            Name = name;
            Preset = preset;
        }
    }

    /// <summary>
    /// ViewModel for AudioCustomQualityWindow. Holds the editable state (codec, bitrate,
    /// buffer, FLAC level, preset selection), the show/hide rules, and the Apply/Cancel
    /// logic. It has no reference to any control, window, or MessageBox. When it needs the
    /// view to do something view-specific (close the dialog, pop a validation warning) it
    /// raises an event and lets the code-behind handle it.
    /// </summary>
    public sealed class AudioCustomQualityViewModel : ViewModelBase
    {
        // Which field the view should focus after a validation warning.
        public enum FocusTarget { None, Bitrate, Buffer, FlacLevel }

        // A request from the VM to the view to show a warning and focus a field.
        public sealed record ValidationRequest(string Message, string Title, FocusTarget Focus);

        // Raised when the dialog should close. The bool becomes DialogResult.
        public event Action<bool>? RequestClose;

        // Raised when a field is invalid on Apply. The view shows the warning.
        public event Action<ValidationRequest>? ValidationRequested;

        // Populated on a successful Apply. Read back by the window. Null if cancelled.
        public (string Codec, string Bitrate, int BufferMs, int FlacLevel)? ResultConfig { get; private set; }

        // Breaks the feedback loop between the codec and preset selections: when one
        // changes the other programmatically, we do not want that change to bounce back.
        private bool _suppressSync;

        private readonly PresetOption _customOption;

        public bool ShowPresetPicker { get; }
        public IReadOnlyList<string> Codecs { get; } = new[] { "opus", "flac", "raw" };
        public IReadOnlyList<PresetOption> Presets { get; }

        public RelayCommand ApplyCommand { get; }
        public RelayCommand CancelCommand { get; }

        public AudioCustomQualityViewModel(MusicConfig current, bool showPresets)
        {
            ShowPresetPicker = showPresets;

            ApplyCommand = new RelayCommand(Apply);
            CancelCommand = new RelayCommand(Cancel);

            _customOption = new PresetOption("Custom", null);
            var options = new List<PresetOption> { _customOption };
            foreach (var preset in AudioQualityPresets.All)
                options.Add(new PresetOption(preset.Name, preset));
            Presets = options;

            // Seed the editable fields from current config.
            string codec = string.IsNullOrWhiteSpace(current.ScrcpyAudioCodec)
                ? "raw"
                : current.ScrcpyAudioCodec.Trim().ToLowerInvariant();
            if (codec != "opus" && codec != "flac" && codec != "raw")
                codec = "opus";

            _suppressSync = true;
            _selectedCodec = codec;
            Bitrate = current.ScrcpyAudioBitrate ?? string.Empty;
            Buffer = (current.ScrcpyAudioBuffer > 0 ? current.ScrcpyAudioBuffer : 80).ToString();
            FlacLevel = Math.Clamp(current.ScrcpyFlacCompressionLevel, 1, 8).ToString();

            // Select the preset that matches the current config, or Custom if none match.
            // Done under suppression so it does not overwrite the fields we just seeded.
            AudioQualityPresets.Preset? matched = showPresets ? AudioQualityPresets.MatchFromConfig(current) : null;
            _selectedPreset = _customOption;
            if (matched != null)
            {
                foreach (var option in Presets)
                {
                    if (option.Preset != null &&
                        option.Preset.Name.Equals(matched.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        _selectedPreset = option;
                        break;
                    }
                }
            }
            _suppressSync = false;
        }

        // ── Bound properties ──────────────────────────────────────────────────

        private string _selectedCodec = "opus";
        public string SelectedCodec
        {
            get => _selectedCodec;
            set
            {
                if (!Set(ref _selectedCodec, value)) return;

                // Show/hide rules depend on the codec, so refresh them.
                RaisePropertyChanged(nameof(ShowBitrate));
                RaisePropertyChanged(nameof(ShowFlac));

                // If the user changed the codec by hand while a preset was selected,
                // the values no longer match that preset, so drop to Custom.
                if (!_suppressSync && ShowPresetPicker)
                {
                    _suppressSync = true;
                    SelectedPreset = _customOption;
                    _suppressSync = false;
                }
            }
        }

        private PresetOption? _selectedPreset;
        public PresetOption? SelectedPreset
        {
            get => _selectedPreset;
            set
            {
                if (!Set(ref _selectedPreset, value)) return;
                if (_suppressSync) return;

                // Picking a concrete preset fills every field. Picking Custom leaves
                // the fields exactly as they are.
                if (value?.Preset is { } preset)
                {
                    _suppressSync = true;
                    SelectedCodec = preset.Codec;
                    Bitrate = preset.Bitrate ?? string.Empty;
                    Buffer = preset.BufferMs.ToString();
                    FlacLevel = preset.FlacCompressionLevel.ToString();
                    _suppressSync = false;
                }
            }
        }

        private string _bitrate = string.Empty;
        public string Bitrate
        {
            get => _bitrate;
            set => Set(ref _bitrate, value);
        }

        private string _buffer = string.Empty;
        public string Buffer
        {
            get => _buffer;
            set => Set(ref _buffer, value);
        }

        private string _flacLevel = string.Empty;
        public string FlacLevel
        {
            get => _flacLevel;
            set => Set(ref _flacLevel, value);
        }

        // Bitrate only matters for opus; FLAC level only for flac. These derived
        // booleans drive the Visibility bindings in the XAML through a converter.
        public bool ShowBitrate => string.Equals(_selectedCodec, "opus", StringComparison.OrdinalIgnoreCase);
        public bool ShowFlac => string.Equals(_selectedCodec, "flac", StringComparison.OrdinalIgnoreCase);

        // ── Commands ──────────────────────────────────────────────────────────

        private void Apply()
        {
            string codec = _selectedCodec;

            // In picker mode, a chosen (non-Custom) preset is applied directly without
            // validating the custom fields, which are only there for display.
            if (ShowPresetPicker && SelectedPreset?.Preset is { } selectedPreset)
            {
                ResultConfig = (
                    selectedPreset.Codec,
                    selectedPreset.Bitrate ?? string.Empty,
                    selectedPreset.BufferMs,
                    selectedPreset.FlacCompressionLevel
                );
                RequestClose?.Invoke(true);
                return;
            }

            // Validate bitrate (opus only).
            string bitrate = string.Empty;
            if (string.Equals(codec, "opus", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(Bitrate.Trim(), out var br) || br <= 0)
                {
                    ValidationRequested?.Invoke(new ValidationRequest(
                        "Enter a valid bitrate in kbps (e.g. 128).", "Invalid bitrate", FocusTarget.Bitrate));
                    return;
                }
                bitrate = br.ToString();
            }

            // Validate buffer.
            if (!int.TryParse(Buffer.Trim(), out var bufferMs) || bufferMs <= 0)
            {
                ValidationRequested?.Invoke(new ValidationRequest(
                    "Enter a valid buffer in milliseconds (e.g. 80).", "Invalid buffer", FocusTarget.Buffer));
                return;
            }

            // Validate FLAC level (flac only).
            int flacLevel = 2;
            if (string.Equals(codec, "flac", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(FlacLevel.Trim(), out flacLevel) || flacLevel < 1 || flacLevel > 8)
                {
                    ValidationRequested?.Invoke(new ValidationRequest(
                        "Enter a FLAC compression level between 1 and 8.", "Invalid FLAC level", FocusTarget.FlacLevel));
                    return;
                }
            }

            ResultConfig = (codec, bitrate, bufferMs, flacLevel);
            RequestClose?.Invoke(true);
        }

        private void Cancel()
        {
            RequestClose?.Invoke(false);
        }
    }
}
