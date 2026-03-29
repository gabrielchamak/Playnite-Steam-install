# Silent Install — Playnite Plugin

> 🤖 **This project was built 100% with AI assistance.**

Install Steam games silently without leaving Playnite Fullscreen mode — no popups, no dialogs, no interaction required.

## Current Status

| Platform | Status |
|----------|--------|
| Steam | ✅ Fully supported |
| Epic Games | 🚧 Coming in a future release |
| Xbox / Microsoft Store | ❌ Not planned |

## How it works

When you press **Install** on a Steam game in Playnite, Silent Install does the following:

1. **Looks up the game** — queries the Steam Store API to get the official installation folder name
2. **Creates an `appmanifest` file** — writes a `appmanifest_APPID.acf` file directly into your steamapps folder with `StateFlags=1026`. This flag tells Steam that a download has been started and is pending
3. **Registers the library** — ensures your steamapps folder is registered in Steam's `libraryfolders.vdf` so both Steam and Playnite can track the game
4. **Restarts Steam silently** — Steam only reads appmanifest files at startup, so the plugin shuts down Steam and restarts it in the background (system tray only, no window)
5. **Triggers the download queue** — opens `steam://open/downloads` to make Steam process the pending download immediately
6. **Monitors until complete** — polls the appmanifest file every 5 seconds. When Steam sets `StateFlags=4` (fully installed), Playnite is notified and the game status updates automatically

The entire process runs in the background. You stay in Playnite Fullscreen the whole time.

## Requirements

- [Playnite](https://playnite.link) 10+
- Steam installed and logged in on the same machine

## Installation

1. Download the latest release from the [Releases](../../releases) page
2. Extract to `%APPDATA%\Roaming\Playnite\Extensions\SilentInstall\`
3. Restart Playnite

## Configuration

Go to **Extensions → Silent Install → Settings**:

| Setting | Description |
|---------|-------------|
| **Steam library** | The steamapps folder where games will be installed. All your Steam libraries are auto-detected from your Steam configuration. |
| **Use a custom path** | Check this to manually enter a steamapps path if your library isn't detected automatically. |

> **Tip:** If your target library doesn't appear in the dropdown, add it first in Steam → Settings → Storage, then restart Playnite.

## Usage

Press **Install** on any uninstalled Steam game in Playnite — Silent Install intercepts the button automatically.

You can also right-click any game → **Silent Install → Install silently**.

Works in both **Desktop** and **Fullscreen** (Ubiquity theme) modes.

## Building from source

```
build.bat
```

Copy `dist\` contents to `%APPDATA%\Roaming\Playnite\Extensions\SilentInstall\`.

## License

MIT
