using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// The in-process audio link: wraps ScrcpyAudioPlayer (scrcpy_audio.dll +
    /// NAudio WasapiOut) with the same lifecycle shape the app previously used
    /// for the external scrcpy.exe process:
    ///
    ///   - TryStart(config, device)  ~ Process.Start
    ///   - HasEnded                  ~ Process.HasExited
    ///   - Ended event               ~ Process.Exited
    ///   - Stop()/StopAsync()        ~ Process.Kill + WaitForExit
    ///
    /// One link instance = one session; a link that ended cannot be restarted
    /// (create a new one), exactly like a Process.
    /// </summary>
    internal sealed class ScrcpyAudioLink
    {
        // The native session is a singleton, and Windows remembers per-app
        // audio volume, not per-link. Carry the last user-set volume across
        // link restarts (transport switches, quality restarts) so the level
        // feels persistent like the old scrcpy.exe session volume did.
        private static float s_lastVolume = 1f;
        private static readonly object s_startLock = new();

        private readonly ScrcpyAudioPlayer _player;
        private readonly object _lock = new();
        private Action<ScrcpyAudioLink>? _endedHandlers;
        private bool _ended;
        private bool _stopRequested;

        public string Device { get; }

        /// <summary>True once the session has terminated (device lost, error,
        /// audio refused, or an explicit stop).</summary>
        public bool HasEnded
        {
            get { lock (_lock) return _ended; }
        }

        /// <summary>
        /// Raised (once, from a background thread) when the session ends on its
        /// own — device disconnected, connection failed, error. Not raised for
        /// an explicit Stop(). If the session already ended when a handler is
        /// attached, the handler is invoked immediately, so the
        /// attach-after-start race cannot lose the notification.
        /// </summary>
        public event Action<ScrcpyAudioLink> Ended
        {
            add
            {
                bool fireNow;
                lock (_lock)
                {
                    fireNow = _ended && !_stopRequested;
                    if (!fireNow)
                        _endedHandlers += value;
                }
                if (fireNow)
                    value(this);
            }
            remove
            {
                lock (_lock) _endedHandlers -= value;
            }
        }

        private ScrcpyAudioLink(ScrcpyAudioPlayer player, string device)
        {
            _player = player;
            Device = device;
        }

        /// <summary>
        /// Checks that the native pieces the audio link needs are present.
        /// </summary>
        public static bool AssetsAvailable(out string missing)
        {
            string dir = ScrcpyAudioNative.NativeDllDirectory;
            foreach (var file in new[] { "scrcpy_audio.dll", "scrcpy-server-v4.1", "SDL3.dll", "avcodec-62.dll", "avutil-60.dll", "swresample-6.dll" })
            {
                var path = Path.Combine(dir, file);
                if (!File.Exists(path))
                {
                    missing = path;
                    return false;
                }
            }
            missing = string.Empty;
            return true;
        }

        /// <summary>
        /// Starts a new audio link bound to the given device id. Returns null
        /// on failure (already logged). Mirrors LaunchScrcpyProcess: no UI side
        /// effects, caller wires state and the Ended handler.
        /// </summary>
        public static ScrcpyAudioLink? TryStart(MusicConfig config, string device)
        {
            if (!AssetsAvailable(out var missing))
            {
                Debugger.show("Audio link: missing native file: " + missing);
                return null;
            }

            var options = BuildOptions(config, device);
            Debugger.show($"Starting audio link (device={device}, codec={options.AudioCodec}, bitrate={options.AudioBitRate}, buffer={options.AudioBufferMs}ms)");

            var player = new ScrcpyAudioPlayer();
            var link = new ScrcpyAudioLink(player, device);

            player.SessionEvent += link.OnSessionEvent;
            player.LogMessage += OnNativeLog;

            // The native session is a singleton; serialize starts so an
            // in-flight stop from a previous link can never race a new start.
            lock (s_startLock)
            {
                try
                {
                    player.Start(options);
                }
                catch (Exception ex)
                {
                    Debugger.show("Audio link start failed: " + ex.Message);
                    try { player.Dispose(); } catch { }
                    return null;
                }
            }

            player.Volume = s_lastVolume;
            return link;
        }

        /// <summary>
        /// Maps the app's AudioLink config to native options, reproducing the
        /// exact arguments the old BuildScrcpyArgs produced for scrcpy.exe
        /// (--audio-source=playback, codec, buffer, bit rate, FLAC options).
        /// </summary>
        private static ScrcpyAudioOptions BuildOptions(MusicConfig config, string device)
        {
            var codec = string.IsNullOrWhiteSpace(config.AudioLink.Codec) ? "raw" : config.AudioLink.Codec.Trim().ToLowerInvariant();
            var buffer = config.AudioLink.BufferMs > 0 ? config.AudioLink.BufferMs : 50;

            var options = new ScrcpyAudioOptions
            {
                Serial = device,
                AdbPath = File.Exists(config.Paths.Adb) ? config.Paths.Adb : null,
                AudioSource = "playback",
                AudioCodec = codec,
                AudioBufferMs = (uint)buffer,
                LogLevel = config.AppSettings.DebugMode ? 1 : 2,
            };

            if (codec != "raw" && !string.IsNullOrWhiteSpace(config.AudioLink.Bitrate))
            {
                var bitrateText = config.AudioLink.Bitrate.Trim();
                if (bitrateText.EndsWith("K", StringComparison.OrdinalIgnoreCase))
                    bitrateText = bitrateText[..^1];

                if (int.TryParse(bitrateText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bitrateValue) && bitrateValue > 0)
                    options.AudioBitRate = (uint)bitrateValue * 1000;
            }

            if (codec == "flac")
                options.AudioCodecOptions = $"flac-compression-level={Math.Clamp(config.AudioLink.FlacCompressionLevel, 1, 8)}";

            return options;
        }

        /// <summary>Stops the session and releases playback. Blocking (may take
        /// a second or two while the device-side server is torn down).</summary>
        public void Stop()
        {
            lock (_lock)
            {
                _stopRequested = true;
                _ended = true;
                _endedHandlers = null;
            }

            lock (s_startLock)
            {
                try
                {
                    _player.Stop();
                }
                catch (Exception ex)
                {
                    Debugger.show("Audio link stop failed: " + ex.Message);
                }
            }
        }

        public Task StopAsync() => Task.Run(Stop);

        // ---- Volume (this process's own playback, replaces the old
        //      scrcpy.exe session-volume COM hunting) ----

        public bool TryGetVolume(out float volume)
        {
            if (HasEnded)
            {
                volume = 0f;
                return false;
            }
            volume = _player.Volume;
            return true;
        }

        public bool TrySetVolume(float volume)
        {
            if (HasEnded)
                return false;
            var clamped = Math.Clamp(volume, 0f, 1f);
            _player.Volume = clamped;
            s_lastVolume = clamped;
            return true;
        }

        public bool TryAdjustVolume(float delta)
        {
            if (!TryGetVolume(out var current))
                return false;
            return TrySetVolume(current + delta);
        }

        private void OnSessionEvent(ScrcpyAudioEvent e)
        {
            switch (e)
            {
                case ScrcpyAudioEvent.Connected:
                    Debugger.show($"Audio link: device connected ({_player.DeviceName}).");
                    break;
                case ScrcpyAudioEvent.StreamStarted:
                    Debugger.show("Audio link: audio stream started.");
                    break;
                case ScrcpyAudioEvent.ConnectionFailed:
                case ScrcpyAudioEvent.Disconnected:
                case ScrcpyAudioEvent.AudioDisabled:
                case ScrcpyAudioEvent.Error:
                    Debugger.show($"Audio link: session ended ({e}).");
                    RaiseEnded();
                    break;
            }
        }

        private void RaiseEnded()
        {
            Action<ScrcpyAudioLink>? handlers;
            lock (_lock)
            {
                if (_ended)
                    return;
                _ended = true;
                handlers = _endedHandlers;
                _endedHandlers = null;
            }

            // Playback teardown must not run on the native callback thread
            // (Stop joins that thread); hand it off.
            _ = Task.Run(() =>
            {
                lock (s_startLock)
                {
                    try { _player.Stop(); } catch { }
                }
                handlers?.Invoke(this);
            });
        }

        private static void OnNativeLog(int level, string message)
        {
            // Debugger.show is internally gated on IsEnabled
            Debugger.show($"[scrcpy-audio:{level}] {message}");
        }
    }
}
