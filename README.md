# Poe Ancients Price Helper

A lightweight screen overlay for **Path of Exile 2**. It watches a calibrated region of your screen,
reads the currency / reward list with OCR, looks up live prices from [poe.ninja](https://poe.ninja/poe2),
and draws a click-through price overlay next to each item — so you never have to alt-tab to check what a
stack is worth.

## Features

- **Live prices** next to each list row, sourced from poe.ninja (auto-refreshed every 30 minutes).
- **Stack-aware** — shows the total and the per-item price, e.g. `2 (0.5 each)`.
- **Uncut gems** (skill / spirit / support) priced by exact type **and level** — a row shows `?`
  rather than a guessed price if the gem type or level can't be read cleanly (neighbouring levels
  can differ several-fold, so a wrong-level price would be misleading).
- **GPU-accelerated capture** — uses Windows Graphics Capture (WGC) by default for low CPU usage,
  with automatic fallback to legacy GDI if WGC is unavailable.
- **Click-through overlay** that never gets in the way of the game.
- **One-time calibration** — just drag a box around the in-game list panel.
- **Hotkeys:** `F5` start/stop · `F4` recalibrate · `F3` debug boxes · `Esc` / `Ctrl+Click` hide.
- **Minimize to tray** — scanning keeps running in the background.

## Download & run

Grab the latest release from the
[**Releases**](../../releases) page, unzip it anywhere, and double-click **`Start.cmd`**.
No install and no .NET runtime required — it's a self-contained Windows x64 build.

> Windows SmartScreen may warn that the app is unsigned — click **More info → Run anyway**.

## Build from source

Requires the **.NET 10 SDK** and **Windows 10 version 2004+** / Windows 11.

```sh
# restore + build
dotnet build src/

# run tests
dotnet test src/PoeAncientsPriceHelper.Tests/

# build a self-contained release
dotnet publish src/PoeAncientsPriceHelper/ -c Release -r win-x64 --self-contained true -o publish
```

## Capture backend

The screen capture method is configurable via `config.json`:

| Value | Description |
|---|---|
| `"Auto"` (default) | Uses WGC (GPU-based) with automatic GDI fallback per frame |
| `"GDI"` | Forces legacy BitBlt capture (higher CPU, universal compatibility) |

WGC requires Windows 10 2004+. If WGC fails at runtime, the app silently falls back to GDI without
crashing.

## Tech

- **.NET 10** (`net10.0-windows10.0.19041.0`) — WPF (settings window) + WinForms (overlay)
- **Windows.Media.Ocr** OCR with 3x upscaling for glyph accuracy
- **Windows Graphics Capture** via Vortice.Direct3D11 + WinRT interop
- **poe.ninja** API for live price data (parallel fetch, 30-min auto-refresh)
- **SharpHook** for global hotkeys

## Support

If this tool saves you some alt-tabbing, there's a **Buy me a coffee** button right in the app.
Thanks!

## Disclaimer

Yes it was greatly helped by AI :D nevertheless it works and it's free!
