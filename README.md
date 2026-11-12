# Tab Cleaner

A small Windows taskbar (system tray) app that ends WorkStream Browser tabs left idle for 30 minutes, using
WorkStream's own Task Manager (Shift+Esc). An ended tab shows a reload page; reloading it brings the page back.
Nothing inside the browser is changed (WorkStream doesn't accept extensions).

## How it decides

1. Every minute it looks at WorkStream's tab processes. Chromium lowers a process to Idle priority while its tab
   isn't on screen. A process that stays that way for the whole limit (30 min), using almost no CPU, is due.
   Active tabs are never ended: the tab on screen, tabs in other visible windows, tabs playing sound, and the tab
   each window had on screen last (even while the window is covered or minimised).
2. For due processes it reads WorkStream's Task Manager, which names what each process holds:
   - `Tab: <page title>`: a tab, the only thing Tab Cleaner ends;
   - `Subframe: https://site/`: something embedded in a tab (ads, videos, maps), never ended on its own, because
     that breaks the page around it, and it closes anyway when its tab is ended.
3. Allow list: a process holding a tab whose Task Manager name contains an allow-listed word is never ended.
   Default: `Google Maps, Dataminr, dataminr.com, google.com/maps, maps.google`.
4. Otherwise it selects the row and presses **End task**. If the button can't be used (the Task Manager is kept
   off-screen), it ends that same process directly, which is what End task does.

## Tray menu

Status, Idle limit (15/30/45/60/90 min), Edit allow list, Pause, Show a message when tabs are ended,
Start with Windows, Open log, Exit. Settings and log: `%LOCALAPPDATA%\TabCleaner\`.

## Build and test

`build.bat` compiles `src\TabCleaner.cs` with the C# compiler included in Windows (.NET Framework 4, plus Windows'
UI Automation libraries) into `out\TabCleaner.exe`.

Test switches: `--minutes N` (short limit), `--dry-run` (only log what would be ended), `--exe name.exe`.
