using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// Audio codec group: the available codec list, the quality-preset picker, the
    /// bitrate/buffer/FLAC fields, and listing scrcpy encoders. The bitrate and FLAC panels show
    /// or hide based on the selected codec. The high-bitrate and large-buffer confirmations run
    /// on save only (PromptRiskyAudioValuesForSave), never during the dirty check.
    /// </summary>
    internal sealed partial class SettingsViewModel
    {
        public ObservableCollection<string> AudioCodecs { get; } = new();

        public string[] QualityPresets { get; } = AudioQualityPresets.All.Select(p => p.Name).ToArray();

        private bool _isLoadingCodecs;

        public RelayCommand ListCodecsCommand { get; private set; } = null!;

        private string? _selectedCodec;
        public string? SelectedCodec
        {
            get => _selectedCodec;
            set
            {
                if (!Set(ref _selectedCodec, value)) return;
                RaisePropertyChanged(nameof(ShowBitratePanel));
                RaisePropertyChanged(nameof(ShowFlacPanel));
            }
        }

        private string? _selectedQualityPreset;
        public string? SelectedQualityPreset
        {
            get => _selectedQualityPreset;
            set
            {
                if (!Set(ref _selectedQualityPreset, value)) return;
                if (!string.IsNullOrEmpty(value))
                    ApplyQualityPreset(value);
            }
        }

        private string _audioBitrate = string.Empty;
        public string AudioBitrate { get => _audioBitrate; set => Set(ref _audioBitrate, value); }

        private string _audioBuffer = "50";
        public string AudioBuffer { get => _audioBuffer; set => Set(ref _audioBuffer, value); }

        private string _flacCompressionLevel = "5";
        public string FlacCompressionLevel { get => _flacCompressionLevel; set => Set(ref _flacCompressionLevel, value); }

        private string _codecStatus = string.Empty;
        public string CodecStatus { get => _codecStatus; set => Set(ref _codecStatus, value); }

        private static bool CodecIs(string? codec, string name)
            => (codec ?? "raw").Equals(name, StringComparison.OrdinalIgnoreCase);

        public bool ShowBitratePanel => !CodecIs(SelectedCodec, "raw") && !CodecIs(SelectedCodec, "flac");
        public bool ShowFlacPanel => CodecIs(SelectedCodec, "flac");

        partial void InitAudioCodec()
        {
            ListCodecsCommand = new RelayCommand(LoadScrcpyCodecs);
            LoadAudioCodecFromConfig();
        }

        partial void LoadAudioCodecFromConfig()
        {
            AudioCodecs.Clear();
            if (_config.AudioLink.AvailableCodecs != null && _config.AudioLink.AvailableCodecs.Count > 0)
            {
                foreach (var codec in _config.AudioLink.AvailableCodecs.Distinct(StringComparer.OrdinalIgnoreCase))
                    AudioCodecs.Add(codec.ToLowerInvariant());
            }
            if (!AudioCodecs.Any(c => c.Equals("raw", StringComparison.OrdinalIgnoreCase)))
                AudioCodecs.Insert(0, "raw");

            _audioBitrate = _config.AudioLink.Bitrate ?? string.Empty;
            _audioBuffer = _config.AudioLink.BufferMs > 0 ? _config.AudioLink.BufferMs.ToString() : "50";
            _flacCompressionLevel = _config.AudioLink.FlacCompressionLevel.ToString();

            SelectCodecFromConfig();
        }

        partial void ApplyAudioCodecToConfig(MusicConfig config)
        {
            var selectedCodec = SelectedCodec ?? "raw";
            config.AudioLink.Codec = selectedCodec;

            if (selectedCodec.Equals("raw", StringComparison.OrdinalIgnoreCase))
            {
                config.AudioLink.Bitrate = string.Empty;
            }
            else
            {
                var bitrateText = AudioBitrate.Trim();
                if (string.IsNullOrEmpty(bitrateText))
                {
                    config.AudioLink.Bitrate = string.Empty;
                }
                else if (int.TryParse(bitrateText, out var bitrateValue))
                {
                    if (bitrateValue < 1)
                        bitrateValue = 1;
                    config.AudioLink.Bitrate = bitrateValue > 0 ? bitrateValue.ToString() : string.Empty;
                }
                else
                {
                    config.AudioLink.Bitrate = string.Empty;
                }
            }

            if (int.TryParse(AudioBuffer.Trim(), out var bufferValue) && bufferValue > 0)
                config.AudioLink.BufferMs = Math.Max(1, bufferValue);
            else
                config.AudioLink.BufferMs = 50;

            if (int.TryParse(FlacCompressionLevel.Trim(), out var flacLevel))
                config.AudioLink.FlacCompressionLevel = Math.Clamp(flacLevel, 1, 8);
            else
                config.AudioLink.FlacCompressionLevel = 5;
        }

        // Save-only interactive coercion. Adjusts the bound bitrate/buffer when the user declines
        // an extreme value, before BuildConfig reads them.
        partial void PromptRiskyAudioValuesForSave()
        {
            var selectedCodec = SelectedCodec ?? "raw";

            if (!selectedCodec.Equals("raw", StringComparison.OrdinalIgnoreCase))
            {
                var bitrateText = AudioBitrate.Trim();
                if (!string.IsNullOrEmpty(bitrateText) && int.TryParse(bitrateText, out var bitrateValue))
                {
                    if (bitrateValue < 1)
                        bitrateValue = 1;

                    if (bitrateValue > 10000)
                    {
                        var message = BuildBitrateWarningMessage(selectedCodec, bitrateValue);
                        if (Interaction?.ConfirmYesNo(message, "High bitrate warning") == false)
                        {
                            bitrateValue = GetTypicalBitrate(selectedCodec);
                            AudioBitrate = bitrateValue.ToString();
                        }
                    }
                }
            }

            if (int.TryParse(AudioBuffer.Trim(), out var bufferValue) && bufferValue > 0)
            {
                if (bufferValue > 2000)
                {
                    if (Interaction?.ConfirmYesNo(
                            "The audio buffer is above 2000 ms, which can introduce a noticeable delay. Continue with this value?",
                            "Large audio buffer") == false)
                    {
                        AudioBuffer = "2000";
                    }
                }
            }
        }

        private void SelectCodecFromConfig()
        {
            var codec = string.IsNullOrWhiteSpace(_config.AudioLink.Codec) ? "raw" : _config.AudioLink.Codec.Trim();
            if (!AudioCodecs.Any(c => c.Equals(codec, StringComparison.OrdinalIgnoreCase)))
                codec = "raw";

            SelectedCodec = AudioCodecs.FirstOrDefault(c => c.Equals(codec, StringComparison.OrdinalIgnoreCase))
                            ?? AudioCodecs.FirstOrDefault();
        }

        private void ApplyQualityPreset(string presetName)
        {
            var preset = AudioQualityPresets.FindByName(presetName);
            if (preset == null)
                return;

            SelectedCodec = preset.Codec;

            if (!string.IsNullOrEmpty(preset.Bitrate))
                AudioBitrate = preset.Bitrate;

            AudioBuffer = preset.BufferMs.ToString();

            if (preset.Codec.Equals("flac", StringComparison.OrdinalIgnoreCase))
                FlacCompressionLevel = preset.FlacCompressionLevel.ToString();

            // Stamp the preset onto the in-memory config so the media player's quality button
            // shows the friendly label even before Save.
            _config.AudioLink.QualityPresetName = preset.Name;
        }

        // The audio codecs the pipeline can actually use. This set is fixed by
        // what both ends support, not by the device: the scrcpy server only
        // accepts these for --audio-codec, and the audio DLL's decoder only
        // handles these (raw/opus/aac/flac). All four are guaranteed on any
        // Android 10+ device, so there is nothing to query — listing device
        // encoders would only ever return a subset of this and needs no adb
        // round-trip, no connected device, and no external scrcpy.exe.
        // "raw" is first (safe fallback), then the rest alphabetically.
        private static readonly string[] SupportedAudioCodecs = { "raw", "aac", "flac", "opus" };

        private void LoadScrcpyCodecs()
        {
            if (_isLoadingCodecs)
                return;

            _isLoadingCodecs = true;
            try
            {
                AudioCodecs.Clear();
                foreach (var codec in SupportedAudioCodecs)
                    AudioCodecs.Add(codec);

                _config.AudioLink.AvailableCodecs = SupportedAudioCodecs.ToList();
                MusicConfigManager.Save(_config);
                UpdateSavedSnapshot();

                SelectCodecFromConfig();
                CodecStatus = $"{AudioCodecs.Count} codecs";
            }
            catch (Exception ex)
            {
                CodecStatus = "Failed to load codecs.";
                Debugger.show("LoadScrcpyCodecs failed: " + ex.Message);
            }
            finally
            {
                _isLoadingCodecs = false;
            }
        }

        private static int GetTypicalBitrate(string codec)
        {
            return codec.ToLowerInvariant() switch
            {
                "opus" => 160,
                "aac" => 256,
                "flac" => 1000,
                "raw" => 0,
                _ => 320
            };
        }

        private static string BuildBitrateWarningMessage(string codec, int bitrateValue)
        {
            var guidance = codec.ToLowerInvariant() switch
            {
                "opus" => "Opus is typically transparent around 96-160 kbps for stereo music.",
                "aac" => "AAC is typically transparent around 128-256 kbps for stereo music.",
                "flac" => "FLAC is lossless; bitrate depends on content and is often 700-1100 kbps.",
                "raw" => "RAW is uncompressed PCM; bitrate depends on sample rate and channels.",
                _ => "Most encoders reach high quality well below 10000 kbps."
            };

            return $"The selected bitrate ({bitrateValue} kbps) is extremely high and usually unnecessary.\n\n" +
                   $"Encoder info: {guidance}\n\n" +
                   "Do you want to keep this value?";
        }
    }
}
