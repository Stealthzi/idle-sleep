@echo off
rem Builds IdleSleep.exe using the C# compiler that comes with Windows.
rem The "build files" folder holds the icon and the manifest (which makes it run as administrator).
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /out:IdleSleep.exe /win32icon:"build files\IdleSleep.ico" /win32manifest:"build files\IdleSleep.manifest" /r:System.Windows.Forms.dll /r:System.Drawing.dll IdleSleep.cs
if errorlevel 1 (echo BUILD FAILED & pause & exit /b 1)
echo Built IdleSleep.exe
