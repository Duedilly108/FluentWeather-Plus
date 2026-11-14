FluentWeather-Plus build
========================

This folder builds FluentWeather-Plus. It downloads release 2.1.0 of FluentWeather
by Gabboxl from GitHub, adds the FluentWeather-Plus changes, signs the app with a
certificate that only exists on your PC, and installs it. It always builds on
release 2.1.0. It does not check for newer releases, because a newer release can
break the changes. New versions of FluentWeather-Plus are on its releases page.
https://github.com/Duedilly108/FluentWeather-Plus/releases


What you need
-------------
Windows 10 version 1809 or later, or Windows 11.
An administrator account. Use your own account. Setup must run as the same user
who will use the app.
About 15 GB of free disk space for Visual Studio Build Tools.
An internet connection during setup and during each update.
No Microsoft account, Microsoft Store or winget is needed.


Setup steps (one time)
----------------------
1. Right-click the downloaded zip file. Select Properties. Select Unblock if it is
   shown. Select OK.

2. Extract the zip to any folder, for example your Downloads folder. Do not extract
   it into Program Files yourself. Setup copies the files there.

   If your firewall blocks PowerShell (recommended), download these two installers
   with your web browser into your Downloads folder first:
     Git for Windows, 64-bit installer   https://git-scm.com/download/win
     Visual Studio 2026 Build Tools      https://aka.ms/vs/stable/vs_buildtools.exe
   Setup finds them there, checks their digital signatures, and runs them.

3. Double-click Setup.cmd. Approve the administrator prompt.

   Optional settings. Open a command prompt in the extracted folder and add them:
     Setup.cmd -StartApp               also open the app window when you sign in
     Setup.cmd -NoTray                 do not install the tray icon
     Setup.cmd -NoRelay                git and MSBuild connect directly (see Firewall)
     Setup.cmd -RemoveStoreVersion     uninstall a Microsoft Store copy, if you have one
   You can combine them, for example:
     Setup.cmd -StartApp -RemoveStoreVersion
   By default only the tray icons start when you sign in. The app window does
   not open.

4. If Git or Visual Studio 2026 Build Tools are missing, Setup downloads them.
   Git comes from the Git for Windows release page on GitHub. Build Tools come
   from Microsoft. Setup checks each installer's digital signature before it
   runs it, and deletes the file if the check fails.
   The Build Tools download is several GB and can take 20 minutes or more.
   If Windows asks for a restart, restart and run Setup.cmd again.

5. Setup creates your signing certificate, builds and starts the tray icon, and opens a second window for the first build. The first
   build takes 5 to 15 minutes.

6. When the second window shows "Installed FluentWeather ... with saved locations",
   open "FluentWeather-Plus" from the Start menu. Pick your first location.
   Click the star next to the place name to save it. Search for more cities and
   save them the same way. The app window shows these steps until you save a place.


Single-file installer (to share with others)
--------------------------------------------
Start menu > "Make FluentWeather installer" builds one file on your Desktop:
    FluentWeatherLocal-Installer.exe
You can also double-click Make-Installer.cmd in C:\Program Files\FluentWeatherLocal
or in the extracted folder. It takes a few seconds.

Windows builds the installer on your PC from FluentWeatherInstaller.cs, with the C#
compiler that ships with Windows. It does not use the internet. The installer holds
the setup files of this folder only. It holds no certificate, no settings, no saved
locations and no log files. Each PC that runs it creates its own signing certificate.

When someone runs FluentWeatherLocal-Installer.exe, it asks:
    Yes      install now. It extracts the files to a temporary folder, runs Setup,
             waits for it, and deletes the temporary folder.
    No       extract the files to a new FluentWeatherLocal folder on the Desktop and
             open it, so the files can be read first. Setup.cmd in that folder installs.
    Cancel   do nothing.

What the other person should know:
    The installer is not signed by a publisher. When it was downloaded or received by
    e-mail, Windows SmartScreen shows "Windows protected your PC". Select More info,
    then Run anyway. Some antivirus programs also ask about it, because it runs
    PowerShell scripts.
    Most e-mail services block .exe files, also inside a zip file. Share it through a
    cloud drive or a USB drive instead.
    The first install downloads Git and Visual Studio Build Tools (about 15 GB of disk
    space) and can take 30 minutes or more.
    If their firewall blocks PowerShell, they download the two installers with a web
    browser first, as described in step 2 above.
    They follow the firewall section below for FluentWeatherNet.exe and the app.


