# Claude Usage Limit Tracker

A Windows native desktop widget that lives on your desktop like a sticky note, every app
window sits **on top** of it, showing your live Claude usage limits: how much you've used,
how much is left, and exactly when each limit resets.

> **Lightweight**: a 191 KB exe, ~63 MB private RAM, ~0% idle CPU, zero third party
> packages. Total CPU used since launch: under 1 second.

## Screenshots

| Dark theme | Light theme |
|---|---|
| ![Dark theme](assets/dark.png) | ![Light theme](assets/light.png) |

## What it does

One meter per limit on your Claude plan, straight from Anthropic's usage endpoint,
showing the same numbers claude.ai displays:

* **Session (5h)**: the rolling five hour window
* **Week · all models**: the weekly cap
* **Week · \<model\>**: weekly caps for individual models (Fable, for example), when your plan has them

Each row shows percent used, percent left, and a live reset countdown
("resets in 4h 40m", or "resets Wed 20:00" when further out). Meters are coral normally,
turn **amber at 80%** and **red at 95%**, and a toast notification fires once per reset
period at each of those thresholds.

## How it works

* Reads Claude Code's local OAuth token from `%USERPROFILE%\.claude\.credentials.json`.
* Polls `https://api.anthropic.com/api/oauth/usage` every 5 minutes, plus once on
  waking from sleep. No scraping or manual logging.
* If the saved sign in has expired, the widget renews it itself with the same refresh
  grant Claude Code uses and saves the new token back for Claude Code, so it loads with
  live data straight from PC startup without Claude Code ever being opened.
* If anything else goes wrong it degrades gracefully: it keeps the last data and shows
  `stale` or `offline` in the footer, or a hint if Claude Code has never signed in on
  the PC.

## The sticky note trick

The window is forced to the bottom of the z order whenever anything tries to raise it
(`WM_WINDOWPOSCHANGING` is intercepted and the window is pinned at `HWND_BOTTOM`), so it
can never cover another app. It has no taskbar entry, is hidden from Alt+Tab, never
steals focus, and quietly restores itself if Show Desktop minimises it.

## Usage

* **Drag** it anywhere with the left mouse button (position is remembered).
* **Right click** for the menu:
  * Refresh now
  * Lock position
  * Notifications
  * Light theme
  * Gemini API key
  * Start with Windows
  * Exit

Settings are stored in `%APPDATA%\UsageWidget\settings.json`.

## Daily project idea (optional)

Add a free Gemini API key (right click the widget, then Gemini API key) and the widget
shows one short project idea at the bottom each day, phrased as a little spark like
"What if we could make something that finds duplicate photos". The API is called once
per day at most, so a free key never gets tired, and the day's idea is cached locally.
If Google ever retires the model it uses, the widget finds a current one by itself.
Refresh now in the right click menu also fetches a fresh idea on the spot.
Without a key the widget stays exactly as compact as before. If the API call fails, the
widget shows a small grey API error note in the footer and nothing else. Get a key at
[aistudio.google.com/apikey](https://aistudio.google.com/apikey).

## Requirements

* Windows 10 or 11.
* The [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) to run
  (Windows offers the download automatically if it's missing); the .NET 8 SDK to build.
* **Claude Code installed and signed in** with a claude.ai subscription (Pro or Max) on
  the same PC. The widget reads Claude Code's local sign in to fetch your limits. Without
  it, the widget starts fine but tells you no Claude Code sign in was found. API key,
  Bedrock and Vertex sign ins have no subscription limits and won't work.

## Download

Grab `UsageWidget.exe` from the [latest release](../../releases/latest) and run it.
Then open the right click menu and tick *Start with Windows* to make it permanent.

## Building

Requires the .NET 8 SDK.

```powershell
# Run from source
dotnet run --project src\UsageWidget

# Publish a single file exe to .\publish\
dotnet publish src\UsageWidget\UsageWidget.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

Then run `publish\UsageWidget.exe`.

## Notes

* The usage endpoint is undocumented, so a future change on Anthropic's side would show
  up as a persistent `offline` or `stale` state rather than a crash, and would be a
  small fix here.
* ChatGPT and Gemini don't offer usage APIs for consumer plans; the provider interface
  in `src/UsageWidget/Services` is ready for manual tracker providers if they're ever
  wanted.
