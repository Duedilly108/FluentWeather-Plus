# Collects what Windows and the app recorded about FluentWeather-Plus errors into one text file
# on the Desktop, FluentWeather-crash-report.txt, and opens it in Notepad.
#
# It reads:
#   crash.log of the app (written by the app for errors it catches)
#   the Windows Application log (Application Error, Application Hang, .NET Runtime,
#     Windows Error Reporting) for entries that name FluentWeather
#   the AppModel-Runtime log (app start and stop problems)
#   the Windows Error Reporting folders for FluentWeather crash reports
#
# It reads local files only. It makes no internet connection and changes nothing.
# Run it without "Run as administrator". As administrator it also reads the crash reports
# that Windows keeps for all users.
#
# -Days sets how far back to look. The default is 3 days.

param([int]$Days = 3)

$ErrorActionPreference = 'Continue'
$family   = 'FluentWeather.LocalBuild_qqrp5dnjcw9xy'
$since    = (Get-Date).AddDays(-$Days)
$out      = Join-Path ([Environment]::GetFolderPath('Desktop')) 'FluentWeather-crash-report.txt'
$lines    = New-Object System.Collections.Generic.List[string]

function Add-Line([string]$text) { $script:lines.Add($text) }
function Add-Title([string]$text) {
    Add-Line ''
    Add-Line $text
    Add-Line ('-' * $text.Length)
}

Add-Line ('FluentWeather-Plus crash report, ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + ', last ' + $Days + ' days')

# --- App and Windows version ---
Add-Title 'Installed app'
$pkg = Get-AppxPackage -Name 'FluentWeather.LocalBuild' -ErrorAction SilentlyContinue
if ($pkg) {
    Add-Line ('Version: ' + $pkg.Version + '  (v' + $pkg.Version.Split('.')[3] + ')')
    Add-Line ('Folder: ' + $pkg.InstallLocation)
} else {
    Add-Line 'FluentWeather.LocalBuild is not installed for this user.'
}
$os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
if ($os) { Add-Line ('Windows: ' + $os.Caption + ' ' + $os.Version) }

# --- crash.log of the app ---
Add-Title 'App crash.log'
$crashLog = Join-Path $env:LOCALAPPDATA ('Packages\' + $family + '\LocalState\crash.log')
if (Test-Path -LiteralPath $crashLog) {
    $text = Get-Content -LiteralPath $crashLog -Raw -Encoding UTF8
    if ($text.Length -gt 60000) { $text = '(older entries cut)' + "`r`n" + $text.Substring($text.Length - 60000) }
    Add-Line $text.TrimEnd()
} else {
    Add-Line 'No crash.log. The app has not recorded an error.'
}

# --- Application log ---
Add-Title 'Windows Application log'
$providers = @('Application Error', 'Application Hang', '.NET Runtime', 'Windows Error Reporting')
$found = 0
foreach ($provider in $providers) {
    $events = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = $provider; StartTime = $since } -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match 'FluentWeather' }
    foreach ($e in $events) {
        $found++
        Add-Line ('[' + $e.TimeCreated.ToString('yyyy-MM-dd HH:mm:ss') + '] ' + $e.ProviderName + ' ' + $e.Id)
        Add-Line $e.Message.Trim()
        Add-Line ''
    }
}
if ($found -eq 0) { Add-Line 'No entries that name FluentWeather.' }

# --- AppModel-Runtime log ---
Add-Title 'AppModel-Runtime log'
$found = 0
$events = Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-AppModel-Runtime/Admin'; StartTime = $since } -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -match 'FluentWeather' }
foreach ($e in $events) {
    $found++
    Add-Line ('[' + $e.TimeCreated.ToString('yyyy-MM-dd HH:mm:ss') + '] ' + $e.Id + ' ' + $e.LevelDisplayName)
    Add-Line $e.Message.Trim()
    Add-Line ''
}
if ($found -eq 0) { Add-Line 'No entries that name FluentWeather.' }

# --- Windows Error Reporting folders ---
Add-Title 'Windows Error Reporting'
$werRoots = @(
    (Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\WER\ReportArchive'),
    (Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\WER\ReportQueue'),
    (Join-Path $env:ProgramData 'Microsoft\Windows\WER\ReportArchive'),
    (Join-Path $env:ProgramData 'Microsoft\Windows\WER\ReportQueue')
)
$found = 0
foreach ($root in $werRoots) {
    if (-not (Test-Path -LiteralPath $root)) { continue }
    $dirs = Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match 'FluentWeather' -and $_.LastWriteTime -ge $since }
    foreach ($dir in $dirs) {
        $wer = Join-Path $dir.FullName 'Report.wer'
        if (-not (Test-Path -LiteralPath $wer)) { continue }
        $found++
        Add-Line ('[' + $dir.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss') + '] ' + $dir.FullName)
        # Report.wer is UTF-16. Only the lines that describe the crash are kept.
        Get-Content -LiteralPath $wer -Encoding Unicode -ErrorAction SilentlyContinue |
            Where-Object { $_ -match '^(EventType|AppName|AppPath|Sig\[\d+\]\.(Name|Value)|FriendlyEventName|ConsentKey)=' } |
            ForEach-Object { Add-Line ('  ' + $_) }
        Add-Line ''
    }
}
if ($found -eq 0) { Add-Line 'No crash reports for FluentWeather.' }

# --- Write and open ---
[System.IO.File]::WriteAllLines($out, $lines, (New-Object System.Text.UTF8Encoding($false)))
Write-Host ('Report written to ' + $out)
Start-Process notepad.exe -ArgumentList ('"' + $out + '"')