Direct installer (to share the app you built)
---------------------------------------------
Start menu > "Make FluentWeather direct installer" makes a folder on your Desktop:
    FluentWeather-Plus\FluentWeather-Plus-Setup-v49.exe
    FluentWeather-Plus\FluentWeather-Plus-Uninstall-v49.exe
    FluentWeather-Plus\README.txt
and a zip of these three files, FluentWeather-Plus-v49.zip. The version is the last
part of the build number, so build 2.1.0.49 is v49. Each build on your PC adds one.
The build number is also in README.txt and in Properties > Details of both programs.
It also writes FluentWeather-Plus-v49-release-notes.txt on the Desktop. Copy its text into
the description of the GitHub release. It has the VirusTotal links of both files filled in.

The setup holds the app as it is built and installed on your PC, signed again with
a throwaway certificate, the Microsoft packages it needs and the tray icons program. The other PC installs it in about a minute. It needs no Visual Studio,
no Git and no internet connection for the install.

Windows builds both programs on your PC from FluentWeatherDirect.cs, with the C#
compiler that ships with Windows. Make-DirectInstaller.cmd in
C:\Program Files\FluentWeatherLocal does the same as the Start menu item.

What the other person should know:
    Your own signing certificate is never used. Each time, the direct installer
    makes a throwaway certificate, signs a copy of the app with it, and deletes the
    certificate and its private key from your PC. The setup adds the public part to
    "Trusted People" of their PC, with one administrator approval. Nobody can sign
    anything else with it, so their PC can only accept this one app package.
    The installed app does not update itself. Send them a new direct installer after
    each update. It installs over the old version and keeps their saved locations.
    README.txt in the folder explains the install. To remove the app, they use
    Start menu > FluentWeather-Plus > Uninstall FluentWeather-Plus, or Settings > Apps.
    The setup and the uninstaller are not signed by a publisher, so SmartScreen
    shows "Windows protected your PC". They select More info, then Run anyway.
    Do not run the direct installer on your own PC. It stops there, because your PC
    already has the build.


Where the files go
------------------
C:\Program Files\FluentWeatherLocal
    The scripts, the patch, the C# sources, Make-Installer.cmd and config.json.
    Only administrators can change these files. So another program running as you
    cannot change what the build runs.

%LOCALAPPDATA%\FluentWeatherLocal
    Source code, build output, state.json and logs. The logs folder keeps the
    last 20 log files.

Your certificate
    The private key is in your user certificate store (certmgr.msc, Personal).
    It cannot be exported. The public certificate is in the computer store
    under Trusted People. Windows accepts app packages signed with this key only.


How new versions work
---------------------
FluentWeather-Plus does not check for updates. A new version comes as a new package
of this folder. Extract it and double-click Setup.cmd. Setup copies the new files
and builds the new version. Your saved locations and settings are kept.

To rebuild the installed version, for example after a failed install, open a command
prompt in C:\Program Files\FluentWeatherLocal and run:
    "Update FluentWeather.cmd" -Force
Do not use "Run as administrator".

Earlier versions had a logon task that checked GitHub for a new FluentWeather release.
Setup removes that task.


Firewall (TinyWall or similar)
------------------------------
During Setup only: switch the firewall to learning mode (or "Allow outgoing")
with nothing else running. The Build Tools installer downloads its parts through
several helper programs. Switch back to normal protection when the first build has
installed the app. Then remove the Visual Studio Installer helper programs that
learning mode added (setup.exe, BackgroundDownload.exe and similar in
C:\Program Files (x86)\Microsoft Visual Studio\Installer).

TinyWall block rules stay in force in learning mode and in "Allow outgoing" mode.
If you block powershell.exe, Setup cannot download the installers. Keep that block,
and download the two installers with your browser as described in step 2.

Allow only these two programs permanently:
    C:\Program Files\FluentWeatherLocal\FluentWeatherNet.exe
    FluentWeather-Plus         the app itself, for weather data

FluentWeatherNet.exe is a network relay that Setup compiles from FluentWeatherNet.cs.
During an update, git and MSBuild connect only to the relay on your own PC
(127.0.0.1). The relay makes the outside connections, and only to the host names in
allowed-hosts.txt: github.com, api.github.com (release notes), api.nuget.org and
globalcdn.nuget.org, on HTTPS. The update script reads the release notes through
the relay too, so PowerShell needs no firewall exception.
It refuses everything else and runs only while an update runs. So git, MSBuild and
PowerShell need no firewall exception. Those general tools stay blocked for any
other program that might try to use them.

If learning mode added git-remote-https.exe, MSBuild.exe or powershell.exe to your
allowed list, remove them.

