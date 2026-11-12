# WorkStream Tab Cleaner

A small Windows taskbar (system tray) app that ends WorkStream Browser tabs left idle for 30 minutes, like
"End task" in Task Manager. An ended tab shows a reload page; reloading it brings the page back.

## How it decides a tab is idle

WorkStream is Chromium-based, so every tab runs in its own process. Chromium lowers a tab's process to Idle priority
while the tab isn't on screen, and raises it again when you view it. A tab is ended when its process has:

- stayed unviewed for the whole limit (30 minutes by default), and
- used almost no CPU in that time (under 5 CPU-seconds), so tabs still working in the background are left alone.

Never ended: the tab you last had on screen in each window (even when the window is covered or minimised), tabs
playing sound, extensions (CloudFactory's included), the WorkStream monitor, the browser itself, GPU and helpers.
Nothing inside WorkStream is changed.

## Tray menu

Status · Idle limit (15/30/45/60/90 minutes) · Pause · Show a message when tabs are ended · Start with Windows ·
End idle tabs now · Open log · Exit

Settings and log: `%LOCALAPPDATA%\WorkStreamTabCleaner\`.

## Build

`build.bat` compiles `src\TabCleaner.cs` with the C# compiler included in Windows (.NET Framework 4) into
`out\WorkStreamTabCleaner.exe`. No other tools needed.

Testing on another Chromium browser: `WorkStreamTabCleaner.exe --exe chrome.exe --match <profile folder> --minutes 1`.
