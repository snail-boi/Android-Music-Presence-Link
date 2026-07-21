# scrcpy-audio

An audio-only port of [scrcpy](https://github.com/Genymobile/scrcpy) v4.1,
refactored into a Windows DLL (`scrcpy_audio.dll`) for the AMPL project.

Instead of playing device audio itself, the DLL decodes it to raw PCM and
hands it to the host application, which plays it with NAudio `WasapiOut`.
Windows therefore attributes the audio session to the host process, not to a
separate scrcpy.exe.

## What was kept / removed from scrcpy

Kept (audio pipeline, unmodified scrcpy sources):
- `server.c` + `adb/` — starts adb, pushes `scrcpy-server`, opens the tunnel
- `demuxer.c` — receives the audio packet stream from the device
- `decoder.c` — FFmpeg decoding (Opus/AAC/FLAC/raw)
- `audio_regulator.c` + `util/audiobuf.c` — scrcpy's buffering/clock-drift
  compensation (the "audio buffer" logic), unchanged
- `options.c`, `compat.c`, `trait/`, most of `util/`

Removed: everything video (screen, texture, opengl, frame buffer, v4l2),
SDL window/rendering, all input handling (keyboard/mouse/gamepad/HID/UHID/
USB/AOA), the controller/control socket, recorder, file pusher, CLI, icon,
and **SDL audio playback** (`audio_player.c` is replaced by a PCM sink).

New files:
- `src/scrcpy_audio.h` — the public C API (start / stop / settings /
  PCM read + optional PCM push callback)
- `src/scrcpy_audio.c` — session lifecycle (replaces `main.c`/`scrcpy.c`)
  and the PCM frame sink feeding the audio regulator
- `src/config.h` — hand-written replacement for the meson-generated one

SDL3 is still linked, but only for threads/mutexes/logging (scrcpy's
`util/thread.c` is built on it). No SDL subsystem is initialized, so SDL
never opens a window or an audio device.

## PCM format

Interleaved IEEE float32, 48000 Hz, stereo — exactly what the scrcpy audio
regulator produces. In NAudio: `WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)`.

The recommended integration is **pull**: call `sca_read()` from the WASAPI
render thread (an `IWaveProvider.Read`). `sca_read` always fills the buffer
(silence-padded before the stream starts / after it ends), and because the
consumer then runs at the sound card's real rate, scrcpy's drift compensation
works exactly as it does upstream. A push callback (`pcm_cb`, ~10 ms chunks)
is also available.

## Building

Requires [w64devkit](https://github.com/skeeto/w64devkit) and the dep
packages in `deps/` (see below):

```
set PATH=C:\path\to\w64devkit\bin;%PATH%
cd scrcpy-audio
make -j
```

Output: `build/scrcpy_audio.dll` (exports: `sca_settings_init`, `sca_start`,
`sca_stop`, `sca_get_format`, `sca_read`, `sca_get_device_name`).

`deps/` contains:
- `ffmpeg-n8.1-latest-win64-gpl-shared-8.1/` — FFmpeg 8.1 headers + import
  libs (BtbN builds); DLL majors (avcodec-62, avutil-60, swresample-6) match
  the DLLs shipped with scrcpy 4.x / in AMPL Assets
- `SDL3-3.4.12/` — SDL3 mingw devel package
- `scrcpy-server-v4.1` — official Genymobile server binary; **its version
  must match `SCRCPY_VERSION` in `src/config.h`**

Runtime dependencies next to the host app: `avcodec-62.dll`, `avutil-60.dll`,
`swresample-6.dll`, `SDL3.dll`, `adb.exe`, `scrcpy-server-v4.1`.

## Host-side integration (AMPL)

See `AndroidMusicPresence/Services/ScrcpyAudio/` in the AMPL repo:
- `ScrcpyAudioNative.cs` — P/Invoke bindings (loads the DLL from `Assets\`)
- `ScrcpyAudioPlayer.cs` — `Start(options)` / `Stop()`, session events,
  `Volume`, and the NAudio `WasapiOut` + `IWaveProvider` glue

```csharp
var player = new ScrcpyAudioPlayer();
player.SessionEvent += e => ...; // raised on background threads
player.Start(new ScrcpyAudioOptions { Serial = deviceSerial });
player.Volume = 0.5f;            // this process's own audio session
...
player.Stop();
```

## Testing

`test/test_host.c` is a minimal native smoke test:

```
gcc -std=c11 test/test_host.c -o build/test_host.exe build/scrcpy_audio.dll
build\test_host.exe <serial> <path-to-adb.exe> <path-to-scrcpy-server-v4.1>
```

(The runtime DLLs must be on PATH.)

## License and attribution

`scrcpy-audio` is a **derivative work of
[scrcpy](https://github.com/Genymobile/scrcpy)** (v4.1) and is distributed
under the **Apache License, Version 2.0** — the same license as scrcpy. The
full text is in [`LICENSE`](LICENSE).

    Copyright (C) 2018-2025 Genymobile
    Copyright (C) 2018-2025 Romain Vimont
    Copyright (C) 2026 snail-boi          (audio-only port modifications)

Most files under `src/` (`server.c`, `adb/`, `demuxer.c`, `decoder.c`,
`audio_regulator.c`, `options.c`, `compat.c`, `trait/`, `util/`, `sys/`) are
taken from scrcpy, unmodified or lightly modified. The modifications made for
this audio-only port are described in "What was kept / removed from scrcpy"
above (per Apache 2.0 §4(b)). The new files written for this port —
`src/scrcpy_audio.c`, `src/scrcpy_audio.h`, and `src/config.h` — are likewise
licensed under Apache 2.0.

The `scrcpy-server` binary pushed to the device (shipped in the host app as
`scrcpy-server-v4.1`) is the **unmodified** official Genymobile build.

This source is included to satisfy the corresponding-source obligation of the
host application (Android Music Presence Link, GPL-3.0), which bundles the
compiled `scrcpy_audio.dll`. Apache 2.0 is one-way compatible with GPL-3.0, so
there is no license conflict.
