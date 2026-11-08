<#
.SYNOPSIS
    Builds and installs FluentWeather with the saved-locations patch.

.DESCRIPTION
    Run by the "FluentWeather Local Update" scheduled task at logon, or by hand
    through "Update FluentWeather.cmd".

    1. Uses release 2.1.0 of the developer's GitHub repository, the release this patch
       is made for. It does not check for newer releases, because a newer release can
       break the patch.
    2. Stops if that release and this patch are already built and installed.
    3. Downloads that release's source code with git.
    4. Applies FluentWeather-saved-locations.patch.
    5. Sets the package name and publisher to your local signing certificate.
    6. Builds and signs the package with Visual Studio Build Tools.
    7. Checks the signature and installs the package for the current user.

    If any step fails, the installed app is left unchanged.
    Work files and logs are in %LOCALAPPDATA%\FluentWeatherLocal.

.PARAMETER Force
    Rebuild and reinstall even if the newest release is already installed. Does not ask.

.PARAMETER Tag
    Build this release tag (for example 2.1.0) instead of the newest one.

.PARAMETER Quiet
    Show a notification only when something was installed or an error occurred.

.PARAMETER StoreTest
    Builds the version prepared for the developer (FluentWeather-store-features.patch next to
    this script) as a separate test app named "FluentWeather (Store test)". It has the tray icons
    inside the app package. Your own FluentWeather, its data, its tray icons and its update state
    are not changed. Uses the certificate, Git, Build Tools and relay from your Setup.
    Uninstall the test app in Settings > Apps or with "Remove Store test.cmd".

.PARAMETER Scheduled
    Set by the logon task. The script then checks GitHub only if the last check is older
    than CheckEveryDays in config.json (default 30). A manual run always checks.

.NOTES
    Exit codes: 0 = installed, up to date, postponed or skipped, 1 = error, 2 = patch does not apply.
#>

[CmdletBinding()]
param(
    [switch]$Force,
    [string]$Tag,
    [switch]$Quiet,
    [switch]$Scheduled,
    [switch]$StoreTest
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$RepoUrl     = 'https://github.com/Gabboxl/FluentWeather'
$PinnedTag   = '2.1.0'   # FluentWeather-Plus builds on this release only
$PackageName = 'FluentWeather.LocalBuild'
$DisplayName = 'FluentWeather-Plus'
$ProtocolName = 'fluentweather-local'
$AppServiceName = 'FluentWeatherTrayRefresh'
$TaskTitle   = 'FluentWeather local update'

$InstallDir = $PSScriptRoot
$ConfigPath = Join-Path $InstallDir 'config.json'
$PatchPath  = Join-Path $InstallDir 'FluentWeather-saved-locations.patch'

$WorkDir   = Join-Path $env:LOCALAPPDATA 'FluentWeatherLocal'
$SrcDir    = Join-Path $WorkDir 'src'
$OutRoot   = Join-Path $WorkDir 'out'
$LogDir    = Join-Path $WorkDir 'logs'
$StatePath = Join-Path $WorkDir 'state.json'

# Store test: a separate app next to your own build. See -StoreTest above.
$LocalInstallDir = Join-Path $env:ProgramFiles 'FluentWeatherLocal'
if ($StoreTest) {
    $PackageName  = 'FluentWeather.StoreTest'
    $DisplayName  = 'FluentWeather (Store test)'
    $ProtocolName = ''    # the store version registers its own "fluentweather" link
    $TaskTitle    = 'FluentWeather Store test'
    $InstallDir   = $LocalInstallDir    # config.json and the relay from your Setup
    $ConfigPath   = Join-Path $InstallDir 'config.json'
    $PatchPath    = Join-Path $PSScriptRoot 'FluentWeather-store-features.patch'
    $OutRoot      = Join-Path $WorkDir 'out-storetest'
    $StatePath    = Join-Path $WorkDir 'state-storetest.json'
    $Force        = [switch]$true
}

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

function Write-UpdateLog([string]$Text) {
    Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $Text)
}

function Show-Toast([string]$Title, [string]$Text) {
    try {
        $null = [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
        $template = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent(
            [Windows.UI.Notifications.ToastTemplateType]::ToastText02)
        $nodes = $template.GetElementsByTagName('text')
        $null = $nodes.Item(0).AppendChild($template.CreateTextNode($Title))
        $null = $nodes.Item(1).AppendChild($template.CreateTextNode($Text))
        $toast = [Windows.UI.Notifications.ToastNotification]::new($template)
        $appId = '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe'
        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId).Show($toast)
    } catch {
        # Notifications are optional. The log file has the details.
    }
}

# Throws an error that carries an exit code for the end of the script.
function Stop-Step([string]$Message, [int]$Code = 1) {
    $err = New-Object System.Exception($Message)
    $err.Data['ExitCode'] = $Code
    throw $err
}

function Get-GitPath {
    $cmd = Get-Command git.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $fallback = Join-Path $env:ProgramFiles 'Git\cmd\git.exe'
    if (Test-Path $fallback) { return $fallback }
    Stop-Step 'Git was not found. Run Setup again to install it.' 1
}

