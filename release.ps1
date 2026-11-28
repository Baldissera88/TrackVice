<#
.SYNOPSIS
    Builds, signs and hashes a TrackVice release, and with -Publish creates the GitHub release.

.DESCRIPTION
    Run it on Windows from a clean checkout of the commit to release, after bump.ps1 and the changelog.

    It stops when the working tree has changes, when TrackVice.wxs, src/AssemblyInfo.cs and the top of
    CHANGELOG.md give different versions, or when the tag v<version> already exists.

    Then it builds TrackVice.exe with build.ps1, signs it, builds TrackVice.msi with WiX Toolset 3 around the
    signed exe (or takes an MSI built with package.sh, see -MsiPath), signs the MSI and checks both signatures.
    It writes SHA256SUMS.txt and release-notes.md to out\release\<version>: the top CHANGELOG.md entry and a
    table of the files with their SHA-256. SHA256SUMS.txt has a "<hash>  <file>" line for TrackVice.msi,
    TrackVice.exe and the scripts in agent/, as package.sh writes it. TrackVice checks an update against it.

    Signing is optional. Give a certificate with -Thumbprint or -PfxPath, or Azure Trusted Signing
    with -TrustedSigningMetadata. Without any of them the files are not signed.

    -WhatIf runs the checks and says what would be done, and changes nothing.

.PARAMETER Thumbprint
    Sign with this certificate from the CurrentUser\My or LocalMachine\My store.

.PARAMETER PfxPath
    Sign with the certificate in this .pfx file.

.PARAMETER PfxPassword
    The password of the .pfx file. When it is left out, the file must have no password.
    signtool.exe takes the password on its command line, where every process on this PC can read it while
    it signs. Prefer -Thumbprint or -TrustedSigningMetadata, and sign with a .pfx file only on a PC nobody else uses.

.PARAMETER TrustedSigningMetadata
    Sign with Azure Trusted Signing. The path of the metadata.json with Endpoint, CodeSigningAccountName
    and CertificateProfileName. Sign in with Azure CLI (az login) first.

.PARAMETER TrustedSigningDlib
    The path of Azure.CodeSigning.Dlib.dll (x64). When it is left out, the folder of the Trusted Signing
    client tools is searched.

.PARAMETER TimestampUrl
    The RFC 3161 time stamp server. The default is http://timestamp.acs.microsoft.com with Trusted Signing
    and http://timestamp.digicert.com with a certificate. Use the server of the CA that issued the certificate.

.PARAMETER MsiPath
    Take this TrackVice.msi instead of building one with WiX, for example one built with package.sh on
    Linux or WSL. Its version and product code must match TrackVice.wxs.

.PARAMETER Publish
    Create the GitHub release v<version> with gh, with TrackVice.exe, TrackVice.msi, SHA256SUMS.txt and
    the scripts in agent/. The commit must be pushed first.

.PARAMETER Prerelease
    With -Publish, mark the release as a pre-release.

.EXAMPLE
    .\release.ps1 -WhatIf

.EXAMPLE
    .\release.ps1 -Thumbprint 0123456789ABCDEF0123456789ABCDEF01234567

.EXAMPLE
    .\release.ps1 -TrustedSigningMetadata C:\Signing\metadata.json -Publish
#>
[CmdletBinding(SupportsShouldProcess = $true, DefaultParameterSetName = 'Unsigned')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Thumbprint')]
    [string]$Thumbprint,

    [Parameter(Mandatory = $true, ParameterSetName = 'Pfx')]
    [string]$PfxPath,

    [Parameter(ParameterSetName = 'Pfx')]
    [securestring]$PfxPassword,

    [Parameter(Mandatory = $true, ParameterSetName = 'TrustedSigning')]
    [string]$TrustedSigningMetadata,

    [Parameter(ParameterSetName = 'TrustedSigning')]
    [string]$TrustedSigningDlib,

    [string]$TimestampUrl,

    [string]$MsiPath,

    [switch]$Publish,

    [switch]$Prerelease
)

$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot

$repoUrl = 'https://github.com/Baldissera88/TrackVice'
$script:signMode = $PSCmdlet.ParameterSetName
$script:signTool = $null

