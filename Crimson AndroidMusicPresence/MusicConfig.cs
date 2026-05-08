using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;

namespace musicpresense
{
    public enum UpdateIntervalMode
    {
        Extreme = 1,
        Fast = 2,
        Medium = 3,
        Slow = 4,
        None = 5
    }

    public enum WirelessMode
    {
        // Classic adb tcpip 5555 flow. Requires USB to re-enable after every reboot.
        // Port is fixed and predictable, so reconnecting after an IP change is trivial.
        TcpIp = 0,

        // Android 11+ Wireless Debugging. One-time TLS pairing over USB-free network.
        // Survives reboots (pairing persists), but the connection port is randomly
        // assigned each time wireless debugging toggles on, so we need mDNS to find it.
        WirelessDebugging = 1
    }

    public sealed class EligibleAppConfig
    {
        public string PackageName { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = true;
        public bool EnableCoverSearch { get; set; } = true;
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
        public UpdateIntervalMode UpdateIntervalMode { get; set; } = UpdateIntervalMode.Fast;
        public bool DebugMode { get; set; } = false;
        public bool UseDarkMode { get; set; } = true;
        public bool OpenInTaskbar { get; set; } = false;
        public bool StartWithWindows { get; set; } = false;
        public bool ShowMediaPlayerWindow { get; set; } = false;
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

        public int CachClearInMB { get; set; } = 200;
        public string LyricsSearchFolderOverride { get; set; } = string.Empty;
        public string CoverArtFileNamePatterns { get; set; } = "cover.jpg;cover.png;folder.jpg";
        public string CopyTrackInfoTemplate { get; set; } = "{artist} - {title}";

        public List<string> AllowedApps { get; set; } = new List<string> { };
        public List<EligibleAppConfig> EligibleApps { get; set; } = new List<EligibleAppConfig>();

        public int HotkeyVolumeUpKey { get; set; } = 0xAF;
        public int HotkeyVolumeDownKey { get; set; } = 0xAE;
        public int HotkeyToggleScrcpyKey { get; set; } = 0x53;
        public int HotkeyToggleLyricsOverlayKey { get; set; } = 0x4C;
        public int HotkeyCopyTrackInfoKey { get; set; } = 0x43;
        public int HotkeyModifier { get; set; } = 0x0004;
    }

    public class PathsConfig
    {
        public string Adb { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Snail",
            "Resources",
            "adb.exe");

        public string FfmpegPath { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Snail",
            "Resources",
            "ffmpeg.exe");

        public string Scrcpy { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Snail",
            "Resources",
            "scrcpy.exe");

        public string CoverCachePath { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Snail",
            "CoverCache");
    }

    public static class MusicConfigManager
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public static string ConfigPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Snail",
            "musicconfig.json");

        public static MusicConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    var loaded = JsonSerializer.Deserialize<MusicConfig>(json);
                    if (loaded != null)
                        return NormalizeConfig(loaded);
                }

                var fresh = new MusicConfig();
                return NormalizeConfig(fresh);
            }
            catch
            {
                return NormalizeConfig(new MusicConfig());
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

            if (config.EligibleApps.Count == 0 && config.AllowedApps.Count > 0)
            {
                config.EligibleApps = config.AllowedApps
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Select(a => a.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(a => new EligibleAppConfig
                    {
                        PackageName = a,
                        IsEnabled = true,
                        EnableCoverSearch = true
                    })
                    .ToList();
            }

            if (config.EligibleApps.Count == 0)
            {
                config.EligibleApps.Add(new EligibleAppConfig
                {
                    PackageName = "in.krosbits.musicolet",
                    IsEnabled = true,
                    EnableCoverSearch = true
                });
            }

            config.EligibleApps = config.EligibleApps
                .Where(a => !string.IsNullOrWhiteSpace(a.PackageName))
                .GroupBy(a => a.PackageName.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var first = g.First();
                    return new EligibleAppConfig
                    {
                        PackageName = g.Key,
                        IsEnabled = g.Any(x => x.IsEnabled),
                        EnableCoverSearch = g.Any(x => x.EnableCoverSearch)
                    };
                })
                .ToList();

            if (!config.EligibleApps.Any(a => a.IsEnabled))
            {
                config.EligibleApps.Add(new EligibleAppConfig
                {
                    PackageName = "in.krosbits.musicolet",
                    IsEnabled = true,
                    EnableCoverSearch = true
                });
            }

            config.AllowedApps = config.EligibleApps
                .Where(a => a.IsEnabled)
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

            var allowedMods = new[] { 0x0001, 0x0002, 0x0004 };
            if (!allowedMods.Contains(config.HotkeyModifier)) config.HotkeyModifier = 0x0001;

            // Sanity: WirelessDebugging without a service name is functionally broken,
            // but we don't auto-rewrite to TcpIp because the user may be mid-pairing.
            // The presence service handles that gracefully.

            return config;
        }
    }
}