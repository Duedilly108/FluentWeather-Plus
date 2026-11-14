<#
.SYNOPSIS
    Makes FluentWeather-Plus-Setup-<version>.exe, FluentWeather-Plus-Uninstall-<version>.exe and README.txt from the app that is
    built and installed on this PC.

.DESCRIPTION
    Run it with "Make FluentWeather direct installer" in the Start menu, or with
    Make-DirectInstaller.cmd. It runs as the signed-in user and needs no administrator rights.
    It makes no internet connection.

    It collects:
      - the newest built app package (.msix) of the installed version and the Microsoft packages it needs,
      - a copy of that package, signed again with a new throwaway certificate (see below),
      - the tray icons program FluentWeatherTray.exe that Setup compiled on this PC,
      - Direct-README.txt, the README for recipients.
    It compiles FluentWeatherDirect.cs twice with the C# compiler that ships with Windows and writes
    the result to a new folder "FluentWeather-Plus" on the Desktop, plus a zip of that folder.
    It also writes FluentWeather-Plus-<version>-release-notes.txt on the Desktop. That file holds the
    text for the GitHub release page from Release-notes.txt, with the VirusTotal links of both files.

    The throwaway certificate: your own signing certificate is never used and never leaves this
    PC. Each run creates a new certificate with the same name, signs the copy of the app with it,
    exports its public part (.cer) for the setup, and then deletes the certificate and its private
    key from this PC. Nobody can sign anything with it after that, so the PCs that trust it can
    only ever accept this one package.

    The setup holds no saved locations, settings or logs of this PC.
#>

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$InstallDir  = $PSScriptRoot
$WorkDir     = Join-Path $env:LOCALAPPDATA 'FluentWeatherLocal'
$PackageName = 'FluentWeather.LocalBuild'

function Write-Step([string]$Text) { Write-Host "[*] $Text" -ForegroundColor Cyan }
function Write-Ok([string]$Text)   { Write-Host "[+] $Text" -ForegroundColor Green }
function Stop-Make([string]$Text)  { Write-Host "[x] $Text" -ForegroundColor Red; exit 1 }

function Get-DesktopPath {
    $desk = [Environment]::GetFolderPath('Desktop')
    if (-not $desk -or -not (Test-Path -LiteralPath $desk)) { $desk = $env:USERPROFILE }
    return $desk
}