function Get-VersionInfo {
    $wxs = [xml](Get-Content -Path TrackVice.wxs -Raw)
    $assembly = Get-Content -Path src\AssemblyInfo.cs -Raw
    $changelog = Get-Content -Path CHANGELOG.md

    $info = [ordered]@{
        Wxs         = [string]$wxs.Wix.Product.Version
        ProductCode = '{' + ([string]$wxs.Wix.Product.Id).ToUpperInvariant() + '}'
        Assembly    = $null
        Changelog   = $null
        Notes       = @()
    }
    if ($assembly -match 'AssemblyInformationalVersion\("([^"]+)"\)') {
        $info.Assembly = $Matches[1]
    }

    # The top entry runs from the first "## TrackVice <version>" line to the next "## " line.
    $inEntry = $false
    foreach ($line in $changelog) {
        if ($line -match '^## ') {
            if ($inEntry) {
                break
            }
            if ($line -match '^## TrackVice (\S+)\s*$') {
                $info.Changelog = $Matches[1]
            }
            $inEntry = $true
            continue
        }
        if ($inEntry) {
            $info.Notes += $line.TrimEnd()
        }
    }
    $text = ($info.Notes -join "`n").Trim()
    $info.Notes = $text
    return $info
}

function Find-SignTool {
    $command = Get-Command -Name signtool.exe -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }
    $kits = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    $found = Get-ChildItem -Path $kits -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -eq 'x64' } |
        Sort-Object -Property { $_.Directory.Parent.Name -as [version] } -Descending |
        Select-Object -First 1
    if (-not $found) {
        throw 'signtool.exe was not found. Install the Windows SDK signing tools, or put signtool.exe on the PATH.'
    }
    return $found.FullName
}

