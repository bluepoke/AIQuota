# AIQuota Stream Dock plugin

Shows AIQuota's session/weekly/credit usage on a key of a Stream Dock-protocol
controller (Soomfon "Stream Controller", Ajazz, Mirabox, ... - these are the
same hardware/software family under different brands) and refreshes AIQuota
when that key is pressed.

## How it works

```
Stream Dock host app  <-- WebSocket -->  eu.schicht8.aiquota.sdPlugin (this plugin, Python)
                                                     |
                                                     | HTTP, 127.0.0.1:51477
                                                     v
                                              AIQuota.exe (StreamDockBridge.cs)
```

- AIQuota renders the same bars/ring it already draws for the tray icon as a
  200x200 PNG and serves it, plus the raw percentages, from a small
  loopback-only HTTP endpoint (`GET /status`) - see
  [`AIQuota/StreamDock/StreamDockBridge.cs`](../AIQuota/StreamDock/StreamDockBridge.cs).
  A `POST /refresh` there triggers the same refresh the tray's "Refresh now"
  menu entry does.
- This plugin is a normal Stream Dock plugin (per the
  [official SDK](https://github.com/MiraboxSpace/StreamDock-Plugin-SDK) /
  [docs](https://sdk.key123.vip/en/)) that polls `/status` every 4 seconds
  while its key is visible and pushes the image it gets back onto the key via
  `setImage`, and calls `/refresh` on `keyDown`.
- The two only talk to each other via `127.0.0.1` - no pairing, no vendor
  HID/USB protocol, no Companion/OpenDeck middleware.

In AIQuota, enable the bridge from the tray menu: **"Show on Stream Dock
controller"** (off by default, since it opens a local port).

## Setup

1. In AIQuota's tray menu, enable **"Show on Stream Dock controller"**.
2. Build the plugin executable (Windows, since that's what `manifest.json`'s
   `CodePathWin` points at):
   ```
   cd streamdock-plugin/eu.schicht8.aiquota.sdPlugin
   pip install -r requirements.txt pyinstaller
   pyinstaller main.spec
   copy dist\aiquota_plugin.exe .
   ```
3. Copy the whole `eu.schicht8.aiquota.sdPlugin` folder (with
   `aiquota_plugin.exe` now inside it) into the Stream Dock host app's plugin
   directory:
   ```
   %AppData%\HotSpot\StreamDock\plugins\
   ```
4. Restart the Stream Dock host app (it only scans the plugins folder on
   startup). The action shows up as "AIQuota" / "Claude Usage" in its action
   list - drag it onto a key.

## Known caveats

- Only verified against the publicly documented protocol (registration
  handshake, `willAppear`/`willDisappear`/`keyDown`, `setImage`), not against
  a physical Soomfon "Stream Controller Classic" - I don't have one to test
  end-to-end. If the key doesn't update, open the host app's plugin debug
  console at `http://localhost:23519/` and check this plugin's log output.
- `aiquota_plugin.exe` isn't checked in (it's a build artifact); run the
  PyInstaller step above after every change to `main.py`.
- The bridge's port (51477) is fixed and shared between
  `StreamDockBridge.cs` and `main.py` - if you change one, change the other.
