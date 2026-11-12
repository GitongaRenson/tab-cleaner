@echo off
rem Builds WorkStreamTabCleaner.exe with the C# compiler that comes with Windows (.NET Framework 4).
cd /d "%~dp0"
if not exist out mkdir out
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /out:out\WorkStreamTabCleaner.exe /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll src\TabCleaner.cs
