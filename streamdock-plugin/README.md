# AIQuota Stream Dock plugin

Shows AIQuota's session/weekly/credit usage on a key of a Stream Dock-protocol
controller (Soomfon "Stream Controller", Ajazz, Mirabox, ... - these are the
same hardware/software family under different brands) and refreshes AIQuota
when that key is pressed.

## Display styles

There's one action, "AIQuota". Select a key with it and pick what that key
shows from the **Display** dropdown in the host app's settings panel, so you
can put several keys side by side:

| Style | Shows |
|---|---|
| **Combined** (default) | Same as the tray icon: all bars plus the session countdown ring (the ring follows AIQuota's "Show session countdown ring" setting) and the weekly pace marker (following "Show weekly pace marker"), drawn as two notches above and below the week bar's text. |
| **5h session** | The session usage as one large bar, with 5 countdown blocks underneath - one per hour left, following the same rules as the ring. Before a session starts, the bar is an outline and the blocks are dimmed. |
| **Week** | The weekly usage as one large bar, with the weekly pace marker as two notches at the bar's left and right edges (following AIQuota's "Show weekly pace marker" setting). |
| **Credit** | The usage credit balance as one large bar, or "–" when your plan reports none. |

Every style uses the same colours as the tray icon (green ≤ 50%, yellow
50–80%, red > 80%) and refreshes AIQuota when pressed. Keys placed before
styles existed keep showing the combined view.

## Flashing at a threshold

A key flashes while the value it shows is at or above a percentage you set:

| Style | Flashes based on |
|---|---|
| **Combined** | the higher of session and week |
| **5h session** | the session usage |
| **Week** | the weekly usage |
| **Credit** | the credit usage (never, if your plan reports no credits) |

- **Settings**: in the key's settings panel, **Flash when usage reaches
  threshold** (on by default) and **Flash threshold (%)** (1–100, default 90 -
  the same as AIQuota's warning notification). The threshold field is disabled
  while flashing is off. Changes apply right away. Keys placed before this
  version get the defaults.
- **How it flashes**: the key alternates every 500 ms between its normal image
  and a copy dimmed to 40% - one flash per second, below the WCAG limit of 3
  per second. It never switches to a brighter frame, and with AIQuota's
  accessibility mode on the dimmed frame stays greyscale.
- **When it stops**: as soon as the value drops below the threshold (e.g.
  after a reset), and while AIQuota isn't reachable or you're not logged in.
- **Stop it by holding the key**: hold a flashing key for at least 0.6 seconds.
  It stops flashing and shows its normal image, without refreshing. It stays
  quiet until its value has dropped below the threshold, and flashes again the
  next time the value reaches it. Holding one key doesn't affect other keys.
  This is kept in memory only: after the host app or the plugin restarts, a key
  that is still at or above its threshold flashes again.
- **Short press**: still refreshes, as before. Holding a key that isn't
  flashing does nothing.

## How it works

```mermaid
flowchart LR
    Host["Stream Dock host app\n(loads plugin/index.html\ndirectly in its own runtime)"] <-->|WebSocket| Plugin["plugin/index.js"]
    Plugin <-->|"HTTP, 127.0.0.1:51477"| Bridge["AIQuota.exe\n(StreamDockBridge.cs)"]
```

- AIQuota renders the key image as a 200x200 PNG and serves it, plus the raw
  percentages, from a small loopback-only HTTP endpoint
  (`GET /status?style=combined|session|week|credit`, defaulting to
  `combined`) - see
  [`AIQuota/StreamDock/StreamDockBridge.cs`](../AIQuota/StreamDock/StreamDockBridge.cs)
  and [`DeckKeyRenderer.cs`](../AIQuota/StreamDock/DeckKeyRenderer.cs).
  A `POST /refresh` there triggers the same refresh the tray's "Refresh now"
  menu entry does.
- This is a normal Stream Dock JavaScript plugin (per the
  [official SDK](https://github.com/MiraboxSpace/StreamDock-Plugin-SDK) /
  [docs](https://sdk.key123.vip/en/)): `manifest.json`'s `CodePath` points at
  `plugin/index.html`, which the Stream Dock host loads directly inside its
  own embedded runtime - there's no separate executable or build step. The
  plugin's JS (`plugin/index.js`) polls `/status` every 4 seconds for each
  visible key, with that key's style, and pushes the image it gets back onto
  the key via `setImage`, and calls `/refresh` on a short press (decided on
  `keyUp`, measured from `keyDown`), using the bundled `axios` HTTP client
  (`plugin/utils/axios.js`, vendored from the SDK's own template).
- Flashing is done entirely in the plugin: it compares the percentages from
  `/status` with the key's threshold, makes the dimmed frame by drawing the
  PNG onto a canvas, and runs a 500 ms timer per flashing key next to the
  4-second poll.
- The settings panel is the action's property inspector
  (`propertyInspector/usage/`, set as `PropertyInspectorPath` in
  `manifest.json`). It saves the choices as the key's `style`,
  `flashEnabled` and `flashThreshold` settings, which the plugin receives via
  `willAppear`/`didReceiveSettings`.
- The two only talk to each other via `127.0.0.1` - no pairing, no vendor
  HID/USB protocol, no Companion/OpenDeck middleware.

In AIQuota, enable the bridge from the tray menu: **"Show on Stream Dock
controller"** (off by default, since it opens a local port).

## Setup

1. In AIQuota's tray menu, enable **"Show on Stream Dock controller"**.
2. Copy the whole `eu.schicht8.aiquota.sdPlugin` folder as-is into the Stream
   Dock host app's plugin directory (no build step - it's plain JS/HTML):
   ```
   %AppData%\HotSpot\StreamDock\plugins\
   ```
3. Restart the Stream Dock host app (it only scans the plugins folder on
   startup). The action shows up as "AIQuota" / "Claude Usage" in its action
   list - drag it onto a key.
4. Optionally pick a **Display** style for the key in its settings panel (see
   [Display styles](#display-styles)); repeat with more keys for a separate
   session/week/credit view, and adjust [flashing](#flashing-at-a-threshold)
   if you like. When updating from an older version, replace the whole folder
   and restart the host app.

## Known caveats

- Only verified against the publicly documented protocol (registration
  handshake, `willAppear`/`willDisappear`/`keyDown`, `setImage`), not against
  a physical Soomfon "Stream Controller Classic" - I don't have one to test
  end-to-end. If the key doesn't update, open the host app's plugin debug
  console at `http://localhost:23519/` and check this plugin's log output.
- The bridge's port (51477) is fixed and shared between
  `StreamDockBridge.cs` and `plugin/index.js` - if you change one, change
  the other.
- The settings panel (property inspector), flashing and the long press are
  also only built against the SDK template, not tested on a physical device.
  If the settings panel stays empty, check the host app's debug console as
  above.
- `plugin/utils/common.js`, `plugin/utils/worker.js`,
  `plugin/utils/axios.js`, `propertyInspector/utils/common.js`,
  `propertyInspector/utils/action.js` and everything in `static/css/` are
  vendored verbatim from the SDK's own
  [JavaScript plugin template](https://github.com/MiraboxSpace/StreamDock-Plugin-SDK/tree/main/SDJavaScriptSDK/com.mirabox.streamdock.xxx.sdPlugin) -
  don't hand-edit them, re-fetch from there instead if the SDK updates.
