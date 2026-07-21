#include "scrcpy_audio.h"

#include <assert.h>
#include <stdatomic.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
// winsock2.h must never be included AFTER windows.h
# include <winsock2.h>
# include <windows.h>
#endif

#include <libavutil/log.h>
#include <SDL3/SDL_log.h>

#include "common.h"
#include "adb/adb.h"
#include "audio_regulator.h"
#include "decoder.h"
#include "demuxer.h"
#include "options.h"
#include "server.h"
#include "util/log.h"
#include "util/net.h"
#include "util/rand.h"
#include "util/thread.h"
#include "util/tick.h"

#define SCA_DEFAULT_AUDIO_BUFFER_MS 50
// Cadence of the optional PCM push callback
#define SCA_PCM_CALLBACK_PERIOD_MS 10

// PCM sink: receives decoded AVFrames from the decoder and feeds the audio
// regulator, from which the host pulls PCM (directly via sca_read(), or
// through the internal feeder thread when a pcm_cb is configured)
struct sca_pcm_sink {
    struct sc_frame_sink frame_sink;

    // Guards `open` and the regulator lifecycle against concurrent readers
    sc_mutex mutex;
    bool open;

    uint32_t sample_rate;
    uint32_t nb_channels;
    size_t sample_size; // bytes per frame (all channels)

    struct sc_audio_regulator reg;
};

struct sca_session {
    bool in_use; // a session exists (running or already self-ended)

    struct sca_settings settings; // deep copy (strings owned)

    struct sc_server server;
    struct sc_demuxer demuxer;
    struct sc_decoder decoder;
    struct sca_pcm_sink sink;

    sc_thread thread; // session thread
    bool thread_started;

    sc_thread pcm_thread; // feeder thread (callback mode)
    bool pcm_thread_started;

    sc_mutex mutex;
    sc_cond cond;
    // State below is guarded by mutex and signalled via cond
    bool connected;
    bool connection_failed;
    bool demuxer_ended;
    enum sc_demuxer_status demuxer_status;
    bool stop_requested;

    bool demuxer_started;

    char device_name[SC_DEVICE_NAME_FIELD_LENGTH];
};

// Single static session. The struct (and its mutexes) intentionally lives
// forever so that sca_read() from an audio render thread can never touch
// freed memory.
static struct sca_session g_session;
static bool g_static_init_done;
static bool g_net_init_done;

// Copies of the host callbacks (also read by the log forwarder)
static sca_log_cb g_log_cb;
static void *g_log_userdata;

static void
sca_emit_event(struct sca_session *s, enum sca_event event) {
    if (s->settings.event_cb) {
        s->settings.event_cb((int32_t) event, s->settings.userdata);
    }
}

// ---------------------------------------------------------------------------
// Logging: forward SDL logs (used by all scrcpy code via util/log.h) and
// FFmpeg logs to the host log callback (or stderr by default)
// ---------------------------------------------------------------------------

static int32_t
sca_log_level_from_sdl(SDL_LogPriority priority) {
    switch (priority) {
        case SDL_LOG_PRIORITY_VERBOSE:
            return SCA_LOG_VERBOSE;
        case SDL_LOG_PRIORITY_DEBUG:
            return SCA_LOG_DEBUG;
        case SDL_LOG_PRIORITY_INFO:
            return SCA_LOG_INFO;
        case SDL_LOG_PRIORITY_WARN:
            return SCA_LOG_WARN;
        default:
            return SCA_LOG_ERROR;
    }
}

static void SDLCALL
sca_sdl_log_output(void *userdata, int category, SDL_LogPriority priority,
                   const char *message) {
    (void) userdata;
    (void) category;

    sca_log_cb cb = g_log_cb;
    if (cb) {
        cb(sca_log_level_from_sdl(priority), message, g_log_userdata);
    } else {
        FILE *out = priority < SDL_LOG_PRIORITY_WARN ? stdout : stderr;
        fprintf(out, "scrcpy-audio: %s\n", message);
    }
}

