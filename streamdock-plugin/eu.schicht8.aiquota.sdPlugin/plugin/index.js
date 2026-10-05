/// <reference path="./utils/common.js" />
/// <reference path="./utils/axios.js" />

// Talks to AIQuota over the small local HTTP bridge it exposes on
// 127.0.0.1 (see AIQuota/StreamDock/StreamDockBridge.cs and the "Show on
// Stream Dock controller" tray menu item, which must be enabled) rather
// than to the deck hardware directly.
const AIQUOTA_BASE_URL = "http://127.0.0.1:51477";
const AIQUOTA_HEADERS = { "X-AIQuota-Client": "streamdock-plugin" };
const POLL_INTERVAL_MS = 4000;

async function fetchStatus() {
    try {
        const { data } = await axios.get(`${AIQUOTA_BASE_URL}/status`, {
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

async function pushStatus(context) {
    const status = await fetchStatus();
    if (status) window.socket.setImage(context, status.image);
}

const plugin = new Plugins("aiquota");

plugin.usage = new Actions({
    default: {},
    _willAppear({ context }) {
        plugin.setInterval(context, () => pushStatus(context), POLL_INTERVAL_MS);
        pushStatus(context);
    },
    _willDisappear({ context }) {
        plugin.clearInterval(context);
    },
    keyDown({ context }) {
        requestRefresh();
        window.socket.send(JSON.stringify({ event: "showOk", context }));
        pushStatus(context);
    },
});
