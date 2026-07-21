// Minimal smoke-test host for scrcpy_audio.dll
// Starts an audio session, pulls PCM for a few seconds, reports peak level.

#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <windows.h>

#include "../src/scrcpy_audio.h"

static const char *
event_name(int32_t e) {
    switch (e) {
        case SCA_EVENT_CONNECTED: return "CONNECTED";
        case SCA_EVENT_CONNECTION_FAILED: return "CONNECTION_FAILED";
        case SCA_EVENT_STREAM_STARTED: return "STREAM_STARTED";
        case SCA_EVENT_STREAM_STOPPED: return "STREAM_STOPPED";
        case SCA_EVENT_DISCONNECTED: return "DISCONNECTED";
        case SCA_EVENT_AUDIO_DISABLED: return "AUDIO_DISABLED";
        case SCA_EVENT_ERROR: return "ERROR";
        default: return "?";
    }
}

static volatile LONG g_last_event = -1;

static void
on_event(int32_t event, void *userdata) {
    (void) userdata;
    printf("[event] %s\n", event_name(event));
    fflush(stdout);
    InterlockedExchange(&g_last_event, event);
}

static void
on_log(int32_t level, const char *message, void *userdata) {
    (void) userdata;
    printf("[log %d] %s\n", level, message);
    fflush(stdout);
}

int
main(int argc, char *argv[]) {
    struct sca_settings settings;
    sca_settings_init(&settings);
    settings.serial = argc > 1 ? argv[1] : NULL;
    settings.adb_path = argc > 2 ? argv[2] : NULL;
    settings.server_path = argc > 3 ? argv[3] : NULL;
    settings.log_level = SCA_LOG_DEBUG;
    settings.event_cb = on_event;
    settings.log_cb = on_log;

    printf("starting...\n");
    int32_t ret = sca_start(&settings);
    if (ret) {
        printf("sca_start failed: %ld\n", (long) ret);
        return 1;
    }

    // Wait up to 15 s for the stream to start (or fail)
    uint32_t rate = 0, ch = 0, bits = 0;
    int waited = 0;
    while (sca_get_format(&rate, &ch, &bits) != 0 && waited < 15000) {
        LONG ev = g_last_event;
        if (ev == SCA_EVENT_CONNECTION_FAILED || ev == SCA_EVENT_ERROR
                || ev == SCA_EVENT_AUDIO_DISABLED) {
            printf("session failed, giving up\n");
            sca_stop();
            return 2;
        }
        Sleep(100);
        waited += 100;
    }

    if (rate == 0) {
        printf("timed out waiting for the stream\n");
        sca_stop();
        return 3;
    }

    printf("format: %u Hz, %u channels, %u-bit float\n", rate, ch, bits);
    printf("device: %s\n", sca_get_device_name());

    // Pull 5 seconds of PCM in 10 ms chunks, track the peak level
    uint32_t chunk_frames = rate / 100;
    uint32_t chunk_bytes = chunk_frames * ch * 4;
    uint8_t *buf = malloc(chunk_bytes);
    float peak = 0.f;
    for (int i = 0; i < 500; ++i) {
        int32_t n = sca_read(buf, (int32_t) chunk_bytes);
        const float *f = (const float *) buf;
        for (int32_t s = 0; s < n / 4; ++s) {
            float v = fabsf(f[s]);
            if (v > peak) {
                peak = v;
            }
        }
        Sleep(10);
    }
    free(buf);

    printf("peak amplitude over 5 s: %f %s\n", peak,
           peak > 0.f ? "(audio flowing!)" : "(silence)");

    printf("stopping...\n");
    sca_stop();
    printf("stopped cleanly\n");
    return 0;
}
