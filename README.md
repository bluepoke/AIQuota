# AI Quota

A small Windows system tray app that shows how much of your Claude.ai Pro/Max plan
limits you've used — at a glance, without opening the Claude Desktop app.

![platform](https://img.shields.io/badge/platform-Windows-blue)
![.NET](https://img.shields.io/badge/.NET-9-512BD4)
![license](https://img.shields.io/badge/license-MIT-green)

~~[Get it from the Microsoft Store](https://apps.microsoft.com/detail/9NK00NML9J4F)~~

## What it does

The tray icon shows two stacked bars:

- **Top bar** — your current 5-hour session usage
- **Bottom bar** — your current 7-day (weekly) usage

Each bar is colour-coded (green ≤ 50%, yellow 50–80%, red > 80%) so you can tell your
usage level without reading a number. Hover over the icon for exact percentages plus
the time remaining until each window resets (e.g. `Session (5h): 37% (1.5h)`,
`Week: 82% (2d3h)`):

![Tooltip showing session and week usage](screenshots/claude_usage_tooltip.png)

Right-click for the full menu: signed-in account, exact percentages with reset day/time,
manual refresh, login/logout, run-at-startup toggle, a check-for-new-version toggle, and
a language switch (German/English):

![Right-click menu with account, usage, and app options](screenshots/claude_usage_rmb_menu.png)

The app gets this data the same way the official `claude` CLI does: by signing you in
with the same public OAuth client Claude Code uses, then calling the same
`/api/oauth/usage` endpoint. No API key, no scraping, no reading another app's stored
credentials — you log in once via your browser, and the token is encrypted on disk for
your Windows user account only.

If your usage crosses 90% for either window, you'll get a one-time balloon
notification.

## Installation

**Microsoft Store** (not yet available)
~~(recommended — handles installs and updates automatically):~~

~~- [Install via Microsoft Store Website](https://apps.microsoft.com/detail/9NK00NML9J4F)~~
~~- Install via Microsoft Store App: `ms-windows-store://pdp/?productid=9NK00NML9J4F`~~

Or, install manually from a GitHub Release — each release comes in two variants, pick one:

- **`AIQuota-<version>-win-x64.zip`** (self-contained) — no installer, no .NET
  runtime to install separately. Larger download, but works out of the box.
- **`AIQuota-<version>-win-x64-framework-dependent.zip`** (framework-dependent)
  — much smaller download, but requires the
  [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) to already
  be installed.

For the manual zip route, **Windows Defender currently notifies about an unknown
publisher** — the Microsoft Store version above doesn't have this problem.

Then:

1. Download your chosen zip from the
   [Releases](https://github.com/bluepoke/ClaudeUsage/releases) page.
2. Unzip it anywhere (e.g. `C:\Tools\AIQuota`).
3. Run `AIQuota.exe`.

The app has no visible window; look for its icon in the system tray (you may need to
click the "show hidden icons" `^` arrow the first time).

## Usage

1. On first run, the icon shows a grey `?` — you're not logged in yet.
2. Right-click the icon → **Log in with claude.ai…**. Your browser opens; approve the
   sign-in there.
3. The icon updates with your live usage bars. Right-click any time for exact numbers,
   or hover for a quick summary.
4. Optionally check **Run at Windows startup** so it's always there after a reboot.
5. Switch the display language under **Language** (German/English) — it defaults to
   your Windows UI language and remembers your choice.

The app polls for updated usage every 5 minutes while it's running (plus whenever you
double-click the icon or hit **Refresh now**), and refreshes its access token
automatically in the background.

**Automatically check for new version** (on by default) periodically checks GitHub
Releases for a newer version. When one is found, you'll get a one-time balloon
notification and a new menu entry (e.g. `New version available: v1.2.0`) — click either
to open the release page and download it yourself; the app doesn't download or install
anything on its own.

## Building from source

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download) and Windows.

```
git clone https://github.com/bluepoke/ClaudeUsage.git
cd ClaudeUsage
dotnet build
```

To produce a single-file release build like the ones in GitHub Releases:

```
# self-contained (no .NET runtime required on the target machine)
dotnet publish AIQuota -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish

# framework-dependent (smaller, requires the .NET 9 Desktop Runtime on the target machine)
dotnet publish AIQuota -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

Releases are built automatically by [`.github/workflows/release.yml`](.github/workflows/release.yml)
whenever a tag matching `v*.*.*` is pushed.

### MSIX package (Microsoft Store submission)

The same workflow also builds an unsigned `.msix` package for submission to the
Microsoft Store, from [`AIQuota/Package/AppxManifest.xml`](AIQuota/Package/AppxManifest.xml)
and the placeholder icons in [`AIQuota/Package/Assets`](AIQuota/Package/Assets/).
It's uploaded as a workflow artifact (not attached to the public GitHub Release), for
manual upload to [Partner Center](https://partner.microsoft.com/dashboard) — the Store
signs the package itself during certification, so no local code-signing certificate is
needed.

The app is registered in Partner Center as `PeterKossek.ClaudeUsageTray`, listed at
[apps.microsoft.com/detail/9NK00NML9J4F](https://apps.microsoft.com/detail/9NK00NML9J4F).
The only thing still worth doing manually: replace the placeholder icons in
`AIQuota/Package/Assets/` with real artwork (same file names/sizes).

## Privacy & security notes

- The app only requests the minimal OAuth scopes needed to read your usage
  (`user:inference user:profile`) — not the full scope set Claude Code itself uses.
- Your access/refresh token is stored encrypted (Windows DPAPI, current user only) in
  `%APPDATA%\AIQuota\`, and nowhere else.
- The app never reads Claude Desktop's or Claude Code's stored credentials — every
  login is a fresh, explicit sign-in you approve in your browser.

## About this project

This is a personal utility, not an official Anthropic product. It was built with the
help of [Claude Code](https://claude.com/claude-code).

## License

[MIT](LICENSE)
