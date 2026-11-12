# Tab Cleaner

A small Windows taskbar (system tray) app that ends WorkStream Browser tabs left idle for 15 minutes, using
WorkStream's own Task Manager (Shift+Esc). An ended tab shows a reload page; reloading it brings the page back.
Nothing inside the browser is changed (WorkStream doesn't accept extensions).

## How it decides (every minute)

1. Only WorkStream's **tab strip** is read (read-only, like a screen reader). **Active** = the selected tab of each
   window; it is never ended, even while its window is minimised or covered. Every other tab is idle from the moment
   you leave it, including tabs playing sound and busy tabs. Tabs already open when Tab Cleaner starts are counted
   from when the browser opened them, not from when the app started, and the clocks are remembered across restarts.
2. A tab idle for **15 minutes**, or **10 minutes** when it uses more than **1.5%** of the computer's memory, is due,
   unless its title contains an allow-listed word (default `Google Maps, Dataminr, dataminr.com, google.com/maps,
   maps.google`).
3. The due tab's process (`Tab: <title>` row) is found in WorkStream's **Task Manager** (kept open off-screen, no
   taskbar button) and ended, which is what End task does. All processes for a title are ended together, so duplicate
   copies of a page go at once. Embedded frames (`Subframe: ...`) are never ended on their own. Tabs the browser has
   itself put to sleep have no process, so they are left alone.

### Works when WorkStream is minimised

The tab strip and the renderer process list stay readable even when WorkStream is minimised, but Chromium **freezes
its Task Manager** the moment the window is minimised, so the tab&nbsp;→&nbsp;process map can't be read then. Tab Cleaner
remembers the last map it read while the window was in use and, while minimised, ends idle tabs from it — killing a
process only after confirming it is still a live WorkStream renderer, so a tab that has since slept or closed is
skipped. The active tab, allow list and idle timing all come from the tab strip, so nothing you are using is closed.

The idle limit menu also has **1 minute (for testing)**.

## Install on any Windows PC

Run `TabCleaner-Setup.exe` (from the GitHub release, or `out\` after building). It installs for the current user
(no administrator rights) into `%LOCALAPPDATA%\Programs\TabCleaner`, adds Start Menu and desktop shortcuts,
optionally starts with Windows, and is listed in Settings > Apps for uninstalling. Needs .NET Framework 4
(built into Windows 10 and 11). Silent: `TabCleaner-Setup.exe /silent [/nodesktop] [/nostartup] [/nolaunch]`.

## Use

It runs in the background from the taskbar's notification area. Click its icon:
**Stop Tab Cleaner / Start Tab Cleaner**, status, idle limit, Edit allow list, messages on/off, Start with Windows,
Open log, Exit. Settings and log: `%LOCALAPPDATA%\TabCleaner\` (`settings.ini`: allow, idleMinutes,
bigTabPercent, bigTabMinutes, browser).

## Build and test

`build.bat` compiles `src\TabCleaner.cs` with the C# compiler included in Windows (.NET Framework 4, plus Windows'
UI Automation libraries) into `out\TabCleaner.exe` and the installer `out\TabCleaner-Setup.exe`.

Test switches: `--minutes N`, `--big-minutes N` (short limits), `--dry-run` (only log what would be ended),
`--exe name.exe`. Uninstall: `TabCleaner.exe --uninstall [--quiet]`.