If an update fails with a network error, open the newest relay-*.log in
%LOCALAPPDATA%\FluentWeatherLocal\logs. A "BLOCKED" line names a host the build
tried to reach. Add it to allowed-hosts.txt only if you trust it. If no relay log
lines show up at all, the firewall may block connections inside the PC. Then run
Setup.cmd -NoRelay and allow git-remote-https.exe and MSBuild.exe instead.

The relay skips one online check: the certificate revocation lookup for NuGet
packages, because Windows makes that lookup outside the relay. NuGet still checks
every package signature.


Tray icons
----------
FluentWeather is a sandboxed app. Windows does not let that kind of app keep an
icon in the notification area or minimize to it. A small separate program,
FluentWeatherTray.exe, provides two icons side by side. The temperature is on the
left and the weather picture on its right. Drag them on the taskbar to change it.
    the weather picture for the current conditions, one of 15:
        sunny (a full sun), clear night (the moon in its current phase),
        partly cloudy (sun partly behind a cloud), partly cloudy night (moon
        partly behind a cloud), cloudy, cloudy night (the lit edge of the moon
        behind a cloud), fog (horizontal bands),
        light rain (cloud and 2 drops), rain (cloud and 4 drops),
        heavy rain (dark cloud and 7 drops), ice or mixed (cloud, drops and
        snowflakes: freezing rain, sleet, hail, wintry mix), thunderstorm
        (lightning bolt and 7 drops), severe storm (lightning bolt and a red
        warning triangle: strong storms, tornado, tropical storm, hurricane),
        light snow (3 snowflakes), heavy snow (7 snowflakes)
    The moon shows its real phase for today. North of the equator a waxing moon is
    lit on the right and a waning moon on the left. South of the equator the sides
    are swapped, so each saved city shows the moon as it looks there.
    The moon is drawn in a light brown. The hover detail "Moon phase" shows the
    phase name and the lit percentage. "Moonrise" and "Moonset" show the next
    moonrise and moonset. A time on a later day starts with the weekday.
    The tray program draws these pictures itself, so they stay sharp at every display
    scale. They follow the light or dark taskbar.
    The picture follows the weather code and, for English condition text, the words in
    it. "Light Rain" shows 2 drops, "Heavy Rain" shows 7. When the code shows only a
    cloud, sun or moon but the text names rain, snow, ice or thunder, the text decides.
    For example "Showers in the Vicinity" with a cloud code shows light rain.
    the temperature, or the feels-like temperature, as a number

Setup compiles it on your PC from FluentWeatherTray.cs, using the C# compiler that
ships with Windows. You can read that file to see everything it does. It does not
connect to the internet. The app writes the current conditions to two small files:
one for the city shown in the app, one for every saved city. The tray program reads
them and updates within a second of a change. The app's background task refreshes
them about every 15 minutes, even when the app window is closed.

With the window open, the app's own "Auto refresh" setting (15 minutes to 1 hour)
also refreshes the files.

Left-click either icon         open FluentWeather-Plus, at the city the tray shows.
                               If the app window is the active window, left-click
                               closes it again, so only the tray icons stay. If the
                               window is behind other windows, left-click brings it
                               to the front.
Right-click either icon        choose "Number shows temperature" or
                               "Number shows feels-like temperature"
                               "Tray shows": pick a saved city (each with its current
                               temperature), or "Same city as the app". The tray
                               switches at once. The app window stays closed.
                               "Restart FluentWeather (app and tray icons)" closes
                               both and starts them again.
                               "Close FluentWeather window" closes the app window
                               like its X button. If the app still runs after
                               4 seconds, it is ended.
                               "Exit tray icons" closes the icons.
                               "Exit tray icons and close FluentWeather" does both.
                               To show the icons again, select "Show tray icons"
                               in the app's settings, or "FluentWeather tray icons"
                               in the Start menu. They also start at the next sign-in.
                               While they run, the same settings button reads
                               "Hide tray icons" and closes them.
