using System;
using System.Collections.Generic;
using System.Linq;

namespace AndroidMusicPresenceLink
{
    public static class AudioQualityPresets
    {
        public const string CustomLabel = "Custom";

        public sealed class Preset
        {
            public string Name { get; init; } = string.Empty;
            public string ShortName { get; init; } = string.Empty;
            public string Description { get; init; } = string.Empty;
            public string Codec { get; init; } = "raw";
            // Empty string means "no explicit bitrate" (e.g. raw, flac).
            public string Bitrate { get; init; } = string.Empty;
            public int BufferMs { get; init; } = 80;
            // Only used when Codec == "flac".
            public int FlacCompressionLevel { get; init; } = 2;
        }

        /// <summary>
        /// All presets in display order. Names here MUST stay in sync with the
        /// strings handled by MainWindow.UpdateAudioSettings, since legacy code
        /// keys off them.
        /// </summary>
        public static readonly IReadOnlyList<Preset> All = new[]
        {
            new Preset
            {
                Name = "Data Saver (for slow internet)",
                ShortName = "Data Saver",
                Description = "Opus 64 kbps, smallest data use, OK quality.",
                Codec = "opus",
                Bitrate = "64",
                BufferMs = 120,
            },
            new Preset
            {
                Name = "Default Quality (good for general audio)",
                ShortName = "Medium",
                Description = "Opus 128 kbps, balanced for general audio.",
                Codec = "opus",
                Bitrate = "128",
                BufferMs = 100,
            },
            new Preset
            {
                Name = "High Quality (good for streaming music)",
                ShortName = "High",
                Description = "Opus 256 kbps, transparent for most music.",
                Codec = "opus",
                Bitrate = "256",
                BufferMs = 80,
            },
            new Preset
            {
                Name = "Lossless (highest quality lower data use)",
                ShortName = "Lossless",
                Description = "FLAC, lossless with moderate compression.",
                Codec = "flac",
                Bitrate = string.Empty,
                BufferMs = 80,
                FlacCompressionLevel = 2,
            },
            new Preset
            {
                Name = "Max Quality (I payed for the whole WiFi)",
                ShortName = "Max",
                Description = "Raw PCM, uncompressed audio.",
                Codec = "raw",
                Bitrate = string.Empty,
                BufferMs = 80,
            },
        };

        public static Preset? FindByName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;
            return All.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Inspects the saved config and returns the preset that matches it exactly,
        /// or null if the values don't line up with any preset (i.e. "Custom").
        /// </summary>
        public static Preset? MatchFromConfig(MusicConfig config)
        {
            if (config == null) return null;

            var codec = string.IsNullOrWhiteSpace(config.ScrcpyAudioCodec) ? "raw" : config.ScrcpyAudioCodec.Trim().ToLowerInvariant();
            var bitrate = (config.ScrcpyAudioBitrate ?? string.Empty).Trim();
            // Strip a trailing "K" if any tooling added it.
            if (bitrate.EndsWith("K", StringComparison.OrdinalIgnoreCase))
                bitrate = bitrate[..^1];

            var buffer = config.ScrcpyAudioBuffer > 0 ? config.ScrcpyAudioBuffer : 80;
            var flac = config.ScrcpyFlacCompressionLevel;

            foreach (var preset in All)
            {
                if (!preset.Codec.Equals(codec, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Bitrate only matters for codecs that use it.
                bool bitrateMatch = preset.Codec switch
                {
                    "raw" => true,
                    "flac" => true,
                    _ => string.Equals(preset.Bitrate, bitrate, StringComparison.OrdinalIgnoreCase),
                };
                if (!bitrateMatch) continue;

                if (preset.BufferMs != buffer) continue;

                if (preset.Codec.Equals("flac", StringComparison.OrdinalIgnoreCase)
                    && preset.FlacCompressionLevel != flac)
                {
                    continue;
                }

                return preset;
            }

            return null;
        }

        public static void ApplyToConfig(MusicConfig config, Preset preset)
        {
            if (config == null || preset == null) return;

            config.ScrcpyAudioCodec = preset.Codec;
            // Empty means "no explicit bitrate"; the launch code already handles that.
            config.ScrcpyAudioBitrate = preset.Bitrate;
            config.ScrcpyAudioBuffer = preset.BufferMs > 0 ? preset.BufferMs : 80;

            if (preset.Codec.Equals("flac", StringComparison.OrdinalIgnoreCase))
            {
                config.ScrcpyFlacCompressionLevel = Math.Clamp(preset.FlacCompressionLevel, 1, 8);
            }

            config.AudioQualityPresetName = preset.Name;
        }


        public static void ApplyCustomToConfig(MusicConfig config,
            string codec, string bitrate, int bufferMs, int flacLevel)
        {
            if (config == null) return;
            config.ScrcpyAudioCodec = codec;
            config.ScrcpyAudioBitrate = bitrate;
            config.ScrcpyAudioBuffer = bufferMs;
            config.ScrcpyFlacCompressionLevel = flacLevel;
            config.AudioQualityPresetName = CustomLabel;
        }

        public static string GetShortLabelForConfig(MusicConfig config)
        {
            var matched = MatchFromConfig(config);
            return matched?.ShortName ?? CustomLabel;
        }
    }
}