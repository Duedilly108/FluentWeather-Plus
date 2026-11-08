<#
.SYNOPSIS
    One-time setup for the local FluentWeather build with saved locations.

.DESCRIPTION
    Run "Setup.cmd". It asks for administrator approval once, then:

    1. Copies the update files to C:\Program Files\FluentWeatherLocal.
       Only administrators can change files there, so the files that run
       at every logon cannot be changed by other programs running as you.
    2. Installs Git and Visual Studio 2026 Build Tools if they are missing. Both are
       downloaded from their official sites, and each installer's digital signature is
       checked before it runs. No Microsoft account, Store or winget is used.
       The Build Tools download is several GB.
    3. Creates your own code-signing certificate. The private key stays in your
       user certificate store and cannot be exported. The public part is added to
       LocalMachine\TrustedPeople, so Windows accepts packages signed with that key.
    4. Registers the scheduled task "FluentWeather Local Update". It runs at sign-in.
       It checks GitHub at most once every CheckEveryDays days (default 30) and builds
       a new version only when the developer has published a new release.
    5. Compiles the tray helper (FluentWeatherTray.cs) and the network relay
       (FluentWeatherNet.cs) with the C# compiler built into Windows. The tray helper
       is added to your Startup folder and started.
    6. Opens "Update FluentWeather.cmd" for the first build and install.

    Firewall: during updates, git and MSBuild connect only to the relay on 127.0.0.1.
    The relay connects only to the hosts in allowed-hosts.txt. Allow FluentWeatherNet.exe
    and the FluentWeather-Plus app in your firewall. git and MSBuild need no exception.

.PARAMETER StartApp
    Also open the FluentWeather window when you sign in. By default only the tray icons
    start, and a left-click on them opens the app.

.PARAMETER NoStartApp
    Kept for older instructions. The app window no longer opens at sign-in by default.

.PARAMETER CheckEveryDays
    Days between checks for a new release. Default 30. Allowed 1 to 365.

.PARAMETER NoTray
    Do not install the tray helper.

.PARAMETER NoRelay
    Let git and MSBuild connect directly instead of through FluentWeatherNet.exe.
    Use this only if your firewall blocks connections inside the PC (127.0.0.1).

.PARAMETER NoLogonTask
    Do not register the logon task. You then run "Update FluentWeather.cmd" by hand.

.PARAMETER UpdateMode
    Ask (default): when a new release exists, show its release notes and the list of
    changes, and ask before the update. Auto: build and install a new release without
    asking. Off: no automatic checks. A manual check still asks first.

.PARAMETER RemoveStoreVersion
    Uninstall the Microsoft Store copy of FluentWeather, so only one copy is in Start.

.PARAMETER SkipPrerequisites
    Do not install Git or Build Tools. Setup stops if they are missing.

.PARAMETER Uninstall
    Removes the local build, the logon task, the certificate, and all files this setup created.
    Git and Build Tools stay installed.
#>

