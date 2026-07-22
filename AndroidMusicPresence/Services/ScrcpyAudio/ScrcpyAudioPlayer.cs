using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AndroidMusicPresenceLink
{
    public enum ScrcpyAudioEvent
    {
        Connected = ScrcpyAudioNative.EventConnected,
        ConnectionFailed = ScrcpyAudioNative.EventConnectionFailed,
        StreamStarted = ScrcpyAudioNative.EventStreamStarted,
        StreamStopped = ScrcpyAudioNative.EventStreamStopped,
        Disconnected = ScrcpyAudioNative.EventDisconnected,
        AudioDisabled = ScrcpyAudioNative.EventAudioDisabled,
        Error = ScrcpyAudioNative.EventError,
    }

    public sealed class ScrcpyAudioOptions
    {
        /// <summary>Device serial (adb -s). Null = the only connected device.</summary>
        public string? Serial { get; set; }

        /// <summary>Path to adb.exe. Null = Assets\adb.exe.</summary>
        public string? AdbPath { get; set; }

        /// <summary>Path to the scrcpy-server file. Null = Assets\scrcpy-server-v4.1.</summary>
        public string? ServerPath { get; set; }

        /// <summary>"opus" (default), "aac", "flac" or "raw".</summary>
        public string? AudioCodec { get; set; }

        /// <summary>Null/"auto" = device audio output. Same values as scrcpy --audio-source.</summary>
        public string? AudioSource { get; set; }

        /// <summary>Codec options string (scrcpy --audio-codec-options format).</summary>
        public string? AudioCodecOptions { get; set; }

        /// <summary>Bit rate in bits/s. 0 = device default (128000).</summary>
        public uint AudioBitRate { get; set; }

        /// <summary>Target buffering in ms (default 50). Higher = more robust, lower = less latency.</summary>
        public uint AudioBufferMs { get; set; } = 50;

        /// <summary>Keep playing the audio on the phone too (Android 13+).</summary>
        public bool AudioDup { get; set; }

        /// <summary>0=verbose 1=debug 2=info 3=warn 4=error.</summary>
        public int LogLevel { get; set; } = 2;

        /// <summary>WASAPI output latency in ms.</summary>
        public int OutputLatencyMs { get; set; } = 60;
    }

    /// <summary>
    /// Forwards Android device audio into this process and plays it through
    /// NAudio WasapiOut, so Windows attributes the audio session to this app
    /// (no more external scrcpy.exe audio session).
    ///
    /// Usage:
    ///   var player = new ScrcpyAudioPlayer();
    ///   player.SessionEvent += e => ...;   // raised on background threads!
    ///   player.Start(new ScrcpyAudioOptions { Serial = "..." });
    ///   ...
    ///   player.Stop();
    /// </summary>
    public sealed class ScrcpyAudioPlayer : IDisposable
    {
        // Keep delegate instances alive for the whole session (the native
        // side stores the function pointers)
        private ScrcpyAudioNative.EventCallback? _eventCb;
        private ScrcpyAudioNative.LogCallback? _logCb;

        private WasapiOut? _wasapiOut;
        private readonly ScrcpyWaveProvider _waveProvider = new();
        private bool _running;

        // Last requested volume (0..1). Applied to this process's Windows audio
        // session, but cached here so the value survives the brief gap before
        // the session registers with Windows, and is readable without a COM hit.
        private float _desiredVolume = 1f;

        /// <summary>Raised from native background threads — marshal to the UI thread yourself.</summary>
        public event Action<ScrcpyAudioEvent>? SessionEvent;

        /// <summary>Raised from native background threads. (level, message)</summary>
        public event Action<int, string>? LogMessage;

        public bool IsRunning => _running;

        public string DeviceName => ScrcpyAudioNative.GetDeviceName();

        /// <summary>
        /// Playback volume of the audio link (0..1). Applied to THIS process's
        /// Windows audio session (ISimpleAudioVolume) — i.e. the per-app slider
        /// shown in the Windows Volume Mixer. Setting it here moves that slider,
        /// dragging that slider changes playback, and it never touches the
        /// global/master volume of the output device.
        /// </summary>
        public float Volume
        {
            get => _desiredVolume;
            set
            {
                _desiredVolume = Math.Clamp(value, 0f, 1f);
                ApplySessionVolume(_desiredVolume);
            }
        }

        // Set ISimpleAudioVolume on every active render session owned by this
        // process. Returns true if at least one session was found and updated.
        // Enumerates all active render endpoints (not just the default) so it
        // still works if the user switched the default output mid-session.
        private static bool ApplySessionVolume(float volume)
        {
            bool applied = false;
            try
            {
                uint pid = (uint)Environment.ProcessId;
                using var enumerator = new MMDeviceEnumerator();
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    using (device)
                    {
                        SessionCollection sessions;
                        try { sessions = device.AudioSessionManager.Sessions; }
                        catch { continue; }

                        for (int i = 0; i < sessions.Count; i++)
                        {
                            var session = sessions[i];
                            if (session.GetProcessID != pid)
                                continue;
                            try
                            {
                                session.SimpleAudioVolume.Volume = volume;
                                applied = true;
                            }
                            catch
                            {
                                // A session can disappear between enumeration and use.
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // No active render endpoint / session manager unavailable.
                System.Diagnostics.Debug.WriteLine("ApplySessionVolume failed: " + ex.Message);
            }
            return applied;
        }

        // The session appears a moment after playback starts; retry applying the
        // pending volume until it exists, so the mixer shows the right level and
        // the restored volume takes effect right away.
        private void ScheduleSessionVolumeApply()
        {
            _ = Task.Run(async () =>
            {
                foreach (var delayMs in new[] { 60, 150, 350, 700, 1200 })
                {
                    await Task.Delay(delayMs).ConfigureAwait(false);
                    if (!_running)
                        return;
                    if (ApplySessionVolume(_desiredVolume))
                        return;
                }
            });
        }

        public void Start(ScrcpyAudioOptions options)
        {
            if (_running)
                throw new InvalidOperationException("scrcpy audio session already running");

            string assets = ScrcpyAudioNative.NativeDllDirectory;

            _eventCb = OnNativeEvent;
            _logCb = OnNativeLog;

            var settings = ScrcpyAudioNative.CreateDefaultSettings();
            settings.Serial = options.Serial;
            settings.AdbPath = options.AdbPath ?? Path.Combine(assets, "adb.exe");
            settings.ServerPath = options.ServerPath ?? Path.Combine(assets, "scrcpy-server-v4.1");
            settings.AudioCodec = options.AudioCodec;
            settings.AudioSource = options.AudioSource;
            settings.AudioCodecOptions = options.AudioCodecOptions;
            settings.AudioBitRate = options.AudioBitRate;
            settings.AudioBufferMs = options.AudioBufferMs;
            // Lets the native regulator tolerate our bursty WASAPI pulls
            // without dropping samples
            settings.OutputBufferMs = (uint)Math.Max(options.OutputLatencyMs, 0);
            settings.AudioDup = options.AudioDup ? (byte)1 : (byte)0;
            settings.LogLevel = (byte)Math.Clamp(options.LogLevel, 0, 4);
            settings.EventCb = _eventCb;
            settings.LogCb = _logCb;

            int ret = ScrcpyAudioNative.sca_start(ref settings);
            if (ret != 0)
                throw new InvalidOperationException($"sca_start failed with code {ret}");

            _running = true;

            try
            {
                // Pull PCM straight from the DLL on the WASAPI render thread.
                // The DLL always fills the buffer (silence when no data), so
                // playback starts immediately and the session shows up for
                // this process right away.
                _wasapiOut = new WasapiOut(AudioClientShareMode.Shared, true, options.OutputLatencyMs);
                _wasapiOut.Init(_waveProvider);
                _wasapiOut.Play();

                // Push the restored/desired volume onto the app's audio session
                // once Windows registers it (a moment after Play()).
                ScheduleSessionVolumeApply();
            }
            catch
            {
                Stop();
                throw;
            }
        }

        public void Stop()
        {
            if (!_running && _wasapiOut == null)
                return;

            try
            {
                _wasapiOut?.Stop();
                _wasapiOut?.Dispose();
            }
            catch
            {
                // Best effort; do not let audio teardown mask the native stop
            }
            _wasapiOut = null;

            if (_running)
            {
                ScrcpyAudioNative.sca_stop();
                _running = false;
            }

            _eventCb = null;
            _logCb = null;
        }

        public void Dispose() => Stop();

        private void OnNativeEvent(int eventId, IntPtr userdata)
        {
            SessionEvent?.Invoke((ScrcpyAudioEvent)eventId);
        }

        private void OnNativeLog(int level, string message, IntPtr userdata)
        {
            LogMessage?.Invoke(level, message);
        }

        /// <summary>
        /// IWaveProvider that pulls interleaved float32 48 kHz stereo PCM from
        /// the native DLL. Read() always fills the buffer (silence-padded), so
        /// WasapiOut never underruns. Volume is not applied here — it is handled
        /// by the Windows audio session (see <see cref="Volume"/>).
        /// </summary>
        private sealed class ScrcpyWaveProvider : IWaveProvider
        {
            public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

            public int Read(byte[] buffer, int offset, int count)
            {
                var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    IntPtr ptr = IntPtr.Add(handle.AddrOfPinnedObject(), offset);
                    ScrcpyAudioNative.sca_read(ptr, count);
                }
                finally
                {
                    handle.Free();
                }
                return count;
            }
        }
    }
}
