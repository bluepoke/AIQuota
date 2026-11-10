/// <reference path="./utils/common.js" />
/// <reference path="./utils/axios.js" />

// Talks to AIQuota over the small local HTTP bridge it exposes on
// 127.0.0.1 (see AIQuota/StreamDock/StreamDockBridge.cs and the "Show on
// Stream Dock controller" tray menu item, which must be enabled) rather
// than to the deck hardware directly.
const AIQUOTA_BASE_URL = "http://127.0.0.1:51477";
const AIQUOTA_HEADERS = { "X-AIQuota-Client": "streamdock-plugin" };
const POLL_INTERVAL_MS = 4000;

// Display style per key, picked in the property inspector (propertyInspector/usage/):
// "combined" (all bars + countdown ring, like the tray icon), "session", "week" or
// "credit". AIQuota renders the matching image (see AIQuota/StreamDock/DeckKeyStyle.cs).
const DEFAULT_STYLE = "combined";

// Flashing at a usage threshold, also set per key in the property inspector. Keys placed
// before these settings existed get the defaults: on, at 90% (the tray app's warning
// threshold).
const DEFAULT_FLASH_ENABLED = true;
const DEFAULT_FLASH_THRESHOLD = 90;

// The key alternates between its normal image and a dimmed copy every 500 ms - one flash
// per second, well below the 3 per second limit of WCAG 2.3.1. The alternate frame is only
// ever darker, never brighter, and dimming keeps a greyscale image (AIQuota's accessibility
// mode) greyscale, so it suits light-sensitive users too.
const FLASH_INTERVAL_MS = 500;
const FLASH_DIM_ALPHA = 0.6; // black overlay: the alternate frame keeps 40% of the brightness

// Holding a key at least this long acknowledges a flashing key instead of refreshing.
const LONG_PRESS_MS = 600;

// Per-key runtime state, by context. In memory only: after a restart a key that is still
// at or above its threshold flashes again.
//   frames:       { normal, dim } data URLs of the latest image
//   flashing:     the flash timer is running
//   showingDim:   the alternate frame is currently shown
//   acknowledged: long-pressed while flashing; cleared once the value drops below the threshold
//   keyDownAt:    time of the last keyDown, to tell a long press from a short one
const keys = {};

async function fetchStatus(style) {
    try {
        const { data } = await axios.get(`${AIQUOTA_BASE_URL}/status`, {
            params: { style },
            headers: AIQUOTA_HEADERS,
            timeout: 3000,
        });
        return data;
    } catch {
        return null; // AIQuota isn't running, or its bridge is disabled
    }
}

function requestRefresh() {
    axios.post(`${AIQUOTA_BASE_URL}/refresh`, null, {
        headers: AIQUOTA_HEADERS,
        timeout: 3000,
    }).catch(() => { });
}

function settingsOf(context) {
    const settings = plugin.usage.data[context] ?? {};
    const threshold = Number.parseInt(settings.flashThreshold, 10);
    return {
        style: settings.style ?? DEFAULT_STYLE,
        flashEnabled: settings.flashEnabled ?? DEFAULT_FLASH_ENABLED,
        flashThreshold: threshold >= 1 && threshold <= 100 ? threshold : DEFAULT_FLASH_THRESHOLD,
    };
}

// The value a key's flashing depends on: the one it shows, or for the combined key the
// higher of session and week. Null when there's nothing to compare - not logged in, or
// (credit key) the plan reports no credits.
function flashValue(style, status) {
    if (!status?.loggedIn) return null;
    switch (style) {
        case "session": return status.sessionPercent;
        case "week": return status.weeklyPercent;
        case "credit": return status.creditPercent ?? null;
        default: return Math.max(status.sessionPercent, status.weeklyPercent);
    }
}

// Decodes the PNG from AIQuota once and returns it as-is plus the dimmed flash frame, so
// the flash timer only has to send ready-made images.
function makeFrames(url) {
    return new Promise((resolve, reject) => {
        const image = new Image();
        image.onload = () => {
            const canvas = document.createElement("canvas");
            canvas.width = image.naturalWidth;
            canvas.height = image.naturalHeight;
            const ctx = canvas.getContext("2d");
            ctx.drawImage(image, 0, 0);
            const normal = canvas.toDataURL("image/png");
            ctx.fillStyle = `rgba(0, 0, 0, ${FLASH_DIM_ALPHA})`;
            ctx.fillRect(0, 0, canvas.width, canvas.height);
            resolve({ normal, dim: canvas.toDataURL("image/png") });
        };
        image.onerror = reject;
        image.src = url;
    });
}

