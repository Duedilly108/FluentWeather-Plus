@echo off
rem Makes FluentWeather-Plus-Setup.exe, FluentWeather-Plus-Uninstall.exe and README.txt in a folder
rem "FluentWeather-Plus" on your Desktop, plus a zip of them to share. Others can install
rem the app with them without building it. Run it without "Run as administrator".
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Make-DirectInstaller.ps1" %*
echo.
pause
