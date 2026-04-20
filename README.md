#old detection setting to remind myself how bad i am at coding 

# Android Music Presence Link

Share what is currently playing on your Android device to Windows, and optionally forward it to Discord using MusicPresence.

Built primarily to work alongside MusicPresence, but can also be used independently.

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

### Audio Link

- Establishes a live audio link from Android to Windows  
- Customizable options including:
  - Encoder selection  
  - Buffer size control  
  - Bitrate settings  

### Controls

- Customizable keybinds for:
  - Starting and stopping the audio link  
  - Adjusting the audio link volume independently without affecting system volume  

### Interface

- Light mode  
- Dark mode  

---

## Requirements

- Android phone  
- No Apple or iOS support, and no plans for it  
- USB debugging must be enabled  
- Uses ADB (Android Debug Bridge) to establish the connection  

Without USB debugging enabled, the app will not work.

---

## Known Issues and Compatibility

### Media Player Compatibility

- Fully tested with Musicolet  
- Most Android music players should work if they expose media session data  
- If a player does not work, please open an issue  

### Folder Structure

Tested structure:

Main Folder
├── Random Unsorted Music
└── Albums
└── Album Name
├── track1.mp3
├── track2.mp3
└── cover.png or cover.jpg



Notes:

- No guarantee for deeply nested folder structures  
- If all songs contain embedded cover art in metadata, it should work reliably  

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
