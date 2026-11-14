<#
.SYNOPSIS
    Writes the winget manifests for one TrackVice release, and can validate and submit them.

.DESCRIPTION
    Writes the version, installer and en-US default locale manifests (schema 1.12.0) for DanielBaldissera.TrackVice
    to <OutputPath>\manifests\d\DanielBaldissera\TrackVice\<version>, the folder layout of microsoft/winget-pkgs.
    The product code and upgrade code are read from TrackVice.wxs, the SHA-256 from the MSI.
    The installer link points at TrackVice.msi in the GitHub release v<version>, so publish the release first,
    with the same MSI.

    winget/manifests.sh writes the same files on Linux.

.PARAMETER Version
    The version of the release. The default is the version in TrackVice.wxs.

.PARAMETER MsiPath
    The TrackVice.msi of the release. The default is TrackVice.msi in the repository folder.

.PARAMETER ReleaseNotesPath
    A file with the release notes. The default is the CHANGELOG.md entry for the version.

.PARAMETER OutputPath
    The folder to write to. The default is out\winget in the repository folder.

.PARAMETER Validate
    Check the manifests with winget validate.

.PARAMETER Submit
    Open a pull request to microsoft/winget-pkgs with wingetcreate submit.

.PARAMETER Token
    A GitHub token for wingetcreate. Without it, wingetcreate uses the token it has stored.

.EXAMPLE
    .\winget\New-Manifests.ps1 -Validate

.EXAMPLE
    .\winget\New-Manifests.ps1 -Validate -Submit
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Version,
    [string]$MsiPath,
    [string]$ReleaseNotesPath,
    [string]$OutputPath,
    [switch]$Validate,
    [switch]$Submit,
    [string]$Token
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Path $PSScriptRoot -Parent
$id = 'DanielBaldissera.TrackVice'
$repo = 'Baldissera88/TrackVice'
$schema = '1.12.0'

function Get-ChangelogEntry {
    param(
        [string]$Path,
        [string]$EntryVersion
    )
    $lines = @()
    $inEntry = $false
    foreach ($line in (Get-Content -Path $Path)) {
        if ($line -match '^## ') {
            if ($inEntry) {
                break
            }
            $inEntry = $line -match ('^## TrackVice ' + [regex]::Escape($EntryVersion) + '\s*$')
            continue
        }
        if ($inEntry) {
            $lines += $line
        }
    }
    return (($lines -join "`n").Trim())
}

function Get-MsiProperty {
    param(
        [string]$Path,
        [string]$Name
    )
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($Path, 0))
    $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @("SELECT Value FROM Property WHERE Property = '$Name'"))
    [void]$view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
    $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
    $value = $null
    if ($record) {
        $value = $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, 1)
    }
    [void]$view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)
    foreach ($object in @($record, $view, $database, $installer)) {
        if ($object) {
            [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($object)
        }
    }
    return $value
}

$wxs = [xml](Get-Content -Path (Join-Path -Path $root -ChildPath 'TrackVice.wxs') -Raw)
$code = '{' + ([string]$wxs.Wix.Product.Id).ToUpperInvariant() + '}'
$upgrade = '{' + ([string]$wxs.Wix.Product.UpgradeCode).ToUpperInvariant() + '}'
if (-not $Version) {
    $Version = [string]$wxs.Wix.Product.Version
}
elseif ($Version -ne [string]$wxs.Wix.Product.Version) {
    throw "TrackVice.wxs has version $($wxs.Wix.Product.Version), not $Version. The product code would not match."
}
if (-not $MsiPath) {
    $MsiPath = Join-Path -Path $root -ChildPath 'TrackVice.msi'
}
if (-not (Test-Path -Path $MsiPath -PathType Leaf)) {
    throw "$MsiPath was not found."
}
$MsiPath = (Resolve-Path -Path $MsiPath).Path
if (-not $OutputPath) {
    $OutputPath = Join-Path -Path $root -ChildPath 'out/winget'
}

# The Windows Installer COM object only exists on Windows.
if ($env:OS -eq 'Windows_NT') {
    $msiVersion = Get-MsiProperty -Path $MsiPath -Name ProductVersion
    $msiCode = Get-MsiProperty -Path $MsiPath -Name ProductCode
    if ($msiVersion -ne $Version -or $msiCode -ne $code) {
        throw "$MsiPath has version $msiVersion and product code $msiCode. Expected $Version and $code from TrackVice.wxs."
    }
}

if ($ReleaseNotesPath) {
    $notes = [System.IO.File]::ReadAllText((Resolve-Path -Path $ReleaseNotesPath).Path)
}
else {
    $notes = Get-ChangelogEntry -Path (Join-Path -Path $root -ChildPath 'CHANGELOG.md') -EntryVersion $Version
    if (-not $notes) {
        Write-Warning "CHANGELOG.md has no entry for $Version, so the manifests have no release notes."
    }
}

$url = "https://github.com/$repo/releases/download/v$Version/TrackVice.msi"
$sha = (Get-FileHash -Path $MsiPath -Algorithm SHA256).Hash.ToUpperInvariant()
$date = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture)
$dir = Join-Path -Path $OutputPath -ChildPath "manifests/d/DanielBaldissera/TrackVice/$Version"

$versionManifest = @(
    "# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.$schema.schema.json"
    ''
    "PackageIdentifier: $id"
    "PackageVersion: $Version"
    'DefaultLocale: en-US'
    'ManifestType: version'
    "ManifestVersion: $schema"
)