function sendImage(context, image) {
    window.socket.send(JSON.stringify({
        event: "setImage",
        context,
        payload: { target: 0, image },
    }));
}

function showCurrentFrame(context) {
    const key = keys[context];
    if (!key?.frames) return;
    sendImage(context, key.flashing && key.showingDim ? key.frames.dim : key.frames.normal);
}

function flashTimerId(context) {
    return `${context}:flash`;
}

function startFlashing(context) {
    const key = keys[context];
    if (!key || key.flashing) return;
    key.flashing = true;
    key.showingDim = false;
    plugin.setInterval(flashTimerId(context), () => {
        const current = keys[context];
        if (!current?.flashing) return;
        current.showingDim = !current.showingDim;
        showCurrentFrame(context);
    }, FLASH_INTERVAL_MS);
}

function stopFlashing(context) {
    plugin.clearInterval(flashTimerId(context));
    const key = keys[context];
    if (!key?.flashing) return;
    key.flashing = false;
    key.showingDim = false;
    showCurrentFrame(context);
}

async function pushStatus(context) {
    const settings = settingsOf(context);
    const status = await fetchStatus(settings.style);
    const key = keys[context];
    if (!key) return; // the key disappeared while the request was running

    if (status) {
        try {
            key.frames = await makeFrames(status.image);
        } catch {
            // Keep the previous image if this one can't be decoded.
        }
        if (!keys[context]) return;
    }

    const value = flashValue(settings.style, status);
    const atThreshold = value !== null && value >= settings.flashThreshold;
    // The acknowledgement only ends once the value is known to be below the threshold -
    // not while AIQuota is unreachable or logged out.
    if (value !== null && !atThreshold) key.acknowledged = false;

    if (settings.flashEnabled && atThreshold && !key.acknowledged) {
        startFlashing(context);
        showCurrentFrame(context);
    } else if (key.flashing) {
        stopFlashing(context); // also shows the normal frame
    } else if (status) {
        showCurrentFrame(context);
    }
}

const plugin = new Plugins("aiquota");

plugin.usage = new Actions({
    default: {
        style: DEFAULT_STYLE,
        flashEnabled: DEFAULT_FLASH_ENABLED,
        flashThreshold: DEFAULT_FLASH_THRESHOLD,
    },
    _willAppear({ context }) {
        keys[context] = { frames: null, flashing: false, showingDim: false, acknowledged: false, keyDownAt: null };
        plugin.setInterval(context, () => pushStatus(context), POLL_INTERVAL_MS);
        pushStatus(context);
    },
    _willDisappear({ context }) {
        plugin.clearInterval(context);
        plugin.clearInterval(flashTimerId(context));
        delete keys[context];
    },
    // Sent when the property inspector saves new settings for this key (style, flashing,
    // threshold) - applied right away.
    didReceiveSettings({ context, payload: { settings } }) {
        this.data[context] = Object.assign({ ...this.default }, settings);
        pushStatus(context);
    },
    keyDown({ context }) {
        if (keys[context]) keys[context].keyDownAt = Date.now();
    },
    // A short press refreshes (as keyDown used to); a long press on a flashing key
    // acknowledges it - it stops flashing until its value has dropped below the threshold -
    // and doesn't refresh. A long press on a key that isn't flashing does nothing.
    keyUp({ context }) {
        const key = keys[context];
        if (!key) return;
        const heldMs = key.keyDownAt === null ? 0 : Date.now() - key.keyDownAt;
        key.keyDownAt = null;

        if (heldMs >= LONG_PRESS_MS) {
            if (key.flashing) {
                key.acknowledged = true;
                stopFlashing(context);
            }
            return;
        }

        requestRefresh();
        window.socket.send(JSON.stringify({ event: "showOk", context }));
        pushStatus(context);
    },
});