# Finds Windows.winmd and the .NET facade assemblies that the WinRT calls (PackageManager) need.
function Get-WinRTReferences {
    $refs = @()

    # The Windows Runtime types. Windows keeps their full descriptions in System32\WinMetadata.
    # The Windows SDK copy is used only if that folder is missing. Its Facade copy holds only
    # pointers to other files, so it is skipped.
    $osMeta = Join-Path $env:WINDIR 'System32\WinMetadata'
    $metaFiles = @()
    if (Test-Path -LiteralPath $osMeta) {
        $metaFiles = @(Get-ChildItem -LiteralPath $osMeta -Filter '*.winmd' -File -ErrorAction SilentlyContinue |
            ForEach-Object { $_.FullName })
    }
    if ($metaFiles -notcontains (Join-Path $osMeta 'Windows.Management.winmd')) {
        $unionRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\UnionMetadata'
        $winmd = $null
        if (Test-Path -LiteralPath $unionRoot) {
            $winmd = Get-ChildItem -Path $unionRoot -Recurse -Filter 'Windows.winmd' -ErrorAction SilentlyContinue |
                Where-Object { $_.DirectoryName -notmatch '\\Facade$' } |
                Sort-Object FullName -Descending | Select-Object -First 1
        }
        if (-not $winmd) { Stop-Make 'The Windows Runtime type files were not found in System32\WinMetadata or in the Windows SDK.' }
        $metaFiles = @($winmd.FullName)
    }
    $refs += $metaFiles

    # The facade assemblies let .NET Framework call the Windows Runtime and await its operations.
    # They are in the .NET Framework targeting pack if it is installed, and always in the .NET
    # Framework folder and the global assembly cache of Windows. The first match is used.
    $folders = @()
    $netRoot = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework'
    if (Test-Path -LiteralPath $netRoot) {
        $folders += @(Get-ChildItem -Path $netRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^v4\.\d' } |
            Sort-Object { [version]($_.Name.TrimStart('v')) } -Descending |
            ForEach-Object { Join-Path $_.FullName 'Facades' })
    }
    $folders += (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319')
    $folders += (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319')
    $gac = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL'

    foreach ($f in 'System.Runtime.dll', 'System.Runtime.InteropServices.WindowsRuntime.dll') {
        $found = $null
        foreach ($d in $folders) {
            $p = Join-Path $d $f
            if (Test-Path -LiteralPath $p) { $found = $p; break }
        }
        if (-not $found -and (Test-Path -LiteralPath $gac)) {
            $name = [IO.Path]::GetFileNameWithoutExtension($f)
            $found = Get-ChildItem -Path (Join-Path $gac $name) -Recurse -Filter $f -ErrorAction SilentlyContinue |
                Where-Object { $_.DirectoryName -match '\\v4\.0_' } |
                Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
        }
        if (-not $found) { Stop-Make "A .NET Framework file that the build needs was not found: $f" }
        $refs += $found
    }
    return $refs
}

$winRTRefs = Get-WinRTReferences

# Compiles FluentWeatherDirect.cs. $Resources maps a file path to its embedded name.
function Invoke-Compile([string]$Out, [string[]]$Defines, $Resources) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path -LiteralPath $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
    if (-not (Test-Path -LiteralPath $csc)) { Stop-Make 'The C# compiler of .NET Framework 4 was not found.' }

    $cscArgs = @('/nologo', '/target:winexe', '/optimize+', "/out:$Out",
        '/r:System.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll')
    foreach ($r in $winRTRefs) { $cscArgs += "/r:$r" }
    $manifest = Join-Path $InstallDir 'FluentWeatherDirect.manifest'
    if (Test-Path -LiteralPath $manifest) { $cscArgs += "/win32manifest:$manifest" }
    $ico = Join-Path $InstallDir 'FluentWeather.ico'
    if (Test-Path -LiteralPath $ico) { $cscArgs += "/win32icon:$ico" }
    foreach ($d in $Defines) { $cscArgs += "/define:$d" }
    foreach ($r in $Resources) { $cscArgs += "/resource:$($r.Path),payload/$($r.Name)" }
    if ($script:VersionInfoPath) { $cscArgs += $script:VersionInfoPath }
    $cscArgs += (Join-Path $InstallDir 'FluentWeatherDirect.cs')

    $ErrorActionPreference = 'Continue'
    $output = & $csc @cscArgs 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($code -ne 0) {
        $output | ForEach-Object { Write-Host "    $_" }
        Stop-Make "Compiling $(Split-Path -Leaf $Out) failed."
    }
}

if ($PSVersionTable.PSEdition -ne 'Desktop') { Stop-Make 'Run Make-DirectInstaller.cmd. It uses Windows PowerShell (powershell.exe).' }

# --- Collect ------------------------------------------------------------------------------------

$configPath = Join-Path $InstallDir 'config.json'
if (-not (Test-Path -LiteralPath $configPath)) { Stop-Make "Setup has not been run. $configPath is missing." }
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$thumb = [string]$config.CertThumbprint

$pkg = Get-AppxPackage -Name $PackageName | Select-Object -First 1
if (-not $pkg) { Stop-Make 'FluentWeather-Plus is not installed on this PC. Run "Update FluentWeather.cmd" first.' }
$version = [string]$pkg.Version
# The FluentWeather-Plus version is the fourth part of the build number: 2.1.0.49 is v49.
# It names the files, the README and the setup window.
$PlusVersion = 'v' + ([version]$version).Revision
$arch = ([string]$pkg.Architecture).ToLowerInvariant()
Write-Step "Installed app: $($pkg.PackageFullName)"

$outRoot = Join-Path $WorkDir 'out'
if (-not (Test-Path -LiteralPath $outRoot)) { Stop-Make "No build output was found in $outRoot." }
$candidates = @(Get-ChildItem -LiteralPath $outRoot -Recurse -File |
    Where-Object { $_.Extension -in '.msix', '.appx' -and $_.FullName -notmatch '\\Dependencies\\' } |
    Sort-Object LastWriteTime -Descending)
$msix = $candidates | Where-Object { $_.Name -like "*_$($version)_*" } | Select-Object -First 1
if (-not $msix) { Stop-Make "The built package of the installed version $version was not found in $outRoot. Run ""Update FluentWeather.cmd"" -Force to build it again." }
$sig = Get-AuthenticodeSignature -FilePath $msix.FullName
if (-not $sig.SignerCertificate -or $sig.SignerCertificate.Thumbprint -ne $thumb) {
    Stop-Make "$($msix.Name) is not signed with this PC's FluentWeather certificate."
}
Write-Ok "App package: $($msix.FullName)"

$depDir = Join-Path $msix.DirectoryName "Dependencies\$arch"
$deps = @()
if (Test-Path -LiteralPath $depDir) {
    $deps = @(Get-ChildItem -LiteralPath $depDir -File -Recurse | Where-Object { $_.Extension -in '.appx', '.msix' })
}
Write-Ok "Microsoft packages it needs: $($deps.Count)"

$tray = Join-Path $InstallDir 'FluentWeatherTray.exe'
if (-not (Test-Path -LiteralPath $tray)) { Stop-Make 'FluentWeatherTray.exe was not found. Run Setup.cmd again.' }

$subject = [string]$config.CertSubject
if (-not $subject) { Stop-Make 'config.json has no certificate name. Run Setup.cmd again.' }

# makeappx.exe and signtool.exe come with the Windows SDK of Visual Studio Build Tools.
$kitBins = @(Get-ChildItem -Path (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin') -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^10\.' } | Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName 'x64' } |
    Where-Object { (Test-Path -LiteralPath (Join-Path $_ 'makeappx.exe')) -and (Test-Path -LiteralPath (Join-Path $_ 'signtool.exe')) })