# Runs git and returns its output lines. Throws on a non-zero exit code.
function Invoke-Git([string[]]$Arguments, [int]$FailCode = 1) {
    $ErrorActionPreference = 'Continue'   # git writes progress to stderr
    # Certificate revocation lookups are made by Windows directly, outside the relay, so a firewall
    # blocks them. "best-effort" still rejects a certificate that is known to be revoked, but does not
    # fail when the revocation server cannot be reached. Requires Git 2.42 or later.
    $allArgs = @('-c', 'http.schannelCheckRevoke=best-effort') + $Arguments
    if ($script:ProxyUrl) { $allArgs = @('-c', "http.proxy=$($script:ProxyUrl)") + $allArgs }
    $output = & $script:Git @allArgs 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) {
        $output | ForEach-Object { Write-Host "    $_" }
        $hint = if ($script:RelayLog) { " If this is a network error, see $($script:RelayLog) for BLOCKED hosts." } else { '' }
        Stop-Step "git $($Arguments -join ' ') failed (exit code $LASTEXITCODE).$hint" $FailCode
    }
    return $output
}

function Get-MSBuildPath {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) {
        Stop-Step 'Visual Studio Build Tools were not found. Run Setup again to install them.' 1
    }
    $path = & $vswhere -latest -products * -version '[18.0,)' `
        -requiresAny -requires Microsoft.VisualStudio.Workload.UniversalBuildTools Microsoft.VisualStudio.Workload.Universal `
        -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    if (-not $path) {
        Stop-Step 'Visual Studio 2026 Build Tools with the WinUI/UWP workload were not found. Run Setup again.' 1
    }
    return $path
}

