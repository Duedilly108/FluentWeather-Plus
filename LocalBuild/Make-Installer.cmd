@echo off
rem Builds FluentWeatherLocal-Installer.exe on your Desktop. Share that one file with others.
rem It is compiled on this PC from FluentWeatherInstaller.cs with the C# compiler built into Windows.
rem It contains only the files listed below. It contains no certificate, settings or saved locations.
rem Run it without "Run as administrator".
setlocal
set "SRC=%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [x] The C# compiler of .NET Framework 4 was not found.
    goto :end
)

rem Find the Desktop folder, also when it is moved to OneDrive.
set "DESK="
for /f "tokens=2,*" %%a in ('reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders" /v Desktop 2^>nul ^| find "REG_"') do set "DESK=%%b"
if defined DESK call set "DESK=%DESK%"
if not defined DESK set "DESK=%USERPROFILE%\Desktop"
if not exist "%DESK%\" set "DESK=%USERPROFILE%"
set "OUT=%DESK%\FluentWeatherLocal-Installer.exe"

for %%f in ("Setup-FluentWeatherLocal.ps1" "Update-FluentWeatherLocal.ps1" "FluentWeather-saved-locations.patch" "Setup.cmd" "Update FluentWeather.cmd" "FluentWeatherTray.cs" "FluentWeatherNet.cs" "FluentWeatherInstaller.cs" "Make-Installer.cmd" "FluentWeather.ico" "allowed-hosts.txt" "README.txt" "FluentWeatherDirect.cs" "FluentWeatherDirect.manifest" "Make-DirectInstaller.cmd" "Make-DirectInstaller.ps1" "Direct-README.txt" "Release-notes.txt") do (
    if not exist "%SRC%%%~f" (
        echo [x] Missing file: "%SRC%%%~f"
        goto :end
    )
)

echo Building %OUT%
"%CSC%" /nologo /target:winexe /optimize+ /out:"%OUT%" /win32icon:"%SRC%FluentWeather.ico" /r:System.Windows.Forms.dll ^
 /resource:"%SRC%Setup-FluentWeatherLocal.ps1",payload/Setup-FluentWeatherLocal.ps1 ^
 /resource:"%SRC%Update-FluentWeatherLocal.ps1",payload/Update-FluentWeatherLocal.ps1 ^
 /resource:"%SRC%FluentWeather-saved-locations.patch",payload/FluentWeather-saved-locations.patch ^
 /resource:"%SRC%Setup.cmd",payload/Setup.cmd ^
 /resource:"%SRC%Update FluentWeather.cmd","payload/Update FluentWeather.cmd" ^
 /resource:"%SRC%FluentWeatherTray.cs",payload/FluentWeatherTray.cs ^
 /resource:"%SRC%FluentWeatherNet.cs",payload/FluentWeatherNet.cs ^
 /resource:"%SRC%FluentWeatherInstaller.cs",payload/FluentWeatherInstaller.cs ^
 /resource:"%SRC%Make-Installer.cmd",payload/Make-Installer.cmd ^
 /resource:"%SRC%FluentWeather.ico",payload/FluentWeather.ico ^
 /resource:"%SRC%allowed-hosts.txt",payload/allowed-hosts.txt ^
 /resource:"%SRC%README.txt",payload/README.txt ^
 /resource:"%SRC%FluentWeatherDirect.cs",payload/FluentWeatherDirect.cs ^
 /resource:"%SRC%FluentWeatherDirect.manifest",payload/FluentWeatherDirect.manifest ^
 /resource:"%SRC%Make-DirectInstaller.cmd",payload/Make-DirectInstaller.cmd ^
 /resource:"%SRC%Make-DirectInstaller.ps1",payload/Make-DirectInstaller.ps1 ^
 /resource:"%SRC%Direct-README.txt",payload/Direct-README.txt ^
 /resource:"%SRC%Release-notes.txt",payload/Release-notes.txt ^
 "%SRC%FluentWeatherInstaller.cs"
if errorlevel 1 (
    echo [x] The build failed.
    goto :end
)
echo [+] Done: %OUT%
echo     Share this one file. Each PC that runs it creates its own signing certificate.

:end
echo.
pause
