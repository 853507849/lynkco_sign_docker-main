@echo off
setlocal

rem ============================================================
rem  LynkSign GUI - build the Windows installer (setup.exe)
rem  Just double-click this file, or run it from a terminal.
rem  Requires: .NET SDK 10 + Inno Setup 6
rem ============================================================

echo.
echo [1] Building installer...
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-installer.ps1" %*
set RC=%ERRORLEVEL%

echo.
if not "%RC%"=="0" (
    echo [FAILED] build-installer.ps1 exited with code %RC%
) else (
    echo [DONE] The setup.exe is under: LynkSign.WinForms\dist
)

echo.
pause
endlocal
