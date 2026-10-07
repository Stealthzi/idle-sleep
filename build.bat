@echo off
rem Builds IdleSleep.exe using the C# compiler that comes with Windows.
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /out:IdleSleep.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll IdleSleep.cs
if errorlevel 1 (echo BUILD FAILED & pause & exit /b 1)
echo Built IdleSleep.exe
