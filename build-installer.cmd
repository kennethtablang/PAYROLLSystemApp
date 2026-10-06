@echo off
rem Double-click to build PAYROLLSystemApp\Installer\PayrollSystemSetup-<version>.exe.
rem To set a new version, run from a terminal:  build-installer.cmd -Version 1.1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-installer.ps1" %*
echo.
pause
