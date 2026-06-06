using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace musicpresense
{
    public partial class MainWindow
    {
        private void InitializeAudioCodecUI()
        {
            _audioCodecs.Clear();
            if (_config.ScrcpyAvailableAudioCodecs != null && _config.ScrcpyAvailableAudioCodecs.Count > 0)
            {
                foreach (var codec in _config.ScrcpyAvailableAudioCodecs.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    _audioCodecs.Add(codec.ToLowerInvariant());
                }
            }

            if (!_audioCodecs.Any(c => c.Equals("raw", StringComparison.OrdinalIgnoreCase)))
            {
                _audioCodecs.Insert(0, "raw");
            }
        }

        private void BtnListCodecs_Click(object sender, RoutedEventArgs e)
        {
            _ = LoadScrcpyCodecsAsync();
        }

        private void LstAudioCodecs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateCodecDependentFields();
        }

        private void UpdateCodecDependentFields()
        {
            var codec = LstAudioCodecs.SelectedItem as string ?? "raw";

            bool showBitrate = !codec.Equals("raw", StringComparison.OrdinalIgnoreCase)
                               && !codec.Equals("flac", StringComparison.OrdinalIgnoreCase);
            PanelAudioBitrate.Visibility = showBitrate ? Visibility.Visible : Visibility.Collapsed;

            PanelFlacCompression.Visibility = codec.Equals("flac", StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void UpdateAudioSettings(string selected)
        {
            var preset = AudioQualityPresets.FindByName(selected);
            if (preset == null)
                return;

            // Apply preset values to the visible controls. The codec list is the
            // source of truth for the listbox selection, so set its item directly.
            LstAudioCodecs.SelectedItem = preset.Codec;

            // Bitrate textbox: only meaningful for non-raw, non-flac codecs.
            if (!string.IsNullOrEmpty(preset.Bitrate))
            {
                TxtAudioBitrate.Text = preset.Bitrate;
            }

            TxtAudioBuffer.Text = preset.BufferMs.ToString();

            if (preset.Codec.Equals("flac", StringComparison.OrdinalIgnoreCase))
            {
                TxtFlacCompressionLevel.Text = preset.FlacCompressionLevel.ToString();
            }

            // Stamp the preset name on the in-memory config so the media player
            // button shows the friendly label even before the user clicks Save.
            _config.AudioQualityPresetName = preset.Name;
        }

        private void SelectCodecFromConfig()
        {
            var codec = string.IsNullOrWhiteSpace(_config.ScrcpyAudioCodec) ? "raw" : _config.ScrcpyAudioCodec.Trim();
            if (!_audioCodecs.Any(c => c.Equals(codec, StringComparison.OrdinalIgnoreCase)))
            {
                codec = "raw";
            }

            var selected = _audioCodecs.FirstOrDefault(c => c.Equals(codec, StringComparison.OrdinalIgnoreCase));
            LstAudioCodecs.SelectedItem = selected ?? _audioCodecs.FirstOrDefault();
        }

        private void CmbQualityPresets_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbQualityPresets.SelectedItem is string selected)
            {
                UpdateAudioSettings(selected);
            }
        }

        private async Task LoadScrcpyCodecsAsync()
        {
            if (_isLoadingCodecs)
                return;

            _isLoadingCodecs = true;
            TxtCodecStatus.Text = "Loading...";

            try
            {
                if (string.IsNullOrWhiteSpace(_config.Paths.Scrcpy) || !File.Exists(_config.Paths.Scrcpy))
                {
                    TxtCodecStatus.Text = "scrcpy.exe not found.";
                    return;
                }

                var device = await DeviceQuery.ResolveActiveDeviceAsync(_config).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(device))
                {
                    await Dispatcher.InvokeAsync(() => TxtCodecStatus.Text = "No device connected.");
                    return;
                }

                Debugger.show("Listing scrcpy encoders...");
                var output = await Task.Run(() => RunScrcpyListEncoders(_config.Paths.Scrcpy, device)).ConfigureAwait(false);
                Debugger.show(string.IsNullOrWhiteSpace(output) ? "scrcpy encoder list returned no output." : "scrcpy encoder list received.");
                var codecs = ParseScrcpyAudioCodecs(output);

                await Dispatcher.InvokeAsync(() =>
                {
                    _audioCodecs.Clear();
                    foreach (var codec in codecs)
                    {
                        _audioCodecs.Add(codec);
                    }

                    _config.ScrcpyAvailableAudioCodecs = codecs.ToList();
                    MusicConfigManager.Save(_config);
                    UpdateSavedSnapshot();

                    SelectCodecFromConfig();
                    UpdateCodecDependentFields();
                    TxtCodecStatus.Text = $"{_audioCodecs.Count} codecs";
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    TxtCodecStatus.Text = "Failed to list codecs.";
                    MessageBox.Show($"Failed to list scrcpy codecs: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
            finally
            {
                _isLoadingCodecs = false;
            }
        }

        private static string RunScrcpyListEncoders(string scrcpyPath, string device)
        {
            var psi = new ProcessStartInfo
            {
                FileName = scrcpyPath,
                Arguments = $"-s {device} --list-encoders",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            try
            {
                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    return string.Empty;
                }

                string output = proc.StandardOutput.ReadToEnd();
                string error = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                return output + Environment.NewLine + error;
            }
            catch (Exception ex)
            {
                Debugger.show("scrcpy list encoders failed: " + ex.Message);
                return string.Empty;
            }
        }

        private static List<string> ParseScrcpyAudioCodecs(string output)
        {
            var codecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "raw"
            };

            if (!string.IsNullOrWhiteSpace(output))
            {
                foreach (var line in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!line.Contains("--audio-codec=", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var match = Regex.Match(line, "--audio-codec=([a-z0-9]+)", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        codecs.Add(match.Groups[1].Value.ToLowerInvariant());
                    }
                }
            }

            Debugger.show($"Parsed scrcpy audio codecs: {string.Join(", ", codecs.OrderBy(c => c))}");

            return codecs
                .OrderBy(c => c.Equals("raw", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(c => c)
                .ToList();
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
