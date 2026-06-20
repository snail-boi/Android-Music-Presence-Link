using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// The dialogs and message boxes the settings flow needs. Implemented by the window so the
    /// ViewModel never touches a Window or MessageBox directly. Reuses the public WifiPairResult
    /// record already defined for onboarding.
    /// </summary>
    internal interface ISettingsInteraction
    {
        void ShowInfo(string message, string title);
        void ShowWarning(string message, string title);
        bool ConfirmYesNo(string message, string title);
        WifiPairResult? ShowWifiPair();
        string? AskDeviceName();
    }

    /// <summary>
    /// ViewModel for the main settings window. Split across partials that mirror the old
    /// MainWindow_* files (Core, Device, Folders, Apps, AudioCodec, Hotkeys).
    ///
    /// Config model: the VM holds bindable properties seeded from a working <c>_config</c>.
    /// On save it assembles a fresh MusicConfig with <see cref="BuildConfig"/> (the single
    /// replacement for the old SaveConfigFromUi and BuildConfigFromUi), persists it, and
    /// snapshots it as <c>_savedConfig</c>. Unsaved-change detection compares a freshly built
    /// config against that snapshot with the original AreConfigsEqual, on demand, so there is no
    /// live IsDirty and the Save button is always enabled, matching the old behavior.
    /// </summary>
    internal sealed partial class SettingsViewModel : ViewModelBase
    {
        private MusicConfig _config;        // working base; BuildConfig clones and overrides it
        private MusicConfig _savedConfig;   // snapshot for the unsaved-changes comparison

        // Set by the window right after construction.
        public ISettingsInteraction? Interaction { get; set; }

        public RelayCommand SaveCommand { get; private set; } = null!;

        public SettingsViewModel(MusicConfig currentConfig)
        {
            _config = currentConfig.Clone();
            _savedConfig = currentConfig.Clone();

            InitCore();
            InitDevice();
            InitFolders();
            InitApps();
            InitAudioCodec();
            InitHotkeys();
        }

        // Per-group seams. Each group provides its own implementation; calls to a partial
        // method with no implementation compile to nothing, so this file builds on its own.
        partial void InitDevice();
        partial void InitFolders();
        partial void InitApps();
        partial void InitAudioCodec();
        partial void InitHotkeys();

        partial void LoadDeviceFromConfig();
        partial void LoadFoldersFromConfig();
        partial void LoadAppsFromConfig();
        partial void LoadAudioCodecFromConfig();
        partial void LoadHotkeysFromConfig();

        partial void ApplyDeviceToConfig(MusicConfig config);
        partial void ApplyFoldersToConfig(MusicConfig config);
        partial void ApplyAppsToConfig(MusicConfig config);
        partial void ApplyAudioCodecToConfig(MusicConfig config);
        partial void ApplyHotkeysToConfig(MusicConfig config);

        // Save-only interactive coercion (high bitrate / large buffer prompts) lives in the
        // AudioCodec partial. It must run on save only, never during the dirty check, which is
        // why it is separate from BuildConfig.
        partial void PromptRiskyAudioValuesForSave();

        // ── General settings ────────────────────────────────────────────────

        private bool _debugMode;
        public bool DebugMode { get => _debugMode; set => Set(ref _debugMode, value); }

        private bool _useDarkMode;
        public bool UseDarkMode { get => _useDarkMode; set => Set(ref _useDarkMode, value); }

        private bool _openInTaskbar;
        public bool OpenInTaskbar { get => _openInTaskbar; set => Set(ref _openInTaskbar, value); }

        private bool _startWithWindows;
        public bool StartWithWindows { get => _startWithWindows; set => Set(ref _startWithWindows, value); }

        private int _updateIntervalIndex;
        public int UpdateIntervalIndex
        {
            get => _updateIntervalIndex;
            set
            {
                if (!Set(ref _updateIntervalIndex, value)) return;
                RaisePropertyChanged(nameof(IntervalWarningVisible));
                RaisePropertyChanged(nameof(IntervalWarningText));
            }
        }

        // Mirrors the old UpdateIntervalWarningVisibility (which warns at index >= 1).
        public bool IntervalWarningVisible => UpdateIntervalIndex >= 1;
        public string IntervalWarningText => UpdateIntervalIndex == 1
            ? "Intervals above 1 sec may cause timing issue's in the mediaplayer"
            : "Intervals above 3 sec disable audio link hotswap recovery and may cause timing issue's in the mediaplayer.";

        private bool _adaptivePollingEnabled;
        public bool AdaptivePollingEnabled { get => _adaptivePollingEnabled; set => Set(ref _adaptivePollingEnabled, value); }

        private string _adaptivePollingThresholdText = "5";
        public string AdaptivePollingThresholdText { get => _adaptivePollingThresholdText; set => Set(ref _adaptivePollingThresholdText, value); }

        private bool _adaptivePollingAlertEnabled;
        public bool AdaptivePollingAlertEnabled { get => _adaptivePollingAlertEnabled; set => Set(ref _adaptivePollingAlertEnabled, value); }

        private string _cacheClearText = "10";
        public string CacheClearText { get => _cacheClearText; set => Set(ref _cacheClearText, value); }

        private string _pauseClearDelayText = "3";
        public string PauseClearDelayText { get => _pauseClearDelayText; set => Set(ref _pauseClearDelayText, value); }

        private string _coverPatterns = string.Empty;
        public string CoverPatterns { get => _coverPatterns; set => Set(ref _coverPatterns, value); }

        private string _copyTrackTemplate = string.Empty;
        public string CopyTrackTemplate { get => _copyTrackTemplate; set => Set(ref _copyTrackTemplate, value); }

        // ── Custom binary paths ───────────────────────────────────────────────

        private string _customAdbPath = string.Empty;
        public string CustomAdbPath { get => _customAdbPath; set => Set(ref _customAdbPath, value); }

        private string _customScrcpyPath = string.Empty;
        public string CustomScrcpyPath { get => _customScrcpyPath; set => Set(ref _customScrcpyPath, value); }

        private string _customFfmpegPath = string.Empty;
        public string CustomFfmpegPath { get => _customFfmpegPath; set => Set(ref _customFfmpegPath, value); }

        private RelayCommand? _browseAdbCommand;
        public RelayCommand BrowseAdbCommand => _browseAdbCommand ??= new RelayCommand(() =>
        {
            var path = BrowseForExecutable("adb.exe", "ADB executable|adb.exe|All executables|*.exe");
            if (path != null) CustomAdbPath = path;
        });

        private RelayCommand? _browseScrcpyCommand;
        public RelayCommand BrowseScrcpyCommand => _browseScrcpyCommand ??= new RelayCommand(() =>
        {
            var path = BrowseForExecutable("scrcpy.exe", "scrcpy executable|scrcpy.exe|All executables|*.exe");
            if (path != null) CustomScrcpyPath = path;
        });

        private RelayCommand? _browseFfmpegCommand;
        public RelayCommand BrowseFfmpegCommand => _browseFfmpegCommand ??= new RelayCommand(() =>
        {
            var path = BrowseForExecutable("ffmpeg.exe", "ffmpeg executable|ffmpeg.exe|All executables|*.exe");
            if (path != null) CustomFfmpegPath = path;
        });

        private RelayCommand? _resetBinaryPathsCommand;
        public RelayCommand ResetBinaryPathsCommand => _resetBinaryPathsCommand ??= new RelayCommand(() =>
        {
            CustomAdbPath = AppPaths.GetResourcePath("adb.exe");
            CustomScrcpyPath = AppPaths.GetResourcePath("scrcpy.exe");
            CustomFfmpegPath = AppPaths.GetResourcePath("ffmpeg.exe");
        });

        private static string? BrowseForExecutable(string fileName, string filter)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select " + fileName,
                Filter = filter,
                CheckFileExists = true
            };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }

        // ── No-cover icon ─────────────────────────────────────────────────────

        private string _noCoverIconPath = string.Empty;
        public string NoCoverIconPath { get => _noCoverIconPath; set => Set(ref _noCoverIconPath, value); }

        private RelayCommand? _browseNoCoverIconCommand;
        public RelayCommand BrowseNoCoverIconCommand => _browseNoCoverIconCommand ??= new RelayCommand(() =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select no-cover icon",
                Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.webp|All files|*.*",
                CheckFileExists = true
            };
            if (dlg.ShowDialog() == true)
                NoCoverIconPath = dlg.FileName;
        });

        private RelayCommand? _clearNoCoverIconCommand;
        public RelayCommand ClearNoCoverIconCommand => _clearNoCoverIconCommand ??= new RelayCommand(() =>
            NoCoverIconPath = string.Empty);

        // ── Toast / notification settings ────────────────────────────────────

        private static readonly string[] HeadlessToastPositionLabels =
            { "Top left", "Top center", "Top right", "Bottom left", "Bottom center", "Bottom right" };

        private static readonly string[] MediaPlayerToastModeLabels =
            { "In media player", "Headless overlay", "Off" };

        private bool _headlessToastEnabled;
        public bool HeadlessToastEnabled
        {
            get => _headlessToastEnabled;
            set => Set(ref _headlessToastEnabled, value);
        }

        private int _headlessToastPositionIndex;
        public int HeadlessToastPositionIndex
        {
            get => _headlessToastPositionIndex;
            set
            {
                if (!Set(ref _headlessToastPositionIndex, value)) return;
                RaisePropertyChanged(nameof(HeadlessToastPositionLabel));
            }
        }
        public string HeadlessToastPositionLabel
            => HeadlessToastPositionLabels[Math.Clamp(_headlessToastPositionIndex, 0, HeadlessToastPositionLabels.Length - 1)];

        private int _mediaPlayerToastModeIndex;
        public int MediaPlayerToastModeIndex
        {
            get => _mediaPlayerToastModeIndex;
            set
            {
                if (!Set(ref _mediaPlayerToastModeIndex, value)) return;
                RaisePropertyChanged(nameof(MediaPlayerToastModeLabel));
                RaisePropertyChanged(nameof(MediaPlayerToastModeOpacity));
            }
        }
        public string MediaPlayerToastModeLabel
            => MediaPlayerToastModeLabels[Math.Clamp(_mediaPlayerToastModeIndex, 0, MediaPlayerToastModeLabels.Length - 1)];
        public double MediaPlayerToastModeOpacity
            => _mediaPlayerToastModeIndex == (int)MediaPlayerToastMode.Off ? 0.45 : 1.0;

        public RelayCommand CycleHeadlessPositionCommand
            => _cycleHeadlessPositionCommand ??= new RelayCommand(() =>
            {
                HeadlessToastPositionIndex = (HeadlessToastPositionIndex + 1) % HeadlessToastPositionLabels.Length;
            });
        private RelayCommand? _cycleHeadlessPositionCommand;

        public RelayCommand CycleMediaPlayerToastModeCommand
            => _cycleMediaPlayerToastModeCommand ??= new RelayCommand(() =>
            {
                MediaPlayerToastModeIndex = (MediaPlayerToastModeIndex + 1) % MediaPlayerToastModeLabels.Length;
            });
        private RelayCommand? _cycleMediaPlayerToastModeCommand;

        private void InitCore()
        {
            SaveCommand = new RelayCommand(() => Save(true));
            LoadCoreFromConfig();
        }

        private void LoadCoreFromConfig()
        {
            _debugMode = _config.DebugMode;
            _useDarkMode = _config.UseDarkMode;
            _openInTaskbar = _config.OpenInTaskbar;
            _startWithWindows = _config.StartWithWindows;

            int mode = (int)_config.UpdateIntervalMode;
            if (mode < 1 || mode > 4) mode = 1;
            _updateIntervalIndex = mode - 1;

            _adaptivePollingEnabled = _config.AdaptivePollingEnabled;
            _adaptivePollingThresholdText = _config.AdaptivePollingThresholdMinutes.ToString();
            _adaptivePollingAlertEnabled = _config.AdaptivePollingAlertEnabled;

            _cacheClearText = _config.CachClearInMB.ToString();
            _pauseClearDelayText = _config.SmtcPauseClearDelayMinutes.ToString();
            _coverPatterns = _config.CoverArtFileNamePatterns ?? string.Empty;
            _copyTrackTemplate = _config.CopyTrackInfoTemplate ?? string.Empty;

            var paths = _config.Paths ?? new PathsConfig();
            _customAdbPath = paths.Adb;
            _customScrcpyPath = paths.Scrcpy;
            _customFfmpegPath = paths.FfmpegPath;
            _noCoverIconPath = paths.NoCoverIconPath ?? string.Empty;

            _headlessToastEnabled = _config.HeadlessToastEnabled;
            _headlessToastPositionIndex = (int)_config.HeadlessToastPosition;
            _mediaPlayerToastModeIndex = (int)_config.MediaPlayerToastMode;
        }

        private void ApplyCoreToConfig(MusicConfig config)
        {
            config.DebugMode = DebugMode;
            config.UseDarkMode = UseDarkMode;
            config.OpenInTaskbar = OpenInTaskbar;
            config.StartWithWindows = StartWithWindows;

            config.UpdateIntervalMode = (UpdateIntervalMode)(Math.Clamp(UpdateIntervalIndex, 0, 3) + 1);
            config.AdaptivePollingEnabled = AdaptivePollingEnabled;
            if (int.TryParse(AdaptivePollingThresholdText.Trim(), out var threshold) && threshold >= 1)
                config.AdaptivePollingThresholdMinutes = threshold;
            else
                config.AdaptivePollingThresholdMinutes = 5;
            config.AdaptivePollingAlertEnabled = AdaptivePollingAlertEnabled;

            if (int.TryParse(CacheClearText.Trim(), out var cache))
            {
                if (cache < 10)
                {
                    cache = 10;
                    CacheClearText = cache.ToString();
                }
                config.CachClearInMB = cache > 0 ? cache : 10;
            }
            else
            {
                CacheClearText = "10";
                config.CachClearInMB = 10;
            }

            if (int.TryParse(PauseClearDelayText.Trim(), out var pause))
            {
                config.SmtcPauseClearDelayMinutes = Math.Max(0, pause);
            }
            else
            {
                config.SmtcPauseClearDelayMinutes = 3;
                PauseClearDelayText = "3";
            }

            config.CoverArtFileNamePatterns = CoverPatterns.Trim();
            config.CopyTrackInfoTemplate = CopyTrackTemplate.Trim();

            config.Paths ??= new PathsConfig();
            config.Paths.Adb = string.IsNullOrWhiteSpace(CustomAdbPath)
                ? AppPaths.GetResourcePath("adb.exe") : CustomAdbPath.Trim();
            config.Paths.Scrcpy = string.IsNullOrWhiteSpace(CustomScrcpyPath)
                ? AppPaths.GetResourcePath("scrcpy.exe") : CustomScrcpyPath.Trim();
            config.Paths.FfmpegPath = string.IsNullOrWhiteSpace(CustomFfmpegPath)
                ? AppPaths.GetResourcePath("ffmpeg.exe") : CustomFfmpegPath.Trim();
            config.Paths.NoCoverIconPath = NoCoverIconPath.Trim();

            config.HeadlessToastEnabled = HeadlessToastEnabled;
            config.HeadlessToastPosition = (HeadlessToastPosition)Math.Clamp(HeadlessToastPositionIndex, 0, 5);
            config.MediaPlayerToastMode = (MediaPlayerToastMode)Math.Clamp(MediaPlayerToastModeIndex, 0, 2);
        }

        // ── Build / Save / Dirty / Revert / Sync ─────────────────────────────

        private MusicConfig BuildConfig()
        {
            var config = _config.Clone();

            ApplyCoreToConfig(config);
            ApplyDeviceToConfig(config);
            ApplyFoldersToConfig(config);
            ApplyAppsToConfig(config);
            ApplyAudioCodecToConfig(config);
            ApplyHotkeysToConfig(config);

            // Record which quality preset (if any) the final values match, else "Custom".
            var detectedPreset = AudioQualityPresets.MatchFromConfig(config);
            config.AudioQualityPresetName = detectedPreset?.Name ?? AudioQualityPresets.CustomLabel;

            return config;
        }

        public void Save(bool showConfirmation)
        {
            // Interactive, save-only prompts that may adjust bitrate/buffer before the build.
            PromptRiskyAudioValuesForSave();

            var built = BuildConfig();

            MusicConfigManager.Save(built);
            (Application.Current as App)?.UpdateConfig(built);

            _config = built;
            _savedConfig = built.Clone();

            Debugger.show("[SETTINGS] Settings saved.");

            if (showConfirmation)
                Interaction?.ShowInfo("Music presence settings saved.", "Saved");
        }

        public bool HasUnsavedChanges()
            => !AreConfigsEqual(BuildConfig(), _savedConfig);

        public void RevertUnsavedChanges()
        {
            _config = _savedConfig.Clone();
            (Application.Current as App)?.UpdateConfig(_config);
            ReseedFromConfig();
        }

        internal void UpdateSavedSnapshot()
        {
            _savedConfig = _config.Clone();
        }

        /// <summary>Pushes a runtime config from elsewhere into the open window.</summary>
        public void SyncRuntimeConfig(MusicConfig config)
        {
            _config = config;
            _savedConfig = config.Clone();
            ReseedFromConfig();
        }

        private void ReseedFromConfig()
        {
            LoadCoreFromConfig();
            LoadDeviceFromConfig();
            LoadFoldersFromConfig();
            LoadAppsFromConfig();
            LoadAudioCodecFromConfig();
            LoadHotkeysFromConfig();

            // Refresh every bound scalar property at once after a bulk reseed.
            RaisePropertyChanged(string.Empty);
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
                    && string.Equals(a.CoverCachePath, b.CoverCachePath, StringComparison.Ordinal)
                    && string.Equals(a.NoCoverIconPath ?? string.Empty, b.NoCoverIconPath ?? string.Empty, StringComparison.OrdinalIgnoreCase);
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
            if (left.AdaptivePollingEnabled != right.AdaptivePollingEnabled) return false;
            if (left.AdaptivePollingThresholdMinutes != right.AdaptivePollingThresholdMinutes) return false;
            if (left.AdaptivePollingAlertEnabled != right.AdaptivePollingAlertEnabled) return false;
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
            if (left.HeadlessToastEnabled != right.HeadlessToastEnabled) return false;
            if (left.HeadlessToastPosition != right.HeadlessToastPosition) return false;
            if (left.MediaPlayerToastMode != right.MediaPlayerToastMode) return false;

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
    }
}