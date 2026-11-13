# Installs the TrackVice agent on a device, so its deployment can be followed from another computer.
# Assign it in Intune as a platform script: system context, no signature check, 64-bit PowerShell.
# See docs/AGENT.md for the three values below.

$Download  = ''   # Link to TrackVice.exe
$Sha256    = ''   # SHA-256 of that TrackVice.exe, from Get-FileHash
$Upload    = ''   # Where the agent uploads: the container SAS link with Create and Write
$KeepHours = 48   # The agent removes itself after this long

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$exe = Join-Path $env:TEMP 'TrackVice.exe'
Invoke-WebRequest -Uri $Download -OutFile $exe -UseBasicParsing

$hash = (Get-FileHash -Path $exe -Algorithm SHA256).Hash
if ($hash -ne $Sha256) {
    Remove-Item -Path $exe -Force
    throw "TrackVice.exe has SHA-256 $hash, expected $Sha256."
}

$arguments = '/agent-install', '/upload', "`"$Upload`"", '/keep', $KeepHours
$process = Start-Process -FilePath $exe -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
Remove-Item -Path $exe -Force
exit $process.ExitCode
