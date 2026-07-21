#ifndef SCRCPY_AUDIO_H
#define SCRCPY_AUDIO_H

// Public C API of the audio-only scrcpy DLL.
//
// The DLL forwards audio from an Android device (via adb + scrcpy-server),
// decodes it, and outputs raw PCM to the host application instead of playing
// it. The host is responsible for playback, so the audio session belongs to
// the host process.
//
// PCM format: interleaved IEEE float32, 48000 Hz, stereo (as produced by the
// scrcpy audio pipeline). Confirm with sca_get_format() after
// SCA_EVENT_STREAM_STARTED.
//
// Threading:
//  - sca_start()/sca_stop() must be called from a single thread (they are not
//    reentrant). sca_stop() blocks until the session is fully torn down.
//  - All callbacks are invoked from internal background threads. Do not call
//    sca_start()/sca_stop() from inside a callback.
//  - sca_read() may be called from any thread (e.g. the audio render thread).

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#ifdef _WIN32
# ifdef SCA_BUILDING
#  define SCA_API __declspec(dllexport)
# else
#  define SCA_API __declspec(dllimport)
# endif
#else
# define SCA_API
#endif

// Session events reported via sca_event_cb
enum sca_event {
    SCA_EVENT_CONNECTED = 0,         // device connected, stream starting
    SCA_EVENT_CONNECTION_FAILED = 1, // could not connect to the device
    SCA_EVENT_STREAM_STARTED = 2,    // audio decoding started, format is known
    SCA_EVENT_STREAM_STOPPED = 3,    // audio stream closed
    SCA_EVENT_DISCONNECTED = 4,      // device disconnected (session ended)
    SCA_EVENT_AUDIO_DISABLED = 5,    // device cannot capture audio (session ended)
    SCA_EVENT_ERROR = 6,             // fatal error (session ended)
};

// Log levels (same order as scrcpy)
enum sca_log_level {
    SCA_LOG_VERBOSE = 0,
    SCA_LOG_DEBUG = 1,
    SCA_LOG_INFO = 2,
    SCA_LOG_WARN = 3,
    SCA_LOG_ERROR = 4,
};

typedef void (*sca_event_cb)(int32_t event, void *userdata);
typedef void (*sca_log_cb)(int32_t level, const char *message, void *userdata);
// PCM push callback: data is interleaved float32 samples, num_bytes is the
// byte length (a multiple of the frame size: channels * 4 bytes)
typedef void (*sca_pcm_cb)(const uint8_t *data, uint32_t num_bytes,
                           void *userdata);

struct sca_settings {
    // Set to sizeof(struct sca_settings), for ABI sanity checking
    uint32_t struct_size;

    // Device serial, NULL = the only connected device
    const char *serial;
    // Path to adb.exe, NULL = "adb" from PATH (or the ADB env var)
    const char *adb_path;
    // Path to the scrcpy-server file, NULL = next to the host executable
    const char *server_path;

    // "opus" (default), "aac", "flac" or "raw"
    const char *audio_codec;
    // NULL/"auto" (default: device audio output), "playback", "mic", ...
    // (same values as scrcpy --audio-source)
    const char *audio_source;
    // Specific device-side encoder name, NULL = default
    const char *audio_encoder;
    // Codec options string (scrcpy --audio-codec-options format), NULL = none
    const char *audio_codec_options;

    // Bit rate in bits/s, 0 = device default (128000)
    uint32_t audio_bit_rate;
    // Target buffering in milliseconds, 0 = default (50). Higher is more
    // robust, lower is less latency.
    uint32_t audio_buffer_ms;
    // Size (in ms) of the host's audio output buffer (e.g. the WASAPI
    // latency when pulling via sca_read). Used to size the overbuffering
    // tolerance so bursty consumption does not cause sample drops.
    // 0 = default (60).
    uint32_t output_buffer_ms;

    // adb tunnel port range, 0 = defaults (27183-27199)
    uint16_t port_first;
    uint16_t port_last;

    // 1 = also keep playing the audio on the device (Android 13+)
    uint8_t audio_dup;
    // enum sca_log_level, default SCA_LOG_INFO
    uint8_t log_level;

    // Optional callbacks (any may be NULL). userdata is passed back to all
    // of them. If pcm_cb is set, PCM is pushed from an internal thread every
    // ~10 ms; otherwise the host must pull PCM with sca_read().
    sca_event_cb event_cb;
    sca_log_cb log_cb;
    sca_pcm_cb pcm_cb;
    void *userdata;
};

// Fill settings with default values (must be called before customizing)
SCA_API void
sca_settings_init(struct sca_settings *settings);

// Start an audio forwarding session. Returns 0 on success (session starting
// asynchronously; completion/failure is reported via event_cb), a negative
// value on immediate error. Only one session may run at a time.
SCA_API int32_t
sca_start(const struct sca_settings *settings);

// Stop the session (if any) and release everything. Blocks until done.
// Safe to call if no session is running.
SCA_API void
sca_stop(void);

// Get the PCM format. Returns 0 if the stream is open (after
// SCA_EVENT_STREAM_STARTED), -1 otherwise.
// bits_per_sample is always 32 (IEEE float).
SCA_API int32_t
sca_get_format(uint32_t *sample_rate, uint32_t *channels,
               uint32_t *bits_per_sample);

// Pull PCM (only when no pcm_cb was configured). Always fills the buffer
// entirely and returns max_bytes: missing data is replaced with silence, so
// it can be called directly from an audio render callback. Returns 0 if
// max_bytes is negative.
SCA_API int32_t
sca_read(uint8_t *buffer, int32_t max_bytes);

// Name of the connected device, or "" if not connected. The returned pointer
// remains valid until sca_stop().
SCA_API const char *
sca_get_device_name(void);

#ifdef __cplusplus
}
#endif

#endif
