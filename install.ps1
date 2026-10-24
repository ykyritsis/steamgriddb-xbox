<#
.SYNOPSIS
    Installs or updates SteamGridDB for Xbox (Community).

.DESCRIPTION
    Run it from an unzipped release (double-click Install.cmd), or straight from the web:

        irm https://raw.githubusercontent.com/ykyritsis/steamgriddb-xbox/master/install.ps1 | iex

    It picks the package for this PC's CPU, downloading the latest release when it is not next to the script,
    trusts the package's signing certificate (one administrator prompt, only the first time), installs any
    runtime packages that are missing, installs or updates the widget, and opens the file system permission page.
#>

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repo = 'ykyritsis/steamgriddb-xbox'
$packageName = 'SteamGridDBforXbox.Community'

function Write-Step($message) { Write-Host "`n> $message" -ForegroundColor Cyan }

function Get-Architecture {
    # PROCESSOR_ARCHITEW6432 is set when 32-bit PowerShell runs on 64-bit Windows
    $arch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }

    switch ($arch) {
        'AMD64' { 'x64' }
        'ARM64' { 'ARM64' }
        default { throw "This PC's processor ($arch) is not supported. Builds are available for x64 and ARM64." }
    }
}

function Get-PackageFolder($arch) {
    # Installing from an unzipped release: the package is next to this script
    if ($PSScriptRoot -and (Get-ChildItem -Path $PSScriptRoot -Filter '*.msix' -ErrorAction SilentlyContinue)) {
        return $PSScriptRoot
    }

    Write-Step "Downloading the latest release for $arch"

    $release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers @{ 'User-Agent' = 'SteamGridDB-Xbox-Installer' }
    $asset = $release.assets | Where-Object { $_.name -like "*_$arch.zip" } | Select-Object -First 1

    if (-not $asset) {
        throw "The latest release ($($release.tag_name)) has no $arch package."
    }

    $work = Join-Path ([IO.Path]::GetTempPath()) "SteamGridDB.Xbox.Community-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $work | Out-Null
    $zip = Join-Path $work $asset.name

    Invoke-WebRequest $asset.browser_download_url -OutFile $zip -UseBasicParsing -Headers @{ 'User-Agent' = 'SteamGridDB-Xbox-Installer' }
    Expand-Archive -Path $zip -DestinationPath $work -Force

    Write-Host "  $($release.tag_name) downloaded"

    return $work
}

function Install-Certificate($certificatePath) {
    $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $certificatePath
    $trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $certificate.Thumbprint

    if ($trusted) {
        Write-Host '  Certificate already trusted'
        return
    }

    Write-Host '  Windows will ask for administrator permission to trust the package certificate (one time only)' -ForegroundColor Yellow

    $command = "Import-Certificate -FilePath '$certificatePath' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null"
    $process = Start-Process powershell -Verb RunAs -Wait -PassThru -WindowStyle Hidden -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', $command

    if ($process.ExitCode -ne 0 -or -not (Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $certificate.Thumbprint)) {
        throw 'The certificate was not trusted, so Windows will not install the package. Run the installer again and accept the administrator prompt.'
    }

    Write-Host '  Certificate trusted'
}

function Install-Dependencies($folder) {
    # Runtime packages (VCLibs, .NET Native) that the widget needs. Skip any already installed at the same or a
    # newer version: reinstalling one that Game Bar or the Xbox app has open fails.
    foreach ($dependency in Get-ChildItem -Path $folder -Recurse -Filter '*.appx') {
        $manifest = Get-AppxPackageManifestFromFile $dependency.FullName

        if (-not $manifest) { continue }

        $installed = Get-AppxPackage -Name $manifest.Name | Where-Object { $_.Architecture -eq $manifest.Architecture -and [version]$_.Version -ge [version]$manifest.Version }

        if ($installed) { continue }

        Write-Host "  Installing $($manifest.Name) $($manifest.Version)"
        Add-AppxPackage -Path $dependency.FullName
    }
}

function Get-AppxPackageManifestFromFile($path) {
    # Read Name/Version/Architecture from the package's AppxManifest.xml without installing it
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($path)

    try {
        $entry = $archive.Entries | Where-Object FullName -eq 'AppxManifest.xml' | Select-Object -First 1

        if (-not $entry) { return $null }

        $reader = New-Object IO.StreamReader $entry.Open()
        [xml]$xml = $reader.ReadToEnd()
        $reader.Dispose()

        $architecture = $xml.Package.Identity.ProcessorArchitecture
        if (-not $architecture) { $architecture = 'Neutral' }

        return [pscustomobject]@{
            Name         = $xml.Package.Identity.Name
            Version      = $xml.Package.Identity.Version
            Architecture = $architecture
        }
    }
    finally {
        $archive.Dispose()
    }
}

try {
    Write-Host 'SteamGridDB for Xbox (Community) installer' -ForegroundColor Green

    $arch = Get-Architecture
    $folder = Get-PackageFolder $arch

    # Files from a downloaded zip carry the "from the internet" mark
    Get-ChildItem -Path $folder -Recurse -File | Unblock-File -ErrorAction SilentlyContinue

    $msix = Get-ChildItem -Path $folder -Recurse -Filter '*.msix' | Select-Object -First 1
    $certificate = Get-ChildItem -Path $folder -Recurse -Filter '*.cer' | Select-Object -First 1

    if (-not $msix -or -not $certificate) {
        throw "Could not find the package (.msix) and certificate (.cer) in $folder."
    }

    Write-Step 'Checking the signing certificate'
    Install-Certificate $certificate.FullName

    Write-Step 'Installing required Windows runtime packages'
    Install-Dependencies $folder

    $existing = Get-AppxPackage -Name $packageName
    Write-Step $(if ($existing) { "Updating from version $($existing.Version)" } else { 'Installing the widget' })

    # Closes the widget if it is open, so an update does not fail
    Add-AppxPackage -Path $msix.FullName -ForceApplicationShutdown

    $installed = Get-AppxPackage -Name $packageName
    Write-Host "  Installed version $($installed.Version)" -ForegroundColor Green

    if (-not $existing) {
        Write-Step 'One more step: allow file system access'
        Write-Host '  Opening Settings. Turn on "SteamGridDB for Xbox (Community)" so it can read the Xbox app''s artwork.'
        Start-Process 'ms-settings:privacy-broadfilesystemaccess'
    }

    Write-Host "`nDone! Open Game Bar (Win+G), find 'SteamGridDB (Community)' in the widget menu, and paste your SteamGridDB API key in its Settings (gear icon)." -ForegroundColor Green
    Write-Host 'Get a free key at https://www.steamgriddb.com/profile/preferences/api'
}
catch {
    Write-Host "`nInstallation failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "If this keeps happening, please open an issue at https://github.com/$repo/issues"
}

# Keep the window open when started by double-clicking Install.cmd
if ($Host.Name -eq 'ConsoleHost' -and $env:SGDB_INSTALLER_PAUSE -eq '1') {
    Read-Host "`nPress Enter to close"
}
