# Tab Cleaner

A small Windows taskbar (system tray) app that ends WorkStream Browser tabs left idle for 30 minutes, using
WorkStream's own Task Manager (Shift+Esc). An ended tab shows a reload page; reloading it brings the page back.
Nothing inside the browser is changed (WorkStream doesn't accept extensions).

## How it decides

1. Every minute it looks at WorkStream's tab processes. Chromium lowers a process to Idle priority while its tab
   isn't on screen. A process that stays that way for the whole limit is due: 30 minutes, or 10 minutes for a tab
   using more than 1.5% of the computer's memory. A tab using real CPU each minute (an upload, processing) is busy
   and not ended.
   Active tabs are never ended: the tab on screen, tabs in other visible windows, tabs playing sound, and the tab
   each window had on screen last (even while the window is covered or minimised).
2. For due processes it reads WorkStream's Task Manager, which names what each process holds:
   - `Tab: <page title>`: a tab, the only thing Tab Cleaner ends;
   - `Subframe: https://site/`: something embedded in a tab (ads, videos, maps), never ended on its own, because
     that breaks the page around it, and it closes anyway when its tab is ended.
3. Allow list: a process holding a tab whose Task Manager name contains an allow-listed word is never ended.
   Default: `Google Maps, Dataminr, dataminr.com, google.com/maps, maps.google`.
4. Otherwise it ends exactly that process, which is what the Task Manager's End task does. The list is read in
   full (it scrolls through it); the Task Manager window is kept open off-screen.

## Install on any Windows PC

Run `TabCleaner-Setup.exe` (from the GitHub release, or `out\` after building). It installs for the current user
(no administrator rights) into `%LOCALAPPDATA%\Programs\TabCleaner`, adds Start Menu and desktop shortcuts,
optionally starts with Windows, and is listed in Settings > Apps for uninstalling. Needs .NET Framework 4
(built into Windows 10 and 11). Silent: `TabCleaner-Setup.exe /silent [/nodesktop] [/nostartup] [/nolaunch]`.

## Use

It runs in the background from the taskbar's notification area. Click its icon:
**Stop Tab Cleaner / Start Tab Cleaner**, status, idle limit, Edit allow list, messages on/off, Start with Windows,
Open log, Exit. Settings and log: `%LOCALAPPDATA%\TabCleaner\` (`settings.ini`: allow, idleMinutes,
bigTabPercent, bigTabMinutes, busyCpuPerMinute, browser).

## Build and test

`build.bat` compiles `src\TabCleaner.cs` with the C# compiler included in Windows (.NET Framework 4, plus Windows'
UI Automation libraries) into `out\TabCleaner.exe` and the installer `out\TabCleaner-Setup.exe`.

Test switches: `--minutes N`, `--big-minutes N` (short limits), `--dry-run` (only log what would be ended),
`--exe name.exe`. Uninstall: `TabCleaner.exe --uninstall [--quiet]`.
