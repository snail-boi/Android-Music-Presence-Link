# Android Music Presence Link

Share what is currently playing on your Android device to Windows, and optionally forward it to Discord using [MusicPresence](https://github.com/ungive/discord-music-presence).
Built primarily to work alongside [MusicPresence](https://github.com/ungive/discord-music-presence), but can also be used independently.

---

<!-- Replace the paths below with actual screenshot paths once available -->
<p>
  <img src="docs/mediaplayer_settings_collapsed.png" width="48%" />
  <img src="docs/mediaplayer_settings_opened.png" width="48%" />
</p>

---

## Who Is This For?

This app is made for:

- People who download all their music and use offline players
- People who dislike keeping two copies of their music, one on a phone and one on a PC
- People who want to share their currently playing media from their phone

If you mainly listen offline and want Discord Rich Presence without syncing your entire library to Windows, this is for you.

---

## Features

### Media Presence Forwarding

- Passes currently playing media from Android to Windows
- Can be forwarded to Discord using MusicPresence
- Reads title, artist, and album from the active Android media session
- Forwards presence to Windows SMTC (System Media Transport Controls), making it available to any app that reads SMTC

### Cover Art

- Automatically fetches album art from your phone and caches it locally
- Supports embedded cover art in audio files (extracted via ffmpeg)
- Supports folder-level cover images (e.g. `cover.jpg`, `cover.png`, `folder.jpg`)
- Cover filename patterns are configurable
- Configurable cache size limit with automatic eviction

### Lyrics

- Pulls `.lrc` lyrics files from your phone over ADB
- Displays synced (timestamped) lyrics as a floating overlay on your desktop
- Also shows lyrics inline inside the media player window
- Plain-text lyrics files (no timestamps) are supported as a fallback
- Lyrics are cached locally to avoid re-fetching on every track change
- A custom lyrics folder override can be configured separately from music folders

### Audio Link

- Streams audio from your Android device to your Windows PC in real time using scrcpy
- Customizable options:
  - Encoder selection (raw PCM, Opus, FLAC, AAC, and others detected from your device)
  - Buffer size control
  - Bitrate settings
  - FLAC compression level
- Built-in quality presets: Data Saver, Default, High, Lossless, Max
- Custom values are saved and labeled as "Custom" in the UI

### Controls

- Transport controls: play/pause, next, previous
- Skip back/forward 30 seconds (shown for tracks longer than 10 minutes)
- Volume control: a slider when the audio link is active, or +/- step buttons otherwise
- Configurable global hotkeys for:
  - Volume up and volume down
  - Starting and stopping the audio link
  - Toggling the lyrics overlay
  - Copying current track info to the clipboard
- Hotkey modifier is configurable (Shift, Ctrl, or Alt)
- Copy-to-clipboard template is customizable (supports `{artist}`, `{title}`, `{album}`)

### Interface

- Two view modes: a full settings window and a compact media player window
- Media player view shows cover art, animated gradient background extracted from the album art, track info, progress bar, and transport controls
- Inline lyrics panel replaces the cover art area when toggled
- Dark mode and light mode
- Always-on-top toggle
- Hide-decorations toggle (removes title bar and borders; window stays movable and resizable)
- Connection status pill showing USB/Wi-Fi state
- Audio quality button showing current preset, with a popup to switch presets without opening settings
- System tray icon with state-specific icons for USB, Wi-Fi, audio link active, and no device
- Now Playing shown in the tray tooltip and tray menu
- Start minimized to tray option
- Start with Windows option
- Debug logging with a log folder shortcut in settings
- SMTC paused-clear delay: clears the Windows media session after a configurable number of minutes when paused (0 = disabled)

### Connection

- USB connection support
- Optional Wi-Fi connection via either classic ADB `tcpip` or Android 11+ Wireless Debugging
- Wireless Debugging pairing support with mDNS-based reconnects after the device restarts or changes IP
- Automatic Wi-Fi recovery when the port is lost, prompting you to reconnect USB and re-establish the wireless bridge
- Auto-detect button in setup that finds your device, optionally configures Wi-Fi, and asks for a friendly device name

### Onboarding

- Step-by-step setup wizard covering USB debugging, device connection, Wireless Debugging pairing, music folders, allowed apps, hotkeys, and startup options

---

## Devices tested and known working

- Redmi note 13
- Redmi note 15
- Samsung s25 edge
- Samsung s10 (Audio link doesn't work due to android 12)

---

## Requirements

- Android phone running **Android 13 or newer** (will work for 12 and 11, but audio link will not function and other parts may cease to function right)
- Android 14 and 15 are currently untested. If you encounter any issues on those versions, please open an issue.
- No Apple or iOS support, and no plans for it
- USB debugging must be enabled on your phone
- Uses ADB (Android Debug Bridge) to establish the connection
- Wireless Debugging mode requires Android 13+
- scrcpy is required for the Audio Link feature

Without USB debugging enabled, the app will not work.

---

## Known Issues and Compatibility

### Media Player Compatibility

- Fully tested with Musicolet
- Most Android music players should work if they expose media session data
- If a player does not work, please open an issue

### Folder Structure

Tested structure:

```
Main Folder
├── Random Unsorted Music
└── Album Name
    ├── track1.mp3
    ├── track2.mp3
    └── cover.png or cover.jpg
```

Notes:

- No guarantee for deeply nested folder structures
- of title and filename do not match then wrong or no cover will be pulled
- If all songs contain embedded cover art in metadata, it should work reliably
- Multiple music root folders can be configured

### Seeking

- The progress bar is read-only. Arbitrary seeking from Windows to Android is not supported over ADB.
- The app ticks the position forward manually between polls. The 30-second skip buttons use ADB media key events, which let the playing app handle the actual seek amount.

### Wireless Mode

- Wireless mode may have stability issues on some mobile devices, especially laptops

---

## Why This Exists

Many users:

- Keep their music offline
- Prefer Android music players
- Do not want to duplicate large libraries on their PC
- Still want Discord Rich Presence integration

This project bridges that gap.