static SDL_LogPriority
sca_sdl_priority_from_av_level(int level) {
    switch (level) {
        case AV_LOG_PANIC:
        case AV_LOG_FATAL:
        case AV_LOG_ERROR:
            return SDL_LOG_PRIORITY_ERROR;
        case AV_LOG_WARNING:
            return SDL_LOG_PRIORITY_WARN;
        case AV_LOG_INFO:
            return SDL_LOG_PRIORITY_INFO;
    }
    // do not forward others, which are too verbose
    return 0;
}

static void
sca_av_log_callback(void *avcl, int level, const char *fmt, va_list vl) {
    (void) avcl;
    SDL_LogPriority priority = sca_sdl_priority_from_av_level(level);
    if (priority == 0) {
        return;
    }

    char buf[512];
    vsnprintf(buf, sizeof(buf), fmt, vl);
    // Strip the trailing newline added by FFmpeg formats
    size_t len = strlen(buf);
    while (len && (buf[len - 1] == '\n' || buf[len - 1] == '\r')) {
        buf[--len] = '\0';
    }
    if (len) {
        SDL_LogMessage(SDL_LOG_CATEGORY_CUSTOM, priority, "[FFmpeg] %s", buf);
    }
}

// ---------------------------------------------------------------------------
// PCM sink (frame sink trait implementation)
// ---------------------------------------------------------------------------

#define SINK_DOWNCAST(SINK) \
    container_of(SINK, struct sca_pcm_sink, frame_sink)

static struct sca_session *
sink_session(struct sca_pcm_sink *sink) {
    return container_of(sink, struct sca_session, sink);
}

static bool
sca_pcm_sink_open(struct sc_frame_sink *frame_sink, const AVCodecContext *ctx,
                  const struct sc_stream_session *session) {
    (void) session;

    struct sca_pcm_sink *sink = SINK_DOWNCAST(frame_sink);
    struct sca_session *s = sink_session(sink);

#ifdef SCRCPY_LAVU_HAS_CHLAYOUT
    assert(ctx->ch_layout.nb_channels > 0 && ctx->ch_layout.nb_channels < 256);
    uint8_t nb_channels = ctx->ch_layout.nb_channels;
#else
    int tmp = av_get_channel_layout_nb_channels(ctx->channel_layout);
    assert(tmp > 0 && tmp < 256);
    uint8_t nb_channels = tmp;
#endif

    assert(ctx->sample_rate > 0);
    assert(!av_sample_fmt_is_planar(SC_AV_SAMPLE_FMT));
    int out_bytes_per_sample = av_get_bytes_per_sample(SC_AV_SAMPLE_FMT);
    assert(out_bytes_per_sample > 0);

    uint32_t buffer_ms = s->settings.audio_buffer_ms
                       ? s->settings.audio_buffer_ms
                       : SCA_DEFAULT_AUDIO_BUFFER_MS;
    uint32_t target_buffering_samples =
        (uint64_t) buffer_ms * ctx->sample_rate / 1000;

    // The host consumes in bursts of up to output_buffer_ms, which makes the
    // buffering level oscillate by that amount; widen the skip threshold
    // accordingly (on top of the regulator's default 60 ms margin)
    uint32_t output_buffer_ms = s->settings.output_buffer_ms
                              ? s->settings.output_buffer_ms
                              : 60;
    uint32_t overbuffering_margin_samples =
        (uint64_t) (60 + output_buffer_ms) * ctx->sample_rate / 1000;

    size_t sample_size = nb_channels * out_bytes_per_sample;

    sc_mutex_lock(&sink->mutex);
    bool ok = sc_audio_regulator_init(&sink->reg, sample_size, ctx,
                                      target_buffering_samples);
    if (!ok) {
        sc_mutex_unlock(&sink->mutex);
        return false;
    }
    sink->reg.overbuffering_margin = overbuffering_margin_samples;
    sink->sample_rate = ctx->sample_rate;
    sink->nb_channels = nb_channels;
    sink->sample_size = sample_size;
    sink->open = true;
    sc_mutex_unlock(&sink->mutex);

    LOGI("Audio stream started: %d Hz, %u channels, float32",
         ctx->sample_rate, (unsigned) nb_channels);
    sca_emit_event(s, SCA_EVENT_STREAM_STARTED);
    return true;
}

