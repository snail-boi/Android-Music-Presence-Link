using System;
using System.Windows;
using System.Windows.Controls;

namespace musicpresense
{
    /// <summary>
    /// Audio quality window with two modes:
    ///
    /// Hotkey mode (showPresets = true): shows a preset combobox at the top so the
    /// user can pick a preset OR dial in custom values. This is for non-UI users who
    /// trigger the window via the global hotkey without the media player open.
    ///
    /// Media player mode (showPresets = false): shows only the custom codec/bitrate/
    /// buffer/FLAC fields. Preset switching is already available via the media player
    /// popup, so there is no need to repeat it here.
    ///
    /// On confirm, <see cref="ResultConfig"/> is populated and <see cref="DialogResult"/>
    /// is true.
    /// </summary>
    public partial class AudioCustomQualityWindow : Window
    {
        // Populated when the user clicks Apply. Null if cancelled.
        public (string Codec, string Bitrate, int BufferMs, int FlacLevel)? ResultConfig { get; private set; }

        // Suppress CmbCodec_SelectionChanged feedback while CmbPreset fills the fields.
        private bool _suppressCodecChanged;

        public AudioCustomQualityWindow(MusicConfig current, bool showPresets = false)
        {
            InitializeComponent();

            if (showPresets)
            {
                // Show preset picker + divider.
                LblPreset.Visibility = Visibility.Visible;
                CmbPreset.Visibility = Visibility.Visible;
                BrdDivider.Visibility = Visibility.Visible;

                // Populate preset combobox.
                var customItem = new ComboBoxItem { Content = "Custom", Tag = (object?)null };
                CmbPreset.Items.Add(customItem);
                AudioQualityPresets.Preset? matchedPreset = AudioQualityPresets.MatchFromConfig(current);

                foreach (var preset in AudioQualityPresets.All)
                {
                    var item = new ComboBoxItem { Content = preset.Name, Tag = preset };
                    CmbPreset.Items.Add(item);
                    if (matchedPreset != null &&
                        preset.Name.Equals(matchedPreset.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        CmbPreset.SelectedItem = item;
                    }
                }

                if (CmbPreset.SelectedItem == null)
                    CmbPreset.SelectedItem = customItem;
            }

            // Pre-fill custom fields from current config.
            var codec = string.IsNullOrWhiteSpace(current.ScrcpyAudioCodec)
                ? "raw"
                : current.ScrcpyAudioCodec.Trim().ToLowerInvariant();

            _suppressCodecChanged = true;
            SelectCodecCombo(codec);
            _suppressCodecChanged = false;

            TxtBitrate.Text = current.ScrcpyAudioBitrate ?? string.Empty;
            TxtBuffer.Text = (current.ScrcpyAudioBuffer > 0 ? current.ScrcpyAudioBuffer : 80).ToString();
            TxtFlacLevel.Text = Math.Clamp(current.ScrcpyFlacCompressionLevel, 1, 8).ToString();

            UpdateCodecDependentVisibility(codec);
        }

        // ── Preset combobox ───────────────────────────────────────────────────

        private void CmbPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbPreset.SelectedItem is not ComboBoxItem item) return;
            if (item.Tag is not AudioQualityPresets.Preset preset) return;

            // Fill the custom fields from the chosen preset.
            _suppressCodecChanged = true;
            SelectCodecCombo(preset.Codec);
            _suppressCodecChanged = false;

            TxtBitrate.Text = preset.Bitrate ?? string.Empty;
            TxtBuffer.Text = preset.BufferMs.ToString();
            TxtFlacLevel.Text = preset.FlacCompressionLevel.ToString();

            UpdateCodecDependentVisibility(preset.Codec);
        }

        // ── Codec combobox ────────────────────────────────────────────────────

        private void CmbCodec_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressCodecChanged) return;

            var codec = (CmbCodec.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "opus";
            UpdateCodecDependentVisibility(codec);

            // If a preset is selected and the user manually changes the codec,
            // switch preset combobox to "Custom".
            if (CmbPreset.Visibility == Visibility.Visible)
            {
                foreach (ComboBoxItem ci in CmbPreset.Items)
                {
                    if (ci.Tag == null)
                    {
                        CmbPreset.SelectionChanged -= CmbPreset_SelectionChanged;
                        CmbPreset.SelectedItem = ci;
                        CmbPreset.SelectionChanged += CmbPreset_SelectionChanged;
                        break;
                    }
                }
            }
        }

        private void SelectCodecCombo(string codec)
        {
            foreach (ComboBoxItem item in CmbCodec.Items)
            {
                if (string.Equals(item.Tag?.ToString(), codec, StringComparison.OrdinalIgnoreCase))
                {
                    CmbCodec.SelectedItem = item;
                    return;
                }
            }
            CmbCodec.SelectedIndex = 0;
        }

        private void UpdateCodecDependentVisibility(string codec)
        {
            bool isFlac = string.Equals(codec, "flac", StringComparison.OrdinalIgnoreCase);
            bool isRaw = string.Equals(codec, "raw", StringComparison.OrdinalIgnoreCase);

            // Bitrate: only meaningful for opus.
            var bitrateVis = (isFlac || isRaw) ? Visibility.Collapsed : Visibility.Visible;
            LblBitrate.Visibility = bitrateVis;
            TxtBitrate.Visibility = bitrateVis;

            // FLAC level: only for flac.
            var flacVis = isFlac ? Visibility.Visible : Visibility.Collapsed;
            LblFlac.Visibility = flacVis;
            TxtFlacLevel.Visibility = flacVis;
        }

        // ── Apply / Cancel ────────────────────────────────────────────────────

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            var codec = (CmbCodec.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "opus";

            // If a non-custom preset is selected in hotkey mode, use that directly
            // without validating the custom fields (they are just for display).
            if (CmbPreset.Visibility == Visibility.Visible &&
                CmbPreset.SelectedItem is ComboBoxItem presetItem &&
                presetItem.Tag is AudioQualityPresets.Preset selectedPreset)
            {
                ResultConfig = (
                    selectedPreset.Codec,
                    selectedPreset.Bitrate ?? string.Empty,
                    selectedPreset.BufferMs,
                    selectedPreset.FlacCompressionLevel
                );
                DialogResult = true;
                Close();
                return;
            }

            // Validate bitrate (opus only).
            string bitrate = string.Empty;
            if (string.Equals(codec, "opus", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(TxtBitrate.Text.Trim(), out var br) || br <= 0)
                {
                    MessageBox.Show("Enter a valid bitrate in kbps (e.g. 128).", "Invalid bitrate",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    TxtBitrate.Focus();
                    return;
                }
                bitrate = br.ToString();
            }

            // Validate buffer.
            if (!int.TryParse(TxtBuffer.Text.Trim(), out var bufferMs) || bufferMs <= 0)
            {
                MessageBox.Show("Enter a valid buffer in milliseconds (e.g. 80).", "Invalid buffer",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtBuffer.Focus();
                return;
            }

            // Validate FLAC level.
            int flacLevel = 2;
            if (string.Equals(codec, "flac", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(TxtFlacLevel.Text.Trim(), out flacLevel) || flacLevel < 1 || flacLevel > 8)
                {
                    MessageBox.Show("Enter a FLAC compression level between 1 and 8.", "Invalid FLAC level",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    TxtFlacLevel.Focus();
                    return;
                }
            }

            ResultConfig = (codec, bitrate, bufferMs, flacLevel);
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}