$installerManifest = @(
    "# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.$schema.schema.json"
    ''
    "PackageIdentifier: $id"
    "PackageVersion: $Version"
    'MinimumOSVersion: 10.0.14393.0'
    'InstallerType: msi'
    'Scope: machine'
    'InstallModes:'
    '- interactive'
    '- silent'
    '- silentWithProgress'
    'UpgradeBehavior: install'
    "ProductCode: '$code'"
    "ReleaseDate: $date"
    'AppsAndFeaturesEntries:'
    '- DisplayName: TrackVice'
    '  Publisher: Daniel L. Baldissera'
    "  ProductCode: '$code'"
    "  UpgradeCode: '$upgrade'"
    'Installers:'
    '- Architecture: x64'
    "  InstallerUrl: $url"
    "  InstallerSha256: $sha"
    'ManifestType: installer'
    "ManifestVersion: $schema"
)

$localeManifest = @(
    "# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.$schema.schema.json"
    ''
    "PackageIdentifier: $id"
    "PackageVersion: $Version"
    'PackageLocale: en-US'
    'Publisher: Daniel L. Baldissera'
    'PublisherUrl: https://github.com/Baldissera88'
    "PublisherSupportUrl: https://github.com/$repo/issues"
    'Author: Daniel L. Baldissera'
    'PackageName: TrackVice'
    "PackageUrl: https://github.com/$repo"
    'License: MIT'
    "LicenseUrl: https://github.com/$repo/blob/main/LICENSE"
    'Copyright: Copyright (c) 2026 Daniel L. Baldissera'
    'ShortDescription: Follows SCCM task sequences, SCCM apps and updates, Windows Autopilot and Intune apps on one device as they install.'
    'Description: |-'
    '  TrackVice shows one deployment on one Windows device as a live timeline: the phases, every step, app, update and policy in them, and what each one is doing now.'
    '  It reads smsts.log, the ConfigMgr client logs and WMI, the Enrollment Status Page tracking keys, the Intune Management Extension logs and the Autopilot and MDM event logs, and keeps the log line behind every step.'
    '  It finds failed installs, apps that were not detected after install, retry loops, restarts, ESP time limits and installs with no progress. A companion to LogVice and PolicyVice.'
    'Moniker: trackvice'
    'Tags:'
    '- autopilot'
    '- configmgr'
    '- endpoint-management'
    '- esp'
    '- intune'
    '- mecm'
    '- osd'
    '- sccm'
    '- task-sequence'
    '- troubleshooting'
)

# The notes go in a YAML block: leading blank lines dropped, every line indented by two spaces.
if ($notes -and $notes.Trim()) {
    $noteLines = $notes.Replace("`r", '').Split("`n")
    if ($noteLines.Count -gt 1 -and $noteLines[-1] -eq '') {
        $noteLines = $noteLines[0..($noteLines.Count - 2)]
    }
    $localeManifest += 'ReleaseNotes: |-'
    $started = $false
    foreach ($line in $noteLines) {
        if (-not $started -and -not $line.Trim()) {
            continue
        }
        $started = $true
        $line = $line.TrimEnd()
        if ($line) {
            $localeManifest += '  ' + $line
        }
        else {
            $localeManifest += ''
        }
    }
}

$localeManifest += @(
    "ReleaseNotesUrl: https://github.com/$repo/releases/tag/v$Version"
    'Documentations:'
    '- DocumentLabel: Changelog'
    "  DocumentUrl: https://github.com/$repo/blob/main/CHANGELOG.md"
    'ManifestType: defaultLocale'
    "ManifestVersion: $schema"
)

if ($PSCmdlet.ShouldProcess($dir, 'Write the winget manifests')) {
    New-Item -Path $dir -ItemType Directory -Force | Out-Null
    $utf8 = New-Object System.Text.UTF8Encoding -ArgumentList $false
    $files = [ordered]@{
        "$id.yaml"                  = $versionManifest
        "$id.installer.yaml"        = $installerManifest
        "$id.locale.en-US.yaml"     = $localeManifest
    }
    foreach ($name in $files.Keys) {
        [System.IO.File]::WriteAllText((Join-Path -Path $dir -ChildPath $name), (($files[$name] -join "`n") + "`n"), $utf8)
    }
    Write-Output "Wrote $dir"
}

if ($Validate -and $PSCmdlet.ShouldProcess($dir, 'Validate with winget')) {
    if (-not (Get-Command -Name winget.exe -ErrorAction SilentlyContinue)) {
        throw 'winget.exe was not found.'
    }
    & winget.exe validate --manifest $dir
    if ($LASTEXITCODE -ne 0) {
        throw "winget validate failed with exit code $LASTEXITCODE."
    }
}

if ($Submit -and $PSCmdlet.ShouldProcess($dir, 'Submit to microsoft/winget-pkgs with wingetcreate')) {
    if (-not (Get-Command -Name wingetcreate.exe -ErrorAction SilentlyContinue)) {
        throw 'wingetcreate.exe was not found. Install it with: winget install wingetcreate'
    }
    $submitArgs = @('submit')
    if ($Token) {
        $submitArgs += @('--token', $Token)
    }
    $submitArgs += $dir
    & wingetcreate.exe @submitArgs
    if ($LASTEXITCODE -ne 0) {
        throw "wingetcreate submit failed with exit code $LASTEXITCODE."
    }
}

if (-not $Submit) {
    Write-Output "Test the install with: winget install --manifest $dir (after: winget settings --enable LocalManifestFiles)"
    Write-Output "Submit with: .\winget\New-Manifests.ps1 -Submit, or: wingetcreate submit $dir"
}
