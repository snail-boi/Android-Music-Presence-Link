using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

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

    public class MusicConfig
    {
        public PathsConfig Paths { get; set; } = new PathsConfig();
        public string SelectedDeviceUSB { get; set; } = string.Empty;
        public string SelectedDeviceWiFi { get; set; } = string.Empty;
        public string SelectedDeviceName { get; set; } = string.Empty;
        public string MusicRemoteRoot { get; set; } = string.Empty;
        public List<string> AllowedApps { get; set; } = new List<string> { "in.krosbits.musicolet" };
        public UpdateIntervalMode UpdateIntervalMode { get; set; } = UpdateIntervalMode.Medium;
        public bool DebugMode { get; set; } = false;
        public string ScrcpyAudioCodec { get; set; } = "raw";
        public string ScrcpyAudioBitrate { get; set; } = string.Empty;
        public int ScrcpyAudioBuffer { get; set; } = 50;
        public int ScrcpyFlacCompressionLevel { get; set; } = 5;
        public List<string> ScrcpyAvailableAudioCodecs { get; set; } = new List<string> { "raw" };
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
                TryImportFromMainConfig(fresh);
                return NormalizeConfig(fresh);
            }
            catch
            {
                return new MusicConfig();
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

            if (config.AllowedApps.Count == 0)
                config.AllowedApps.Add("in.krosbits.musicolet");

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

            return config;
        }

        private static void TryImportFromMainConfig(MusicConfig target)
        {
            try
            {
                var folder = Path.GetDirectoryName(ConfigPath);
                if (string.IsNullOrEmpty(folder)) return;

                var mainConfigPath = Path.Combine(folder, "config.json");
                if (!File.Exists(mainConfigPath))
                {
                    var altPath = Path.Combine(folder, "Config.json");
                    if (File.Exists(altPath)) mainConfigPath = altPath;
                }

                if (!File.Exists(mainConfigPath)) return;

                using var doc = JsonDocument.Parse(File.ReadAllText(mainConfigPath));
                var root = doc.RootElement;

                if (root.TryGetProperty("Paths", out var paths))
                {
                    if (paths.TryGetProperty("Adb", out var adb)) target.Paths.Adb = adb.GetString() ?? target.Paths.Adb;
                    if (paths.TryGetProperty("FfmpegPath", out var ffmpeg)) target.Paths.FfmpegPath = ffmpeg.GetString() ?? target.Paths.FfmpegPath;
                    if (paths.TryGetProperty("CoverCachePath", out var cache)) target.Paths.CoverCachePath = cache.GetString() ?? target.Paths.CoverCachePath;
                    if (paths.TryGetProperty("Scrcpy", out var scrcpy)) target.Paths.Scrcpy = scrcpy.GetString() ?? target.Paths.Scrcpy;
                    else if (paths.TryGetProperty("ScrcpyPath", out var scrcpyPath)) target.Paths.Scrcpy = scrcpyPath.GetString() ?? target.Paths.Scrcpy;
                }

                if (root.TryGetProperty("SelectedDeviceUSB", out var usb)) target.SelectedDeviceUSB = usb.GetString() ?? string.Empty;
                if (root.TryGetProperty("SelectedDeviceWiFi", out var wifi)) target.SelectedDeviceWiFi = wifi.GetString() ?? string.Empty;
                if (root.TryGetProperty("SelectedDeviceName", out var name)) target.SelectedDeviceName = name.GetString() ?? string.Empty;

                if (root.TryGetProperty("SpecialOptions", out var specials) &&
                    specials.TryGetProperty("MusicRemoteRoot", out var remoteRoot))
                {
                    target.MusicRemoteRoot = remoteRoot.GetString() ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                Debugger.show("Failed to import main config: " + ex.Message);
            }
        }
    }
}
