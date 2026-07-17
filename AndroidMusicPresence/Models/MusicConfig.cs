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

    // Classic  = horizontal icon with proportional fill bar + terminal cap (original).
    // Pill     = One UI 7 style solid rounded pill, percentage centred, no fill bar/cap.
    // Vertical = Classic rotated 90 degrees; percentage is always rendered outside.
    public enum BatteryVisualStyle
    {
        Classic = 0,
        Pill = 1,
        Vertical = 2
    }

    // Enabled   = green at full, red when critical, theme brush otherwise (original).
    // TextColor = always the theme/icon brush, no green/red threshold overrides.
    // Disabled  = neutral, no color emphasis at all.
    public enum BatteryColorMode
    {
        Enabled = 0,
        TextColor = 1,
        Disabled = 2
    }

    public enum NextSongMode
    {
        Off = 0,
        TextOnly = 1,
        FullArt = 2,
        Kirsten = 3
    }

    public enum PredictiveUiMode
    {
        Off = 0,
        // Instant buttons; next/prev flips the title to a generic "Loading" state
        // without touching the library list.
        Safe = 1,
        // Instant buttons; next/prev shows the predicted neighbour title from the
        // library list.
        Full = 2
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

    public enum MediaPlayerToastMode
    {
        InMediaPlayer = 0,
        Headless = 1,
        Off = 2
    }

    public sealed class ThemeOverrides
    {
        public string Background { get; set; } = string.Empty;
        public string Accent { get; set; } = string.Empty;
        public string Foreground { get; set; } = string.Empty;

        public ThemeOverrides Clone() => new ThemeOverrides
        {
            Background = Background,
            Accent = Accent,
            Foreground = Foreground
        };

        public bool ValueEquals(ThemeOverrides? other)
            => other != null
            && string.Equals(Background ?? string.Empty, other.Background ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Accent ?? string.Empty, other.Accent ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Foreground ?? string.Empty, other.Foreground ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    public sealed class ThemeProfile
    {
        // Capped at ThemeConfig.NameMaxLength for user-created profiles.
        public string Name { get; set; } = string.Empty;
        public string Background { get; set; } = string.Empty;
        public string Accent { get; set; } = string.Empty;
        public string Foreground { get; set; } = string.Empty;

        public ThemeProfile Clone() => new ThemeProfile
        {
            Name = Name,
            Background = Background,
            Accent = Accent,
            Foreground = Foreground
        };

        public bool ValueEquals(ThemeProfile? other)
            => other != null
            && string.Equals(Name ?? string.Empty, other.Name ?? string.Empty, StringComparison.Ordinal)
            && string.Equals(Background ?? string.Empty, other.Background ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Accent ?? string.Empty, other.Accent ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Foreground ?? string.Empty, other.Foreground ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    public sealed class EligibleAppConfig
    {
        public string PackageName { get; set; } = string.Empty;
        // Legacy field kept for migration only. New code uses PresenceMode.
        public bool IsEnabled { get; set; } = false;
        public bool EnableCoverSearch { get; set; } = false;
        // Independent of EnableCoverSearch. When both are on, the local file search runs first
        // and Subsonic is a fallback; when only this is on, Subsonic is queried directly.
        public bool UseSubsonic { get; set; } = false;
        public PresenceMode PresenceMode { get; set; } = PresenceMode.Off;
    }

    // ── Sub-config classes ─────────────────────────────────────────────────────

    public class PathsConfig
    {
        public string Adb { get; set; } = AppPaths.GetResourcePath("adb.exe");
        public string FfmpegPath { get; set; } = AppPaths.GetResourcePath("ffmpeg.exe");
        public string Scrcpy { get; set; } = AppPaths.GetResourcePath("scrcpy.exe");
        public string CoverCachePath { get; set; } = AppPaths.GetDataPath("CoverCache");
        // Custom image shown in the media player when no cover art is found. Empty = use no image.
        public string NoCoverIconPath { get; set; } = string.Empty;
    }

    public class DeviceConfig
    {
        public string SelectedDeviceUSB { get; set; } = string.Empty;
        // For TcpIp: "ip:5555". For WirelessDebugging: last-known "ip:port", may go stale
        // (falls back to mDNS lookup via MdnsServiceName).
        public string SelectedDeviceWiFi { get; set; } = string.Empty;
        public string SelectedDeviceName { get; set; } = string.Empty;
        // Defaults to WirelessDebugging (Android 11+). Existing configs with a saved value are unaffected.
        public WirelessMode WifiMode { get; set; } = WirelessMode.WirelessDebugging;
        // mDNS service name from `adb mdns services`, e.g. "adb-XXXXXXXX-XXXXXX".
        // Stable across reboots once paired. Only used when WifiMode is WirelessDebugging.
        public string MdnsServiceName { get; set; } = string.Empty;
        public bool IsWifiEnabled { get; set; } = false;
    }

    public class LibraryConfig
    {
        public string MusicRemoteRoot { get; set; } = string.Empty;
        public List<string> MusicRemoteRoots { get; set; } = new List<string>();
        public bool RetainDateModifiedOnTagEdit { get; set; } = true;
        public bool SaveLyricsAsLrcInFolder { get; set; } = false;
        public string LyricsSearchFolderOverride { get; set; } = string.Empty;
        public string CoverArtFileNamePatterns { get; set; } = "cover.jpg;cover.png;folder.jpg";
    }

    public class AppsConfig
    {
        public List<string> AllowedApps { get; set; } = new List<string>();
        public List<EligibleAppConfig> EligibleApps { get; set; } = new List<EligibleAppConfig>();
    }

    public class SubsonicConfig
    {
        // Base URL of the Subsonic-API-compatible server, e.g. "http://192.168.1.50:4533".
        public string ServerUrl { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        // DPAPI-encrypted (CurrentUser) password, base64-encoded. Never plaintext on disk.
        // Encrypt/decrypt via SecretProtector. Empty means "no password set".
        public string EncryptedPassword { get; set; } = string.Empty;
    }

    public class PollingConfig
    {
        public UpdateIntervalMode Interval { get; set; } = UpdateIntervalMode.Extreme;
        // Poll rate gradually slows after inactivity; snaps back on interaction.
        // Off by default because it can make song changes and position lag until you interact.
        public bool AdaptiveEnabled { get; set; } = false;
        // Minutes of inactivity before first slowdown. Playing tracks use double this value.
        public int AdaptiveThresholdMinutes { get; set; } = 5;
        public bool AdaptiveAlertEnabled { get; set; } = false;
    }

    public class AudioLinkConfig
    {
        public string Codec { get; set; } = "raw";
        public string Bitrate { get; set; } = string.Empty;
        public int BufferMs { get; set; } = 80;
        public int FlacCompressionLevel { get; set; } = 2;
        public List<string> AvailableCodecs { get; set; } = new List<string> { "raw" };
        public string QualityPresetName { get; set; } = string.Empty;
        // When false, scrcpy will not automatically restart on connection/transport change or crash.
        public bool AutoRestartOnConnection { get; set; } = true;
        // When false, scrcpy will not automatically restart when the audio quality preset changes.
        public bool AutoRestartOnQualityChange { get; set; } = true;
        // When false, the mute/unmute keyevents around scrcpy restarts are skipped.
        public bool Bleedless { get; set; } = true;
    }

    public class MediaPlayerConfig
    {
        public bool ShowWindow { get; set; } = false;
        public bool SettingsPaneOpen { get; set; } = false;
        public bool InlineLyricsViewActive { get; set; } = false;
        public bool FullscreenActive { get; set; } = false;
        public bool PlayerSettingsPaneOpen { get; set; } = false;

        public bool ShowTitle { get; set; } = true;
        public bool ShowArtist { get; set; } = true;
        public bool ShowAlbum { get; set; } = true;
        public bool ShowCover { get; set; } = true;
        public bool ShowVolumeButton { get; set; } = true;
        public bool ShowLyricsButton { get; set; } = true;
        public bool ShowBattery { get; set; } = true;
        public bool ShowHelpButton { get; set; } = true;
        public bool ShowFullscreenButton { get; set; } = true;
        // When true, seek buttons appear only for tracks >= SeekButtonThresholdSeconds.
        public bool ShowSeekButtons { get; set; } = true;
        // Minimum track length in seconds before seek buttons appear (default 600 = 10 min).
        public int SeekButtonThresholdSeconds { get; set; } = 600;
        // false = elapsed, true = remaining.
        public bool ShowTimeLeft { get; set; } = false;

        public BatteryVisualStyle BatteryVisualStyle { get; set; } = BatteryVisualStyle.Classic;
        public bool BatteryShowPercent { get; set; } = true;
        // Ignored for the Vertical style, which always renders the percentage outside.
        public bool BatteryPercentInside { get; set; } = true;
        public bool BatteryShowBolt { get; set; } = true;
        public bool BatteryBoltInside { get; set; } = true;
        public BatteryColorMode BatteryColorMode { get; set; } = BatteryColorMode.Enabled;
        // How often the phone is pinged (dumpsys battery over ADB) for the battery icon.
        public int BatteryPollIntervalSeconds { get; set; } = 150;

        public bool SwapArtistAlbum { get; set; } = false;
        public bool CoverRoundedCorners { get; set; } = true;
        public bool CoverShadow { get; set; } = false;
        public bool TextShadow { get; set; } = false;
        // Valid values: 2, 4, 6, or 8.
        public int GradientSamplePoints { get; set; } = 8;
        // When true, main settings pane moves to the right and player settings to the left.
        public bool SwapSettingsLocation { get; set; } = false;

        // 0 = Full (icon + text), 1 = Mini (icon only), 2 = Off
        public int PillModeConnection { get; set; } = 0;
        public int PillModeAudioLink { get; set; } = 0;
        public int PillModeQuality { get; set; } = 0;
        public int PillModeAlwaysOnTop { get; set; } = 0;

        public string CopyTrackInfoTemplate { get; set; } = "{artist} - {title}";
        public int SmtcPauseClearDelayMinutes { get; set; } = 0;

        // Instant-response transport buttons: pause flips the icon immediately, and
        // next/prev swap the title right away (Safe = "Loading", Full = predicted
        // neighbour from the library list) with the no-cover image until the real
        // MediaSession state arrives and corrects it. Safe by default: it needs no
        // library list and its only wrong-guess cost is a briefly generic title.
        public PredictiveUiMode PredictiveUi { get; set; } = PredictiveUiMode.Safe;
        // Predicted neighbour covers held in memory for an instant swap on next/prev.
        // 0 = off, 1 = next+prev preloaded, 2 = two out in each direction.
        public int PredictiveCoverMode { get; set; } = 0;
    }

    public class ToastConfig
    {
        public bool HeadlessEnabled { get; set; } = true;
        public HeadlessToastPosition HeadlessPosition { get; set; } = HeadlessToastPosition.TopCenter;
        public MediaPlayerToastMode MediaPlayerMode { get; set; } = MediaPlayerToastMode.InMediaPlayer;
    }

    public class NextSongConfig
    {
        public NextSongMode Mode { get; set; } = NextSongMode.Off;
        public NextSongSortMode SortMode { get; set; } = NextSongSortMode.FilenameAZ;
    }

    public class ThemeConfig
    {
        public const int NameMaxLength = 30;

        public bool UseDarkMode { get; set; } = true;
        // Legacy per-mode overrides. Superseded by the profile system; retained for one-time migration only.
        public ThemeOverrides LightTheme { get; set; } = new ThemeOverrides();
        public ThemeOverrides DarkTheme { get; set; } = new ThemeOverrides();
        // Built-in profiles (Default Light/Dark, High Contrast) are defined in code, not stored here.
        public List<ThemeProfile> CustomProfiles { get; set; } = new List<ThemeProfile>();
        // Empty means "not yet migrated"; triggers one-time conversion from UseDarkMode/LightTheme/DarkTheme.
        public string ActiveProfile { get; set; } = string.Empty;
        // Profiles removed from the cycle rotation but still selectable directly.
        public List<string> DisabledProfiles { get; set; } = new List<string>();
        public bool RandomAtStartup { get; set; } = false;
    }

    public class HotkeysConfig
    {
        // Each hotkey is a combo string like "CTRL+ALT+C": up to 5 keys, modifiers included.
        // Empty means "not yet migrated" from the legacy fields below; MusicConfigManager
        // seeds them from Modifier + the single-key fields on load.
        public string VolumeUpKeys { get; set; } = string.Empty;
        public string VolumeDownKeys { get; set; } = string.Empty;
        public string ToggleScrcpyKeys { get; set; } = string.Empty;
        public string ToggleLyricsOverlayKeys { get; set; } = string.Empty;
        public string CopyTrackInfoKeys { get; set; } = string.Empty;
        public string AudioQualityKeys { get; set; } = string.Empty;

        // Set once the legacy single-key fields have been migrated into the combo strings
        // above. After that an empty combo means "disabled by the user" (Esc while recording)
        // and must NOT be re-seeded from the legacy defaults on load.
        public bool ComboHotkeysMigrated { get; set; }

        // Custom media hotkeys. In SMTC mode (default) Windows' own media keys drive the app's
        // media session, so these are unused; in Direct mode they are registered and send the
        // play/pause/next/previous adb keyevents straight to the phone, for keyboards without
        // dedicated media keys. Empty = unset/disabled.
        public bool MediaKeysDirectMode { get; set; }
        public string PlayPauseKeys { get; set; } = string.Empty;
        public string NextTrackKeys { get; set; } = string.Empty;
        public string PreviousTrackKeys { get; set; } = string.Empty;

        // Legacy format: single virtual key per hotkey plus one shared Win32 modifier flag
        // (MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4). Only read to migrate old configs.
        public int VolumeUp { get; set; } = 0xAF;
        public int VolumeDown { get; set; } = 0xAE;
        public int ToggleScrcpy { get; set; } = 0x53;
        public int ToggleLyricsOverlay { get; set; } = 0x4C;
        public int CopyTrackInfo { get; set; } = 0x43;
        public int AudioQuality { get; set; } = 0x51;
        public int Modifier { get; set; } = 0x0001;
    }

    public class MainWindowConfig
    {
        public double Width { get; set; } = 900;
        public double Height { get; set; } = 600;
        public double Top { get; set; } = 100;
        public double Left { get; set; } = 100;
        public System.Windows.WindowState State { get; set; } = System.Windows.WindowState.Normal;
    }

    public class PlayerWindowConfig
    {
        public double Width { get; set; } = 1080;
        public double Height { get; set; } = 760;
        public double Top { get; set; } = 100;
        public double Left { get; set; } = 100;
        public System.Windows.WindowState State { get; set; } = System.Windows.WindowState.Normal;
    }

    public class AppSettingsConfig
    {
        public bool OpenInTaskbar { get; set; } = false;
        public bool StartWithWindows { get; set; } = false;
        public string IgnoredUpdateVersion { get; set; } = string.Empty;
        public bool OnboardingCompleted { get; set; } = false;
        public bool DebugMode { get; set; } = false;
        // Replaces normal logging with a single advanced_debug.log that traces every
        // adb command and its output. Persisted so it can capture app startup too.
        public bool AdvancedDebugMode { get; set; } = false;
        public int CachClearInMB { get; set; } = 200;
        // Longest side (px) newly cached covers are downscaled to. 0 = max quality (no downscale).
        public int CoverMaxSizePx { get; set; } = 0;
    }

    // ── Root config ────────────────────────────────────────────────────────────

    public class MusicConfig
    {
        public PathsConfig Paths { get; set; } = new PathsConfig();
        public DeviceConfig Device { get; set; } = new DeviceConfig();
        public LibraryConfig Library { get; set; } = new LibraryConfig();
        public AppsConfig Apps { get; set; } = new AppsConfig();
        public SubsonicConfig Subsonic { get; set; } = new SubsonicConfig();
        public PollingConfig Polling { get; set; } = new PollingConfig();
        public AudioLinkConfig AudioLink { get; set; } = new AudioLinkConfig();
        public MediaPlayerConfig MediaPlayer { get; set; } = new MediaPlayerConfig();
        public ToastConfig Toast { get; set; } = new ToastConfig();
        public NextSongConfig NextSong { get; set; } = new NextSongConfig();
        public ThemeConfig Theme { get; set; } = new ThemeConfig();
        public HotkeysConfig Hotkeys { get; set; } = new HotkeysConfig();
        public MainWindowConfig MainWindow { get; set; } = new MainWindowConfig();
        public PlayerWindowConfig PlayerWindow { get; set; } = new PlayerWindowConfig();
        public AppSettingsConfig AppSettings { get; set; } = new AppSettingsConfig();

        public MusicConfig Clone()
        {
            return new MusicConfig
            {
                Paths = new PathsConfig
                {
                    Adb = Paths.Adb,
                    FfmpegPath = Paths.FfmpegPath,
                    Scrcpy = Paths.Scrcpy,
                    CoverCachePath = Paths.CoverCachePath,
                    NoCoverIconPath = Paths.NoCoverIconPath
                },
                Device = new DeviceConfig
                {
                    SelectedDeviceUSB = Device.SelectedDeviceUSB,
                    SelectedDeviceWiFi = Device.SelectedDeviceWiFi,
                    SelectedDeviceName = Device.SelectedDeviceName,
                    WifiMode = Device.WifiMode,
                    MdnsServiceName = Device.MdnsServiceName,
                    IsWifiEnabled = Device.IsWifiEnabled
                },
                Library = new LibraryConfig
                {
                    MusicRemoteRoot = Library.MusicRemoteRoot,
                    MusicRemoteRoots = Library.MusicRemoteRoots?.ToList() ?? new List<string>(),
                    RetainDateModifiedOnTagEdit = Library.RetainDateModifiedOnTagEdit,
                    SaveLyricsAsLrcInFolder = Library.SaveLyricsAsLrcInFolder,
                    LyricsSearchFolderOverride = Library.LyricsSearchFolderOverride,
                    CoverArtFileNamePatterns = Library.CoverArtFileNamePatterns
                },
                Apps = new AppsConfig
                {
                    AllowedApps = Apps.AllowedApps?.ToList() ?? new List<string>(),
                    EligibleApps = Apps.EligibleApps?.Select(a => new EligibleAppConfig
                    {
                        PackageName = a.PackageName,
                        IsEnabled = a.IsEnabled,
                        EnableCoverSearch = a.EnableCoverSearch,
                        UseSubsonic = a.UseSubsonic,
                        PresenceMode = a.PresenceMode
                    }).ToList() ?? new List<EligibleAppConfig>()
                },
                Subsonic = new SubsonicConfig
                {
                    ServerUrl = Subsonic.ServerUrl,
                    Username = Subsonic.Username,
                    EncryptedPassword = Subsonic.EncryptedPassword
                },
                Polling = new PollingConfig
                {
                    Interval = Polling.Interval,
                    AdaptiveEnabled = Polling.AdaptiveEnabled,
                    AdaptiveThresholdMinutes = Polling.AdaptiveThresholdMinutes,
                    AdaptiveAlertEnabled = Polling.AdaptiveAlertEnabled
                },
                AudioLink = new AudioLinkConfig
                {
                    Codec = AudioLink.Codec,
                    Bitrate = AudioLink.Bitrate,
                    BufferMs = AudioLink.BufferMs,
                    FlacCompressionLevel = AudioLink.FlacCompressionLevel,
                    AvailableCodecs = AudioLink.AvailableCodecs?.ToList() ?? new List<string>(),
                    QualityPresetName = AudioLink.QualityPresetName,
                    AutoRestartOnConnection = AudioLink.AutoRestartOnConnection,
                    AutoRestartOnQualityChange = AudioLink.AutoRestartOnQualityChange,
                    Bleedless = AudioLink.Bleedless
                },
                MediaPlayer = new MediaPlayerConfig
                {
                    ShowWindow = MediaPlayer.ShowWindow,
                    SettingsPaneOpen = MediaPlayer.SettingsPaneOpen,
                    InlineLyricsViewActive = MediaPlayer.InlineLyricsViewActive,
                    FullscreenActive = MediaPlayer.FullscreenActive,
                    PlayerSettingsPaneOpen = MediaPlayer.PlayerSettingsPaneOpen,
                    ShowTitle = MediaPlayer.ShowTitle,
                    ShowArtist = MediaPlayer.ShowArtist,
                    ShowAlbum = MediaPlayer.ShowAlbum,
                    ShowCover = MediaPlayer.ShowCover,
                    ShowVolumeButton = MediaPlayer.ShowVolumeButton,
                    ShowLyricsButton = MediaPlayer.ShowLyricsButton,
                    ShowBattery = MediaPlayer.ShowBattery,
                    ShowHelpButton = MediaPlayer.ShowHelpButton,
                    ShowFullscreenButton = MediaPlayer.ShowFullscreenButton,
                    ShowSeekButtons = MediaPlayer.ShowSeekButtons,
                    SeekButtonThresholdSeconds = MediaPlayer.SeekButtonThresholdSeconds,
                    ShowTimeLeft = MediaPlayer.ShowTimeLeft,
                    BatteryVisualStyle = MediaPlayer.BatteryVisualStyle,
                    BatteryShowPercent = MediaPlayer.BatteryShowPercent,
                    BatteryPercentInside = MediaPlayer.BatteryPercentInside,
                    BatteryShowBolt = MediaPlayer.BatteryShowBolt,
                    BatteryBoltInside = MediaPlayer.BatteryBoltInside,
                    BatteryColorMode = MediaPlayer.BatteryColorMode,
                    BatteryPollIntervalSeconds = MediaPlayer.BatteryPollIntervalSeconds,
                    SwapArtistAlbum = MediaPlayer.SwapArtistAlbum,
                    CoverRoundedCorners = MediaPlayer.CoverRoundedCorners,
                    CoverShadow = MediaPlayer.CoverShadow,
                    TextShadow = MediaPlayer.TextShadow,
                    GradientSamplePoints = MediaPlayer.GradientSamplePoints,
                    SwapSettingsLocation = MediaPlayer.SwapSettingsLocation,
                    PillModeConnection = MediaPlayer.PillModeConnection,
                    PillModeAudioLink = MediaPlayer.PillModeAudioLink,
                    PillModeQuality = MediaPlayer.PillModeQuality,
                    PillModeAlwaysOnTop = MediaPlayer.PillModeAlwaysOnTop,
                    CopyTrackInfoTemplate = MediaPlayer.CopyTrackInfoTemplate,
                    SmtcPauseClearDelayMinutes = MediaPlayer.SmtcPauseClearDelayMinutes,
                    PredictiveUi = MediaPlayer.PredictiveUi,
                    PredictiveCoverMode = MediaPlayer.PredictiveCoverMode
                },
                Toast = new ToastConfig
                {
                    HeadlessEnabled = Toast.HeadlessEnabled,
                    HeadlessPosition = Toast.HeadlessPosition,
                    MediaPlayerMode = Toast.MediaPlayerMode
                },
                NextSong = new NextSongConfig
                {
                    Mode = NextSong.Mode,
                    SortMode = NextSong.SortMode
                },
                Theme = new ThemeConfig
                {
                    UseDarkMode = Theme.UseDarkMode,
                    LightTheme = Theme.LightTheme?.Clone() ?? new ThemeOverrides(),
                    DarkTheme = Theme.DarkTheme?.Clone() ?? new ThemeOverrides(),
                    CustomProfiles = Theme.CustomProfiles?.Select(t => t.Clone()).ToList() ?? new List<ThemeProfile>(),
                    ActiveProfile = Theme.ActiveProfile,
                    DisabledProfiles = Theme.DisabledProfiles?.ToList() ?? new List<string>(),
                    RandomAtStartup = Theme.RandomAtStartup
                },
                Hotkeys = new HotkeysConfig
                {
                    VolumeUpKeys = Hotkeys.VolumeUpKeys,
                    VolumeDownKeys = Hotkeys.VolumeDownKeys,
                    ToggleScrcpyKeys = Hotkeys.ToggleScrcpyKeys,
                    ToggleLyricsOverlayKeys = Hotkeys.ToggleLyricsOverlayKeys,
                    CopyTrackInfoKeys = Hotkeys.CopyTrackInfoKeys,
                    AudioQualityKeys = Hotkeys.AudioQualityKeys,
                    VolumeUp = Hotkeys.VolumeUp,
                    VolumeDown = Hotkeys.VolumeDown,
                    ToggleScrcpy = Hotkeys.ToggleScrcpy,
                    ToggleLyricsOverlay = Hotkeys.ToggleLyricsOverlay,
                    CopyTrackInfo = Hotkeys.CopyTrackInfo,
                    AudioQuality = Hotkeys.AudioQuality,
                    Modifier = Hotkeys.Modifier,
                    ComboHotkeysMigrated = Hotkeys.ComboHotkeysMigrated,
                    MediaKeysDirectMode = Hotkeys.MediaKeysDirectMode,
                    PlayPauseKeys = Hotkeys.PlayPauseKeys,
                    NextTrackKeys = Hotkeys.NextTrackKeys,
                    PreviousTrackKeys = Hotkeys.PreviousTrackKeys
                },
                MainWindow = new MainWindowConfig
                {
                    Width = MainWindow.Width,
                    Height = MainWindow.Height,
                    Top = MainWindow.Top,
                    Left = MainWindow.Left,
                    State = MainWindow.State
                },
                PlayerWindow = new PlayerWindowConfig
                {
                    Width = PlayerWindow.Width,
                    Height = PlayerWindow.Height,
                    Top = PlayerWindow.Top,
                    Left = PlayerWindow.Left,
                    State = PlayerWindow.State
                },
                AppSettings = new AppSettingsConfig
                {
                    OpenInTaskbar = AppSettings.OpenInTaskbar,
                    StartWithWindows = AppSettings.StartWithWindows,
                    IgnoredUpdateVersion = AppSettings.IgnoredUpdateVersion,
                    OnboardingCompleted = AppSettings.OnboardingCompleted,
                    DebugMode = AppSettings.DebugMode,
                    AdvancedDebugMode = AppSettings.AdvancedDebugMode,
                    CachClearInMB = AppSettings.CachClearInMB,
                    CoverMaxSizePx = AppSettings.CoverMaxSizePx
                }
            };
        }
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

                return Finalize(new MusicConfig());
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

        // Writes the given config to an arbitrary path (config import/export). Exceptions
        // propagate so the caller can surface a failure message.
        public static void ExportTo(string path, MusicConfig config)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));
        }

        // Reads and normalizes a config from an arbitrary path. Uses NormalizeConfig rather
        // than Finalize so importing never touches the local legacy config.json. Throws on a
        // missing/invalid file so the caller can report it.
        public static MusicConfig ImportFrom(string path)
        {
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<MusicConfig>(json)
                ?? throw new InvalidDataException("The file did not contain a valid configuration.");
            return NormalizeConfig(loaded);
        }

        private static string NormalizeHotkeyCombo(string? stored, int legacyModifier, int legacyKey)
        {
            var fallback = HotkeyHelper.ComboFromLegacy(legacyModifier, legacyKey);
            return HotkeyHelper.ComboToDisplayName(HotkeyHelper.ParseCombo(stored, fallback));
        }

        // Re-normalizes an already-migrated combo without any legacy fallback: empty (a
        // deliberately disabled hotkey) stays empty; a valid combo is reformatted.
        private static string NormalizeExistingCombo(string? stored)
        {
            if (string.IsNullOrWhiteSpace(stored)) return string.Empty;
            return HotkeyHelper.ComboToDisplayName(HotkeyHelper.ParseCombo(stored, Array.Empty<int>()));
        }

        private static MusicConfig NormalizeConfig(MusicConfig config)
        {
            config.Paths ??= new PathsConfig();
            config.Device ??= new DeviceConfig();
            config.Library ??= new LibraryConfig();
            config.Apps ??= new AppsConfig();
            config.Subsonic ??= new SubsonicConfig();
            config.Polling ??= new PollingConfig();
            config.AudioLink ??= new AudioLinkConfig();
            config.MediaPlayer ??= new MediaPlayerConfig();
            config.Toast ??= new ToastConfig();
            config.NextSong ??= new NextSongConfig();
            config.Theme ??= new ThemeConfig();
            config.Hotkeys ??= new HotkeysConfig();
            config.MainWindow ??= new MainWindowConfig();
            config.PlayerWindow ??= new PlayerWindowConfig();
            config.AppSettings ??= new AppSettingsConfig();

            config.Theme.LightTheme ??= new ThemeOverrides();
            config.Theme.DarkTheme ??= new ThemeOverrides();
            config.Theme.CustomProfiles ??= new List<ThemeProfile>();
            config.Theme.DisabledProfiles ??= new List<string>();
            MigrateThemes(config);

            config.Apps.AllowedApps ??= new List<string>();
            config.Apps.EligibleApps ??= new List<EligibleAppConfig>();
            config.Library.MusicRemoteRoots ??= new List<string>();
            config.Device.MdnsServiceName ??= string.Empty;

            config.Subsonic.ServerUrl = (config.Subsonic.ServerUrl ?? string.Empty).Trim();
            config.Subsonic.Username = (config.Subsonic.Username ?? string.Empty).Trim();
            config.Subsonic.EncryptedPassword ??= string.Empty;

            if (!Enum.IsDefined(typeof(UpdateIntervalMode), config.Polling.Interval))
                config.Polling.Interval = UpdateIntervalMode.Extreme;

            if (config.Polling.AdaptiveThresholdMinutes < 1)
                config.Polling.AdaptiveThresholdMinutes = 1;

            if (config.Apps.EligibleApps.Count == 0 && config.Apps.AllowedApps.Count > 0)
            {
                config.Apps.EligibleApps = config.Apps.AllowedApps
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

            if (config.Apps.EligibleApps.Count == 0)
            {
                config.Apps.EligibleApps.Add(new EligibleAppConfig
                {
                    PackageName = "in.krosbits.musicolet",
                    PresenceMode = PresenceMode.Full,
                    EnableCoverSearch = true
                });
            }

            config.Apps.EligibleApps = config.Apps.EligibleApps
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
                        EnableCoverSearch = g.Any(x => x.EnableCoverSearch),
                        UseSubsonic = g.Any(x => x.UseSubsonic)
                    };
                })
                .ToList();

            config.Apps.AllowedApps = config.Apps.EligibleApps
                .Where(a => a.PresenceMode != PresenceMode.Off)
                .Select(a => a.PackageName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var normalizedRoots = config.Library.MusicRemoteRoots
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (normalizedRoots.Count == 0 && !string.IsNullOrWhiteSpace(config.Library.MusicRemoteRoot))
                normalizedRoots.Add(config.Library.MusicRemoteRoot.Trim());

            config.Library.MusicRemoteRoots = normalizedRoots;
            config.Library.MusicRemoteRoot = normalizedRoots.FirstOrDefault() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(config.AudioLink.Codec))
                config.AudioLink.Codec = "raw";

            config.AudioLink.Bitrate ??= string.Empty;

            if (config.AudioLink.BufferMs <= 0)
                config.AudioLink.BufferMs = 50;

            if (config.AudioLink.FlacCompressionLevel < 1)
                config.AudioLink.FlacCompressionLevel = 1;
            else if (config.AudioLink.FlacCompressionLevel > 8)
                config.AudioLink.FlacCompressionLevel = 8;

            config.AudioLink.AvailableCodecs ??= new List<string>();
            if (config.AudioLink.AvailableCodecs.Count == 0)
                config.AudioLink.AvailableCodecs.Add("raw");

            config.Library.LyricsSearchFolderOverride ??= string.Empty;
            config.Library.LyricsSearchFolderOverride = config.Library.LyricsSearchFolderOverride.Trim();

            config.Library.CoverArtFileNamePatterns ??= "cover.jpg;cover.png;folder.jpg";
            config.Library.CoverArtFileNamePatterns = config.Library.CoverArtFileNamePatterns.Trim();
            if (string.IsNullOrWhiteSpace(config.Library.CoverArtFileNamePatterns))
                config.Library.CoverArtFileNamePatterns = "cover.jpg;cover.png;folder.jpg";

            config.MediaPlayer.CopyTrackInfoTemplate ??= "{artist} - {title}";
            config.MediaPlayer.CopyTrackInfoTemplate = config.MediaPlayer.CopyTrackInfoTemplate.Trim();
            if (string.IsNullOrWhiteSpace(config.MediaPlayer.CopyTrackInfoTemplate))
                config.MediaPlayer.CopyTrackInfoTemplate = "{artist} - {title}";

            if (config.MediaPlayer.SmtcPauseClearDelayMinutes < 0)
                config.MediaPlayer.SmtcPauseClearDelayMinutes = 0;

            if (config.MediaPlayer.PredictiveCoverMode < 0 || config.MediaPlayer.PredictiveCoverMode > 2)
                config.MediaPlayer.PredictiveCoverMode = 0;
            if (!Enum.IsDefined(typeof(PredictiveUiMode), config.MediaPlayer.PredictiveUi))
                config.MediaPlayer.PredictiveUi = PredictiveUiMode.Safe;

            // Floor of 5s so a typo can't turn the battery poll into an ADB hammer.
            if (config.MediaPlayer.BatteryPollIntervalSeconds < 5)
                config.MediaPlayer.BatteryPollIntervalSeconds = 150;

            // Legacy single-key fields are only clamped so they seed sane combo strings below.
            if (config.Hotkeys.VolumeUp < 0 || config.Hotkeys.VolumeUp > 0xFF)
                config.Hotkeys.VolumeUp = 0xAF;
            if (config.Hotkeys.VolumeDown < 0 || config.Hotkeys.VolumeDown > 0xFF)
                config.Hotkeys.VolumeDown = 0xAE;
            if (config.Hotkeys.ToggleScrcpy < 0 || config.Hotkeys.ToggleScrcpy > 0xFF)
                config.Hotkeys.ToggleScrcpy = 0x53;
            if (config.Hotkeys.ToggleLyricsOverlay < 0 || config.Hotkeys.ToggleLyricsOverlay > 0xFF)
                config.Hotkeys.ToggleLyricsOverlay = 0x4C;
            if (config.Hotkeys.CopyTrackInfo < 0 || config.Hotkeys.CopyTrackInfo > 0xFF)
                config.Hotkeys.CopyTrackInfo = 0x43;
            if (config.Hotkeys.AudioQuality < 0 || config.Hotkeys.AudioQuality > 0xFF)
                config.Hotkeys.AudioQuality = 0x51;

            var allowedMods = new[] { 0x0001, 0x0002, 0x0004 };
            if (!allowedMods.Contains(config.Hotkeys.Modifier))
                config.Hotkeys.Modifier = 0x0001;

            // One-time migration of the legacy modifier+key hotkeys into combo strings. This
            // seeds a combo from the legacy default only while it is empty; it runs once and
            // then sets the flag. After migration an empty combo means the user disabled it
            // (Esc while recording), so we must not resurrect it from the legacy default —
            // instead we only re-normalize whatever is stored (empty stays empty).
            int mod = config.Hotkeys.Modifier;
            if (!config.Hotkeys.ComboHotkeysMigrated)
            {
                config.Hotkeys.VolumeUpKeys = NormalizeHotkeyCombo(config.Hotkeys.VolumeUpKeys, mod, config.Hotkeys.VolumeUp);
                config.Hotkeys.VolumeDownKeys = NormalizeHotkeyCombo(config.Hotkeys.VolumeDownKeys, mod, config.Hotkeys.VolumeDown);
                config.Hotkeys.ToggleScrcpyKeys = NormalizeHotkeyCombo(config.Hotkeys.ToggleScrcpyKeys, mod, config.Hotkeys.ToggleScrcpy);
                config.Hotkeys.ToggleLyricsOverlayKeys = NormalizeHotkeyCombo(config.Hotkeys.ToggleLyricsOverlayKeys, mod, config.Hotkeys.ToggleLyricsOverlay);
                config.Hotkeys.CopyTrackInfoKeys = NormalizeHotkeyCombo(config.Hotkeys.CopyTrackInfoKeys, mod, config.Hotkeys.CopyTrackInfo);
                config.Hotkeys.AudioQualityKeys = NormalizeHotkeyCombo(config.Hotkeys.AudioQualityKeys, mod, config.Hotkeys.AudioQuality);
                config.Hotkeys.ComboHotkeysMigrated = true;
            }
            else
            {
                config.Hotkeys.VolumeUpKeys = NormalizeExistingCombo(config.Hotkeys.VolumeUpKeys);
                config.Hotkeys.VolumeDownKeys = NormalizeExistingCombo(config.Hotkeys.VolumeDownKeys);
                config.Hotkeys.ToggleScrcpyKeys = NormalizeExistingCombo(config.Hotkeys.ToggleScrcpyKeys);
                config.Hotkeys.ToggleLyricsOverlayKeys = NormalizeExistingCombo(config.Hotkeys.ToggleLyricsOverlayKeys);
                config.Hotkeys.CopyTrackInfoKeys = NormalizeExistingCombo(config.Hotkeys.CopyTrackInfoKeys);
                config.Hotkeys.AudioQualityKeys = NormalizeExistingCombo(config.Hotkeys.AudioQualityKeys);
            }

            // Media hotkeys have no legacy equivalent, so they are only ever re-normalized;
            // empty stays empty (disabled / not yet bound).
            config.Hotkeys.PlayPauseKeys = NormalizeExistingCombo(config.Hotkeys.PlayPauseKeys);
            config.Hotkeys.NextTrackKeys = NormalizeExistingCombo(config.Hotkeys.NextTrackKeys);
            config.Hotkeys.PreviousTrackKeys = NormalizeExistingCombo(config.Hotkeys.PreviousTrackKeys);

            var allowedGradientPoints = new[] { 2, 4, 6, 8 };
            if (!allowedGradientPoints.Contains(config.MediaPlayer.GradientSamplePoints))
                config.MediaPlayer.GradientSamplePoints = 8;

            if (!Enum.IsDefined(typeof(BatteryVisualStyle), config.MediaPlayer.BatteryVisualStyle))
                config.MediaPlayer.BatteryVisualStyle = BatteryVisualStyle.Classic;
            if (!Enum.IsDefined(typeof(BatteryColorMode), config.MediaPlayer.BatteryColorMode))
                config.MediaPlayer.BatteryColorMode = BatteryColorMode.Enabled;

            // Sanity: WirelessDebugging without a service name is functionally broken,
            // but we don't auto-rewrite to TcpIp because the user may be mid-pairing.
            // The presence service handles that gracefully.

            return config;
        }

        // One-time migration from the old light/dark-toggle theming to the theme-profile system.
        // Runs only when ActiveProfile is empty (configs saved before the profile system existed).
        // Customized light/dark colors are preserved as named profiles ("Custom Light" / "Custom Dark").
        private static void MigrateThemes(MusicConfig config)
        {
            if (!string.IsNullOrWhiteSpace(config.Theme.ActiveProfile))
                return;

            bool HasAnyColor(ThemeOverrides? o) => o != null &&
                (!string.IsNullOrWhiteSpace(o.Background)
                 || !string.IsNullOrWhiteSpace(o.Accent)
                 || !string.IsNullOrWhiteSpace(o.Foreground));

            bool lightCustom = HasAnyColor(config.Theme.LightTheme);
            bool darkCustom = HasAnyColor(config.Theme.DarkTheme);

            if (lightCustom)
                config.Theme.CustomProfiles.Add(BuiltInThemes.ProfileFromOverrides("Custom Light", config.Theme.LightTheme!, isDark: false));
            if (darkCustom)
                config.Theme.CustomProfiles.Add(BuiltInThemes.ProfileFromOverrides("Custom Dark", config.Theme.DarkTheme!, isDark: true));

            if (config.Theme.UseDarkMode)
                config.Theme.ActiveProfile = darkCustom ? "Custom Dark" : BuiltInThemes.DefaultDark.Name;
            else
                config.Theme.ActiveProfile = lightCustom ? "Custom Light" : BuiltInThemes.DefaultLight.Name;
        }

        private static MusicConfig Finalize(MusicConfig config)
        {
            config = NormalizeConfig(config);
            MigrateLegacyWindowConfig(config);
            return config;
        }

        // One-time migration of the old config.json (window geometry only) into musicconfig.json.
        // The legacy file is removed afterwards so this never runs twice. Losing it is harmless.
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
                        config.MainWindow.Width = wd;
                    if (root.TryGetProperty("WindowHeight", out var h) && h.TryGetDouble(out var hd) && hd > 0)
                        config.MainWindow.Height = hd;
                    if (root.TryGetProperty("WindowTop", out var tp) && tp.TryGetDouble(out var tpd))
                        config.MainWindow.Top = tpd;
                    if (root.TryGetProperty("WindowLeft", out var lf) && lf.TryGetDouble(out var lfd))
                        config.MainWindow.Left = lfd;
                    if (root.TryGetProperty("WindowState", out var st))
                    {
                        if (st.ValueKind == JsonValueKind.Number && st.TryGetInt32(out var si)
                            && Enum.IsDefined(typeof(System.Windows.WindowState), si))
                            config.MainWindow.State = (System.Windows.WindowState)si;
                        else if (st.ValueKind == JsonValueKind.String
                            && Enum.TryParse<System.Windows.WindowState>(st.GetString(), out var se))
                            config.MainWindow.State = se;
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