static void
sca_pcm_sink_close(struct sc_frame_sink *frame_sink) {
    struct sca_pcm_sink *sink = SINK_DOWNCAST(frame_sink);
    struct sca_session *s = sink_session(sink);

    sc_mutex_lock(&sink->mutex);
    sink->open = false;
    sc_audio_regulator_destroy(&sink->reg);
    sc_mutex_unlock(&sink->mutex);

    sca_emit_event(s, SCA_EVENT_STREAM_STOPPED);
}

static bool
sca_pcm_sink_push(struct sc_frame_sink *frame_sink, const AVFrame *frame) {
    struct sca_pcm_sink *sink = SINK_DOWNCAST(frame_sink);
    // Only called between open() and close(), no locking needed for `open`
    return sc_audio_regulator_push(&sink->reg, frame);
}

static void
sca_pcm_sink_init(struct sca_pcm_sink *sink) {
    static const struct sc_frame_sink_ops ops = {
        .open = sca_pcm_sink_open,
        .close = sca_pcm_sink_close,
        .push = sca_pcm_sink_push,
    };

    sink->frame_sink.ops = &ops;
    sink->open = false;
}

// ---------------------------------------------------------------------------
// PCM feeder thread (only when a pcm_cb is configured)
// ---------------------------------------------------------------------------

static int
run_pcm_feeder(void *data) {
    struct sca_session *s = data;
    struct sca_pcm_sink *sink = &s->sink;

    // Enough for the callback period at 48 kHz stereo float32 (with margin)
    static uint8_t buf[SCA_PCM_CALLBACK_PERIOD_MS * 48 * 2 * 4 * 2];

    sc_tick deadline = sc_tick_now();
    for (;;) {
        deadline += SC_TICK_FROM_MS(SCA_PCM_CALLBACK_PERIOD_MS);

        sc_mutex_lock(&s->mutex);
        bool stopped = s->stop_requested || s->demuxer_ended;
        while (!stopped) {
            if (sc_cond_timedwait(&s->cond, &s->mutex, deadline)) {
                stopped = s->stop_requested || s->demuxer_ended;
            } else {
                break; // deadline reached
            }
        }
        sc_mutex_unlock(&s->mutex);
        if (stopped) {
            return 0;
        }

        sc_mutex_lock(&sink->mutex);
        if (sink->open) {
            uint32_t samples =
                (uint32_t) SCA_PCM_CALLBACK_PERIOD_MS * sink->sample_rate
                                                      / 1000;
            size_t bytes = samples * sink->sample_size;
            assert(bytes <= sizeof(buf));
            sc_audio_regulator_pull(&sink->reg, buf, samples);
            sc_mutex_unlock(&sink->mutex);

            s->settings.pcm_cb(buf, (uint32_t) bytes, s->settings.userdata);
        } else {
            sc_mutex_unlock(&sink->mutex);
        }
    }
}

// ---------------------------------------------------------------------------
// Server and demuxer callbacks
// ---------------------------------------------------------------------------

static void
sca_server_on_connection_failed(struct sc_server *server, void *userdata) {
    (void) server;
    struct sca_session *s = userdata;

    sc_mutex_lock(&s->mutex);
    s->connection_failed = true;
    sc_cond_broadcast(&s->cond);
    sc_mutex_unlock(&s->mutex);
}

static void
sca_server_on_connected(struct sc_server *server, void *userdata) {
    (void) server;
    struct sca_session *s = userdata;

    sc_mutex_lock(&s->mutex);
    s->connected = true;
    sc_cond_broadcast(&s->cond);
    sc_mutex_unlock(&s->mutex);
}

static void
sca_server_on_disconnected(struct sc_server *server, void *userdata) {
    (void) server;
    (void) userdata;
    // The disconnection is handled by the demuxer end-of-stream
}

static void
sca_demuxer_on_ended(struct sc_demuxer *demuxer,
                     enum sc_demuxer_status status, void *userdata) {
    (void) demuxer;
    struct sca_session *s = userdata;

    sc_mutex_lock(&s->mutex);
    s->demuxer_ended = true;
    s->demuxer_status = status;
    sc_cond_broadcast(&s->cond);
    sc_mutex_unlock(&s->mutex);
}

