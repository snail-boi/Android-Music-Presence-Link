using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;

namespace AndroidMusicPresenceLink
{
    internal static class AppPaths
    {
        private const string CompanyFolder = "Snail";
        private const string ProductFolder = "AndroidMusicPresenceLink";

        internal static bool IsPortable => File.Exists(Path.Combine(BaseDirectory, "portable.mode"));

        internal static string BaseDirectory => Path.GetFullPath(AppContext.BaseDirectory);

        internal static string DataRoot => IsPortable
            ? BaseDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), CompanyFolder, ProductFolder);

        internal static string ResourceRoot => IsPortable
            ? Path.Combine(BaseDirectory, "Assets")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), CompanyFolder, "Assets");

        internal static string GetDataPath(params string[] parts)
        {
            return parts.Length == 0 ? DataRoot : Path.Combine(new[] { DataRoot }.Concat(parts).ToArray());
        }

        internal static string GetResourcePath(string fileName)
        {
            return Path.Combine(ResourceRoot, fileName);
        }
    }

    public enum UpdateIntervalMode
    {
        Extreme = 1,
        Fast = 2,
        Medium = 3,
        Slow = 4
    }

    public enum WirelessMode
    {
        // Classic adb tcpip 5555 flow. Requires USB to re-enable after every reboot.
        // Port is fixed and predictable, so reconnecting after an IP change is trivial.
        TcpIp = 0,

        // Android 11+ Wireless Debugging. One-time TLS pairing over USB-free network.
        // Survives reboots (pairing persists), but the connection port is randomly
        // assigned each time wireless debugging toggles on, so we need mDNS to find it.
        WirelessDebugging = 1,

        // USB cable only. All Wi-Fi reconnect logic is skipped; the app only talks to
        // the phone over a physical USB connection. Useful when Wireless Debugging
        // cannot be disabled on the phone side (WD mode keeps Wi-Fi on even when the
        // cable is the preferred link).
        UsbOnly = 2
    }

    public enum PresenceMode
    {
        Off = 0,
        Half = 1,
        Full = 2
    }

    public enum NextSongMode
    {
        Off = 0,
        TextOnly = 1,
        FullArt = 2,
        Kirsten = 3
    }

    public enum NextSongSortMode
    {
        FilenameAZ = 0,
        FilenameZA = 1,
        DateModifiedNewest = 2,
        DateModifiedOldest = 3
    }

    public enum HeadlessToastPosition
    {
        TopLeft = 0,
        TopCenter = 1,
        TopRight = 2,
        BottomLeft = 3,
        BottomCenter = 4,
        BottomRight = 5
    }

    // 0 = show inside media player window
    // 1 = show as headless overlay (same as when media player is closed)
    // 2 = off
    public enum MediaPlayerToastMode
    {
        InMediaPlayer = 0,
        Headless = 1,
        Off = 2
    }

    public sealed class EligibleAppConfig
    {
        public string PackageName { get; set; } = string.Empty;

        // Legacy field kept for migration only. New code uses PresenceMode.
        public bool IsEnabled { get; set; } = false;
        public bool EnableCoverSearch { get; set; } = false;
        public PresenceMode PresenceMode { get; set; } = PresenceMode.Off;
    }

    public class MusicConfig
    {
        public PathsConfig Paths { get; set; } = new PathsConfig();
        public string SelectedDeviceUSB { get; set; } = string.Empty;

        // For TcpIp mode:           "ip:5555" (or whatever fixed port)
        // For WirelessDebugging:    last-known "ip:port" from the most recent successful connect.
        //                           This may go stale, in which case we fall back to mDNS lookup
        //                           via WifiMdnsServiceName.
        public string SelectedDeviceWiFi { get; set; } = string.Empty;
        public string SelectedDeviceName { get; set; } = string.Empty;

        // Connection mode for the wireless link. Defaults to TcpIp so existing users are
        // not surprised. Users opt in to WirelessDebugging via onboarding or settings.
        public WirelessMode WifiMode { get; set; } = WirelessMode.TcpIp;

        // mDNS service name reported by `adb mdns services`, e.g. "adb-XXXXXXXX-XXXXXX".
        // Stable across reboots and IP changes once paired. Only used when WifiMode is
        // WirelessDebugging. Empty for TcpIp.
        public string WifiMdnsServiceName { get; set; } = string.Empty;

        public string MusicRemoteRoot { get; set; } = string.Empty;
        public List<string> MusicRemoteRoots { get; set; } = new List<string>();
        public UpdateIntervalMode UpdateIntervalMode { get; set; } = UpdateIntervalMode.Extreme;
        public string IgnoredUpdateVersion { get; set; } = string.Empty;
        public bool DebugMode { get; set; } = false;
        public bool UseDarkMode { get; set; } = true;
        public bool OpenInTaskbar { get; set; } = false;
        public bool StartWithWindows { get; set; } = false;
        public bool ShowMediaPlayerWindow { get; set; } = false;
        public bool MediaPlayerSettingsPaneOpen { get; set; } = false;
        public bool MediaPlayerInlineLyricsViewActive { get; set; } = false;
        public bool MediaPlayerFullscreenActive { get; set; } = false;
        public bool MediaPlayerPlayerSettingsPaneOpen { get; set; } = false;

        // Pill display modes: 0 = Full (icon + text), 1 = Mini (icon only), 2 = Off
        public int PillModeConnection { get; set; } = 0;
        public int PillModeAudioLink { get; set; } = 0;
        public int PillModeQuality { get; set; } = 0;
        public int PillModeAlwaysOnTop { get; set; } = 0;

        // Track info visibility
        public bool PlayerShowTitle { get; set; } = true;
        public bool PlayerShowArtist { get; set; } = true;
        public bool PlayerShowAlbum { get; set; } = true;
        public bool PlayerShowCover { get; set; } = true;
        public bool PlayerShowVolumeButton { get; set; } = true;
        public bool PlayerShowLyricsButton { get; set; } = true;
        public bool PlayerShowBattery { get; set; } = true;
        public bool PlayerShowHelpButton { get; set; } = true;
        public bool PlayerShowFullscreenButton { get; set; } = true;
        // When false the seek buttons are always hidden.
        // When true they appear only for tracks >= PlayerSeekButtonThresholdSeconds.
        public bool PlayerShowSeekButtons { get; set; } = true;
        // Minimum track length in seconds before seek buttons appear (default 600 = 10 min).
        public int PlayerSeekButtonThresholdSeconds { get; set; } = 600;
        // Persisted time-display toggle: false = elapsed, true = remaining.
        public bool PlayerShowTimeLeft { get; set; } = false;

        // Shadow effects
        public bool PlayerCoverShadow { get; set; } = false;
        public bool PlayerTextShadow { get; set; } = false;

        // Layout
        public bool PlayerSwapArtistAlbum { get; set; } = false;
        public bool PlayerCoverRoundedCorners { get; set; } = true;

        // Gradient sample points: 2, 4, 6, or 8
        public int PlayerGradientSamplePoints { get; set; } = 8;

        // When true the main settings pane moves to the right and player settings to the left.
        public bool SwapSettingsLocation { get; set; } = false;

        // Main window geometry (migrated from the legacy config.json)
        public double WindowWidth { get; set; } = 900;
        public double WindowHeight { get; set; } = 600;
        public double WindowTop { get; set; } = 100;
        public double WindowLeft { get; set; } = 100;
        public System.Windows.WindowState WindowState { get; set; } = System.Windows.WindowState.Normal;

        public double MediaPlayerWindowWidth { get; set; } = 1080;
        public double MediaPlayerWindowHeight { get; set; } = 760;
        public double MediaPlayerWindowTop { get; set; } = 100;
        public double MediaPlayerWindowLeft { get; set; } = 100;
        public System.Windows.WindowState MediaPlayerWindowState { get; set; } = System.Windows.WindowState.Normal;
        public string ScrcpyAudioCodec { get; set; } = "raw";
        public string ScrcpyAudioBitrate { get; set; } = string.Empty;
        public int ScrcpyAudioBuffer { get; set; } = 80;
        public int ScrcpyFlacCompressionLevel { get; set; } = 2;
        public List<string> ScrcpyAvailableAudioCodecs { get; set; } = new List<string> { "raw" };
        public string AudioQualityPresetName { get; set; } = string.Empty;
        public int SmtcPauseClearDelayMinutes { get; set; } = 0;
        public bool IsWifiEnabled { get; set; } = false;
        public bool OnboardingCompleted { get; set; } = false;

        // Toast / notification popup settings
        public bool HeadlessToastEnabled { get; set; } = true;
        public HeadlessToastPosition HeadlessToastPosition { get; set; } = HeadlessToastPosition.TopCenter;
        public MediaPlayerToastMode MediaPlayerToastMode { get; set; } = MediaPlayerToastMode.InMediaPlayer;

        public int CachClearInMB { get; set; } = 200;
        public string LyricsSearchFolderOverride { get; set; } = string.Empty;
        public string CoverArtFileNamePatterns { get; set; } = "cover.jpg;cover.png;folder.jpg";
        public string CopyTrackInfoTemplate { get; set; } = "{artist} - {title}";

        // Next/previous song feature
        public NextSongMode NextSongMode { get; set; } = NextSongMode.Off;
        public NextSongSortMode NextSongSortMode { get; set; } = NextSongSortMode.FilenameAZ;

        public List<string> AllowedApps { get; set; } = new List<string> { };
        public List<EligibleAppConfig> EligibleApps { get; set; } = new List<EligibleAppConfig>();

        public int HotkeyVolumeUpKey { get; set; } = 0xAF;
        public int HotkeyVolumeDownKey { get; set; } = 0xAE;
        public int HotkeyToggleScrcpyKey { get; set; } = 0x53;
        public int HotkeyToggleLyricsOverlayKey { get; set; } = 0x4C;
        public int HotkeyCopyTrackInfoKey { get; set; } = 0x43;
        public int HotkeyAudioQualityKey { get; set; } = 0x51;   // Q
        public int HotkeyModifier { get; set; } = 0x0001;

        public MusicConfig Clone()
        {
            var source = this;
            var paths = source.Paths ?? new PathsConfig();
            return new MusicConfig
            {
                Paths = new PathsConfig
                {
                    Adb = paths.Adb,
                    FfmpegPath = paths.FfmpegPath,
                    Scrcpy = paths.Scrcpy,
                    CoverCachePath = paths.CoverCachePath
                },
                SelectedDeviceUSB = source.SelectedDeviceUSB,
                SelectedDeviceWiFi = source.SelectedDeviceWiFi,
                SelectedDeviceName = source.SelectedDeviceName,
                WifiMode = source.WifiMode,
                WifiMdnsServiceName = source.WifiMdnsServiceName ?? string.Empty,
                MusicRemoteRoot = source.MusicRemoteRoot,
                MusicRemoteRoots = source.MusicRemoteRoots?.ToList() ?? new List<string>(),
                UpdateIntervalMode = source.UpdateIntervalMode,
                IgnoredUpdateVersion = source.IgnoredUpdateVersion,
                DebugMode = source.DebugMode,
                UseDarkMode = source.UseDarkMode,
                OpenInTaskbar = source.OpenInTaskbar,
                StartWithWindows = source.StartWithWindows,
                ShowMediaPlayerWindow = source.ShowMediaPlayerWindow,
                MediaPlayerSettingsPaneOpen = source.MediaPlayerSettingsPaneOpen,
                MediaPlayerInlineLyricsViewActive = source.MediaPlayerInlineLyricsViewActive,
                MediaPlayerFullscreenActive = source.MediaPlayerFullscreenActive,
                MediaPlayerPlayerSettingsPaneOpen = source.MediaPlayerPlayerSettingsPaneOpen,
                PillModeConnection = source.PillModeConnection,
                PillModeAudioLink = source.PillModeAudioLink,
                PillModeQuality = source.PillModeQuality,
                PillModeAlwaysOnTop = source.PillModeAlwaysOnTop,
                PlayerShowTitle = source.PlayerShowTitle,
                PlayerShowArtist = source.PlayerShowArtist,
                PlayerShowAlbum = source.PlayerShowAlbum,
                PlayerShowCover = source.PlayerShowCover,
                PlayerShowVolumeButton = source.PlayerShowVolumeButton,
                PlayerShowLyricsButton = source.PlayerShowLyricsButton,
                PlayerShowBattery = source.PlayerShowBattery,
                PlayerShowHelpButton = source.PlayerShowHelpButton,
                PlayerShowFullscreenButton = source.PlayerShowFullscreenButton,
                PlayerShowSeekButtons = source.PlayerShowSeekButtons,
                PlayerSeekButtonThresholdSeconds = source.PlayerSeekButtonThresholdSeconds,
                PlayerShowTimeLeft = source.PlayerShowTimeLeft,
                PlayerCoverShadow = source.PlayerCoverShadow,
                PlayerTextShadow = source.PlayerTextShadow,
                PlayerSwapArtistAlbum = source.PlayerSwapArtistAlbum,
                PlayerCoverRoundedCorners = source.PlayerCoverRoundedCorners,
                PlayerGradientSamplePoints = source.PlayerGradientSamplePoints,
                SwapSettingsLocation = source.SwapSettingsLocation,
                WindowWidth = source.WindowWidth,
                WindowHeight = source.WindowHeight,
                WindowTop = source.WindowTop,
                WindowLeft = source.WindowLeft,
                WindowState = source.WindowState,
                MediaPlayerWindowWidth = source.MediaPlayerWindowWidth,
                MediaPlayerWindowHeight = source.MediaPlayerWindowHeight,
                MediaPlayerWindowTop = source.MediaPlayerWindowTop,
                MediaPlayerWindowLeft = source.MediaPlayerWindowLeft,
                MediaPlayerWindowState = source.MediaPlayerWindowState,
                ScrcpyAudioCodec = source.ScrcpyAudioCodec,
                ScrcpyAudioBitrate = source.ScrcpyAudioBitrate ?? string.Empty,
                ScrcpyAudioBuffer = source.ScrcpyAudioBuffer,
                ScrcpyFlacCompressionLevel = source.ScrcpyFlacCompressionLevel,
                ScrcpyAvailableAudioCodecs = source.ScrcpyAvailableAudioCodecs?.ToList() ?? new List<string>(),
                AudioQualityPresetName = source.AudioQualityPresetName ?? string.Empty,
                SmtcPauseClearDelayMinutes = source.SmtcPauseClearDelayMinutes,
                IsWifiEnabled = source.IsWifiEnabled,
                OnboardingCompleted = source.OnboardingCompleted,
                CachClearInMB = source.CachClearInMB,
                LyricsSearchFolderOverride = source.LyricsSearchFolderOverride ?? string.Empty,
                CoverArtFileNamePatterns = source.CoverArtFileNamePatterns ?? string.Empty,
                CopyTrackInfoTemplate = source.CopyTrackInfoTemplate ?? string.Empty,
                NextSongMode = source.NextSongMode,
                NextSongSortMode = source.NextSongSortMode,
                AllowedApps = source.AllowedApps?.ToList() ?? new List<string>(),
                EligibleApps = source.EligibleApps?.Select(a => new EligibleAppConfig
                {
                    PackageName = a.PackageName,
                    IsEnabled = a.IsEnabled,
                    EnableCoverSearch = a.EnableCoverSearch,
                    PresenceMode = a.PresenceMode
                }).ToList() ?? new List<EligibleAppConfig>(),
                HotkeyVolumeUpKey = source.HotkeyVolumeUpKey,
                HotkeyVolumeDownKey = source.HotkeyVolumeDownKey,
                HotkeyToggleScrcpyKey = source.HotkeyToggleScrcpyKey,
                HotkeyToggleLyricsOverlayKey = source.HotkeyToggleLyricsOverlayKey,
                HotkeyCopyTrackInfoKey = source.HotkeyCopyTrackInfoKey,
                HotkeyAudioQualityKey = source.HotkeyAudioQualityKey,
                HotkeyModifier = source.HotkeyModifier
            };
        }
    }

    public class PathsConfig
    {
        public string Adb { get; set; } = AppPaths.GetResourcePath("adb.exe");

        public string FfmpegPath { get; set; } = AppPaths.GetResourcePath("ffmpeg.exe");

        public string Scrcpy { get; set; } = AppPaths.GetResourcePath("scrcpy.exe");

        public string CoverCachePath { get; set; } = AppPaths.GetDataPath("CoverCache");
    }

    public static class MusicConfigManager
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public static string ConfigPath => AppPaths.GetDataPath("musicconfig.json");

        public static MusicConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    var loaded = JsonSerializer.Deserialize<MusicConfig>(json);
                    if (loaded != null)
                        return Finalize(loaded);
                }

                var fresh = new MusicConfig();
                return Finalize(fresh);
            }
            catch
            {
                return Finalize(new MusicConfig());
            }
        }

        public static void Save(MusicConfig config)
        {
            try
            {
                var folder = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                    Directory.CreateDirectory(folder);

                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOptions));
            }
            catch (Exception ex)
            {
                Debugger.show("Failed to save music config: " + ex.Message);
            }
        }

        private static MusicConfig NormalizeConfig(MusicConfig config)
        {
            config.Paths ??= new PathsConfig();
            config.AllowedApps ??= new List<string>();
            config.EligibleApps ??= new List<EligibleAppConfig>();
            config.MusicRemoteRoots ??= new List<string>();
            config.WifiMdnsServiceName ??= string.Empty;
            if (!Enum.IsDefined(typeof(UpdateIntervalMode), config.UpdateIntervalMode))
            {
                config.UpdateIntervalMode = UpdateIntervalMode.Extreme;
            }

            if (config.EligibleApps.Count == 0 && config.AllowedApps.Count > 0)
            {
                config.EligibleApps = config.AllowedApps
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Select(a => a.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(a => new EligibleAppConfig
                    {
                        PackageName = a,
                        PresenceMode = PresenceMode.Full,
                        EnableCoverSearch = true
                    })
                    .ToList();
            }

            if (config.EligibleApps.Count == 0)
            {
                config.EligibleApps.Add(new EligibleAppConfig
                {
                    PackageName = "in.krosbits.musicolet",
                    PresenceMode = PresenceMode.Full,
                    EnableCoverSearch = true
                });
            }

            config.EligibleApps = config.EligibleApps
                .Where(a => !string.IsNullOrWhiteSpace(a.PackageName))
                .GroupBy(a => a.PackageName.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var first = g.First();
                    // Migrate legacy IsEnabled: if PresenceMode is still Off but IsEnabled was true,
                    // treat as Full (old config had no half mode).
                    var mode = first.PresenceMode;
                    if (mode == PresenceMode.Off && first.IsEnabled)
                        mode = PresenceMode.Full;
                    return new EligibleAppConfig
                    {
                        PackageName = g.Key,
                        PresenceMode = mode,
                        EnableCoverSearch = g.Any(x => x.EnableCoverSearch)
                    };
                })
                .ToList();

            config.AllowedApps = config.EligibleApps
                .Where(a => a.PresenceMode != PresenceMode.Off)
                .Select(a => a.PackageName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var normalizedRoots = config.MusicRemoteRoots
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (normalizedRoots.Count == 0 && !string.IsNullOrWhiteSpace(config.MusicRemoteRoot))
            {
                normalizedRoots.Add(config.MusicRemoteRoot.Trim());
            }

            config.MusicRemoteRoots = normalizedRoots;
            config.MusicRemoteRoot = normalizedRoots.FirstOrDefault() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(config.ScrcpyAudioCodec))
                config.ScrcpyAudioCodec = "raw";

            if (config.ScrcpyAudioBitrate == null)
                config.ScrcpyAudioBitrate = string.Empty;

            if (config.ScrcpyAudioBuffer <= 0)
                config.ScrcpyAudioBuffer = 50;

            if (config.ScrcpyFlacCompressionLevel < 1)
                config.ScrcpyFlacCompressionLevel = 1;
            else if (config.ScrcpyFlacCompressionLevel > 8)
                config.ScrcpyFlacCompressionLevel = 8;

            config.ScrcpyAvailableAudioCodecs ??= new List<string>();
            if (config.ScrcpyAvailableAudioCodecs.Count == 0)
                config.ScrcpyAvailableAudioCodecs.Add("raw");

            config.LyricsSearchFolderOverride ??= string.Empty;
            config.LyricsSearchFolderOverride = config.LyricsSearchFolderOverride.Trim();

            config.CoverArtFileNamePatterns ??= "cover.jpg;cover.png;folder.jpg";
            config.CoverArtFileNamePatterns = config.CoverArtFileNamePatterns.Trim();
            if (string.IsNullOrWhiteSpace(config.CoverArtFileNamePatterns))
                config.CoverArtFileNamePatterns = "cover.jpg;cover.png;folder.jpg";

            config.CopyTrackInfoTemplate ??= "{artist} - {title}";
            config.CopyTrackInfoTemplate = config.CopyTrackInfoTemplate.Trim();
            if (string.IsNullOrWhiteSpace(config.CopyTrackInfoTemplate))
                config.CopyTrackInfoTemplate = "{artist} - {title}";

            if (config.SmtcPauseClearDelayMinutes < 0)
                config.SmtcPauseClearDelayMinutes = 0;

            if (config.HotkeyVolumeUpKey < 0 || config.HotkeyVolumeUpKey > 0xFF)
                config.HotkeyVolumeUpKey = 0xAF;
            if (config.HotkeyVolumeDownKey < 0 || config.HotkeyVolumeDownKey > 0xFF)
                config.HotkeyVolumeDownKey = 0xAE;
            if (config.HotkeyToggleScrcpyKey < 0 || config.HotkeyToggleScrcpyKey > 0xFF)
                config.HotkeyToggleScrcpyKey = 0x53;
            if (config.HotkeyToggleLyricsOverlayKey < 0 || config.HotkeyToggleLyricsOverlayKey > 0xFF)
                config.HotkeyToggleLyricsOverlayKey = 0x4C;
            if (config.HotkeyCopyTrackInfoKey < 0 || config.HotkeyCopyTrackInfoKey > 0xFF)
                config.HotkeyCopyTrackInfoKey = 0x43;
            if (config.HotkeyAudioQualityKey < 0 || config.HotkeyAudioQualityKey > 0xFF)
                config.HotkeyAudioQualityKey = 0x51;

            var allowedMods = new[] { 0x0001, 0x0002, 0x0004 };
            if (!allowedMods.Contains(config.HotkeyModifier)) config.HotkeyModifier = 0x0001;

            var allowedGradientPoints = new[] { 2, 4, 6, 8 };
            if (!allowedGradientPoints.Contains(config.PlayerGradientSamplePoints))
                config.PlayerGradientSamplePoints = 8;

            // Sanity: WirelessDebugging without a service name is functionally broken,
            // but we don't auto-rewrite to TcpIp because the user may be mid-pairing.
            // The presence service handles that gracefully.

            return config;
        }

        private static MusicConfig Finalize(MusicConfig config)
        {
            config = NormalizeConfig(config);
            MigrateLegacyWindowConfig(config);
            return config;
        }

        // One-time migration of the old config.json (which only ever stored the main
        // window geometry) into the unified musicconfig.json. The legacy file is removed
        // afterwards so this never runs twice. Losing it is harmless: it is window
        // position data only.
        private static void MigrateLegacyWindowConfig(MusicConfig config)
        {
            try
            {
                var folder = Path.GetDirectoryName(ConfigPath);
                if (string.IsNullOrEmpty(folder))
                    return;

                var legacyPath = Path.Combine(folder, "config.json");
                if (!File.Exists(legacyPath))
                    return;

                try
                {
                    var json = File.ReadAllText(legacyPath);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("WindowWidth", out var w) && w.TryGetDouble(out var wd) && wd > 0)
                        config.WindowWidth = wd;
                    if (root.TryGetProperty("WindowHeight", out var h) && h.TryGetDouble(out var hd) && hd > 0)
                        config.WindowHeight = hd;
                    if (root.TryGetProperty("WindowTop", out var tp) && tp.TryGetDouble(out var tpd))
                        config.WindowTop = tpd;
                    if (root.TryGetProperty("WindowLeft", out var lf) && lf.TryGetDouble(out var lfd))
                        config.WindowLeft = lfd;
                    if (root.TryGetProperty("WindowState", out var st))
                    {
                        if (st.ValueKind == JsonValueKind.Number && st.TryGetInt32(out var si)
                            && Enum.IsDefined(typeof(System.Windows.WindowState), si))
                            config.WindowState = (System.Windows.WindowState)si;
                        else if (st.ValueKind == JsonValueKind.String
                            && Enum.TryParse<System.Windows.WindowState>(st.GetString(), out var se))
                            config.WindowState = se;
                    }

                    Save(config);
                }
                catch
                {
                    // Corrupt legacy file: ignore its contents, still remove it below.
                }

                try { File.Delete(legacyPath); } catch { }
            }
            catch
            {
                // Migration must never break startup.
            }
        }
    }
}