[CmdletBinding()]
param(
    [switch]$StartApp,
    [switch]$NoStartApp,
    [switch]$NoTray,
    [switch]$NoRelay,
    [ValidateRange(1, 365)]
    [int]$CheckEveryDays = 30,
    [switch]$NoLogonTask,
    [ValidateSet('Ask', 'Auto', 'Off')]
    [string]$UpdateMode = 'Ask',
    [switch]$RemoveStoreVersion,
    [switch]$SkipPrerequisites,
    [switch]$Uninstall,
    [switch]$Elevated   # internal: set when Setup relaunches itself as administrator
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

# Show any unexpected error and keep the administrator window open so it can be read.
trap {
    Write-Host "[x] $($_.Exception.Message)" -ForegroundColor Red
    if ($Elevated) { Read-Host 'Press Enter to close' | Out-Null }
    exit 1
}

$InstallDir    = Join-Path $env:ProgramFiles 'FluentWeatherLocal'
$WorkDir       = Join-Path $env:LOCALAPPDATA 'FluentWeatherLocal'
$ConfigPath    = Join-Path $InstallDir 'config.json'
$TaskName      = 'FluentWeather Local Update'
$PackageName   = 'FluentWeather.LocalBuild'
$StorePackage  = '26937gabboxl.61420B4860E86'
$CertSubject   = 'CN=FluentWeather Local Build'
$FilesToCopy   = @(
    'Setup-FluentWeatherLocal.ps1',
    'Update-FluentWeatherLocal.ps1',
    'FluentWeather-saved-locations.patch',
    'Setup.cmd',
    'Update FluentWeather.cmd',
    'FluentWeatherTray.cs',
    'FluentWeatherNet.cs',
    'allowed-hosts.txt',
    'README.txt',
    'FluentWeatherInstaller.cs',
    'Make-Installer.cmd',
    'FluentWeather.ico',
    'FluentWeatherDirect.cs',
    'FluentWeatherDirect.manifest',
    'Make-DirectInstaller.cmd',
    'Make-DirectInstaller.ps1',
    'Direct-README.txt'
)
$UninstallKey  = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\FluentWeatherLocal'
$SetupVersion  = '65'
$MakeInstallerShortcut = 'Make FluentWeather installer.lnk'
$MakeDirectShortcut    = 'Make FluentWeather direct installer.lnk'

function Write-Step([string]$Text) { Write-Host "[*] $Text" -ForegroundColor Cyan }
function Write-Ok([string]$Text)   { Write-Host "[+] $Text" -ForegroundColor Green }
function Write-Note([string]$Text) { Write-Host "[!] $Text" -ForegroundColor Yellow }

function Stop-Setup([string]$Text) {
    Write-Host "[x] $Text" -ForegroundColor Red
    if ($Elevated) { Read-Host 'Press Enter to close' | Out-Null }
    exit 1
}

function Test-IsAdmin {
    $p = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-SameUser {
    # The certificate, the task and the app all belong to one user account.
    # If a standard user elevates with a different admin account, they would go to the wrong user.
    $me = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $desk = $null
    try { $desk = (Get-CimInstance -ClassName Win32_ComputerSystem).UserName } catch { }
    if ($desk -and ($desk -ine $me)) {
        Stop-Setup ("Setup runs as '$me' but the desktop user is '$desk'. Your own account " +
                    "must be an administrator. Sign in with an administrator account and run Setup again.")
    }
}

function Get-MSBuildPath {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { return $null }
    return & $vswhere -latest -products * -version '[18.0,)' `
        -requiresAny -requires Microsoft.VisualStudio.Workload.UniversalBuildTools Microsoft.VisualStudio.Workload.Universal `
        -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
}

function Get-OsArch {
    try { return [string][System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture } catch { return 'X64' }
}

function Update-PathFromRegistry {
    $machine = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $user = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = "$machine;$user"
}

# Downloads use only official sources. Each installer's digital signature is checked
# before it runs. No Microsoft account, Store or winget is needed.
$GitReleaseApi   = 'https://api.github.com/repos/git-for-windows/git/releases/latest'
$GitSigner       = 'CN=Johannes Schindelin*'       # Git for Windows maintainer
$BuildToolsUrl   = 'https://aka.ms/vs/stable/vs_buildtools.exe'   # Visual Studio 2026 Build Tools
$MicrosoftSigner = 'CN=Microsoft Corporation*'
$DownloadDir     = Join-Path $env:TEMP 'FluentWeatherLocalSetup'

function Initialize-Download {
    # Windows PowerShell 5.1 may default to old TLS versions. GitHub and Microsoft need TLS 1.2.
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $script:ProgressPreference = 'SilentlyContinue'   # the progress bar makes downloads very slow in 5.1
    New-Item -ItemType Directory -Path $DownloadDir -Force | Out-Null
}

function Save-Download([string]$Url, [string]$FileName) {
    $path = Join-Path $DownloadDir $FileName
    Write-Step "Downloading $FileName"
    Invoke-WebRequest -Uri $Url -OutFile $path -UseBasicParsing
    return $path
}

function Assert-Signed([string]$Path, [string]$SignerPattern, [bool]$DeleteOnFail = $true) {
    $sig = Get-AuthenticodeSignature -FilePath $Path
    $subject = if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { '(none)' }
    if ($sig.Status -ne 'Valid' -or $subject -notlike $SignerPattern) {
        if ($DeleteOnFail) {
            Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
            Stop-Setup "Signature check failed for $(Split-Path $Path -Leaf). Status: $($sig.Status). Signer: $subject. The file was deleted."
        }
        Stop-Setup "Signature check failed for $Path. Status: $($sig.Status). Signer: $subject. Do not run this file."
    }
    Write-Ok "Signature valid: $(Split-Path $Path -Leaf), signed by $($sig.SignerCertificate.GetNameInfo('SimpleName', $false))."
}

# Installers you downloaded with a browser. Setup looks in its own folder and in Downloads.
# This avoids PowerShell downloads, which a firewall may block on purpose.
function Find-LocalInstaller([string]$Pattern) {
    $folders = @($PSScriptRoot, (Join-Path $env:USERPROFILE 'Downloads')) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
    return Get-ChildItem -LiteralPath $folders -File -ErrorAction SilentlyContinue |
           Where-Object { $_.Name -match $Pattern } |
           Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

function Get-DownloadHelp {
    return ("Setup could not download the installers. A firewall rule that blocks PowerShell is a likely cause. Keep that rule.`n" +
            "Download these two files with your web browser instead, into your Downloads folder or next to Setup.cmd:`n" +
            "  Git for Windows, 64-bit installer:  https://git-scm.com/download/win`n" +
            "  Visual Studio 2026 Build Tools:     $BuildToolsUrl`n" +
            "Then run Setup.cmd again. Setup checks each file's digital signature before it runs it.")
}

function Install-Git {
    $pattern = if ((Get-OsArch) -eq 'Arm64') { '^Git-[\d.]+-arm64\.exe$' } else { '^Git-[\d.]+-64-bit\.exe$' }
    $local = Find-LocalInstaller $pattern
    if ($local) {
        Write-Step "Using the Git installer you downloaded: $($local.FullName)"
        Assert-Signed $local.FullName $GitSigner $false
        Invoke-GitInstaller $local.FullName $local.Name
        return
    }

    try {
        $release = Invoke-RestMethod -Uri $GitReleaseApi -UseBasicParsing
    } catch {
        Stop-Setup ("Could not reach GitHub: $($_.Exception.Message)`n" + (Get-DownloadHelp))
    }
    $pattern = if ((Get-OsArch) -eq 'Arm64') { '^Git-[\d.]+-arm64\.exe$' } else { '^Git-[\d.]+-64-bit\.exe$' }
    $asset = $release.assets | Where-Object { $_.name -match $pattern } | Select-Object -First 1
    if (-not $asset) { Stop-Setup "Could not find the Git installer in release $($release.tag_name)." }

    try {
        $path = Save-Download $asset.browser_download_url $asset.name
    } catch {
        Stop-Setup ("Could not download Git: $($_.Exception.Message)`n" + (Get-DownloadHelp))
    }

    # GitHub publishes a SHA256 digest for release files. Check it when it is present.
    if ($asset.PSObject.Properties.Match('digest').Count -gt 0 -and "$($asset.digest)" -like 'sha256:*') {
        $expected = "$($asset.digest)".Substring(7)
        $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($actual -ine $expected) {
            Remove-Item -LiteralPath $path -Force
            Stop-Setup "Git installer hash does not match GitHub's published value. The file was deleted."
        }
        Write-Ok 'Git installer SHA256 matches the value published on GitHub.'
    }
    Assert-Signed $path $GitSigner
    Invoke-GitInstaller $path $release.tag_name
}

function Invoke-GitInstaller([string]$Path, [string]$Label) {
    Write-Step "Installing Git ($Label)"
    $p = Start-Process -FilePath $Path -ArgumentList '/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait -PassThru
    if ($p.ExitCode -ne 0) { Stop-Setup "Git installer failed (exit code $($p.ExitCode))." }
    Update-PathFromRegistry
}

function Install-BuildTools {
    $local = Find-LocalInstaller '^vs_buildtools.*\.exe$'
    if ($local) {
        Write-Step "Using the Build Tools installer you downloaded: $($local.FullName)"
        Assert-Signed $local.FullName $MicrosoftSigner $false
        $path = $local.FullName
    } else {
        try {
            $path = Save-Download $BuildToolsUrl 'vs_buildtools.exe'
        } catch {
            Stop-Setup ("Could not download Build Tools: $($_.Exception.Message)`n" + (Get-DownloadHelp))
        }
        Assert-Signed $path $MicrosoftSigner
    }

    $vsArgs = @(
        '--passive', '--wait', '--norestart', '--includeRecommended',
        '--add', 'Microsoft.VisualStudio.Workload.UniversalBuildTools',
        '--add', 'Microsoft.VisualStudio.Component.Windows11SDK.26100',
        '--add', 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64'
    )
    if ((Get-OsArch) -eq 'Arm64') { $vsArgs += @('--add', 'Microsoft.VisualStudio.Component.VC.Tools.ARM64') }

    Write-Step 'Installing Visual Studio 2026 Build Tools. This downloads several GB and can take 20 minutes or more.'
    $p = Start-Process -FilePath $path -ArgumentList $vsArgs -Wait -PassThru
    switch ($p.ExitCode) {
        0       { }
        3010    { Write-Note 'Build Tools need a Windows restart to finish. Setup continues now.' }
        1641    { Stop-Setup 'Build Tools started a restart. Run Setup.cmd again after Windows restarts.' }
        default { Stop-Setup ("Build Tools installer failed (exit code $($p.ExitCode)). " +
                              'Logs are in %TEMP%, in files that start with dd_bootstrapper, dd_client and dd_setup.') }
    }
}

function Install-Prerequisites {
    $haveGit = (Get-Command git.exe -ErrorAction SilentlyContinue) -or
               (Test-Path (Join-Path $env:ProgramFiles 'Git\cmd\git.exe'))
    $haveBuildTools = [bool](Get-MSBuildPath)

    if ((-not $haveGit -or -not $haveBuildTools) -and $SkipPrerequisites) {
        Stop-Setup ("Missing: " + (@(if (-not $haveGit) { 'Git (https://git-scm.com/download/win)' }
                                     if (-not $haveBuildTools) { "Visual Studio 2026 Build Tools ($BuildToolsUrl)" }) -join ', '))
    }

    if (-not $haveGit -or -not $haveBuildTools) { Initialize-Download }
    try {
        if (-not $haveGit) { Install-Git }
        Write-Ok 'Git is installed.'

        if (-not $haveBuildTools) {
            Install-BuildTools
            if (-not (Get-MSBuildPath)) {
                Stop-Setup 'Build Tools were installed but the UWP build tools were not found. Restart Windows and run Setup again.'
            }
        }
        Write-Ok 'Visual Studio Build Tools are installed.'
    }
    finally {
        if (Test-Path $DownloadDir) { Remove-Item -LiteralPath $DownloadDir -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

function Read-Config {
    if (-not (Test-Path $ConfigPath)) { return $null }
    try { return Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json } catch { return $null }
}

function Get-OrCreateCertificate {
    $config = Read-Config
    if ($config -and @($config.PSObject.Properties.Match('CertThumbprint')).Count -gt 0) {
        $existing = Get-Item -LiteralPath "Cert:\CurrentUser\My\$($config.CertThumbprint)" -ErrorAction SilentlyContinue
        if ($existing -and $existing.HasPrivateKey -and $existing.NotAfter -gt (Get-Date).AddDays(60)) {
            Write-Ok "Using your existing signing certificate ($($existing.Thumbprint))."
            return $existing
        }
        if ($existing) { Remove-TrustedCertificate $existing.Thumbprint }
    }

    Write-Step 'Creating your signing certificate'
    # Settings follow Microsoft's guide for MSIX test certificates:
    # code-signing usage only (1.3.6.1.5.5.7.3.3) and not a certificate authority (2.5.29.19).
    $cert = New-SelfSignedCertificate -Type Custom `
        -Subject $CertSubject `
        -FriendlyName 'FluentWeather local build signing' `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
        -KeyExportPolicy NonExportable `
        -NotAfter (Get-Date).AddYears(5) `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    Write-Ok "Created certificate $($cert.Thumbprint), valid until $($cert.NotAfter.ToString('yyyy-MM-dd'))."
    return $cert
}

function Add-TrustedCertificate($Cert) {
    # Add the public certificate only. The private key never leaves your user store.
    $public = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(, $Cert.RawData)
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store('TrustedPeople', 'LocalMachine')
    $store.Open('ReadWrite')
    try {
        if (-not ($store.Certificates | Where-Object Thumbprint -eq $public.Thumbprint)) { $store.Add($public) }
    } finally { $store.Close() }
    Write-Ok 'Your certificate is trusted for app packages (LocalMachine\TrustedPeople).'
}

function Remove-TrustedCertificate([string]$Thumbprint) {
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store('TrustedPeople', 'LocalMachine')
    $store.Open('ReadWrite')
    try {
        $store.Certificates | Where-Object Thumbprint -eq $Thumbprint | ForEach-Object { $store.Remove($_) }
    } finally { $store.Close() }
}

function Copy-InstallFiles {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    $source = (Resolve-Path -LiteralPath $PSScriptRoot).Path.TrimEnd('\')
    if ($source -ieq $InstallDir.TrimEnd('\')) { return }
    foreach ($name in $FilesToCopy) {
        $from = Join-Path $source $name
        if (-not (Test-Path -LiteralPath $from)) { Stop-Setup "Missing file next to Setup: $name" }
        $to = Join-Path $InstallDir $name
        Copy-Item -LiteralPath $from -Destination $to -Force
        # Remove the "downloaded from the internet" mark so Windows does not prompt at every logon.
        Unblock-File -LiteralPath $to
    }
    Write-Ok "Files copied to $InstallDir"
}

function Register-UpdateTask {
    $user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $script = Join-Path $InstallDir 'Update-FluentWeatherLocal.ps1'
    $action = New-ScheduledTaskAction -Execute (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') `
        -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`" -Scheduled -Quiet"
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
    $trigger.Delay = 'PT3M'   # wait 3 minutes after sign-in so startup is not slowed
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -RunOnlyIfNetworkAvailable `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew `
        -ExecutionTimeLimit (New-TimeSpan -Hours 2)
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal `
        -Settings $settings -Description "Checks for a new FluentWeather release every $CheckEveryDays days. Update mode: $UpdateMode." `
        -Force | Out-Null
    Write-Ok "Scheduled task '$TaskName' runs at sign-in without administrator rights. It checks GitHub at most every $CheckEveryDays days."
}

$TrayExeName  = 'FluentWeatherTray.exe'
$TrayShortcut = 'FluentWeather Tray.lnk'
$TrayMenuShortcut = 'FluentWeather tray icons.lnk'
$RestartShortcut = 'Restart FluentWeather.lnk'

function Stop-TrayHelper { Stop-HelperProcess $TrayExeName }

$RelayExeName = 'FluentWeatherNet.exe'

function Stop-HelperProcess([string]$ExeName) {
    Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($ExeName)) -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
}

# Compiles a C# file with the compiler that ships with .NET Framework 4.x in Windows.
# Returns $true on success.
function Invoke-CSharpCompile([string]$SourceName, [string]$ExeName, [string[]]$References, [string[]]$Extra = @(), [switch]$QuietFail) {
    $source = Join-Path $InstallDir $SourceName
    $exe = Join-Path $InstallDir $ExeName
    $csc = @(
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $csc) {
        Write-Note "The .NET Framework C# compiler was not found. $ExeName was not built."
        return $false
    }

    Stop-HelperProcess $ExeName   # a running copy would lock the file
    Write-Step "Compiling $ExeName"
    $cscArgs = @('/nologo', '/target:winexe', '/optimize+', "/out:$exe") + @($Extra) +
               @($References | ForEach-Object { "/reference:$_" }) + @($source)
    $ErrorActionPreference = 'Continue'
    $output = & $csc @cscArgs 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($code -ne 0 -or -not (Test-Path -LiteralPath $exe)) {
        if (-not $QuietFail) {
            $output | ForEach-Object { Write-Host "    $_" }
            Write-Note "$ExeName did not compile (exit code $code)."
        }
        return $false
    }
    return $true
}

function Install-Relay {
    if (-not (Invoke-CSharpCompile 'FluentWeatherNet.cs' $RelayExeName @())) {
        Stop-Setup 'The network relay did not compile. Run Setup again with -NoRelay to use direct connections.'
    }
    Write-Ok "Network relay built: $(Join-Path $InstallDir $RelayExeName)"
}

# The Windows Runtime references the tray helper needs for its 5-minute refresh (app service client).
# Windows.winmd comes from the Windows SDK that Build Tools installed. The .NET Framework adds the rest.
function Get-WinRtReferences {
    $refs = @()
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\UnionMetadata'
    $union = Get-ChildItem -LiteralPath $kits -Filter 'Windows.winmd' -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\Facade\\' } |
        Sort-Object FullName -Descending | Select-Object -First 1
    if ($union) {
        $refs += $union.FullName
    } else {
        foreach ($name in 'Windows.Foundation.winmd', 'Windows.ApplicationModel.winmd') {
            $path = Join-Path $env:WINDIR "System32\WinMetadata\$name"
            if (Test-Path -LiteralPath $path) { $refs += $path }
        }
        if ($refs.Count -lt 2) { return @() }
    }
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    if (-not (Test-Path -LiteralPath $framework)) { $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
    $runtime = Join-Path $framework 'System.Runtime.WindowsRuntime.dll'
    if (-not (Test-Path -LiteralPath $runtime)) { return @() }
    $refs += $runtime
    foreach ($name in 'System.Runtime.dll', 'System.Threading.Tasks.dll', 'System.Runtime.InteropServices.WindowsRuntime.dll') {
        $path = Join-Path $framework $name
        if (Test-Path -LiteralPath $path) { $refs += $path }
    }
    return $refs
}

function Install-TrayHelper {
    $exe = Join-Path $InstallDir $TrayExeName
    $baseRefs = @('System.Windows.Forms.dll', 'System.Drawing.dll', 'System.Web.Extensions.dll')
    $winRt = @(Get-WinRtReferences)
    $built = $false
    if ($winRt.Count -gt 0) {
        $built = Invoke-CSharpCompile 'FluentWeatherTray.cs' $TrayExeName ($baseRefs + $winRt) @('/define:WINRT') -QuietFail
        if ($built) { Write-Ok 'Tray icons: 5-minute updates while the app window is closed are available.' }
    }
    if (-not $built) {
        if ($winRt.Count -gt 0) { Write-Note 'The 5-minute tray updates could not be compiled. The tray uses the 15-minute background updates.' }
        $built = Invoke-CSharpCompile 'FluentWeatherTray.cs' $TrayExeName $baseRefs
    }
    if (-not $built) {
        Write-Note 'The tray helper was skipped. Everything else still works.'
        return
    }

    # Start at sign-in from your own Startup folder.
    $lnk = Join-Path ([Environment]::GetFolderPath('Startup')) $TrayShortcut
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($lnk)
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = $InstallDir
    $shortcut.Description = 'FluentWeather tray icon'
    $shortcut.Save()

    # Start menu item to start the tray icons again after "Exit tray icons".
    $menuLnk = Join-Path ([Environment]::GetFolderPath('Programs')) $TrayMenuShortcut
    $menuShortcut = $shell.CreateShortcut($menuLnk)
    $menuShortcut.TargetPath = $exe
    $menuShortcut.WorkingDirectory = $InstallDir
    $menuShortcut.Description = 'Start the FluentWeather tray icons'
    $menuShortcut.Save()

    # Start menu item that closes the app and the tray icons, then starts both again.
    $restartLnk = Join-Path ([Environment]::GetFolderPath('Programs')) $RestartShortcut
    $restartShortcut = $shell.CreateShortcut($restartLnk)
    $restartShortcut.TargetPath = $exe
    $restartShortcut.Arguments = '/restart'
    $restartShortcut.WorkingDirectory = $InstallDir
    $restartShortcut.Description = 'Close FluentWeather and its tray icons, then start both again'
    $restartShortcut.Save()

    # Start it now as the signed-in user, not as administrator.
    Start-Process -FilePath (Join-Path $env:WINDIR 'explorer.exe') -ArgumentList "`"$exe`""
    Write-Ok 'Tray icons installed. They start at sign-in. Start menu: "FluentWeather tray icons" and "Restart FluentWeather".'
    Write-Note 'If you do not see them, click the ^ arrow in the notification area. Windows puts new icons there first.'
}

function Write-Config($Cert) {
    [pscustomobject]@{
        CertThumbprint  = $Cert.Thumbprint
        CertSubject     = $Cert.Subject
        StartAppAtLogon = [bool]$StartApp -and -not [bool]$NoStartApp
        CheckEveryDays  = $CheckEveryDays
        UseRelay        = -not [bool]$NoRelay
        UpdateMode      = $UpdateMode
        CreatedAt       = (Get-Date).ToString('s')
    } | ConvertTo-Json | Set-Content -LiteralPath $ConfigPath -Encoding UTF8
}

# Adds "FluentWeather local build" to Settings > Apps, so it can be removed from there.
# The FluentWeather-Plus app has its own entry. Removing that one removes only the app.
function Register-UninstallEntry {
    $setupPath = Join-Path $InstallDir 'Setup-FluentWeatherLocal.ps1'
    $powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    New-Item -Path $UninstallKey -Force | Out-Null
    $values = @{
        DisplayName     = 'FluentWeather-Plus build tools (setup and tray icons)'
        DisplayVersion  = $SetupVersion
        Publisher       = 'Local build'
        InstallLocation = $InstallDir
        DisplayIcon     = (Join-Path $InstallDir 'FluentWeather.ico')
        UninstallString = "`"$powershell`" -NoProfile -ExecutionPolicy Bypass -File `"$setupPath`" -Uninstall"
        Comments        = 'Removes the app, the tray icons, the update task, the signing certificate and the files. Git and Build Tools stay installed.'
    }
    foreach ($name in $values.Keys) {
        New-ItemProperty -Path $UninstallKey -Name $name -Value $values[$name] -PropertyType String -Force | Out-Null
    }
    foreach ($name in 'NoModify', 'NoRepair') {
        New-ItemProperty -Path $UninstallKey -Name $name -Value 1 -PropertyType DWord -Force | Out-Null
    }
    Write-Ok 'Added "FluentWeather local build" to Settings > Apps. Uninstall it there to remove everything.'
}

# Start menu item that builds the single-file installer on the Desktop.
function Add-MakeInstallerShortcut {
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) $MakeInstallerShortcut))
    $lnk.TargetPath = Join-Path $InstallDir 'Make-Installer.cmd'
    $lnk.WorkingDirectory = $InstallDir
    $lnk.IconLocation = (Join-Path $InstallDir 'FluentWeather.ico') + ',0'
    $lnk.Description = 'Build FluentWeatherLocal-Installer.exe on your Desktop, to share with others'
    $lnk.Save()

    # Makes FluentWeather-Setup.exe from the app built on this PC. Others install it without building.
    $direct = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) $MakeDirectShortcut))
    $direct.TargetPath = Join-Path $InstallDir 'Make-DirectInstaller.cmd'
    $direct.WorkingDirectory = $InstallDir
    $direct.IconLocation = (Join-Path $InstallDir 'FluentWeather.ico') + ',0'
    $direct.Description = 'Make FluentWeather-Setup.exe and FluentWeather-Uninstall.exe on your Desktop from the app built on this PC'
    $direct.Save()
}

function Invoke-Uninstall {
    Write-Step 'Removing the local FluentWeather build'
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Stop-TrayHelper
    Stop-HelperProcess $RelayExeName
    $trayLnk = Join-Path ([Environment]::GetFolderPath('Startup')) $TrayShortcut
    if (Test-Path $trayLnk) { Remove-Item -LiteralPath $trayLnk -Force }
    $trayMenuLnk = Join-Path ([Environment]::GetFolderPath('Programs')) $TrayMenuShortcut
    if (Test-Path $trayMenuLnk) { Remove-Item -LiteralPath $trayMenuLnk -Force }
    $restartLnk = Join-Path ([Environment]::GetFolderPath('Programs')) $RestartShortcut
    if (Test-Path $restartLnk) { Remove-Item -LiteralPath $restartLnk -Force }
    $makeLnk = Join-Path ([Environment]::GetFolderPath('Programs')) $MakeInstallerShortcut
    if (Test-Path $makeLnk) { Remove-Item -LiteralPath $makeLnk -Force }
    $directLnk = Join-Path ([Environment]::GetFolderPath('Programs')) $MakeDirectShortcut
    if (Test-Path $directLnk) { Remove-Item -LiteralPath $directLnk -Force }
    if (Test-Path $UninstallKey) { Remove-Item -LiteralPath $UninstallKey -Recurse -Force }
    Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue | Remove-AppxPackage
    foreach ($name in @('FluentWeather-Plus.lnk', 'FluentWeather (Local).lnk')) {
        $lnk = Join-Path ([Environment]::GetFolderPath('Startup')) $name
        if (Test-Path $lnk) { Remove-Item -LiteralPath $lnk -Force }
    }

    $config = Read-Config
    if ($config -and @($config.PSObject.Properties.Match('CertThumbprint')).Count -gt 0) {
        Remove-TrustedCertificate $config.CertThumbprint
        Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($config.CertThumbprint)" -DeleteKey -ErrorAction SilentlyContinue
    }
    if (Test-Path $WorkDir) { Remove-Item -LiteralPath $WorkDir -Recurse -Force }
    if (Test-Path $InstallDir) { Remove-Item -LiteralPath $InstallDir -Recurse -Force }
    Write-Ok 'Removed the app, task, certificate and files. Git and Build Tools are still installed.'
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

if ($PSVersionTable.PSEdition -ne 'Desktop') {
    Write-Host 'Run Setup.cmd. It uses Windows PowerShell (powershell.exe).'
    exit 1
}

if (-not (Test-IsAdmin)) {
    # Relaunch as administrator with the same options.
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Elevated')
    foreach ($key in $PSBoundParameters.Keys) {
        $value = $PSBoundParameters[$key]
        if ($value -is [switch]) { if ($value) { $argList += "-$key" } }
        else { $argList += @("-$key", "$value") }
    }
    Write-Host 'Asking for administrator approval...'
    try {
        # Wait for the administrator copy, so a caller (such as the single-file installer) knows when it is done.
        $elevatedRun = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') `
            -ArgumentList $argList -Verb RunAs -PassThru -Wait
    } catch {
        Write-Host 'Administrator approval was declined. Nothing was changed.'
        exit 1
    }
    exit $elevatedRun.ExitCode
}

Assert-SameUser

if ($Uninstall) {
    Invoke-Uninstall
}
else {
    Copy-InstallFiles
    Install-Prerequisites

    $cert = Get-OrCreateCertificate
    Add-TrustedCertificate $cert
    Write-Config $cert

    # FluentWeather-Plus builds on FluentWeather 2.1.0 only, so there is nothing to check for.
    # Earlier setups registered a logon task that checked for new releases. It is removed here.
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Note 'No automatic update checks. New versions are on the GitHub releases page.'

    if (-not $NoRelay) { Install-Relay }

    if ($NoTray) {
        Stop-TrayHelper
        $trayLnk = Join-Path ([Environment]::GetFolderPath('Startup')) $TrayShortcut
        if (Test-Path $trayLnk) { Remove-Item -LiteralPath $trayLnk -Force }
        $trayMenuLnk = Join-Path ([Environment]::GetFolderPath('Programs')) $TrayMenuShortcut
        if (Test-Path $trayMenuLnk) { Remove-Item -LiteralPath $trayMenuLnk -Force }
        $restartLnk = Join-Path ([Environment]::GetFolderPath('Programs')) $RestartShortcut
        if (Test-Path $restartLnk) { Remove-Item -LiteralPath $restartLnk -Force }
    } else {
        Install-TrayHelper
    }

    Register-UninstallEntry
    Add-MakeInstallerShortcut

    if ($RemoveStoreVersion) {
        Get-AppxPackage -Name $StorePackage -ErrorAction SilentlyContinue | Remove-AppxPackage
        Write-Ok 'Removed the Microsoft Store copy of FluentWeather.'
    }

    # Start the first build in a normal (not elevated) window. Explorer opens files as the signed-in user.
    $updateCmd = Join-Path $InstallDir 'Update FluentWeather.cmd'
    Write-Step 'Opening the first build in a new window. It takes several minutes.'
    Start-Process -FilePath (Join-Path $env:WINDIR 'explorer.exe') -ArgumentList "`"$updateCmd`""

    Write-Ok 'Setup is complete.'
    Write-Host ''
    Write-Host 'Firewall (TinyWall): allow only these two programs permanently:' -ForegroundColor Cyan
    if (-not $NoRelay) {
        Write-Host "  $(Join-Path $InstallDir $RelayExeName)   (update downloads, GitHub and NuGet only)"
    } else {
        Write-Host '  git-remote-https.exe and MSBuild.exe   (direct connections, -NoRelay was used)'
    }
    Write-Host '  FluentWeather-Plus      (the app, for weather data)'
    Write-Host '  The tray icon, git, MSBuild and PowerShell need no exception.'
}

if ($Elevated) { Read-Host 'Press Enter to close' | Out-Null }