// ---------------------------------------------------------------------------
// Session thread
// ---------------------------------------------------------------------------

static int
run_session(void *data) {
    struct sca_session *s = data;

    // Wait for the server connection (or failure/stop)
    sc_mutex_lock(&s->mutex);
    while (!s->connected && !s->connection_failed && !s->stop_requested) {
        sc_cond_wait(&s->cond, &s->mutex);
    }
    bool failed = s->connection_failed;
    bool stopped = s->stop_requested;
    sc_mutex_unlock(&s->mutex);

    if (failed) {
        LOGE("Server connection failed");
        sca_emit_event(s, SCA_EVENT_CONNECTION_FAILED);
        goto end;
    }
    if (stopped) {
        goto end;
    }

    // Connected
    memcpy(s->device_name, s->server.info.device_name,
           sizeof(s->device_name));
    s->device_name[sizeof(s->device_name) - 1] = '\0';
    LOGI("Device connected: %s", s->device_name);
    sca_emit_event(s, SCA_EVENT_CONNECTED);

    assert(s->server.audio_socket != SC_SOCKET_NONE);

    static const struct sc_demuxer_callbacks demuxer_cbs = {
        .on_ended = sca_demuxer_on_ended,
    };
    sc_demuxer_init(&s->demuxer, "audio", s->server.audio_socket,
                    &demuxer_cbs, s);

    sc_decoder_init(&s->decoder, "audio");
    sc_packet_source_add_sink(&s->demuxer.packet_source,
                              &s->decoder.packet_sink);

    sca_pcm_sink_init(&s->sink);
    sc_frame_source_add_sink(&s->decoder.frame_source,
                             &s->sink.frame_sink);

    if (!sc_demuxer_start(&s->demuxer)) {
        sca_emit_event(s, SCA_EVENT_ERROR);
        goto end;
    }
    s->demuxer_started = true;

    if (s->settings.pcm_cb) {
        s->pcm_thread_started = sc_thread_create(&s->pcm_thread,
                                                 run_pcm_feeder,
                                                 "sca-pcm", s);
        if (!s->pcm_thread_started) {
            LOGE("Could not start PCM feeder thread");
            sca_emit_event(s, SCA_EVENT_ERROR);
            goto end;
        }
    }

    // Wait for the stream to end or a stop request
    sc_mutex_lock(&s->mutex);
    while (!s->demuxer_ended && !s->stop_requested) {
        sc_cond_wait(&s->cond, &s->mutex);
    }
    bool ended = s->demuxer_ended;
    enum sc_demuxer_status status = s->demuxer_status;
    stopped = s->stop_requested;
    sc_mutex_unlock(&s->mutex);

    if (ended && !stopped) {
        switch (status) {
            case SC_DEMUXER_STATUS_EOS:
                LOGI("Device disconnected");
                sca_emit_event(s, SCA_EVENT_DISCONNECTED);
                break;
            case SC_DEMUXER_STATUS_DISABLED:
                LOGW("Audio disabled by the device");
                sca_emit_event(s, SCA_EVENT_AUDIO_DISABLED);
                break;
            case SC_DEMUXER_STATUS_ERROR:
            default:
                LOGE("Audio demuxer error");
                sca_emit_event(s, SCA_EVENT_ERROR);
                break;
        }
    }

end:
    // Shut down the sockets and kill the device-side server; this unblocks
    // the demuxer if it is still receiving
    sc_server_stop(&s->server);

    if (s->pcm_thread_started) {
        // The feeder wakes up on stop_requested or demuxer_ended; both are
        // signalled by now or will be by the demuxer ending below.
        sc_mutex_lock(&s->mutex);
        sc_cond_broadcast(&s->cond);
        sc_mutex_unlock(&s->mutex);
    }

    if (s->demuxer_started) {
        sc_demuxer_join(&s->demuxer);
    }

    if (s->pcm_thread_started) {
        sc_thread_join(&s->pcm_thread, NULL);
        s->pcm_thread_started = false;
    }

    sc_server_join(&s->server);
    return 0;
}

// ---------------------------------------------------------------------------
// Settings helpers
// ---------------------------------------------------------------------------