if ($kitBins.Count -eq 0) { Stop-Make 'makeappx.exe and signtool.exe of the Windows SDK were not found. Run Setup.cmd again.' }
$makeappx = Join-Path $kitBins[0] 'makeappx.exe'
$signtool = Join-Path $kitBins[0] 'signtool.exe'

function Invoke-Tool([string]$Exe, [string[]]$ToolArgs, [string]$What) {
    $ErrorActionPreference = 'Continue'
    $output = & $Exe @ToolArgs 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($code -ne 0) {
        $output | Select-Object -Last 15 | ForEach-Object { Write-Host "    $_" }
        throw "$What failed (exit code $code)."
    }
}

$stage = Join-Path ([IO.Path]::GetTempPath()) ('FluentWeather-Direct-' + [Guid]::NewGuid().ToString('N').Substring(0, 12))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    # --- Throwaway certificate ---------------------------------------------------------------
    Write-Step 'Creating a throwaway signing certificate for this installer'
    $temp = New-SelfSignedCertificate -Type Custom `
        -Subject $subject `
        -FriendlyName 'FluentWeather direct installer (deleted after use)' `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
        -KeyExportPolicy NonExportable `
        -NotAfter (Get-Date).AddYears(10) `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    $distThumb = $temp.Thumbprint
    $cerPath = Join-Path $stage 'FluentWeatherLocalBuild.cer'
    $stagedMsix = Join-Path $stage $msix.Name
    try {
        # Unpack the built package, drop its signature files, pack it again and sign the copy.
        $unpacked = Join-Path $stage 'unpacked'
        Invoke-Tool $makeappx @('unpack', '/p', $msix.FullName, '/d', $unpacked, '/o') 'Unpacking the app package'
        foreach ($f in 'AppxSignature.p7x', 'AppxBlockMap.xml', '[Content_Types].xml', 'AppxMetadata\CodeIntegrity.cat') {
            $p = Join-Path $unpacked $f
            if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force }
        }
        Invoke-Tool $makeappx @('pack', '/d', $unpacked, '/p', $stagedMsix, '/o') 'Packing the app package'
        Remove-Item -LiteralPath $unpacked -Recurse -Force
        Invoke-Tool $signtool @('sign', '/fd', 'SHA256', '/sha1', $distThumb, '/s', 'My', $stagedMsix) 'Signing the app package'

        $check = Get-AuthenticodeSignature -FilePath $stagedMsix
        if (-not $check.SignerCertificate -or $check.SignerCertificate.Thumbprint -ne $distThumb) {
            throw 'The copy of the app package is not signed with the throwaway certificate.'
        }
        # Only the public part. Export-Certificate -Type CERT never includes the private key.
        Export-Certificate -Cert $temp -FilePath $cerPath -Type CERT | Out-Null
        Write-Ok "App package signed with throwaway certificate $distThumb"
    }
    finally {
        # Delete the throwaway certificate and its private key from this PC, also after an error.
        Remove-Item -LiteralPath "Cert:\CurrentUser\My\$distThumb" -DeleteKey -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath "Cert:\CurrentUser\My\$distThumb") { Stop-Make 'The throwaway certificate could not be deleted.' }
    Write-Ok 'Throwaway certificate and its private key deleted from this PC.'

    $depNames = @()
    $resources = New-Object System.Collections.Generic.List[object]
    foreach ($d in $deps) {
        $name = 'dep_' + $d.Name
        $depNames += $name
        $resources.Add([pscustomobject]@{ Path = $d.FullName; Name = $name })
    }

    $infoPath = Join-Path $stage 'install-info.json'
    [pscustomobject]@{
        PackageName       = $PackageName
        PackageFamilyName = $pkg.PackageFamilyName
        Version           = $version
        Architecture      = $arch
        Thumbprint        = $distThumb
        CertSubject       = $subject
        MsixFile          = $msix.Name
        Dependencies      = $depNames
        MadeAt            = (Get-Date).ToString('s')
    } | ConvertTo-Json | Set-Content -LiteralPath $infoPath -Encoding UTF8

    $readme = Join-Path $InstallDir 'Direct-README.txt'
    if (-not (Test-Path -LiteralPath $readme)) { Stop-Make 'Direct-README.txt is missing. Run Setup.cmd again.' }

    # Splits the README at its "VirusTotal" section. The section runs to the next heading, which is a
    # line followed by a line of dashes. Returns the lines before and after the section.
    # {VERSION} in the README becomes the version of this build, for example 2.1.0.19.
    # {PLUS} becomes the FluentWeather-Plus version, for example v49. {VERSION} becomes the build number.
    $plus = $PlusVersion
    $readmeLines = @(Get-Content -LiteralPath $readme | ForEach-Object { $_.Replace('{VERSION}', $version).Replace('{PLUS}', $plus) })
    $vtStart = -1
    for ($i = 0; $i -lt $readmeLines.Count; $i++) { if ($readmeLines[$i] -match '^VirusTotal') { $vtStart = $i; break } }
    $vtEnd = $readmeLines.Count
    if ($vtStart -ge 0) {
        for ($j = $vtStart + 1; $j -lt $readmeLines.Count - 1; $j++) {
            if ($readmeLines[$j].Trim() -ne '' -and $readmeLines[$j + 1] -match '^-{3,}\s*$') { $vtEnd = $j; break }
        }
    }
    function Write-Readme([string]$Path, [string[]]$Lines) {
        [IO.File]::WriteAllText($Path, (($Lines -join "`r`n") + "`r`n"), [Text.Encoding]::ASCII)
    }

    # The copy inside the setup cannot hold the links of the setup itself, because the README is part
    # of the setup and changes its hash. That copy points to the release page instead.
    $innerReadme = Join-Path $stage 'README.txt'
    if ($vtStart -ge 0) {
        $inner = @()
        if ($vtStart -gt 0) { $inner += $readmeLines[0..($vtStart - 1)] }
        $inner += @('VirusTotal:', '', 'Each version on the release page lists its VirusTotal results.',
            'https://github.com/Duedilly108/FluentWeather-Plus/releases', '', '', '')
        if ($vtEnd -lt $readmeLines.Count) { $inner += $readmeLines[$vtEnd..($readmeLines.Count - 1)] }
        Write-Readme $innerReadme $inner
    } else {
        Write-Readme $innerReadme $readmeLines
    }

    # The version of this build in the Properties > Details of both programs.
    $script:VersionInfoPath = Join-Path $stage 'VersionInfo.cs'
    @(
        "[assembly: System.Reflection.AssemblyVersion(""$version"")]",
        "[assembly: System.Reflection.AssemblyFileVersion(""$version"")]",
        "[assembly: System.Reflection.AssemblyInformationalVersion(""$PlusVersion"")]",
        '[assembly: System.Reflection.AssemblyCopyright("Gabboxl and duedilly108. GNU General Public License version 3.")]'
    ) | Set-Content -LiteralPath $script:VersionInfoPath -Encoding ASCII

    $outDir = Join-Path (Get-DesktopPath) 'FluentWeather-Plus'
    if (Test-Path -LiteralPath $outDir) { Remove-Item -LiteralPath $outDir -Recurse -Force }
    New-Item -ItemType Directory -Path $outDir | Out-Null
    # The file names carry the version. The copy of the uninstaller inside the setup keeps the plain
    # name FluentWeather-Uninstall.exe, because the Start menu and Settings > Apps point to it.
    $uninstallExe = Join-Path $outDir "FluentWeather-Plus-Uninstall-$PlusVersion.exe"
    $setupExe = Join-Path $outDir "FluentWeather-Plus-Setup-$PlusVersion.exe"

    Write-Step "Compiling FluentWeather-Plus-Uninstall-$PlusVersion.exe"
    Invoke-Compile $uninstallExe @('UNINSTALL') @()

    Write-Step "Compiling FluentWeather-Plus-Setup-$PlusVersion.exe"
    $resources.Add([pscustomobject]@{ Path = $cerPath; Name = 'FluentWeatherLocalBuild.cer' })
    $resources.Add([pscustomobject]@{ Path = $stagedMsix; Name = $msix.Name })
    $resources.Add([pscustomobject]@{ Path = $tray; Name = 'FluentWeatherTray.exe' })
    $resources.Add([pscustomobject]@{ Path = $uninstallExe; Name = 'FluentWeather-Uninstall.exe' })
    $resources.Add([pscustomobject]@{ Path = $infoPath; Name = 'install-info.json' })
    $resources.Add([pscustomobject]@{ Path = $innerReadme; Name = 'README.txt' })
    $ico = Join-Path $InstallDir 'FluentWeather.ico'
    if (Test-Path -LiteralPath $ico) { $resources.Add([pscustomobject]@{ Path = $ico; Name = 'FluentWeather.ico' }) }
    $trayIcon = Join-Path $WorkDir 'tray-icon.png'
    if (Test-Path -LiteralPath $trayIcon) { $resources.Add([pscustomobject]@{ Path = $trayIcon; Name = 'tray-icon.png' }) }
    Invoke-Compile $setupExe @() $resources

    # The README next to the files gets the VirusTotal links of the two new files. A link shows a
    # result after the file is uploaded to virustotal.com once.
    $outReadme = Join-Path $outDir 'README.txt'
    if ($vtStart -ge 0) {
        $setupHash = (Get-FileHash -LiteralPath $setupExe -Algorithm SHA256).Hash.ToLowerInvariant()
        $uninstallHash = (Get-FileHash -LiteralPath $uninstallExe -Algorithm SHA256).Hash.ToLowerInvariant()
        $outer = @()
        if ($vtStart -gt 0) { $outer += $readmeLines[0..($vtStart - 1)] }
        $outer += @('VirusTotal:', '', 'installer:', "https://www.virustotal.com/gui/file/$setupHash", '',
            'uninstaller:', "https://www.virustotal.com/gui/file/$uninstallHash", '', '', '')
        if ($vtEnd -lt $readmeLines.Count) { $outer += $readmeLines[$vtEnd..($readmeLines.Count - 1)] }
        Write-Readme $outReadme $outer
    } else {
        Write-Readme $outReadme $readmeLines
    }

    $zip = Join-Path (Get-DesktopPath) "FluentWeather-Plus-$PlusVersion.zip"
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zip

    # The text for the GitHub release page, with the VirusTotal links of these two files filled in.
    # It goes on the Desktop next to the zip, so the zip keeps its three files.
    # Release-notes.txt holds the text. {PLUS}, {VERSION}, {SETUP_LINK} and {UNINSTALL_LINK} are replaced.
    $notesFile = Join-Path (Get-DesktopPath) "FluentWeather-Plus-$PlusVersion-release-notes.txt"
    $setupLink = 'https://www.virustotal.com/gui/file/' + (Get-FileHash -LiteralPath $setupExe -Algorithm SHA256).Hash.ToLowerInvariant()
    $uninstallLink = 'https://www.virustotal.com/gui/file/' + (Get-FileHash -LiteralPath $uninstallExe -Algorithm SHA256).Hash.ToLowerInvariant()
    $notesTemplate = Join-Path $InstallDir 'Release-notes.txt'
    if (Test-Path -LiteralPath $notesTemplate) {
        $notes = [IO.File]::ReadAllText($notesTemplate)
    } else {
        $notes = "FluentWeather-Plus {PLUS}`r`n`r`nVirusTotal:`r`n`r`ninstaller:`r`n{SETUP_LINK}`r`n`r`nuninstaller:`r`n{UNINSTALL_LINK}`r`n"
    }
    $notes = $notes.Replace('{PLUS}', $PlusVersion).Replace('{VERSION}', $version).Replace('{SETUP_LINK}', $setupLink).Replace('{UNINSTALL_LINK}', $uninstallLink)
    [IO.File]::WriteAllText($notesFile, $notes, [Text.Encoding]::ASCII)

    $mb = [Math]::Round((Get-Item -LiteralPath $setupExe).Length / 1MB, 1)
    Write-Ok "Done. Folder: $outDir"
    Write-Host "    FluentWeather-Plus-Setup-$PlusVersion.exe ($mb MB), FluentWeather-Plus-Uninstall-$PlusVersion.exe and README.txt"
    Write-Ok "Zip of the same three files, to share: $zip"
    Write-Ok "Text for the release page, with the VirusTotal links: $notesFile"
    if ($vtStart -ge 0) {
        Write-Host '    README.txt has the VirusTotal links of these two files. Upload both files at'
        Write-Host '    virustotal.com once, so the links show a result.'
    }
}
finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}
