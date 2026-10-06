**English** | [简体中文](README.zh-CN.md)

# Stream Deck Audio Switch

Switch the default playback device in Windows (e.g. headphones ↔ speakers) with a single press on your Stream Deck, or open the Sound control panel just as easily.

<img src="docs/keys.svg" alt="Three keys: headphones (in use), speakers, and sound settings" width="360">

## Features

| Action | Description |
|---|---|
| Switch to Headphones (切换到耳机) / Switch to Speakers (切换到音箱) | Each key is tied to one playback device; press it to switch to that device. The key for the device in use lights up and the others are grayed out; if you switch devices some other way in Windows, the keys stay in sync. |
| Single-Key Toggle (单键来回切换) | One key toggles back and forth between two devices; its icon shows the current one. |
| Open Sound Settings (打开声音设置) | Opens the classic Sound control panel (Playback tab); if it's already open, brings it to the front. |

- Holding down any switching key for 0.5 seconds also opens the Sound control panel.
- Switching sets the device as both the "Default Device" and the "Default Communication Device", the same as clicking "Set Default" in the Sound control panel.
- If a device isn't connected, its key shows a warning; if a device's ID changes, the plugin finds it again by name.

## Installation

Requires Windows 10/11 and the Stream Deck app, version 6.5 or later.

**Install directly:** Download `com.nisemonox.audioswitch.streamDeckPlugin` from [Releases](https://github.com/NiseMonox/streamdeck-audio-switch/releases/latest) and double-click the file to install it in Stream Deck.

**Build from source:** No extra runtimes or third-party tools to install; it's compiled with the .NET Framework C# compiler that ships with Windows.

```powershell
git clone https://github.com/NiseMonox/streamdeck-audio-switch.git
cd streamdeck-audio-switch
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Install
```

`-Install` compiles the plugin, copies it to `%APPDATA%\Elgato\StreamDeck\Plugins` and restarts Stream Deck; `-Package` instead creates a double-click-to-install `.streamDeckPlugin` in `dist\` (that's how the file on the Releases page is built).

Once it's installed, drag an action from the "音频切换" (Audio Switch) category on the right side of the Stream Deck app onto a key, then pick a device from the drop-down below. The plugin's UI text is in Chinese.

## Command line

The compiled `AudioSwitch.exe` also works on its own, e.g. for other tools to call:

```text
AudioSwitch.exe list                           List playback devices (* marks the current default)
AudioSwitch.exe set <device>                   Switch to a device
AudioSwitch.exe toggle <device A> <device B>   Toggle between two devices
AudioSwitch.exe panel                          Open the Sound control panel
```

`<device>` can be a device ID or part of a device name, e.g. `iFi`.

## How it works

- Uses the Core Audio API (`IMMDeviceEnumerator`, `IMMNotificationClient`) to list devices and listen for changes to the default device.
- Setting the default device relies on the undocumented `IPolicyConfig` interface. The Sound control panel uses it too, and it has worked since Windows 7.
- The plugin itself is a C# program ([`src/AudioSwitch.cs`](src/AudioSwitch.cs), C# 5 / .NET Framework 4.x) that talks to Stream Deck over the Stream Deck SDK v2 WebSocket protocol; the settings UI is [`pi/inspector.html`](com.nisemonox.audioswitch.sdPlugin/pi/inspector.html).
- The runtime log is written to `AudioSwitch.log` in the installed plugin's folder.

## License

[MIT](LICENSE)