Hover over either icon         a panel with the city and the details you choose.
                               The panel shows a large, detailed version of the
                               weather picture with a soft glow behind it. The
                               background runs from very dark blue at the top to
                               dark blue at the bottom and stretches with the number of
                               details. The panel has rounded corners and a shadow.
                               "Hover details" in the right-click menu (the item
                               with the blue arrow): tick any of
                               conditions, temperature, feels like, 24 h high / low,
                               24 h change, dew point, heat index, wind chill,
                               humidity, wind and gusts, UV index, pressure and trend,
                               visibility, cloud ceiling, rain (1, 6, 24 h),
                               snow (24 h), sunrise, sunset, moon phase,
                               moonrise, moonset, observation time,
                               nearest station. Moonrise, moonset and nearest
                               station are off by default.
                               "Show all" and "Reset to defaults" are at the bottom.
                               To move the hover panel, drag it with the left mouse
                               button. It then opens at that place. Double-click
                               the panel to put it back next to the icons. After the pointer leaves the icons, the panel
                               stays 0.9 seconds, so you can reach it to drag it.
                               The panel opens only when the pointer stays on the
                               icons for the whole 1.2 seconds. If it leaves sooner,
                               nothing opens.

The number shows "--" when there is no data yet, or when the data is older than
3 hours. Open the app once to refresh it. If it stays at "--", check that
FluentWeather-Plus may run in the background: Settings > Apps > Installed apps >
FluentWeather-Plus > Advanced options > Background app permissions.

Start menu items:
    FluentWeather-Plus           open the app
    FluentWeather tray icons     start the tray icons again after closing them
    Restart FluentWeather        close the app and the tray icons, then start both
You can also start the tray icons by double-clicking
C:\Program Files\FluentWeatherLocal\FluentWeatherTray.exe. They also start at sign-in.

New icons go to the hidden area (the ^ arrow) at first. To keep both visible, open
Settings > Personalization > Taskbar > Other system tray icons, and turn on both
FluentWeatherTray entries. Drag the icons to change their order.


Nearest weather station
-----------------------
"Nearest station" in Hover details shows the closest official weather station that
reports, usually an airport, with its code and distance, for example
"Fairfield (KFFL), 3.1 mi". The app looks it up once per location and keeps it for
30 days. weather.com does not take the current conditions from this one station. It
blends nearby stations, radar, satellite and forecast models for the exact location.
The station is shown for reference.


Full History
------------
The "Full History" button at the right of the "Daily forecast" title shows the
history of a saved location over the main window, by month or by year, with charts
for temperature, wind, rain and snow, air quality, humidity, pressure, and clouds
and sun. The mouse wheel zooms the chart. Shift with the wheel, dragging the chart,
or the scroll bar below it scroll through the year. A view shorter than 26 days
shows every hour. Clicking a day zooms to that day. The minus, plus and Fit
buttons zoom without a wheel. The Back button or Esc closes the page. The app records
the days it runs (see Past days). The Open-Meteo historical weather archive fills
the days it missed, about five days behind today. The table marks each day
"recorded" or "archive". The app downloads one month of one location per request
and keeps it in its data folder. The hours load one month at a time, only when a
short view needs them. A complete month is never downloaded again.
The app connects to archive-api.open-meteo.com, api.open-meteo.com and
air-quality-api.open-meteo.com for this. The firewall rule for the
app covers it.


Nearby water
------------
Two switches under "Nearby water" in the settings panel, both off by default.
"Card in the main window" adds a card with the water temperature and the wind at
the nearest water station. "Line in the tray hover panel" writes two lines to the
tray data, which the tray shows when "Water temperature" and "Wind at water
station" are ticked under Hover details. The stations come from the NOAA National
Data Buoy Center (buoys and shore stations on the coasts and Great Lakes) and USGS
Water Data (rivers and some lakes). The app uses
the closest station within 50 km with a reading under 6 hours old and hides the
card when there is none. On the ocean with no station near, it shows the Open-Meteo
marine model instead. The app connects to www.ndbc.noaa.gov,
api.waterdata.usgs.gov and marine-api.open-meteo.com for this. The firewall rule
for the app covers it.


Extra data
----------
Five feeds at the top of "More data options" in the settings panel, each with
a Main box (a card
in the main window) and a Tray box (a line for the tray hover panel). All are off
by default: waves and sea temperature, wind forecast (gusts, 80 m), other forecast
models (ECMWF, GFS, ICON), river flow, and pollen (weather.com in the US,
Open-Meteo in Europe). The app connects to
api.open-meteo.com, marine-api.open-meteo.com, flood-api.open-meteo.com and
air-quality-api.open-meteo.com, at most once an hour per location and feed.


Weather data source
-------------------
The setting "Weather data source" in the settings panel picks weather.com or
Open-Meteo. Automatic uses weather.com and switches to Open-Meteo when weather.com
refuses the app's key twice in a row. It tries weather.com again every hour. While
Open-Meteo is in use, "Data: Open-Meteo" shows under the update time. The app
connects to api.open-meteo.com and geocoding-api.open-meteo.com for this. The
firewall rule for the app covers it.

