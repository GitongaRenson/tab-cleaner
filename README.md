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
3. Only then is WorkStream's **Task Manager** used (kept open off-screen, no taskbar button): one search for that
   tab's title, cleared straight after, gives its process (`Tab: <title>` row), which is ended, which is what
   End task does. Embedded frames (`Subframe: ...`) are never ended on their own. Tabs the browser already put to
   sleep have no process and are left alone. At most 5 tabs a minute, one at a time.

Why so gentle: version 1.3.0 kept the Task Manager filtered and re-read it every minute while ending many tabs at
once; WorkStream's main process then stopped itself (a Chromium internal check, 0x80000003).

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
