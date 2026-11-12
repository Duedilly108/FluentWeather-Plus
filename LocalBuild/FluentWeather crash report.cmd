@echo off
rem Writes FluentWeather-crash-report.txt on your Desktop with the recent app errors and opens it.
rem It reads local files and the Windows event log only. Run it without "Run as administrator".
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Get-FluentWeatherCrashReport.ps1" %*
echo.
pause