With Open-Meteo the app also reads the nearest airport weather report (METAR)
from aviationweather.gov. It is free and needs no key. The app uses the nearest
station within 50 km that has a report from the last 2 hours. The report gives
the cloud ceiling, the visibility, the pressure and the station for the tray line
"Nearest station". A station within 25 km also gives the measured temperature,
dew point, humidity, wind and gusts. The note under the update time then names
the station, for example "Data: Open-Meteo and station KFFL". Without a station
in range, the ceiling shows "not available". The app asks once per location every
30 minutes. The firewall rule for the app covers aviationweather.gov too.


Crash report
------------
The app writes errors it catches to crash.log in its data folder. A crash that
closes the app goes to the Windows event log instead. "FluentWeather crash report"
in the Start menu writes FluentWeather-crash-report.txt on the Desktop with both,
for the last 3 days, and opens it. Run it without "Run as administrator". It reads
local files only and makes no internet connection.


Using saved locations
---------------------
Star button next to the place name   save or remove the shown location
Click a location button              switch to it
Right-click a location button        move it left or right, or remove it
Ctrl+1 to Ctrl+9                     jump to saved location 1 to 9
Ctrl+Tab, Ctrl+Shift+Tab             next or previous saved location

Below the location buttons the app shows a short tip about the star. Click the x at
the end of the tip to hide it. With no saved locations, the app always shows how to
save one.


Window size, text and picture size
----------------------------------
The app window opens at 820 x 600 the first time. After that, Windows remembers the
window size and position each time you close it. "Close FluentWeather window" in the
tray menu closes the window the same way, so Windows remembers it then too.

Settings (the gear button at the top) > "Text and picture size": 50% to 100%
in 5% steps. Click - or + to change it, or 100% to reset it. The default is 100%. It makes the text, icons and pictures in the
weather area smaller, so more of the forecast fits in a small window. The search box
and the top bar keep their normal size.
In a narrow window the top bar hides the app name, so the refresh button, the search
box and the settings button have room.
The settings flyout under the gear button is drawn at 80% of its normal size.
The list of locations below the search box stays at least 340 pixels wide, and long
names wrap onto a second line, so similar names can be told apart.
Keyboard:
    Ctrl+Minus     5% smaller
    Ctrl+Plus      5% larger
    Ctrl+0         back to 100%


Background contrast
-------------------
Settings (the gear button) > "Background contrast" makes the text easier to read on
the weather background photo:
    Original   the photo as it is, with light text
    Dark       the photo shaded darker, with light text (default)
    Darker     the photo shaded much darker, with light text
    Light      the photo brightened, with dark text
    Lighter    the photo brightened more, with dark text
Light and Lighter also turn the cards, the titles and the window buttons dark, so
the whole page uses dark text. With "Background image" off, Light and Lighter give
a light page and the other choices give a dark page.


Hourly forecast
---------------
Settings (the gear button) > "Hourly forecast" sets how many hours the hourly list
shows:
    Day        the hours of the selected day, as in the original app (default)
    24 h       24 hours
    36 h       36 hours
    48 h       48 hours
For today, 24, 36 and 48 hours start at the current hour. For another day, they
start at midnight of that day. The first hour and each midnight show the weekday,
for example "Wed 00:00".


Language
--------
Settings (the gear button) > "Language" sets the language of the app text and of the
weather descriptions from the weather service. The button shows the current language.
Click it to open a window with every language that comes with the app. Scroll the
list, select a language, and click "Use this language", or double-click the language.
"Windows default" uses the Windows display language. After a change, the app offers
to restart, because Windows applies a new language when the app starts. A few texts of this local build (saved locations tips, the new settings)
are in English only. The tray icons menu is in English.


Remove everything
-----------------
Settings > Apps > Installed apps > "FluentWeather-Plus build tools (setup and tray
icons)" > Uninstall. Or open a command prompt in C:\Program Files\FluentWeatherLocal
and run:
    Setup.cmd -Uninstall
This removes the app, the tray icons, the certificate and these folders.
The separate "FluentWeather-Plus" entry in Settings > Apps removes only the app.
Git and Visual Studio Build Tools stay installed. Remove them in
Settings > Apps if you no longer need them.


If something fails
------------------
Read the newest file in %LOCALAPPDATA%\FluentWeatherLocal\logs.
The build log (build-*.log) has the compiler messages.
If Build Tools did not install, open Visual Studio Installer, select Modify on
Build Tools 2026, and select "WinUI application development build tools" plus
"MSVC x64/x86 build tools (Latest)" and "Windows 11 SDK (10.0.26100)".
