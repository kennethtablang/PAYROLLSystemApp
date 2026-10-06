@echo off
rem Publishes the built installer to GitHub Releases (see publish-release.ps1).
rem Add -Draft to publish it hidden first:  publish-release.cmd -Draft
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish-release.ps1" %*
echo.
pause
