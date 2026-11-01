# R Browser

A small Windows browser: WebView2 content, a hand-drawn dark-glass chrome, and an
ad blocker that also defeats anti-adblock walls. No Electron, no bundled engine —
it rides the Edge runtime that already ships with Windows 10/11.

## Why it is light

| | R Browser |
|---|---|
| Engine | Microsoft Edge WebView2 (shared platform runtime) |
| Shell | WinForms, one window, custom painting (no Chromium UI, no Node) |
| Idle footprint | ~165 MB private for the whole browser on the new-tab page |
| Blocking | In-process filter engine, no proxy, no extension host, no background service |
| Tabs | shared environment, capped renderer count, background tabs suspended |

Hardware acceleration is off by default (saves ~100 MB of private memory); turn it
on in Settings if you prefer smoother scrolling and video.

## Features

- Tabs (Ctrl+T, Ctrl+W, Ctrl+Tab, Ctrl+Shift+T restore, Ctrl+1..9 switch), audio indicator & one-click mute, right-click tab menu, session restore, drag-to-move, resize, maximised and fullscreen
- Address bar with history suggestions, search fallback, `r1://newtab`, `r1://settings`
- Bookmarks, history, downloads, side panel, find-in-page (Ctrl+F), zoom (Ctrl +/-/0)
- Incognito window (`--incognito`, Ctrl+Shift+N) with a throwaway profile
- Dark context menus, DevTools (F12), per-site ad-block allow-list
- Native multi-stream download acceleration: bypasses server per-connection speed caps (MediaFire, Google Drive, MegaUp) by opening parallel HTTP Range streams straight to disk with zero RAM buffering
- Wi-Fi & ISP tracking defense: Encrypted DNS (DNS-over-HTTPS via Cloudflare/Quad9) and Encrypted Client Hello (ECH) to prevent local network packet snooping
- Deep memory optimization: bounded disk/media caches, idle tab suspension & hibernation, working set trimming
- Internal pages are plain HTML/CSS with no external requests
- Strictly governed by [CORE-PRINCIPLES.md](CORE-PRINCIPLES.md)

## Ad blocking

The bundled rule set is compiled from EasyList, EasyPrivacy, AdGuard's anti-adblock
list, uBO quick fixes, Peter Lowe's list and a small hand-written set:

- ~120k network rules matched on host suffix + path with proper inverted type filtering, allocation-free
- ~24k cosmetic rules (generic rules safely chunked + per-site) injected into the page
- YouTube video ad auto-skip and anti-adblock enforcement dismissal
- OAuth and payment button popup recognition (Google/Apple/GitHub/PayPal logins unblocked)
- blocked requests are answered locally with `403 Forbidden`
- anti-adblock: bait elements are kept measurable off-screen, detector globals
  (`canRunAds`, `FuckAdBlock`, `BlockAdBlock`, …) are spoofed, and
  "disable your ad blocker" overlays are removed at runtime

Filter lists can be refreshed from Settings, which rebuilds the compiled rule set
in the background and stores it in `%LOCALAPPDATA%\R1`.

## Build

### Windows
Requires the .NET 9 SDK and the WebView2 runtime (preinstalled on Windows 11).

```powershell
dotnet build -c Debug
dotnet run -c Debug -- https://example.com
```

### Linux (Mint / Ubuntu / GNOME)
Requires GTK 4, WebKitGTK 6.0, and .NET (8 or 9). See [README-LINUX.md](README-LINUX.md) for full instructions:

```bash
cd R1.Linux
dotnet build
dotnet run -- https://example.com
```

The filter data is embedded from `assets/filters.dat`; to regenerate it:

```powershell
node tools/build-filters.mjs
```

## Publish

```powershell
powershell -ExecutionPolicy Bypass -File tools/publish.ps1
```

Output lands in `dist\` as a self-contained x64 build (no .NET install needed).
`RBrowser.exe` in that folder is the app; `assets\` and `WebView2Loader.dll` must stay
next to it.

## Data

Everything lives in `%LOCALAPPDATA%\R1`: settings, bookmarks, history and the
WebView2 profile. Deleting that folder resets R Browser. Set `R1_LOG=1` to write
diagnostics to `r1.log` (off by default).

## Command line

```
RBrowser.exe [url]
RBrowser.exe --incognito [url]
```

## Test hooks

Used by `tools/*.ps1` while developing; all are off unless set:

| Variable | Effect |
|---|---|
| `R1_LOG=1` | append engine/process events to `%LOCALAPPDATA%\R1\r1.log` |
| `R1_DEBUG_PORT=9333` | expose the DevTools protocol on localhost for automation |
| `R1_OFFSCREEN=1` | place the window off the desktop (unattended capture) |
| `R1_GPU=0` / `R1_GPU=1` | force software / hardware rendering for a run |
