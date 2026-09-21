@echo off
setlocal

rem ============================================================
rem  LynkSign GUI - quick pack (for day-to-day iteration)
rem
rem  Just double-click this file. It will:
rem    1) skip dotnet publish if nothing changed
rem    2) detect a running LynkSignGUI locking the publish dir
rem    3) run the credential gate, then Inno Setup
rem
rem  Handy switches (pass them after the file name):
rem    -Kill     auto-close the running app that locks the publish dir
rem    -Force    always re-publish, skip the up-to-date check
rem    -Version 1.1.0
rem
rem  For an official release, use build-installer.bat instead
rem  (it wipes the publish dir first, so the output is pristine).
rem  Requires: .NET SDK 10 + Inno Setup 6
rem ============================================================

echo.
echo [*] Quick pack: LynkSign GUI
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0pack-quick.ps1" %*
set RC=%ERRORLEVEL%

echo.
if not "%RC%"=="0" (
    echo [FAILED] pack-quick.ps1 exited with code %RC%
) else (
    echo [DONE] The setup.exe is under: LynkSign.WinForms\dist
)

echo.
pause
endlocal