static char *
sca_strdup_or_null(const char *s) {
    return s ? strdup(s) : NULL;
}

static void
sca_settings_free_copies(struct sca_settings *settings) {
    free((void *) settings->serial);
    free((void *) settings->adb_path);
    free((void *) settings->server_path);
    free((void *) settings->audio_codec);
    free((void *) settings->audio_source);
    free((void *) settings->audio_encoder);
    free((void *) settings->audio_codec_options);
    memset(settings, 0, sizeof(*settings));
}

static bool
sca_parse_audio_codec(const char *name, enum sc_codec *codec) {
    if (!name || !strcmp(name, "opus")) {
        *codec = SC_CODEC_OPUS;
    } else if (!strcmp(name, "aac")) {
        *codec = SC_CODEC_AAC;
    } else if (!strcmp(name, "flac")) {
        *codec = SC_CODEC_FLAC;
    } else if (!strcmp(name, "raw")) {
        *codec = SC_CODEC_RAW;
    } else {
        return false;
    }
    return true;
}

static bool
sca_parse_audio_source(const char *name, enum sc_audio_source *source) {
    if (!name || !strcmp(name, "auto")) {
        *source = SC_AUDIO_SOURCE_AUTO;
    } else if (!strcmp(name, "output")) {
        *source = SC_AUDIO_SOURCE_OUTPUT;
    } else if (!strcmp(name, "playback")) {
        *source = SC_AUDIO_SOURCE_PLAYBACK;
    } else if (!strcmp(name, "mic")) {
        *source = SC_AUDIO_SOURCE_MIC;
    } else if (!strcmp(name, "mic-unprocessed")) {
        *source = SC_AUDIO_SOURCE_MIC_UNPROCESSED;
    } else if (!strcmp(name, "mic-camcorder")) {
        *source = SC_AUDIO_SOURCE_MIC_CAMCORDER;
    } else if (!strcmp(name, "mic-voice-recognition")) {
        *source = SC_AUDIO_SOURCE_MIC_VOICE_RECOGNITION;
    } else if (!strcmp(name, "mic-voice-communication")) {
        *source = SC_AUDIO_SOURCE_MIC_VOICE_COMMUNICATION;
    } else if (!strcmp(name, "voice-call")) {
        *source = SC_AUDIO_SOURCE_VOICE_CALL;
    } else if (!strcmp(name, "voice-call-uplink")) {
        *source = SC_AUDIO_SOURCE_VOICE_CALL_UPLINK;
    } else if (!strcmp(name, "voice-call-downlink")) {
        *source = SC_AUDIO_SOURCE_VOICE_CALL_DOWNLINK;
    } else if (!strcmp(name, "voice-performance")) {
        *source = SC_AUDIO_SOURCE_VOICE_PERFORMANCE;
    } else {
        return false;
    }
    return true;
}

// Generate a scrcpy id to differentiate multiple running scrcpy instances
static uint32_t
sca_generate_scid(void) {
    struct sc_rand rand;
    sc_rand_init(&rand);
    // Only use 31 bits to avoid issues with signed values on the Java-side
    return sc_rand_u32(&rand) & 0x7FFFFFFF;
}

// ---------------------------------------------------------------------------
// Public API
// ---------------------------------------------------------------------------

void
sca_settings_init(struct sca_settings *settings) {
    memset(settings, 0, sizeof(*settings));
    settings->struct_size = sizeof(*settings);
    settings->log_level = SCA_LOG_INFO;
    settings->audio_buffer_ms = SCA_DEFAULT_AUDIO_BUFFER_MS;
}

