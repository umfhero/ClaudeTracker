# Usage Limit Tracker — Desktop Widget Plan

A lightweight, Windows-native desktop widget that lives on your desktop like a sticky note
(apps sit on top of it), showing live Claude usage limits: how much you've used, how much is
left, and when each limit resets.

## Decisions (from Q&A)

| Decision | Choice |
|---|---|
| Data source | **Auto** — read Claude Code's local OAuth token, query Anthropic's usage endpoint. No manual tracking needed. |
| Services | **Claude** (claude.ai / Claude Code — limits are unified on Pro). Architecture keeps a provider interface so ChatGPT/Gemini manual trackers can be added later. |
| Tech stack | **C# / WPF on .NET 8** (SDK 8.0.412 already installed). No third-party UI frameworks. |
| Behaviors | Start with Windows, near-limit notifications, draggable + lock position. No tray icon — everything via right-click menu on the widget. |

## Data source (verified working on this PC)

- Token: `%USERPROFILE%\.claude\.credentials.json` → `claudeAiOauth.accessToken`
  (read-only; Claude Code refreshes it itself — we never write to this file).
- Endpoint: `GET https://api.anthropic.com/api/oauth/usage` with headers
  `Authorization: Bearer <token>` and `anthropic-beta: oauth-2025-04-20`.
- Response `limits[]` gives everything we need per limit:
  - `kind`: `session` (5-hour window), `weekly_all`, `weekly_scoped` (per-model, e.g. "Fable")
  - `percent` used, `resets_at` timestamp, `severity` (`normal` / warning states)
- Plan name ("pro") comes from the credentials file (`subscriptionType`).

### Failure states
- Credentials file missing → widget shows "Claude Code sign-in not found".
- Token rejected (401) → keep last data, show a "stale — last updated hh:mm" badge.
  (Token auto-heals next time Claude Code runs; we never attempt a refresh ourselves.)
- Network error → keep last data, show "offline" dot; retry on next poll.

## Widget behavior ("sticky note" mechanics)

- Borderless, rounded, semi-transparent dark card. No taskbar entry, hidden from Alt+Tab
  (`WS_EX_TOOLWINDOW`), never steals focus (`WS_EX_NOACTIVATE`).
- **Pinned to the desktop layer**: intercept `WM_WINDOWPOSCHANGING` and force
  `HWND_BOTTOM`, so every app window always sits on top of it — it can never come forward.
  If Show Desktop / Win+D minimizes it, it silently restores itself (without taking focus).
- **Drag anywhere** with left-mouse when unlocked; position persisted. **Lock position**
  toggle in the right-click menu.
- Right-click context menu: Refresh now · Lock position ✓ · Notifications ✓ ·
  Start with Windows ✓ · Exit.

## UI layout

```
┌──────────────────────────────┐
│ ● Claude                 PRO │
│                              │
│ Session (5h)             4%  │
│ ▓▓░░░░░░░░░░░░░░░░░░░░░░░░░  │
│ resets in 3h 12m             │
│                              │
│ Week — all models       26%  │
│ ▓▓▓▓▓▓▓░░░░░░░░░░░░░░░░░░░░  │
│ resets Wed 20:00             │
│                              │
│ Week — Fable            30%  │
│ ▓▓▓▓▓▓▓▓░░░░░░░░░░░░░░░░░░░  │
│ resets Wed 20:00             │
│              updated 21:04 ✓ │
└──────────────────────────────┘
```

- One row per limit returned by the API (rows appear/disappear automatically).
- Bar color by usage: normal → accent, ≥80% → amber, ≥95% → red.
- Reset text: "resets in 2h 14m" when < 24h away, otherwise "resets Wed 20:00".
- Countdown text re-renders every 30s locally; the API is only polled every 5 minutes
  (configurable), plus once on wake-from-sleep.

## Notifications

Custom lightweight toast (small topmost popup, bottom-right, auto-dismisses) — avoids the
~25 MB WinRT projection dependency native toasts would add. Fires once per limit per reset
period when usage crosses 80%, and again at 95%.

## Lightweight budget

- Single WPF process, framework-dependent single-file exe (a few hundred KB + shared .NET runtime).
- Target: < 50 MB RAM, ~0% CPU idle (two timers: 30s UI tick, 5min HTTP poll).
- Zero third-party NuGet packages.

## Project structure

```
src/UsageWidget/
  UsageWidget.csproj
  App.xaml / App.xaml.cs            entry point, single-instance guard
  MainWindow.xaml / .cs             widget card UI + drag/menu logic
  NotificationWindow.xaml / .cs     custom toast popup
  Interop/DesktopPinning.cs         Win32: bottom z-order pinning, no-activate
  Models/UsageSnapshot.cs           parsed limits (kind, percent, resets_at, severity)
  Services/ClaudeUsageProvider.cs   credentials read + HTTP fetch + JSON parse
  Services/SettingsService.cs       %APPDATA%\UsageWidget\settings.json (position, lock, poll, toggles)
  Services/StartupService.cs        HKCU Run key add/remove
```

## Implementation steps

1. Scaffold project (`csproj`, App, single-instance mutex).
2. `ClaudeUsageProvider` — credentials read, HTTP call, parse `limits[]` into models.
3. `MainWindow` — card UI, dynamic limit rows, bar coloring, countdown formatting.
4. Desktop pinning interop + drag/lock + position persistence.
5. Context menu: refresh, lock, notifications toggle, startup toggle (registry), exit.
6. Polling timers + wake-from-sleep refresh + stale/offline states.
7. Notification popup + threshold-crossing logic (once per reset period).
8. Build, run, verify against live data; publish single-file Release exe.

## Later (out of scope now)

- Manual-tracker providers for ChatGPT / Gemini (provider interface already in place).
- Optional extras: tray icon, multiple theme accents, opacity slider.