function Get-BuildPlatform {
    $arch = 'X64'
    try { $arch = [string][System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture } catch { }
    switch ($arch) {
        'Arm64' { 'arm64' }
        'X86'   { 'x86' }
        default { 'x64' }
    }
}

function Assert-SameUser {
    $me = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $desk = $null
    try { $desk = (Get-CimInstance -ClassName Win32_ComputerSystem).UserName } catch { }
    if ($desk -and ($desk -ine $me)) {
        Stop-Step "This runs as '$me' but the desktop user is '$desk'. Run it without 'Run as administrator'." 1
    }
}

function Read-JsonFile([string]$Path) {
    if (-not (Test-Path $Path)) { return $null }
    try { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json } catch { return $null }
}

# Reads a property without failing under StrictMode when it is missing.
function Get-Value($Object, [string]$Name, $Default = $null) {
    if ($null -eq $Object) { return $Default }
    $prop = $Object.PSObject.Properties[$Name]
    if ($prop) { return $prop.Value }
    return $Default
}

# Merges new values into state.json.
function Save-State([hashtable]$Values) {
    $merged = [ordered]@{}
    $old = Read-JsonFile $StatePath
    if ($old) { foreach ($p in $old.PSObject.Properties) { $merged[$p.Name] = $p.Value } }
    foreach ($k in $Values.Keys) { $merged[$k] = $Values[$k] }
    [pscustomobject]$merged | ConvertTo-Json | Set-Content -LiteralPath $StatePath -Encoding UTF8
}

function Get-LatestTag {
    $lines = Invoke-Git @('ls-remote', '--tags', '--refs', $RepoUrl)
    $tags = foreach ($line in $lines) {
        if ($line -match 'refs/tags/(\d+\.\d+\.\d+)$') { $Matches[1] }
    }
    $latest = $tags | Sort-Object { [version]$_ } | Select-Object -Last 1
    if (-not $latest) { Stop-Step 'No release tags were found in the repository.' 1 }
    return $latest
}

function Initialize-Source([string]$ReleaseTag) {
    if (-not (Test-Path (Join-Path $SrcDir '.git'))) {
        if (Test-Path $SrcDir) { Remove-Item -LiteralPath $SrcDir -Recurse -Force }
        Write-UpdateLog "Cloning $RepoUrl"
        # Partial clone: history without file contents. Contents download on demand.
        # This keeps the clone small and still lets git apply --3way merge the patch.
        Invoke-Git @('-c', 'core.autocrlf=false', '-c', 'core.eol=lf',
                     'clone', '--filter=blob:none', '--no-checkout', $RepoUrl, $SrcDir) | Out-Null
        Invoke-Git @('-C', $SrcDir, 'config', 'core.autocrlf', 'false') | Out-Null
        Invoke-Git @('-C', $SrcDir, 'config', 'core.eol', 'lf') | Out-Null
    }

    Write-UpdateLog "Fetching release $ReleaseTag"
    Invoke-Git @('-C', $SrcDir, 'fetch', '--force', '--tags', 'origin') | Out-Null
    Invoke-Git @('-C', $SrcDir, 'checkout', '--force', '--detach', "refs/tags/$ReleaseTag") | Out-Null
    Invoke-Git @('-C', $SrcDir, 'reset', '--hard') | Out-Null
    Invoke-Git @('-C', $SrcDir, 'clean', '-fdx') | Out-Null
}

# Each local build needs its own version number. Windows refuses to install a package with
# the same version as the installed one but different contents (error 0x80073CFB).
# The release gives the first three parts (2.1.0). The fourth part counts local builds.
function Get-NextRevision($State, $Installed) {
    $last = [int](Get-Value $State 'BuildRevision' 0)
    if ($Installed) {
        try { $last = [Math]::Max($last, ([version]$Installed.Version).Revision) } catch { }
    }
    $next = $last + 1
    if ($next -gt 65535) { $next = 1 }   # version parts are limited to 65535
    return $next
}

function Set-PackageIdentity([string]$CertSubject, [int]$Revision) {
    $manifestPath = Join-Path $SrcDir 'FluentWeather\Package.appxmanifest'
    $xml = New-Object System.Xml.XmlDocument
    $xml.PreserveWhitespace = $true
    $xml.Load($manifestPath)

    $ns = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
    $ns.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $ns.AddNamespace('uap', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')

    $identity = $xml.SelectSingleNode('/f:Package/f:Identity', $ns)
    $props    = $xml.SelectSingleNode('/f:Package/f:Properties/f:DisplayName', $ns)
    $visual   = $xml.SelectSingleNode('/f:Package/f:Applications/f:Application/uap:VisualElements', $ns)
    if (-not $identity -or -not $props -or -not $visual) {
        Stop-Step 'The app manifest layout changed. The script needs an update.' 1
    }

    $identity.SetAttribute('Name', $PackageName)
    $identity.SetAttribute('Publisher', $CertSubject)
    $props.InnerText = $DisplayName
    $visual.SetAttribute('DisplayName', $DisplayName)

    # Register the "fluentweather-local:" link that the tray helper uses to open the app.
    $fNs   = 'http://schemas.microsoft.com/appx/manifest/foundation/windows10'
    $uapNs = 'http://schemas.microsoft.com/appx/manifest/uap/windows10'
    $app = $visual.ParentNode
    $extensions = $app.SelectSingleNode('f:Extensions', $ns)
    if (-not $extensions) {
        $extensions = $xml.CreateElement('Extensions', $fNs)
        $null = $app.AppendChild($extensions)
    }
    if ($ProtocolName -and -not $extensions.SelectSingleNode("uap:Extension/uap:Protocol[@Name='$ProtocolName']", $ns)) {
        $ext = $xml.CreateElement('uap', 'Extension', $uapNs)
        $ext.SetAttribute('Category', 'windows.protocol')
        $protocol = $xml.CreateElement('uap', 'Protocol', $uapNs)
        $protocol.SetAttribute('Name', $ProtocolName)
        $protoName = $xml.CreateElement('uap', 'DisplayName', $uapNs)
        $protoName.InnerText = $DisplayName
        $null = $protocol.AppendChild($protoName)
        $null = $ext.AppendChild($protocol)
        $null = $extensions.AppendChild($ext)
    }

    # Register the app service that the tray helper calls every 5 minutes while the window is closed.
    # No EntryPoint: Windows runs it inside the app (App.OnBackgroundActivated), without a window.
    if (-not $extensions.SelectSingleNode("uap:Extension/uap:AppService[@Name='$AppServiceName']", $ns)) {
        $ext = $xml.CreateElement('uap', 'Extension', $uapNs)
        $ext.SetAttribute('Category', 'windows.appService')
        $service = $xml.CreateElement('uap', 'AppService', $uapNs)
        $service.SetAttribute('Name', $AppServiceName)
        $null = $ext.AppendChild($service)
        $null = $extensions.AppendChild($ext)
    }

    # Unique version for this local build (see Get-NextRevision).
    $parts = $identity.GetAttribute('Version').Split('.')
    $newVersion = '{0}.{1}.{2}.{3}' -f $parts[0], $parts[1], $parts[2], $Revision
    $identity.SetAttribute('Version', $newVersion)
    $xml.Save($manifestPath)
    return $newVersion
}

# The project expects win-<platform>.pubxml files. The developer keeps them out of git.
function Add-PublishProfile([string]$Platform) {
    $dir = Join-Path $SrcDir 'FluentWeather\Properties\PublishProfiles'
    $file = Join-Path $dir "win-$Platform.pubxml"
    if (Test-Path $file) { return }
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    @"
<?xml version="1.0" encoding="utf-8"?>
<Project>
  <PropertyGroup>
    <PublishProtocol>FileSystem</PublishProtocol>
    <Platform>$Platform</Platform>
    <RuntimeIdentifier>win-$Platform</RuntimeIdentifier>
    <PublishDir>bin\`$(Configuration)\`$(TargetFramework)\`$(RuntimeIdentifier)\publish\</PublishDir>
    <SelfContained>true</SelfContained>
    <PublishSingleFile>False</PublishSingleFile>
  </PropertyGroup>
</Project>
"@ | Set-Content -LiteralPath $file -Encoding UTF8
}

# ---------------------------------------------------------------------------
# Network relay. git and MSBuild connect only to FluentWeatherNet.exe on 127.0.0.1.
# The relay reaches only the hosts in allowed-hosts.txt. Your firewall needs to
# allow FluentWeatherNet.exe only, not git or MSBuild.
# ---------------------------------------------------------------------------

$script:ProxyUrl = $null
$script:RelayProcess = $null
$script:RelayLog = $null

function Start-Relay {
    $exe = Join-Path $InstallDir 'FluentWeatherNet.exe'
    $hostsFile = Join-Path $InstallDir 'allowed-hosts.txt'
    if (-not (Test-Path -LiteralPath $exe) -or -not (Test-Path -LiteralPath $hostsFile)) {
        Stop-Step 'FluentWeatherNet.exe or allowed-hosts.txt is missing. Run Setup again.' 1
    }

    $portFile = Join-Path $WorkDir 'relay-port.txt'
    Remove-Item -LiteralPath $portFile -Force -ErrorAction SilentlyContinue
    $script:RelayLog = Join-Path $LogDir "relay-$stamp.log"

    # The relay exits by itself when this script's process ($PID) ends.
    $relayArgs = @("`"$hostsFile`"", "`"$portFile`"", "`"$($script:RelayLog)`"", "$PID")
    $script:RelayProcess = Start-Process -FilePath $exe -ArgumentList $relayArgs -WindowStyle Hidden -PassThru

    $port = $null
    for ($i = 0; $i -lt 50 -and -not $port; $i++) {
        Start-Sleep -Milliseconds 200
        if (Test-Path -LiteralPath $portFile) {
            $text = (Get-Content -LiteralPath $portFile -Raw -ErrorAction SilentlyContinue)
            if ($text -match '^\s*(\d+)\s*$') { $port = $Matches[1] }
        }
    }
    if (-not $port) { Stop-Step "The network relay did not start. See $($script:RelayLog)" 1 }

    $script:ProxyUrl = "http://127.0.0.1:$port"
    # git reads https_proxy. NuGet (inside MSBuild) reads http_proxy. Windows names are not case sensitive.
    $env:HTTP_PROXY  = $script:ProxyUrl
    $env:HTTPS_PROXY = $script:ProxyUrl
    Remove-Item Env:\NO_PROXY -ErrorAction SilentlyContinue
    # Package signatures are still checked. Only the online certificate revocation lookup is skipped,
    # because Windows makes that lookup directly from MSBuild.exe, outside the relay.
    $env:NUGET_CERT_REVOCATION_MODE = 'offline'
    Write-UpdateLog "Network relay running on 127.0.0.1:$port. Only hosts in allowed-hosts.txt are reachable."
}

function Stop-Relay {
    if ($script:RelayProcess -and -not $script:RelayProcess.HasExited) {
        Stop-Process -Id $script:RelayProcess.Id -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-Build([string]$MSBuild, [string]$Platform, [string]$OutDir, [string]$Thumbprint, [string]$BuildLog) {
    $csproj = Join-Path $SrcDir 'FluentWeather\FluentWeather.csproj'
    $buildArgs = @(
        # /nr:false: no MSBuild worker processes stay behind with old proxy settings.
        $csproj, '/restore', '/m', '/nr:false', '/nologo', '/v:minimal',
        '/p:Configuration=Release',
        "/p:Platform=$Platform",
        '/p:UapAppxPackageBuildMode=SideloadOnly',
        '/p:AppxBundle=Never',
        '/p:GenerateAppxPackageOnBuild=true',
        '/p:AppxSymbolPackageEnabled=false',
        '/p:AppxPackageSigningEnabled=true',
        "/p:PackageCertificateThumbprint=$Thumbprint",
        # Double backslash so the closing quote is not escaped when the path has spaces.
        "/p:AppxPackageDir=$OutDir\\"
    )

    Write-UpdateLog "Building for $Platform. This takes several minutes."
    $ErrorActionPreference = 'Continue'
    & $MSBuild @buildArgs *>&1 | ForEach-Object { "$_" } | Out-File -LiteralPath $BuildLog -Encoding utf8
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'

    if ($code -ne 0) {
        Get-Content -LiteralPath $BuildLog -Tail 25 | ForEach-Object { Write-Host "    $_" }
        $hint = if ($script:RelayLog) { " If packages could not be downloaded, see $($script:RelayLog) for BLOCKED hosts." } else { '' }
        Stop-Step "The build failed (exit code $code). See $BuildLog.$hint" 1
    }
}

function Find-BuiltPackage([string]$OutDir, [string]$Platform) {
    $msix = Get-ChildItem -LiteralPath $OutDir -Recurse -File |
            Where-Object { $_.Extension -in '.msix', '.appx' -and $_.FullName -notmatch '\\Dependencies\\' } |
            Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $msix) { Stop-Step "No package was found in $OutDir." 1 }

    $depDir = Join-Path $msix.DirectoryName "Dependencies\$Platform"
    $deps = @()
    if (Test-Path $depDir) {
        $deps = @(Get-ChildItem -LiteralPath $depDir -File -Recurse | Where-Object { $_.Extension -in '.appx', '.msix' })
    }
    return [pscustomobject]@{ Package = $msix; Dependencies = $deps }
}

function Assert-Signatures($Built, [string]$Thumbprint) {
    $sig = Get-AuthenticodeSignature -FilePath $Built.Package.FullName
    if (-not $sig.SignerCertificate -or $sig.SignerCertificate.Thumbprint -ne $Thumbprint) {
        Stop-Step 'The built package is not signed with your local certificate.' 1
    }
    # A self-signed certificate in TrustedPeople is accepted for app packages, but Authenticode
    # can still report "UnknownError" because the certificate has no trusted root. The thumbprint
    # match above is the real check. Here only a broken or missing signature is rejected.
    if ($sig.Status -in 'HashMismatch', 'NotSigned', 'Incompatible', 'NotSupportedFileFormat') {
        Stop-Step "The package signature is invalid (status: $($sig.Status))." 1
    }
    foreach ($dep in $Built.Dependencies) {
        $d = Get-AuthenticodeSignature -FilePath $dep.FullName
        if ($d.Status -ne 'Valid' -or $d.SignerCertificate.Subject -notlike 'CN=Microsoft Corporation*') {
            Stop-Step "Dependency $($dep.Name) does not carry a valid Microsoft signature." 1
        }
    }
    Write-UpdateLog 'Signatures checked.'
}

function Set-StartupShortcut([bool]$Enabled) {
    $lnk = Join-Path ([Environment]::GetFolderPath('Startup')) "$DisplayName.lnk"
    # Builds before the rename to FluentWeather-Plus were named "FluentWeather (Local)". Their shortcut would start the app twice.
    $oldLnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'FluentWeather (Local).lnk'
    if (-not $StoreTest -and (Test-Path $oldLnk)) { Remove-Item -LiteralPath $oldLnk -Force }
    if (-not $Enabled) {
        if (Test-Path $lnk) { Remove-Item -LiteralPath $lnk -Force }
        return
    }
    $pkg = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue
    if (-not $pkg) { return }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($lnk)
    $shortcut.TargetPath = Join-Path $env:WINDIR 'explorer.exe'
    $shortcut.Arguments = "shell:AppsFolder\$($pkg.PackageFamilyName)!App"
    $shortcut.Save()
}

# Files the tray helper reads: the package family name (to find the saved list) and the app icon.
function Update-TrayFiles([bool]$CopyIcon) {
    $pkg = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue
    if ($pkg) {
        Set-Content -LiteralPath (Join-Path $WorkDir 'package-family-name.txt') -Value $pkg.PackageFamilyName -Encoding ASCII
    }
    $assets = Join-Path $SrcDir 'FluentWeather\Assets'
    if ($CopyIcon) {
        foreach ($name in 'Square44x44Logo.altform-unplated_targetsize-32.png', 'Square44x44Logo.targetsize-32.png') {
            $png = Join-Path $assets $name
            if (Test-Path -LiteralPath $png) {
                Copy-Item -LiteralPath $png -Destination (Join-Path $WorkDir 'tray-icon.png') -Force
                break
            }
        }
    }

    # The app's weather pictures (one per condition code), for the tray icon next to the temperature.
    $iconSource = Join-Path $assets 'weticonspng'
    $iconTarget = Join-Path $WorkDir 'weather-icons'
    if (($CopyIcon -or -not (Test-Path -LiteralPath $iconTarget)) -and (Test-Path -LiteralPath $iconSource)) {
        New-Item -ItemType Directory -Path $iconTarget -Force | Out-Null
        Copy-Item -Path (Join-Path $iconSource '*.png') -Destination $iconTarget -Force
    }
}

# After a Store test install: checks that the tray icons program is in the package, closes your own
# tray icons so only the test icons show, and opens the test app.
function Complete-StoreTest($Package) {
    $trayExe = Join-Path $Package.InstallLocation 'FluentWeatherTray\FluentWeatherTray.exe'
    if (Test-Path -LiteralPath $trayExe) {
        Write-UpdateLog 'The tray icons program is inside the package.'
    } else {
        Write-UpdateLog "WARNING: $trayExe is missing. The Tray icons setting cannot start the icons."
    }

    Get-Process -Name 'FluentWeatherTray' -ErrorAction SilentlyContinue | ForEach-Object {
        $path = $null
        try { $path = $_.Path } catch { }
        if ($path -and $path.StartsWith($LocalInstallDir + '\', [StringComparison]::OrdinalIgnoreCase)) {
            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
            Write-UpdateLog 'Closed your own tray icons for the test. Start menu "FluentWeather tray icons" starts them again.'
        }
    }

    Write-UpdateLog "Installed $DisplayName. Opening it now."
    Write-UpdateLog 'Pick a location, then open the settings (gear button) and set "Tray icons" to On.'
    Start-Process -FilePath (Join-Path $env:WINDIR 'explorer.exe') -ArgumentList "shell:AppsFolder\$($Package.PackageFamilyName)!App"
    Show-Toast $TaskTitle "$DisplayName is installed. Set Tray icons to On in its settings."
}

function Remove-OldFiles([string]$KeepOutDir) {
    if (Test-Path $OutRoot) {
        Get-ChildItem -LiteralPath $OutRoot -Directory |
            Where-Object { $_.FullName -ne $KeepOutDir } |
            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    }
    Get-ChildItem -LiteralPath $LogDir -File | Sort-Object LastWriteTime -Descending |
        Select-Object -Skip 20 | Remove-Item -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------------------
# Ask before an update
# ---------------------------------------------------------------------------

# Removes Markdown marks from the release notes so they read well as plain text.
function ConvertFrom-Markdown([string]$Text) {
    if (-not $Text) { return '' }
    $t = $Text -replace "`r", ''
    $t = $t -replace '<!--[\s\S]*?-->', ''
    $t = $t -replace '!\[[^\]]*\]\([^)]*\)', ''
    $t = $t -replace '\[([^\]]+)\]\(([^)]+)\)', '$1 ($2)'
    $t = $t -replace '(?m)^#{1,6}\s*', ''
    $t = $t -replace '(?m)^\s*[\*\-\+]\s+', '  - '
    $t = $t -replace '\*\*|__|`', ''
    $t = $t -replace "\n{3,}", "`n`n"
    return $t.Trim()
}

# Reads the developer's release notes and the list of changes between two releases.
# Nothing here is required. If a part cannot be read, the dialog says so.
function Get-ReleaseInfo([string]$OldTag, [string]$NewTag) {
    $info = @{ Name = ''; Date = ''; Notes = ''; Commits = @(); CommitTotal = 0 }

    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        $request = @{
            Uri             = "https://api.github.com/repos/Gabboxl/FluentWeather/releases/tags/$NewTag"
            UseBasicParsing = $true
            TimeoutSec      = 30
            UserAgent       = 'FluentWeatherLocal-Update'
        }
        if ($script:ProxyUrl) { $request.Proxy = $script:ProxyUrl }
        $release = Invoke-RestMethod @request
        $info.Name  = [string](Get-Value $release 'name' '')
        $info.Notes = ConvertFrom-Markdown ([string](Get-Value $release 'body' ''))
        $published = Get-Value $release 'published_at' ''
        if ($published) {
            try { $info.Date = ([datetime]$published).ToString('yyyy-MM-dd') } catch { $info.Date = [string]$published }
        }
    }
    catch {
        Write-UpdateLog "Release notes could not be read: $($_.Exception.Message)"
    }

    try {
        if ($OldTag -and (Test-Path (Join-Path $SrcDir '.git'))) {
            Invoke-Git @('-C', $SrcDir, 'fetch', '--force', '--tags', 'origin') | Out-Null
            $lines = @(Invoke-Git @('-C', $SrcDir, 'log', '--no-merges', '--format=%as  %s',
                                    "refs/tags/$OldTag..refs/tags/$NewTag") | Where-Object { $_ })
            $info.CommitTotal = $lines.Count
            $info.Commits = @($lines | Select-Object -First 60)
        }
    }
    catch {
        Write-UpdateLog "Change list could not be read: $($_.Exception.Message)"
    }

    return $info
}

function Get-UpdateText([string]$OldTag, [string]$NewTag, $Info, $State, [int]$CheckDays, [bool]$WasSkipped) {
    $nl = "`r`n"
    $sb = New-Object System.Text.StringBuilder

    $newLine = "New release:     $NewTag"
    if ($Info.Date) { $newLine += "   (published $($Info.Date))" }
    $oldLine = "Your version:    $OldTag"
    $pkgVersion = Get-Value $State 'PackageVersion' ''
    $installedAt = Get-Value $State 'InstalledAt' ''
    if ($pkgVersion) { $oldLine += "   (local build $pkgVersion" + $(if ($installedAt) { ", installed $(([datetime]$installedAt).ToString('yyyy-MM-dd'))" } else { '' }) + ')' }

    [void]$sb.Append("A new FluentWeather release is available.$nl$nl")
    [void]$sb.Append("$newLine$nl$oldLine$nl")
    if ($Info.Name -and $Info.Name -ne $NewTag) { [void]$sb.Append("Release title:   $($Info.Name)$nl") }
    if ($WasSkipped) { [void]$sb.Append("${nl}You chose to skip this release earlier.$nl") }

    [void]$sb.Append("${nl}Developer's release notes$nl-------------------------$nl")
    if ($Info.Notes) {
        [void]$sb.Append(($Info.Notes -replace "`n", $nl) + $nl)
    } else {
        [void]$sb.Append("The release notes could not be loaded. They are at$nl$RepoUrl/releases/tag/$NewTag$nl")
    }

    [void]$sb.Append("${nl}Changes since your version$nl--------------------------$nl")
    if ($Info.CommitTotal -gt 0) {
        foreach ($c in $Info.Commits) { [void]$sb.Append("$c$nl") }
        if ($Info.CommitTotal -gt $Info.Commits.Count) {
            [void]$sb.Append("... and $($Info.CommitTotal - $Info.Commits.Count) more.$nl")
        }
    } else {
        [void]$sb.Append("The change list could not be loaded. It is at$nl$RepoUrl/compare/$OldTag...$NewTag$nl")
    }

    [void]$sb.Append("${nl}What each button does$nl---------------------$nl")
    [void]$sb.Append("Update now: downloads the new source code, adds your saved locations and tray features, builds, signs and installs it. This takes 5 to 15 minutes. Your saved locations and settings are kept. If the new release changes the same code as the saved-locations patch, the update stops and your current version stays installed.$nl$nl")
    [void]$sb.Append("Not now: asks again at the next check in $CheckDays days, or when you select ""Check for updates now"" in the tray menu.$nl$nl")
    [void]$sb.Append("Skip this version: the automatic check stops asking about $NewTag. It asks again when a newer release appears. ""Check for updates now"" still shows $NewTag.$nl")
    return $sb.ToString()
}

# Shows the release information and returns 'Update', 'Later' or 'Skip'.
# An unanswered window closes after 60 minutes and counts as 'Later'.
function Show-UpdateDialog([string]$Title, [string]$Text) {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    [System.Windows.Forms.Application]::EnableVisualStyles()

    $form = New-Object System.Windows.Forms.Form
    $form.Text = $Title
    $form.StartPosition = 'CenterScreen'
    $form.Size = New-Object System.Drawing.Size(760, 600)
    $form.MinimumSize = New-Object System.Drawing.Size(500, 340)
    $form.Font = New-Object System.Drawing.Font('Segoe UI', 9.5)
    $form.TopMost = $true
    $form.ShowInTaskbar = $true
    $form.MaximizeBox = $true
    $form.MinimizeBox = $true

    $box = New-Object System.Windows.Forms.TextBox
    $box.Multiline = $true
    $box.ReadOnly = $true
    $box.WordWrap = $true
    $box.ScrollBars = 'Vertical'
    $box.BackColor = [System.Drawing.SystemColors]::Window
    $box.Font = New-Object System.Drawing.Font('Consolas', 10)
    $box.Dock = 'Fill'
    $box.Text = $Text

    $buttons = New-Object System.Windows.Forms.FlowLayoutPanel
    $buttons.Dock = 'Bottom'
    $buttons.FlowDirection = 'RightToLeft'
    $buttons.Height = 50
    $buttons.Padding = New-Object System.Windows.Forms.Padding(8, 8, 8, 8)

    $later = New-Object System.Windows.Forms.Button
    $later.Text = 'Not now'
    $later.AutoSize = $true
    $later.MinimumSize = New-Object System.Drawing.Size(110, 30)
    $later.DialogResult = [System.Windows.Forms.DialogResult]::No

    $update = New-Object System.Windows.Forms.Button
    $update.Text = 'Update now'
    $update.AutoSize = $true
    $update.MinimumSize = New-Object System.Drawing.Size(110, 30)
    $update.DialogResult = [System.Windows.Forms.DialogResult]::Yes

    $skip = New-Object System.Windows.Forms.Button
    $skip.Text = 'Skip this version'
    $skip.AutoSize = $true
    $skip.MinimumSize = New-Object System.Drawing.Size(130, 30)
    $skip.DialogResult = [System.Windows.Forms.DialogResult]::Ignore

    # RightToLeft order: the first button added is at the right edge.
    $buttons.Controls.Add($later)
    $buttons.Controls.Add($update)
    $buttons.Controls.Add($skip)

    # Add the Fill control first. Windows Forms docks the last added control first.
    $form.Controls.Add($box)
    $form.Controls.Add($buttons)
    $form.CancelButton = $later
    $form.ActiveControl = $later

    $timer = New-Object System.Windows.Forms.Timer
    $timer.Interval = 60 * 60 * 1000
    $timer.Tag = $form
    $timer.Add_Tick({ $this.Stop(); $this.Tag.DialogResult = [System.Windows.Forms.DialogResult]::No })
    $form.Add_Shown({ $this.Activate() })

    try {
        $timer.Start()
        $result = $form.ShowDialog()
    }
    finally {
        $timer.Stop()
        $timer.Dispose()
        $form.Dispose()
    }

    switch ($result) {
        ([System.Windows.Forms.DialogResult]::Yes)    { return 'Update' }
        ([System.Windows.Forms.DialogResult]::Ignore) { return 'Skip' }
        default                                       { return 'Later' }
    }
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

if ($PSVersionTable.PSEdition -ne 'Desktop') {
    Write-Host 'Run this with Windows PowerShell (powershell.exe). Use "Update FluentWeather.cmd".'
    exit 1
}

New-Item -ItemType Directory -Path $WorkDir, $OutRoot, $LogDir -Force | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runLog = Join-Path $LogDir "update-$stamp.log"
Start-Transcript -LiteralPath $runLog | Out-Null

# Only one update at a time.
$mutex = New-Object System.Threading.Mutex($false, 'Local\FluentWeatherLocalUpdate')
$haveLock = $false
$exitCode = 0

try {
    try { $haveLock = $mutex.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $haveLock = $true }
    if (-not $haveLock) { Stop-Step 'Another update is already running.' 1 }

    Assert-SameUser

    $config = Read-JsonFile $ConfigPath
    if (-not $config -or -not $config.CertThumbprint) {
        Stop-Step "Setup has not been run. $ConfigPath is missing." 1
    }
    $startApp = [bool](@($config.PSObject.Properties.Match('StartAppAtLogon')).Count -gt 0 -and $config.StartAppAtLogon)
    if (-not (Test-Path $PatchPath)) { Stop-Step "Patch file missing: $PatchPath" 1 }

    $cert = Get-Item -LiteralPath "Cert:\CurrentUser\My\$($config.CertThumbprint)" -ErrorAction SilentlyContinue
    if (-not $cert -or -not $cert.HasPrivateKey) {
        Stop-Step 'The signing certificate was not found in your certificate store. Run Setup again.' 1
    }
    if ($cert.NotAfter -lt (Get-Date).AddDays(1)) {
        Stop-Step 'The signing certificate has expired. Run Setup again to renew it.' 1
    }

    $checkDays = [int](Get-Value $config 'CheckEveryDays' 30)
    $lastCheck = Get-Value (Read-JsonFile $StatePath) 'LastCheck'
    $skipCheck = $false
    if ($Scheduled -and -not $Force -and $lastCheck) {
        $nextCheck = ([datetime]$lastCheck).AddDays($checkDays)
        if ((Get-Date) -lt $nextCheck) {
            $skipCheck = $true
            Write-UpdateLog ("Last check was on {0:yyyy-MM-dd}. Next check on or after {1:yyyy-MM-dd} (every {2} days)." -f [datetime]$lastCheck, $nextCheck, $checkDays)
            Set-StartupShortcut $startApp
        }
    }

    if (-not $skipCheck) {
        if ([bool](Get-Value $config 'UseRelay' $true)) { Start-Relay }
        $script:Git = Get-GitPath
        $releaseTag = if ($Tag) { $Tag } else { $PinnedTag }
        $patchHash = (Get-FileHash -LiteralPath $PatchPath -Algorithm SHA256).Hash
        $state = Read-JsonFile $StatePath
        $installed = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue

        Write-UpdateLog "Base release: $releaseTag"
        if (Get-Value $state 'Tag') { Write-UpdateLog "Last local build: $(Get-Value $state 'Tag')" }

        $upToDate = $installed -and ((Get-Value $state 'Tag') -eq $releaseTag) -and ((Get-Value $state 'PatchSha256') -eq $patchHash)
        $oldTag = [string](Get-Value $state 'Tag' '')
        $updateMode = [string](Get-Value $config 'UpdateMode' 'Ask')
        if ($updateMode -notin @('Ask', 'Auto', 'Off')) { $updateMode = 'Ask' }

        # Build, UpToDate, Older, Later, Skip or Skipped.
        $decision = 'Build'
        if ($upToDate -and -not $Force) {
            $decision = 'UpToDate'
        }
        elseif ($installed -and $oldTag -and ($oldTag -ne $releaseTag) -and -not $Force -and -not $Tag) {
            $isOlder = $false
            try { $isOlder = ([version]$releaseTag -lt [version]$oldTag) } catch { }
            if ($isOlder) {
                $decision = 'Older'
            }
            elseif ($updateMode -ne 'Auto') {
                # A patch-only change (same release) is built without asking. A new release asks first.
                $skippedTag = [string](Get-Value $state 'SkippedTag' '')
                if ($Scheduled -and ($skippedTag -eq $releaseTag)) {
                    $decision = 'Skipped'
                }
                else {
                    Write-UpdateLog "Release $releaseTag is available. Asking before the update."
                    $info = Get-ReleaseInfo $oldTag $releaseTag
                    $text = Get-UpdateText $oldTag $releaseTag $info $state $checkDays ($skippedTag -eq $releaseTag)
                    $answer = Show-UpdateDialog "FluentWeather $releaseTag is available" $text
                    Write-UpdateLog "Answer: $answer"
                    if ($answer -eq 'Later') { $decision = 'Later' }
                    elseif ($answer -eq 'Skip') { $decision = 'Skip' }
                }
            }
        }

        if ($decision -ne 'Build' -and $decision -ne 'UpToDate') {
            $now = (Get-Date).ToString('s')
            switch ($decision) {
                'Older' {
                    Write-UpdateLog "The newest release tag ($releaseTag) is older than your build ($oldTag). Nothing to do."
                    Save-State @{ LastCheck = $now }
                }
                'Skipped' {
                    Write-UpdateLog "You chose to skip $releaseTag. The automatic check does not ask again for this release."
                    Save-State @{ LastCheck = $now }
                }
                'Later' {
                    Write-UpdateLog "Update to $releaseTag postponed. Your current version $oldTag was kept."
                    Save-State @{ LastCheck = $now; LastResult = "Postponed $releaseTag" }
                }
                'Skip' {
                    Write-UpdateLog "Skipping $releaseTag. Your current version $oldTag was kept."
                    Save-State @{ LastCheck = $now; SkippedTag = $releaseTag; LastResult = "Skipped $releaseTag" }
                }
            }
            Update-TrayFiles $false
            Set-StartupShortcut $startApp
        }
        elseif ($decision -eq 'UpToDate') {
            Write-UpdateLog 'Already up to date. Nothing to do.'
            Save-State @{ LastCheck = (Get-Date).ToString('s') }
            Update-TrayFiles $false
            Set-StartupShortcut $startApp
            if (-not $Quiet) { Show-Toast $TaskTitle "FluentWeather $releaseTag is up to date." }
        }
        else {
            $msbuild  = Get-MSBuildPath
            $platform = Get-BuildPlatform

            Initialize-Source $releaseTag

            Write-UpdateLog "Applying $(Split-Path -Leaf $PatchPath)"
            Invoke-Git @('-C', $SrcDir, 'apply', '--3way', '--whitespace=nowarn', $PatchPath) -FailCode 2 | Out-Null

            $revision = Get-NextRevision $state $installed
            $version = Set-PackageIdentity $config.CertSubject $revision
            Write-UpdateLog "Package version for this build: $version"
            Add-PublishProfile $platform

            $outDir = Join-Path $OutRoot $releaseTag
            if (Test-Path $outDir) { Remove-Item -LiteralPath $outDir -Recurse -Force }
            New-Item -ItemType Directory -Path $outDir -Force | Out-Null

            Invoke-Build $msbuild $platform $outDir $config.CertThumbprint (Join-Path $LogDir "build-$stamp.log")

            $built = Find-BuiltPackage $outDir $platform
            Assert-Signatures $built $config.CertThumbprint

            Write-UpdateLog "Installing $($built.Package.Name)"
            $addArgs = @{
                Path                       = $built.Package.FullName
                ForceApplicationShutdown   = $true
                ForceUpdateFromAnyVersion  = $true
            }
            if ($built.Dependencies.Count -gt 0) { $addArgs.DependencyPath = @($built.Dependencies | ForEach-Object FullName) }
            Add-AppxPackage @addArgs

            $pkg = Get-AppxPackage -Name $PackageName
            if (-not $pkg) { Stop-Step 'Install finished but the app is not registered.' 1 }

            Save-State @{
                Tag            = $releaseTag
                PackageVersion = $version
                BuildRevision  = $revision
                PatchSha256    = $patchHash
                InstalledAt    = (Get-Date).ToString('s')
                LastCheck      = (Get-Date).ToString('s')
                LastResult     = 'Installed'
            }

            if ($StoreTest) {
                Remove-OldFiles $outDir
                Complete-StoreTest $pkg
            }
            else {
                Set-StartupShortcut $startApp
                Update-TrayFiles $true
                Remove-OldFiles $outDir

                Write-UpdateLog "Installed FluentWeather $releaseTag with saved locations."
                Show-Toast $TaskTitle "FluentWeather $releaseTag with saved locations is installed."
            }
        }
    }
}
catch {
    $exitCode = 1
    if ($_.Exception.Data.Contains('ExitCode')) { $exitCode = [int]$_.Exception.Data['ExitCode'] }
    Write-UpdateLog "ERROR: $($_.Exception.Message)"
    if ($exitCode -eq 2) {
        # Count this as a check, so the notice repeats once per interval and not at every sign-in.
        try { Save-State @{ LastCheck = (Get-Date).ToString('s'); LastResult = 'Patch does not apply' } } catch { }
        Write-UpdateLog 'The new release changed the same code as the patch. The installed app was not changed.'
        Write-UpdateLog 'If the developer added a locations feature of their own, switch to the official release.'
        Show-Toast $TaskTitle 'A new FluentWeather release does not accept the saved-locations patch. Your current version was kept.'
    } else {
        Show-Toast $TaskTitle "Update failed. Your current version was kept. Log: $runLog"
    }
}
finally {
    Stop-Relay
    if ($haveLock) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
    Write-UpdateLog "Log: $runLog"
    Stop-Transcript | Out-Null
}

exit $exitCode
