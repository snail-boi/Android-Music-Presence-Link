using Microsoft.VisualBasic.Devices;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace musicpresense
{
    public partial class MainWindow
    {
        private void ApplyConfigToUI()
        {
            TxtUsbSerial.Text = _config.SelectedDeviceUSB;
            TxtWifi.Text = _config.SelectedDeviceWiFi;
            TxtDeviceName.Text = _config.SelectedDeviceName;
            if (TxtMdnsService != null)
                TxtMdnsService.Text = _config.WifiMdnsServiceName ?? string.Empty;
            SelectWifiModeFromConfig();
            UpdatePairButtonVisibility();

            _remoteRoots.Clear();
            foreach (var root in GetNormalizedRemoteRoots(_config))
            {
                _remoteRoots.Add(root);
            }

            RefreshAppsSummary();

            int mode = (int)_config.UpdateIntervalMode;
            if (mode < 1 || mode > 4) mode = 1;
            CmbUpdateInterval.SelectedIndex = mode - 1;
            UpdateIntervalWarningVisibility();

            ChkDebugMode.IsChecked = _config.DebugMode;
            ChkDarkMode.IsChecked = _config.UseDarkMode;
            ChkOpenInTaskbar.IsChecked = _config.OpenInTaskbar;
            ChkStartWithWindows.IsChecked = _config.StartWithWindows;
            UpdateThemeToggleText(_config.UseDarkMode);

            TxtAudioBitrate.Text = _config.ScrcpyAudioBitrate ?? string.Empty;
            TxtAudioBuffer.Text = _config.ScrcpyAudioBuffer > 0 ? _config.ScrcpyAudioBuffer.ToString() : "50";
            TxtFlacCompressionLevel.Text = _config.ScrcpyFlacCompressionLevel.ToString();
            TxtPauseClearDelayMinutes.Text = _config.SmtcPauseClearDelayMinutes.ToString();
            TxtCacheClear.Text = _config.CachClearInMB.ToString();

            SelectCodecFromConfig();
            UpdateCodecDependentFields();

            try { TxtHotkeyVolumeUp.Text = HotkeyHelper.VirtualKeyToDisplayName(_config.HotkeyVolumeUpKey); } catch { TxtHotkeyVolumeUp.Text = string.Empty; }
            try { TxtHotkeyVolumeDown.Text = HotkeyHelper.VirtualKeyToDisplayName(_config.HotkeyVolumeDownKey); } catch { TxtHotkeyVolumeDown.Text = string.Empty; }
            try { TxtHotkeyToggleScrcpy.Text = HotkeyHelper.VirtualKeyToDisplayName(_config.HotkeyToggleScrcpyKey); } catch { TxtHotkeyToggleScrcpy.Text = string.Empty; }
            try { TxtHotkeyToggleLyricsOverlay.Text = HotkeyHelper.VirtualKeyToDisplayName(_config.HotkeyToggleLyricsOverlayKey); } catch { TxtHotkeyToggleLyricsOverlay.Text = string.Empty; }
            try { TxtHotkeyCopyTrackInfo.Text = HotkeyHelper.VirtualKeyToDisplayName(_config.HotkeyCopyTrackInfoKey); } catch { TxtHotkeyCopyTrackInfo.Text = string.Empty; }
            try { TxtHotkeyAudioQuality.Text = HotkeyHelper.VirtualKeyToDisplayName(_config.HotkeyAudioQualityKey); } catch { TxtHotkeyAudioQuality.Text = string.Empty; }
            TxtLyricsFolderOverride.Text = _config.LyricsSearchFolderOverride ?? string.Empty;
            TxtCoverPatterns.Text = _config.CoverArtFileNamePatterns ?? string.Empty;
            TxtCopyTrackTemplate.Text = _config.CopyTrackInfoTemplate ?? string.Empty;

            try
            {
                foreach (var item in CmbHotkeyModifier.Items)
                {
                    if (item is System.Windows.Controls.ComboBoxItem cbi && cbi.Tag != null)
                    {
                        if (int.TryParse(cbi.Tag.ToString()?.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out var mod) && mod == _config.HotkeyModifier)
                        {
                            CmbHotkeyModifier.SelectedItem = cbi;
                            break;
                        }
                    }
                }
            }
            catch { }
        }
        private void CmbUpdateInterval_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateIntervalWarningVisibility();
        }

        private void UpdateIntervalWarningVisibility()
        {
            if (TxtUpdateIntervalWarning == null) return;
            // Indices 0=1sec, 1=3sec, 2=5sec, 3=10sec. Warn for index >= 2 (5 sec and above).
            bool slow = CmbUpdateInterval.SelectedIndex >= 1;
            TxtUpdateIntervalWarning.Visibility = slow ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            if (CmbUpdateInterval.SelectedIndex == 1)
            {
                TxtUpdateIntervalWarning.Text = "Intervals above 1 sec may cause timing issue's in the mediaplayer";
            }
            else
            {
                TxtUpdateIntervalWarning.Text = "Intervals above 3 sec disable audio link hotswap recovery and may cause timing issue's in the mediaplayer.";
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            SaveConfigFromUi(true);
        }
        private void SaveConfigFromUi(bool showConfirmation)
        {
            _config.SelectedDeviceUSB = TxtUsbSerial.Text.Trim();
            _config.SelectedDeviceWiFi = TxtWifi.Text.Trim();
            _config.SelectedDeviceName = TxtDeviceName.Text.Trim();
            _config.WifiMode = GetSelectedWifiMode();
            // WifiMdnsServiceName is updated by the pair flow only, never typed by hand.

            // Mode-specific cleanup on save: drop fields that don't apply to
            // the saved mode so they don't linger as stale values. The user's
            // mode-switch is materialized at this point.
            //   TcpIp mode:           drop WifiMdnsServiceName (WD-only).
            //   WirelessDebugging:    SelectedDeviceWiFi from the pair flow
            //                         is valid; keep both. But if the user
            //                         switched FROM TcpIp without re-pairing,
            //                         the stored ip:5555 would be stale, so
            //                         we clear it unless the pair flow has
            //                         since populated WifiMdnsServiceName
            //                         (which means we have a real WD ip:port).
            if (_config.WifiMode == WirelessMode.TcpIp)
            {
                if (!string.IsNullOrWhiteSpace(_config.WifiMdnsServiceName))
                {
                    _config.WifiMdnsServiceName = string.Empty;
                    if (TxtMdnsService != null) TxtMdnsService.Text = string.Empty;
                }
            }
            else // WirelessDebugging
            {
                if (string.IsNullOrWhiteSpace(_config.WifiMdnsServiceName))
                {
                    // No pair has happened yet, so any ip:port currently in
                    // SelectedDeviceWiFi came from the old TcpIp config and
                    // is meaningless here.
                    _config.SelectedDeviceWiFi = string.Empty;
                    if (TxtWifi != null) TxtWifi.Text = string.Empty;
                }
            }
            _config.MusicRemoteRoots = _remoteRoots
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            _config.MusicRemoteRoot = _config.MusicRemoteRoots.FirstOrDefault() ?? string.Empty;

            _config.EligibleApps = _appPackages
                .Select(item => new EligibleAppConfig
                {
                    PackageName = item.PackageName,
                    PresenceMode = item.PresenceMode,
                    EnableCoverSearch = item.EnableCoverSearch
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.PackageName))
                .GroupBy(item => item.PackageName, StringComparer.OrdinalIgnoreCase)
                .Select(g => new EligibleAppConfig
                {
                    PackageName = g.Key,
                    PresenceMode = g.Max(x => (int)x.PresenceMode) switch { 2 => PresenceMode.Full, 1 => PresenceMode.Half, _ => PresenceMode.Off },
                    EnableCoverSearch = g.Any(x => x.EnableCoverSearch)
                })
                .ToList();

            _config.AllowedApps = _config.EligibleApps
                .Where(a => a.PresenceMode != PresenceMode.Off)
                .Select(a => a.PackageName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!_isInitializing && CmbUpdateInterval.SelectedIndex >= 0)
            {
                _config.UpdateIntervalMode = (UpdateIntervalMode)(CmbUpdateInterval.SelectedIndex + 1);
            }

            _config.DebugMode = ChkDebugMode.IsChecked == true;
            _config.UseDarkMode = ChkDarkMode.IsChecked == true;
            _config.OpenInTaskbar = ChkOpenInTaskbar.IsChecked == true;
            _config.StartWithWindows = ChkStartWithWindows.IsChecked == true;
            if (int.TryParse(TxtCacheClear.Text.Trim(), out var CacheValue))
            {
                if (CacheValue < 10)
                {
                    CacheValue = 10;
                    TxtCacheClear.Text = CacheValue.ToString();
                }


                _config.CachClearInMB = CacheValue > 0 ? CacheValue : 10;
            }
            else
            {
                CacheValue = 10;
                TxtCacheClear.Text = CacheValue.ToString();
                _config.CachClearInMB = CacheValue > 0 ? CacheValue : 10;
            }

            var selectedCodec = LstAudioCodecs.SelectedItem as string ?? "raw";
            _config.ScrcpyAudioCodec = selectedCodec;

            if (selectedCodec.Equals("raw", StringComparison.OrdinalIgnoreCase))
            {
                _config.ScrcpyAudioBitrate = string.Empty;
            }
            else
            {
                var bitrateText = TxtAudioBitrate.Text.Trim();
                if (string.IsNullOrEmpty(bitrateText))
                {
                    _config.ScrcpyAudioBitrate = string.Empty;
                }
                else if (int.TryParse(bitrateText, out var bitrateValue))
                {
                    if (bitrateValue < 1)
                        bitrateValue = 1;

                    if (bitrateValue > 10000)
                    {
                        var message = BuildBitrateWarningMessage(selectedCodec, bitrateValue);
                        var response = MessageBox.Show(message, "High bitrate warning", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                        if (response == MessageBoxResult.No)
                        {
                            bitrateValue = GetTypicalBitrate(selectedCodec);
                            TxtAudioBitrate.Text = bitrateValue.ToString();
                        }
                    }

                    _config.ScrcpyAudioBitrate = bitrateValue > 0 ? bitrateValue.ToString() : string.Empty;
                }
                else
                {
                    _config.ScrcpyAudioBitrate = string.Empty;
                }
            }

            if (int.TryParse(TxtAudioBuffer.Text.Trim(), out var bufferValue) && bufferValue > 0)
            {
                if (bufferValue > 2000)
                {
                    var response = MessageBox.Show(
                        "The audio buffer is above 2000 ms, which can introduce a noticeable delay. Continue with this value?",
                        "Large audio buffer",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (response == MessageBoxResult.No)
                    {
                        bufferValue = 2000;
                        TxtAudioBuffer.Text = bufferValue.ToString();
                    }
                }

                _config.ScrcpyAudioBuffer = Math.Max(1, bufferValue);
            }
            else
            {
                _config.ScrcpyAudioBuffer = 50;
            }

            if (int.TryParse(TxtFlacCompressionLevel.Text.Trim(), out var flacLevel))
            {
                var clampedFlac = Math.Clamp(flacLevel, 1, 8);
                if (clampedFlac != flacLevel)
                {
                    TxtFlacCompressionLevel.Text = clampedFlac.ToString();
                }

                _config.ScrcpyFlacCompressionLevel = clampedFlac;
            }
            else
            {
                _config.ScrcpyFlacCompressionLevel = 5;
            }

            if (int.TryParse(TxtPauseClearDelayMinutes.Text.Trim(), out var pauseDelay))
            {
                _config.SmtcPauseClearDelayMinutes = Math.Max(0, pauseDelay);
            }
            else
            {
                _config.SmtcPauseClearDelayMinutes = 3;
            }

            // Parse and store hotkey settings (allows hex 0x.., decimal, single letters or common names)
            _config.HotkeyVolumeUpKey = HotkeyHelper.ParseVirtualKey(TxtHotkeyVolumeUp.Text.Trim(), _config.HotkeyVolumeUpKey);
            _config.HotkeyVolumeDownKey = HotkeyHelper.ParseVirtualKey(TxtHotkeyVolumeDown.Text.Trim(), _config.HotkeyVolumeDownKey);
            _config.HotkeyToggleScrcpyKey = HotkeyHelper.ParseVirtualKey(TxtHotkeyToggleScrcpy.Text.Trim(), _config.HotkeyToggleScrcpyKey);
            _config.HotkeyToggleLyricsOverlayKey = HotkeyHelper.ParseVirtualKey(TxtHotkeyToggleLyricsOverlay.Text.Trim(), _config.HotkeyToggleLyricsOverlayKey);
            _config.HotkeyCopyTrackInfoKey = HotkeyHelper.ParseVirtualKey(TxtHotkeyCopyTrackInfo.Text.Trim(), _config.HotkeyCopyTrackInfoKey);
            _config.HotkeyAudioQualityKey = HotkeyHelper.ParseVirtualKey(TxtHotkeyAudioQuality.Text.Trim(), _config.HotkeyAudioQualityKey);
            _config.LyricsSearchFolderOverride = TxtLyricsFolderOverride.Text.Trim();
            _config.CoverArtFileNamePatterns = TxtCoverPatterns.Text.Trim();
            _config.CopyTrackInfoTemplate = TxtCopyTrackTemplate.Text.Trim();

            // Modifier: use selected combobox item
            try
            {
                if (CmbHotkeyModifier.SelectedItem is System.Windows.Controls.ComboBoxItem cbi && cbi.Tag != null)
                {
                    if (int.TryParse(cbi.Tag.ToString()?.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out var mod))
                    {
                        _config.HotkeyModifier = mod;
                    }
                }
            }
            catch { }

            // Auto-detect which preset (if any) the saved values match. Anything that
            // doesn't match a preset exactly is recorded as "Custom".
            var detectedPreset = AudioQualityPresets.MatchFromConfig(_config);
            _config.AudioQualityPresetName = detectedPreset?.Name ?? AudioQualityPresets.CustomLabel;

            MusicConfigManager.Save(_config);
            (Application.Current as App)?.UpdateConfig(_config);
            _savedConfig = _config.Clone();

            Debugger.show("[SETTINGS] Settings saved.");

            if (showConfirmation)
            {
                MessageBox.Show("Music presence settings saved.", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        private MusicConfig BuildConfigFromUi()
        {
            var config = _config.Clone();

            config.SelectedDeviceUSB = TxtUsbSerial.Text.Trim();
            config.SelectedDeviceWiFi = TxtWifi.Text.Trim();
            config.SelectedDeviceName = TxtDeviceName.Text.Trim();
            config.WifiMode = GetSelectedWifiMode();
            config.WifiMdnsServiceName = _config.WifiMdnsServiceName ?? string.Empty;

            // Mirror the cleanup that SaveConfigFromUi performs, so the
            // unsaved-changes diff reflects the post-save state. Without this,
            // toggling the mode dropdown would not register as "changed"
            // because the stale fields would still equal the saved snapshot.
            if (config.WifiMode == WirelessMode.TcpIp)
            {
                config.WifiMdnsServiceName = string.Empty;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(config.WifiMdnsServiceName))
                {
                    config.SelectedDeviceWiFi = string.Empty;
                }
            }
            config.MusicRemoteRoots = _remoteRoots
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            config.MusicRemoteRoot = config.MusicRemoteRoots.FirstOrDefault() ?? string.Empty;

            if (_appPackages.Count > 0)
            {
                config.EligibleApps = _appPackages
                    .Select(item => new EligibleAppConfig
                    {
                        PackageName = item.PackageName,
                        PresenceMode = item.PresenceMode,
                        EnableCoverSearch = item.EnableCoverSearch
                    })
                    .Where(item => !string.IsNullOrWhiteSpace(item.PackageName))
                    .GroupBy(item => item.PackageName, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new EligibleAppConfig
                    {
                        PackageName = g.Key,
                        PresenceMode = g.Max(x => (int)x.PresenceMode) switch { 2 => PresenceMode.Full, 1 => PresenceMode.Half, _ => PresenceMode.Off },
                        EnableCoverSearch = g.Any(x => x.EnableCoverSearch)
                    })
                    .ToList();
            }
            else
            {
                config.EligibleApps = _config.EligibleApps?.Select(a => new EligibleAppConfig
                {
                    PackageName = a.PackageName,
                    PresenceMode = a.PresenceMode,
                    EnableCoverSearch = a.EnableCoverSearch
                }).ToList() ?? new List<EligibleAppConfig>();
            }

            config.AllowedApps = config.EligibleApps
                .Where(a => a.PresenceMode != PresenceMode.Off)
                .Select(a => a.PackageName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            config.DebugMode = ChkDebugMode.IsChecked == true;
            config.UseDarkMode = ChkDarkMode.IsChecked == true;
            config.OpenInTaskbar = ChkOpenInTaskbar.IsChecked == true;
            config.StartWithWindows = ChkStartWithWindows.IsChecked == true;
            if (int.TryParse(TxtCacheClear.Text.Trim(), out var CacheValue))
            {
                if (CacheValue < 10)
                {
                    CacheValue = 10;
                    TxtCacheClear.Text = CacheValue.ToString();
                }


                _config.CachClearInMB = CacheValue > 0 ? CacheValue : 10;
            }
            else
            {
                CacheValue = 10;
                TxtCacheClear.Text = CacheValue.ToString();
                _config.CachClearInMB = CacheValue > 0 ? CacheValue : 10;
            }


            var selectedCodec = LstAudioCodecs.SelectedItem as string ?? "raw";
            config.ScrcpyAudioCodec = selectedCodec;

            if (selectedCodec.Equals("raw", StringComparison.OrdinalIgnoreCase))
            {
                config.ScrcpyAudioBitrate = string.Empty;
            }
            else
            {
                var bitrateText = TxtAudioBitrate.Text.Trim();
                if (string.IsNullOrEmpty(bitrateText))
                {
                    config.ScrcpyAudioBitrate = string.Empty;
                }
                else if (int.TryParse(bitrateText, out var bitrateValue))
                {
                    if (bitrateValue < 1)
                        bitrateValue = 1;

                    config.ScrcpyAudioBitrate = bitrateValue > 0 ? bitrateValue.ToString() : string.Empty;
                }
                else
                {
                    config.ScrcpyAudioBitrate = string.Empty;
                }
            }

            if (int.TryParse(TxtAudioBuffer.Text.Trim(), out var bufferValue) && bufferValue > 0)
            {
                config.ScrcpyAudioBuffer = Math.Max(1, bufferValue);
            }
            else
            {
                config.ScrcpyAudioBuffer = 50;
            }

            if (int.TryParse(TxtFlacCompressionLevel.Text.Trim(), out var flacLevel))
            {
                config.ScrcpyFlacCompressionLevel = Math.Clamp(flacLevel, 1, 8);
            }
            else
            {
                config.ScrcpyFlacCompressionLevel = 5;
            }

            if (int.TryParse(TxtPauseClearDelayMinutes.Text.Trim(), out var pauseDelay))
            {
                config.SmtcPauseClearDelayMinutes = Math.Max(0, pauseDelay);
            }
            else
            {
                config.SmtcPauseClearDelayMinutes = 3;
            }

            config.HotkeyVolumeUpKey = HotkeyHelper.ParseVirtualKey(TxtHotkeyVolumeUp.Text.Trim(), _config.HotkeyVolumeUpKey);
            config.HotkeyVolumeDownKey = HotkeyHelper.ParseVirtualKey(TxtHotkeyVolumeDown.Text.Trim(), _config.HotkeyVolumeDownKey);
            config.HotkeyToggleScrcpyKey = HotkeyHelper.ParseVirtualKey(TxtHotkeyToggleScrcpy.Text.Trim(), _config.HotkeyToggleScrcpyKey);
            config.HotkeyToggleLyricsOverlayKey = HotkeyHelper.ParseVirtualKey(TxtHotkeyToggleLyricsOverlay.Text.Trim(), _config.HotkeyToggleLyricsOverlayKey);
            config.HotkeyCopyTrackInfoKey = HotkeyHelper.ParseVirtualKey(TxtHotkeyCopyTrackInfo.Text.Trim(), _config.HotkeyCopyTrackInfoKey);
            config.HotkeyAudioQualityKey = HotkeyHelper.ParseVirtualKey(TxtHotkeyAudioQuality.Text.Trim(), _config.HotkeyAudioQualityKey);
            config.LyricsSearchFolderOverride = TxtLyricsFolderOverride.Text.Trim();
            config.CoverArtFileNamePatterns = TxtCoverPatterns.Text.Trim();
            config.CopyTrackInfoTemplate = TxtCopyTrackTemplate.Text.Trim();

            try
            {
                if (CmbHotkeyModifier.SelectedItem is System.Windows.Controls.ComboBoxItem cbi && cbi.Tag != null)
                {
                    if (int.TryParse(cbi.Tag.ToString()?.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out var mod))
                    {
                        config.HotkeyModifier = mod;
                    }
                }
            }
            catch { }

            // Auto-detect which preset (if any) the saved values match. If none match
            // we record "Custom" so the media player's quick-quality button reflects
            // the user's manual edits accurately.
            var detectedPreset = AudioQualityPresets.MatchFromConfig(config);
            config.AudioQualityPresetName = detectedPreset?.Name ?? AudioQualityPresets.CustomLabel;

            return config;
        }
        private static bool AreConfigsEqual(MusicConfig left, MusicConfig right)
        {
            if (left == null || right == null) return false;

            bool PathsEqual(PathsConfig? a, PathsConfig? b)
            {
                if (a == null || b == null) return a == b;
                return string.Equals(a.Adb, b.Adb, StringComparison.Ordinal)
                    && string.Equals(a.FfmpegPath, b.FfmpegPath, StringComparison.Ordinal)
                    && string.Equals(a.Scrcpy, b.Scrcpy, StringComparison.Ordinal)
                    && string.Equals(a.CoverCachePath, b.CoverCachePath, StringComparison.Ordinal);
            }

            if (!PathsEqual(left.Paths, right.Paths)) return false;
            if (!string.Equals(left.SelectedDeviceUSB, right.SelectedDeviceUSB, StringComparison.Ordinal)) return false;
            if (!string.Equals(left.SelectedDeviceWiFi, right.SelectedDeviceWiFi, StringComparison.Ordinal)) return false;
            if (!string.Equals(left.SelectedDeviceName, right.SelectedDeviceName, StringComparison.Ordinal)) return false;
            if (left.WifiMode != right.WifiMode) return false;
            if (!string.Equals(left.WifiMdnsServiceName ?? string.Empty, right.WifiMdnsServiceName ?? string.Empty, StringComparison.Ordinal)) return false;

            var leftRoots = (left.MusicRemoteRoots ?? new List<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var rightRoots = (right.MusicRemoteRoots ?? new List<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!leftRoots.SequenceEqual(rightRoots, StringComparer.OrdinalIgnoreCase)) return false;

            if (left.UpdateIntervalMode != right.UpdateIntervalMode) return false;
            if (left.DebugMode != right.DebugMode) return false;
            if (left.UseDarkMode != right.UseDarkMode) return false;
            if (left.OpenInTaskbar != right.OpenInTaskbar) return false;
            if (left.StartWithWindows != right.StartWithWindows) return false;
            if (left.ShowMediaPlayerWindow != right.ShowMediaPlayerWindow) return false;
            if (left.MediaPlayerSettingsPaneOpen != right.MediaPlayerSettingsPaneOpen) return false;
            if (left.MediaPlayerInlineLyricsViewActive != right.MediaPlayerInlineLyricsViewActive) return false;
            if (left.MediaPlayerFullscreenActive != right.MediaPlayerFullscreenActive) return false;
            if (left.OnboardingCompleted != right.OnboardingCompleted) return false;
            if (!string.Equals(left.ScrcpyAudioCodec, right.ScrcpyAudioCodec, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(left.ScrcpyAudioBitrate ?? string.Empty, right.ScrcpyAudioBitrate ?? string.Empty, StringComparison.Ordinal)) return false;
            if (left.ScrcpyAudioBuffer != right.ScrcpyAudioBuffer) return false;
            if (left.ScrcpyFlacCompressionLevel != right.ScrcpyFlacCompressionLevel) return false;
            if (left.SmtcPauseClearDelayMinutes != right.SmtcPauseClearDelayMinutes) return false;
            if (left.CachClearInMB != right.CachClearInMB) return false;
            if (left.HotkeyVolumeUpKey != right.HotkeyVolumeUpKey) return false;
            if (left.HotkeyVolumeDownKey != right.HotkeyVolumeDownKey) return false;
            if (left.HotkeyToggleScrcpyKey != right.HotkeyToggleScrcpyKey) return false;
            if (left.HotkeyToggleLyricsOverlayKey != right.HotkeyToggleLyricsOverlayKey) return false;
            if (left.HotkeyCopyTrackInfoKey != right.HotkeyCopyTrackInfoKey) return false;
            if (left.HotkeyAudioQualityKey != right.HotkeyAudioQualityKey) return false;
            if (left.HotkeyModifier != right.HotkeyModifier) return false;
            if (!string.Equals(left.LyricsSearchFolderOverride ?? string.Empty, right.LyricsSearchFolderOverride ?? string.Empty, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(left.CoverArtFileNamePatterns ?? string.Empty, right.CoverArtFileNamePatterns ?? string.Empty, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(left.CopyTrackInfoTemplate ?? string.Empty, right.CopyTrackInfoTemplate ?? string.Empty, StringComparison.Ordinal)) return false;

            var eligibleLeft = (left.EligibleApps ?? new List<EligibleAppConfig>())
                .Where(a => !string.IsNullOrWhiteSpace(a.PackageName))
                .GroupBy(a => a.PackageName.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => new EligibleAppConfig
                    {
                        PackageName = g.Key,
                        PresenceMode = g.Max(x => (int)x.PresenceMode) switch { 2 => PresenceMode.Full, 1 => PresenceMode.Half, _ => PresenceMode.Off },
                        EnableCoverSearch = g.Any(x => x.EnableCoverSearch)
                    },
                    StringComparer.OrdinalIgnoreCase);

            var eligibleRight = (right.EligibleApps ?? new List<EligibleAppConfig>())
                .Where(a => !string.IsNullOrWhiteSpace(a.PackageName))
                .GroupBy(a => a.PackageName.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => new EligibleAppConfig
                    {
                        PackageName = g.Key,
                        PresenceMode = g.Max(x => (int)x.PresenceMode) switch { 2 => PresenceMode.Full, 1 => PresenceMode.Half, _ => PresenceMode.Off },
                        EnableCoverSearch = g.Any(x => x.EnableCoverSearch)
                    },
                    StringComparer.OrdinalIgnoreCase);

            if (eligibleLeft.Count != eligibleRight.Count)
                return false;

            foreach (var pair in eligibleLeft)
            {
                if (!eligibleRight.TryGetValue(pair.Key, out var rightItem))
                    return false;

                if (pair.Value.PresenceMode != rightItem.PresenceMode)
                    return false;

                if (pair.Value.EnableCoverSearch != rightItem.EnableCoverSearch)
                    return false;
            }

            var codecsLeft = new HashSet<string>(left.ScrcpyAvailableAudioCodecs ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var codecsRight = new HashSet<string>(right.ScrcpyAvailableAudioCodecs ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            if (!codecsLeft.SetEquals(codecsRight)) return false;

            return true;
        }
        private void UpdateSavedSnapshot()
        {
            _savedConfig = _config.Clone();
        }
        private void RevertUnsavedChanges()
        {
            _config = _savedConfig.Clone();
            (Application.Current as App)?.UpdateConfig(_config);
            InitializeAudioCodecUI();
            ApplyConfigToUI();
        }
        private bool HasUnsavedChanges()
        {
            var currentConfig = BuildConfigFromUi();
            return !AreConfigsEqual(currentConfig, _savedConfig);
        }
        internal void SyncRuntimeConfig(MusicConfig config)
        {
            _config = config;
            _savedConfig = config.Clone();
            ApplyConfigToUI();
            UpdateMediaPlayerModeButton((Application.Current as App)?.IsMediaPlayerModeActive() == true);
        }
    }
}