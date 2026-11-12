@echo off
rem Builds Tab Cleaner with the C# compiler that comes with Windows (.NET Framework 4):
rem   out\TabCleaner.exe        the app
rem   out\TabCleaner-Setup.exe  installer for any Windows PC (carries the app inside)
cd /d "%~dp0"
if not exist out mkdir out
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set WPF=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF
"%CSC%" /nologo /target:winexe /optimize+ /win32icon:src\app.ico /out:out\TabCleaner.exe /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:"%WPF%\UIAutomationClient.dll" /r:"%WPF%\UIAutomationTypes.dll" /r:"%WPF%\WindowsBase.dll" src\TabCleaner.cs || exit /b 1
"%CSC%" /nologo /target:winexe /optimize+ /win32icon:src\app.ico /out:out\TabCleaner-Setup.exe /resource:out\TabCleaner.exe,TabCleaner.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll src\Setup.cs || exit /b 1
echo Built out\TabCleaner.exe and out\TabCleaner-Setup.exe