int32_t
sca_start(const struct sca_settings *settings) {
    if (!settings || settings->struct_size != sizeof(*settings)) {
        return -1; // ABI mismatch
    }
    if (settings->log_level > SCA_LOG_ERROR) {
        return -1;
    }

    struct sca_session *s = &g_session;
    if (s->in_use) {
        return -2; // already running (call sca_stop() first)
    }

    enum sc_codec audio_codec;
    if (!sca_parse_audio_codec(settings->audio_codec, &audio_codec)) {
        return -3; // invalid audio codec
    }
    enum sc_audio_source audio_source;
    if (!sca_parse_audio_source(settings->audio_source, &audio_source)) {
        return -4; // invalid audio source
    }
    if (audio_source == SC_AUDIO_SOURCE_AUTO) {
        // Resolve "auto" like the scrcpy CLI does (no camera here)
        audio_source = SC_AUDIO_SOURCE_OUTPUT;
    }

    if (!g_static_init_done) {
        if (!sc_mutex_init(&s->mutex) || !sc_cond_init(&s->cond)
                || !sc_mutex_init(&s->sink.mutex)) {
            return -5;
        }
        g_static_init_done = true;
    }

    // Reset per-session state
    s->connected = false;
    s->connection_failed = false;
    s->demuxer_ended = false;
    s->stop_requested = false;
    s->demuxer_started = false;
    s->thread_started = false;
    s->pcm_thread_started = false;
    s->device_name[0] = '\0';
    sca_pcm_sink_init(&s->sink);

    // Deep-copy the settings (the host may free its strings after this call)
    struct sca_settings *cfg = &s->settings;
    memset(cfg, 0, sizeof(*cfg));
    cfg->struct_size = sizeof(*cfg);
    cfg->serial = sca_strdup_or_null(settings->serial);
    cfg->adb_path = sca_strdup_or_null(settings->adb_path);
    cfg->server_path = sca_strdup_or_null(settings->server_path);
    cfg->audio_codec = sca_strdup_or_null(settings->audio_codec);
    cfg->audio_source = sca_strdup_or_null(settings->audio_source);
    cfg->audio_encoder = sca_strdup_or_null(settings->audio_encoder);
    cfg->audio_codec_options =
        sca_strdup_or_null(settings->audio_codec_options);
    cfg->audio_bit_rate = settings->audio_bit_rate;
    cfg->audio_buffer_ms = settings->audio_buffer_ms;
    cfg->output_buffer_ms = settings->output_buffer_ms;
    cfg->port_first = settings->port_first;
    cfg->port_last = settings->port_last;
    cfg->audio_dup = settings->audio_dup;
    cfg->log_level = settings->log_level;
    cfg->event_cb = settings->event_cb;
    cfg->log_cb = settings->log_cb;
    cfg->pcm_cb = settings->pcm_cb;
    cfg->userdata = settings->userdata;

    // Configure logging before anything can log
    g_log_cb = cfg->log_cb;
    g_log_userdata = cfg->userdata;
    SDL_SetLogOutputFunction(sca_sdl_log_output, NULL);
    av_log_set_callback(sca_av_log_callback);
    sc_set_log_level((enum sc_log_level) cfg->log_level);

    // Set paths natively (NOT via environment variables, which would leak
    // into child processes spawned later by the host application)
    if (cfg->adb_path && !sc_adb_set_executable(cfg->adb_path)) {
        LOGE("Could not set adb path");
        goto error_free_settings;
    }
    if (cfg->server_path && !sc_server_set_server_path(cfg->server_path)) {
        LOGE("Could not set scrcpy-server path");
        goto error_free_settings;
    }

    if (!g_net_init_done) {
        if (!net_init()) {
            goto error_free_settings;
        }
        g_net_init_done = true;
    }

    struct sc_server_params params = {0};
    params.scid = sca_generate_scid();
    params.req_serial = cfg->serial;
    params.log_level = (enum sc_log_level) cfg->log_level;
    params.video_codec = SC_CODEC_H264; // unused (video disabled)
    params.audio_codec = audio_codec;
    params.video_source = SC_VIDEO_SOURCE_DISPLAY;
    params.audio_source = audio_source;
    params.camera_facing = SC_CAMERA_FACING_ANY;
    params.port_range.first = cfg->port_first ? cfg->port_first
                                              : DEFAULT_LOCAL_PORT_RANGE_FIRST;
    params.port_range.last = cfg->port_last ? cfg->port_last
                                            : DEFAULT_LOCAL_PORT_RANGE_LAST;
    params.audio_bit_rate = cfg->audio_bit_rate;
    params.audio_codec_options = cfg->audio_codec_options;
    params.audio_encoder = cfg->audio_encoder;
    params.capture_orientation = SC_ORIENTATION_0;
    params.capture_orientation_lock = SC_ORIENTATION_UNLOCKED;
    params.display_ime_policy = SC_DISPLAY_IME_POLICY_UNDEFINED;
    // Fields where 0 is NOT the scrcpy default ("unset" sentinels); getting
    // these wrong sends bogus args to the server (e.g. screen_off_timeout=0
    // would make the device screen turn off instantly)
    params.screen_off_timeout = -1;
    params.min_size_alignment = 1;
    params.clipboard_autosync = true;  // irrelevant, control is disabled
    params.downsize_on_error = true;   // irrelevant, video is disabled
    params.vd_destroy_content = true;  // irrelevant, no virtual display
    params.vd_system_decorations = true;
    params.video = false;
    params.audio = true;
    params.audio_dup = cfg->audio_dup;
    params.control = false;
    params.power_on = false;
    params.cleanup = true;

    static const struct sc_server_callbacks cbs = {
        .on_connection_failed = sca_server_on_connection_failed,
        .on_connected = sca_server_on_connected,
        .on_disconnected = sca_server_on_disconnected,
    };
    if (!sc_server_init(&s->server, &params, &cbs, s)) {
        goto error_free_settings;
    }

    if (!sc_server_start(&s->server)) {
        sc_server_destroy(&s->server);
        goto error_free_settings;
    }

    s->in_use = true; // from now on, sca_stop() performs the cleanup

    s->thread_started = sc_thread_create(&s->thread, run_session,
                                         "sca-session", s);
    if (!s->thread_started) {
        LOGE("Could not start session thread");
        sca_stop();
        return -6;
    }

    return 0;

error_free_settings:
    sca_settings_free_copies(&s->settings);
    g_log_cb = NULL;
    return -5;
}