function Find-TrustedSigningDlib {
    $folders = @(
        (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Microsoft\MicrosoftArtifactSigningClientTools'),
        (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Microsoft\MicrosoftTrustedSigningClientTools')
    )
    foreach ($folder in $folders) {
        $found = Get-ChildItem -Path $folder -Filter Azure.CodeSigning.Dlib.dll -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.Directory.Name -ne 'x86' } |
            Select-Object -First 1
        if ($found) {
            return $found.FullName
        }
    }
    throw 'Azure.CodeSigning.Dlib.dll was not found. Install the client tools (winget install -e --id Microsoft.Azure.ArtifactSigningClientTools) or give -TrustedSigningDlib.'
}

function Find-WixBin {
    $candle = Get-Command -Name candle.exe -ErrorAction SilentlyContinue
    if ($candle) {
        return (Split-Path -Path $candle.Source)
    }
    $found = Get-ChildItem -Path "${env:ProgramFiles(x86)}\WiX Toolset v3*\bin\candle.exe" -ErrorAction SilentlyContinue |
        Sort-Object -Property FullName -Descending |
        Select-Object -First 1
    if ($found) {
        return $found.DirectoryName
    }
    return $null
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

    # The database stays open, and the file locked, until the COM objects are released.
    foreach ($object in @($record, $view, $database, $installer)) {
        if ($object) {
            [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($object)
        }
    }
    [System.GC]::Collect()
    [System.GC]::WaitForPendingFinalizers()
    return $value
}

function Invoke-Sign {
    param(
        [string]$Path
    )
    $signArgs = @('sign', '/fd', 'SHA256', '/tr', $script:timestamp, '/td', 'SHA256', '/d', 'TrackVice', '/du', $repoUrl)
    switch ($script:signMode) {
        'Thumbprint' {
            $signArgs += @('/sha1', $Thumbprint)
        }
        'Pfx' {
            $signArgs += @('/f', $PfxPath)
            if ($PfxPassword) {
                $signArgs += @('/p', (New-Object System.Net.NetworkCredential -ArgumentList '', $PfxPassword).Password)
            }
        }
        'TrustedSigning' {
            $signArgs += @('/dlib', $script:dlib, '/dmdf', $TrustedSigningMetadata)
        }
    }
    $signArgs += $Path
    & $script:signTool @signArgs
    if ($LASTEXITCODE -ne 0) {
        throw "signtool.exe could not sign $Path (exit code $LASTEXITCODE)."
    }

    & $script:signTool verify /pa $Path
    if ($LASTEXITCODE -ne 0) {
        throw "The signature of $Path did not verify (exit code $LASTEXITCODE)."
    }
    $signature = Get-AuthenticodeSignature -FilePath $Path
    if ($signature.Status -ne 'Valid') {
        throw "The signature of $Path is $($signature.Status): $($signature.StatusMessage)"
    }
    if (-not $signature.TimeStamperCertificate) {
        throw "The signature of $Path has no time stamp."
    }
    Write-Output "Signed $Path by $($signature.SignerCertificate.Subject)"
}

# Checks. These change nothing, so they also run with -WhatIf.

$git = Get-Command -Name git -ErrorAction SilentlyContinue
if (-not $git) {
    throw 'git.exe was not found.'
}
$changes = @(git status --porcelain)
if ($LASTEXITCODE -ne 0) {
    throw 'git status failed. Run this script in the TrackVice repository.'
}
if ($changes.Count -gt 0) {
    throw ("The working tree has changes. Commit or remove them first:`n" + ($changes -join "`n"))
}

$info = Get-VersionInfo
$version = $info.Wxs
if (-not $version -or $info.Assembly -ne $version -or $info.Changelog -ne $version) {
    throw ("The versions do not agree. TrackVice.wxs: {0}, src/AssemblyInfo.cs: {1}, top of CHANGELOG.md: {2}. Run bump.ps1." -f $info.Wxs, $info.Assembly, $info.Changelog)
}
# bump.ps1 writes only the date line, so an entry with nothing else in it is still empty.
if (-not ($info.Notes -replace '(?m)^\d{1,2} [A-Za-z]+ \d{4}\.[ \t]*$', '').Trim()) {
    throw "The CHANGELOG.md entry for $version is empty. Write it first."
}
$tag = 'v' + $version

$localTag = @(git tag --list $tag)
if ($localTag.Count -gt 0) {
    throw "The tag $tag already exists in this repository."
}
$remoteTag = @(git ls-remote --tags origin "refs/tags/$tag")
if ($LASTEXITCODE -ne 0) {
    if ($Publish) {
        throw 'Could not reach origin to check its tags.'
    }
    Write-Warning 'Could not reach origin to check its tags.'
}
elseif ($remoteTag.Count -gt 0) {
    throw "The tag $tag already exists on origin."
}

$commit = (git rev-parse HEAD).Trim()

if ($PSCmdlet.ParameterSetName -ne 'Unsigned') {
    $script:signTool = Find-SignTool
    switch ($PSCmdlet.ParameterSetName) {
        'Thumbprint' {
            $certificate = @(Get-ChildItem -Path Cert:\CurrentUser\My, Cert:\LocalMachine\My |
                Where-Object { $_.Thumbprint -eq $Thumbprint.Replace(' ', '').ToUpperInvariant() })
            if ($certificate.Count -eq 0) {
                throw "No certificate with thumbprint $Thumbprint is in CurrentUser\My or LocalMachine\My."
            }
        }
        'Pfx' {
            if (-not (Test-Path -Path $PfxPath -PathType Leaf)) {
                throw "$PfxPath was not found."
            }
            $PfxPath = (Resolve-Path -Path $PfxPath).Path
        }
        'TrustedSigning' {
            if (-not (Test-Path -Path $TrustedSigningMetadata -PathType Leaf)) {
                throw "$TrustedSigningMetadata was not found."
            }
            $TrustedSigningMetadata = (Resolve-Path -Path $TrustedSigningMetadata).Path
            if ($TrustedSigningDlib) {
                if (-not (Test-Path -Path $TrustedSigningDlib -PathType Leaf)) {
                    throw "$TrustedSigningDlib was not found."
                }
                $script:dlib = (Resolve-Path -Path $TrustedSigningDlib).Path
            }
            else {
                $script:dlib = Find-TrustedSigningDlib
            }
        }
    }
    if ($TimestampUrl) {
        $script:timestamp = $TimestampUrl
    }
    elseif ($PSCmdlet.ParameterSetName -eq 'TrustedSigning') {
        $script:timestamp = 'http://timestamp.acs.microsoft.com'
    }
    else {
        $script:timestamp = 'http://timestamp.digicert.com'
    }
}
else {
    Write-Warning 'No signing was asked for. The files will not be signed.'
}

$wixBin = $null
if ($MsiPath) {
    if (-not (Test-Path -Path $MsiPath -PathType Leaf)) {
        throw "$MsiPath was not found."
    }
    $MsiPath = (Resolve-Path -Path $MsiPath).Path
    $msiVersion = Get-MsiProperty -Path $MsiPath -Name ProductVersion
    $msiCode = Get-MsiProperty -Path $MsiPath -Name ProductCode
    if ($msiVersion -ne $version -or $msiCode -ne $info.ProductCode) {
        throw "$MsiPath has version $msiVersion and product code $msiCode. TrackVice.wxs has $version and $($info.ProductCode). Build it again from this commit."
    }
}
else {
    $wixBin = Find-WixBin
    if (-not $wixBin) {
        throw 'WiX Toolset 3 was not found. Install it, or build the MSI with package.sh on Linux or WSL and give it with -MsiPath.'
    }
}

if ($Publish) {
    if (-not (Get-Command -Name gh -ErrorAction SilentlyContinue)) {
        throw 'gh.exe (GitHub CLI) was not found.'
    }
    # Windows PowerShell 5.1 turns redirected stderr of a native command into an error, which would stop here.
    $ErrorActionPreference = 'Continue'
    gh auth status 2>&1 | Out-Null
    $signedIn = $LASTEXITCODE -eq 0
    $ErrorActionPreference = 'Stop'
    if (-not $signedIn) {
        throw 'gh is not signed in. Run: gh auth login'
    }
    $pushed = @(git branch -r --contains $commit)
    if ($pushed.Count -eq 0) {
        throw "Commit $commit is not on origin. Push it first."
    }
}

Write-Output "TrackVice $version, commit $commit, product code $($info.ProductCode)"

# Build and sign.

$outDir = Join-Path -Path $PSScriptRoot -ChildPath "out\release\$version"
$exe = Join-Path -Path $PSScriptRoot -ChildPath 'TrackVice.exe'
$msi = Join-Path -Path $PSScriptRoot -ChildPath 'TrackVice.msi'
$agentScript = Join-Path -Path $PSScriptRoot -ChildPath 'agent\Install-TrackViceAgent.ps1'
$linksScript = Join-Path -Path $PSScriptRoot -ChildPath 'agent\Install-TrackViceAgent-Links.ps1'
$shareScript = Join-Path -Path $PSScriptRoot -ChildPath 'agent\Install-TrackViceAgent-Share.ps1'
$customerScript = Join-Path -Path $PSScriptRoot -ChildPath 'agent\New-TrackViceCustomer.ps1'
$sumsPath = Join-Path -Path $outDir -ChildPath 'SHA256SUMS.txt'

if ($PSCmdlet.ShouldProcess('TrackVice.exe', 'Build with build.ps1')) {
    & (Join-Path -Path $PSScriptRoot -ChildPath 'build.ps1')
}

if ($script:signTool -and $PSCmdlet.ShouldProcess('TrackVice.exe', 'Sign')) {
    Invoke-Sign -Path $exe
}

if ($MsiPath) {
    if ($MsiPath -ne $msi -and $PSCmdlet.ShouldProcess($MsiPath, 'Copy to TrackVice.msi')) {
        Copy-Item -Path $MsiPath -Destination $msi -Force
    }
    Write-Warning 'The MSI was built elsewhere, so the TrackVice.exe inside it is not the one built and signed here.'
}
elseif ($PSCmdlet.ShouldProcess('TrackVice.msi', 'Build with WiX Toolset 3')) {
    # The MSI is built here rather than with build.ps1 -Msi, which would build the exe again, unsigned.
    $wixObj = Join-Path -Path $PSScriptRoot -ChildPath 'TrackVice.wixobj'
    & (Join-Path -Path $wixBin -ChildPath 'candle.exe') -nologo -arch x64 -out $wixObj TrackVice.wxs
    if ($LASTEXITCODE -ne 0) {
        throw "candle.exe failed with exit code $LASTEXITCODE."
    }
    & (Join-Path -Path $wixBin -ChildPath 'light.exe') -nologo -out $msi $wixObj
    if ($LASTEXITCODE -ne 0) {
        throw "light.exe failed with exit code $LASTEXITCODE."
    }
    Remove-Item -Path $wixObj, (Join-Path -Path $PSScriptRoot -ChildPath 'TrackVice.wixpdb') -ErrorAction SilentlyContinue
    Write-Output 'Built TrackVice.msi'
}

if ($script:signTool -and $PSCmdlet.ShouldProcess('TrackVice.msi', 'Sign')) {
    Invoke-Sign -Path $msi
}

# Hashes and release notes.

if ($PSCmdlet.ShouldProcess($outDir, 'Write SHA256SUMS.txt and release-notes.md')) {
    New-Item -Path $outDir -ItemType Directory -Force | Out-Null

    $files = @(
        @{ Name = 'TrackVice.exe'; Path = $exe },
        @{ Name = 'TrackVice.msi'; Path = $msi },
        @{ Name = 'Install-TrackViceAgent.ps1'; Path = $agentScript },
        @{ Name = 'Install-TrackViceAgent-Links.ps1'; Path = $linksScript },
        @{ Name = 'Install-TrackViceAgent-Share.ps1'; Path = $shareScript },
        @{ Name = 'New-TrackViceCustomer.ps1'; Path = $customerScript }
    )
    $sums = @()
    $table = @('| File | SHA-256 |', '|---|---|')
    foreach ($file in $files) {
        $hash = (Get-FileHash -Path $file.Path -Algorithm SHA256).Hash
        $file.Hash = $hash
        $sums += ('{0}  {1}' -f $hash.ToLowerInvariant(), $file.Name)
        $table += ('| `{0}` | `{1}` |' -f $file.Name, $hash)
    }

    $notes = @(
        $info.Notes,
        '',
        '### Files',
        ''
    ) + $table + @(
        '',
        'Check a download with `Get-FileHash .\TrackVice.msi`. The hash of `TrackVice.exe` is the one `$Sha256` in the agent''s platform script needs.',
        '',
        '| | |',
        '|---|---|',
        ('| Product code | `{0}` |' -f $info.ProductCode),
        '| Install | `msiexec /i TrackVice.msi /qn` |',
        ('| Uninstall | `msiexec /x {0} /qn` |' -f $info.ProductCode)
    )

    $utf8 = New-Object System.Text.UTF8Encoding -ArgumentList $false
    [System.IO.File]::WriteAllText($sumsPath, (($sums -join "`n") + "`n"), $utf8)
    [System.IO.File]::WriteAllText((Join-Path -Path $outDir -ChildPath 'release-notes.md'), (($notes -join "`n") + "`n"), $utf8)

    foreach ($file in $files) {
        Write-Output ('{0}  {1}' -f $file.Hash, $file.Name)
    }
    Write-Output "Wrote $outDir"
}

# Publish.

if ($Publish) {
    $notesPath = Join-Path -Path $outDir -ChildPath 'release-notes.md'
    $ghArgs = @('release', 'create', $tag, $exe, $msi, $sumsPath, $agentScript, $linksScript, $shareScript, $customerScript, '--title', "TrackVice $version", '--notes-file', $notesPath, '--target', $commit)
    if ($Prerelease) {
        $ghArgs += '--prerelease'
    }
    if ($PSCmdlet.ShouldProcess("$repoUrl/releases/tag/$tag", 'Create the GitHub release')) {
        # TrackVice installs an update only when SHA256SUMS.txt in the release lists the file with the same hash.
        if (-not (Test-Path -Path $sumsPath -PathType Leaf)) {
            throw "$sumsPath was not found."
        }
        & gh @ghArgs
        if ($LASTEXITCODE -ne 0) {
            throw "gh release create failed with exit code $LASTEXITCODE."
        }
        Write-Output "Created $repoUrl/releases/tag/$tag"
    }
}
else {
    Write-Output 'Not published. Check the files, then run again with -Publish.'
}
