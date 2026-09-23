"""AIQuota Stream Dock plugin.

Mirrors AIQuota's tray icon (session / weekly / credit usage) onto a Stream
Dock key and lets pressing that key trigger a refresh. Talks to AIQuota over
the small local HTTP bridge it exposes on 127.0.0.1 (see
AIQuota/StreamDock/StreamDockBridge.cs and the "Show on Stream Dock
controller" tray menu item, which must be enabled) rather than to the deck
hardware directly.

Implements the plugin protocol documented at
https://sdk.key123.vip/en/guide/registration.html,
https://sdk.key123.vip/en/guide/events-received.html and
https://sdk.key123.vip/en/guide/events-sent.html directly - a plain
WebSocket JSON protocol - instead of depending on the official SDK's own
runtime classes, since this plugin only needs a single action.
"""

import argparse
import json
import threading
import urllib.error
import urllib.request

import websocket  # pip install websocket-client

AIQUOTA_PORT = 51477
AIQUOTA_HEADERS = {"X-AIQuota-Client": "streamdock-plugin"}
POLL_INTERVAL_SECONDS = 4


def fetch_status():
    """GET /status from AIQuota. Returns the parsed JSON dict, or None if
    AIQuota isn't running or its Stream Dock bridge is turned off."""
    request = urllib.request.Request(
        f"http://127.0.0.1:{AIQUOTA_PORT}/status", headers=AIQUOTA_HEADERS
    )
    try:
        with urllib.request.urlopen(request, timeout=3) as response:
            return json.loads(response.read())
    except (urllib.error.URLError, OSError, ValueError):
        return None


def request_refresh():
    request = urllib.request.Request(
        f"http://127.0.0.1:{AIQUOTA_PORT}/refresh",
        headers=AIQUOTA_HEADERS,
        method="POST",
    )
    try:
        urllib.request.urlopen(request, timeout=3).close()
    except (urllib.error.URLError, OSError):
        pass


class AiquotaPlugin:
    def __init__(self, port, plugin_uuid, register_event):
        self._plugin_uuid = plugin_uuid
        self._register_event = register_event
        self._ws = websocket.WebSocketApp(
            f"ws://127.0.0.1:{port}",
            on_open=self._on_open,
            on_message=self._on_message,
            on_close=self._on_close,
        )
        self._send_lock = threading.Lock()
        self._contexts = set()  # button instances currently visible on a device
        self._poll_stop = threading.Event()
        self._poll_thread = None

    def run(self):
        self._ws.run_forever()

    def _send(self, message):
        with self._send_lock:
            self._ws.send(json.dumps(message))

    def _on_open(self, _ws):
        self._send({"event": self._register_event, "uuid": self._plugin_uuid})

    def _on_close(self, _ws, *_args):
        self._poll_stop.set()

    def _on_message(self, _ws, raw_message):
        message = json.loads(raw_message)
        event = message.get("event")
        context = message.get("context")

        if event == "willAppear":
            self._contexts.add(context)
            self._ensure_polling()
            self._push_status(context)
        elif event == "willDisappear":
            self._contexts.discard(context)
            if not self._contexts:
                self._poll_stop.set()
        elif event == "keyDown":
            request_refresh()
            self._send({"event": "showOk", "context": context})
            self._push_status(context)

    def _ensure_polling(self):
        if self._poll_thread is not None and self._poll_thread.is_alive():
            return
        self._poll_stop.clear()
        self._poll_thread = threading.Thread(target=self._poll_loop, daemon=True)
        self._poll_thread.start()

    def _poll_loop(self):
        while not self._poll_stop.is_set():
            for context in list(self._contexts):
                self._push_status(context)
            self._poll_stop.wait(POLL_INTERVAL_SECONDS)

    def _push_status(self, context):
        status = fetch_status()
        if status is None:
            return
        self._send(
            {
                "event": "setImage",
                "context": context,
                "payload": {"image": status["image"], "target": 0, "state": 0},
            }
        )


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("-port", type=int, required=True)
    parser.add_argument("-pluginUUID", required=True)
    parser.add_argument("-registerEvent", required=True)
    parser.add_argument("-info", required=False, default="{}")
    args = parser.parse_args()

    AiquotaPlugin(args.port, args.pluginUUID, args.registerEvent).run()


if __name__ == "__main__":
    main()