void
sca_stop(void) {
    struct sca_session *s = &g_session;
    if (!s->in_use) {
        return;
    }

    // Request the session thread to stop, then wait for it to tear
    // everything down
    sc_mutex_lock(&s->mutex);
    s->stop_requested = true;
    sc_cond_broadcast(&s->cond);
    sc_mutex_unlock(&s->mutex);

    // Unblock a server connection in progress
    sc_server_stop(&s->server);

    if (s->thread_started) {
        sc_thread_join(&s->thread, NULL);
        s->thread_started = false;
    } else {
        // The session thread never started; join the server directly
        sc_server_join(&s->server);
    }

    sc_server_destroy(&s->server);

    sca_settings_free_copies(&s->settings);
    g_log_cb = NULL;
    g_log_userdata = NULL;

    s->in_use = false;
}

int32_t
sca_get_format(uint32_t *sample_rate, uint32_t *channels,
               uint32_t *bits_per_sample) {
    struct sca_pcm_sink *sink = &g_session.sink;
    if (!g_static_init_done) {
        return -1;
    }

    int32_t ret = -1;
    sc_mutex_lock(&sink->mutex);
    if (sink->open) {
        if (sample_rate) {
            *sample_rate = sink->sample_rate;
        }
        if (channels) {
            *channels = sink->nb_channels;
        }
        if (bits_per_sample) {
            *bits_per_sample = 32;
        }
        ret = 0;
    }
    sc_mutex_unlock(&sink->mutex);
    return ret;
}

int32_t
sca_read(uint8_t *buffer, int32_t max_bytes) {
    if (max_bytes <= 0) {
        return 0;
    }

    struct sca_pcm_sink *sink = &g_session.sink;
    if (!g_static_init_done) {
        memset(buffer, 0, max_bytes);
        return max_bytes;
    }

    size_t filled = 0;
    sc_mutex_lock(&sink->mutex);
    if (sink->open) {
        uint32_t samples = (uint32_t) max_bytes / sink->sample_size;
        sc_audio_regulator_pull(&sink->reg, buffer, samples);
        filled = samples * sink->sample_size;
    }
    sc_mutex_unlock(&sink->mutex);

    // Pad any remainder (stream closed, or partial trailing sample) with
    // silence so the output stream stays continuous
    if (filled < (size_t) max_bytes) {
        memset(buffer + filled, 0, max_bytes - filled);
    }
    return max_bytes;
}

const char *
sca_get_device_name(void) {
    return g_session.device_name;
}
