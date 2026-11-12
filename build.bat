@echo off
rem Builds TabCleaner.exe with the C# compiler that comes with Windows (.NET Framework 4).
cd /d "%~dp0"
if not exist out mkdir out
set WPF=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /out:out\TabCleaner.exe /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:"%WPF%\UIAutomationClient.dll" /r:"%WPF%\UIAutomationTypes.dll" /r:"%WPF%\WindowsBase.dll" src\TabCleaner.cs
