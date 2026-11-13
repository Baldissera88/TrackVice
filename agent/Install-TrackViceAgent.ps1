#Requires -RunAsAdministrator

# Installs the TrackVice agent on a device, so its deployment can be followed from another computer.
# Assign it in Intune as a platform script: system context, no signature check, 64-bit PowerShell.
# docs/AGENT.md explains the three links and how to make them.

$Download  = ''   # Link to TrackVice.exe
$Sha256    = ''   # SHA-256 of that TrackVice.exe, from Get-FileHash
$Upload    = ''   # Where the agent uploads: the container SAS link with Create and Write
$KeepHours = 48   # The agent removes itself after this long
$MaxAgeHours = 0  # Only install on a device enrolled within this many hours. 0 installs on every device

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

if (-not $Download -or -not $Sha256 -or -not $Upload) {
    throw 'Fill in $Download, $Sha256 and $Upload at the top of the script.'
}

# Without this, a device that was enrolled long ago also installs the agent when the script is assigned to it.
if ($MaxAgeHours -gt 0) {
    $enrolled = Get-Item -Path (Join-Path $env:ProgramData 'Microsoft\IntuneManagementExtension') -ErrorAction SilentlyContinue
    if ($enrolled -and $enrolled.CreationTime -lt (Get-Date).AddHours(-$MaxAgeHours)) {
        Write-Output 'Not a new enrollment. The TrackVice agent was not installed.'
        exit 0
    }
}

$exe = Join-Path $env:TEMP 'TrackVice.exe'
$out = Join-Path $env:TEMP 'TrackVice-install.out'
$err = Join-Path $env:TEMP 'TrackVice-install.err'

Invoke-WebRequest -Uri $Download -OutFile $exe -UseBasicParsing

$hash = (Get-FileHash -Path $exe -Algorithm SHA256).Hash
if ($hash -ne $Sha256) {
    Remove-Item -Path $exe -Force
    throw "TrackVice.exe has SHA-256 $hash, expected $Sha256. Update `$Sha256 after uploading a new TrackVice.exe."
}

$arguments = '/agent-install', '/upload', "`"$Upload`"", '/keep', $KeepHours
$process = Start-Process -FilePath $exe -ArgumentList $arguments -Wait -PassThru -RedirectStandardOutput $out -RedirectStandardError $err

$message = (Get-Content -Path $out, $err -ErrorAction SilentlyContinue | Where-Object { $_.Trim() }) -join ' '
Remove-Item -Path $exe, $out, $err -Force -ErrorAction SilentlyContinue

if ($process.ExitCode -ne 0) {
    throw "TrackVice.exe /agent-install returned $($process.ExitCode). $message"
}
Write-Output $message
exit 0
