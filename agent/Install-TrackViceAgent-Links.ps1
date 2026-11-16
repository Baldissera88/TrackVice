#Requires -RunAsAdministrator

# Installs the TrackVice agent on a device, so its deployment can be followed from another computer.
# Assign it in Intune as a platform script: system context, no signature check, 64-bit PowerShell.
# docs/AGENT.md explains the three links and how to make them.

$Download  = ''   # Link to TrackVice.exe
$Sha256    = ''   # SHA-256 of that TrackVice.exe, from Get-FileHash
$Upload    = ''   # Where the agent uploads: the container SAS link with Create and Write
$KeepHours = 48   # The agent removes itself this many hours after the deployment is done, and at most $MaxDays days after it was installed
$MaxDays   = 30   # The agent removes itself this many days after it was installed, also when the deployment is not done
$MaxAgeHours = 0  # Only install on a device enrolled within this many hours. 0 installs on every device
$Alert     = ''   # Optional: a Teams workflow or other webhook link. The agent posts failures and the end of the deployment there
$UpdateKey = ''   # Optional: the public key from TrackVice > Settings > Publish agent update. With it the agent installs new versions you publish
$UpdateLink = ''  # Optional, with $UpdateKey: where the agent reads published versions: a container SAS link with Read, or a folder

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
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

# Downloads go to a new folder that only SYSTEM and administrators can change, so the file that is checked is the one that runs.
$work = Join-Path $env:TEMP ('TrackVice-' + [guid]::NewGuid().ToString('N'))
$acl = New-Object -TypeName System.Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)
foreach ($sid in 'S-1-5-18', 'S-1-5-32-544') {
    $account = New-Object -TypeName System.Security.Principal.SecurityIdentifier -ArgumentList $sid
    $acl.AddAccessRule((New-Object -TypeName System.Security.AccessControl.FileSystemAccessRule -ArgumentList $account, 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow'))
}
$null = [IO.Directory]::CreateDirectory($work, $acl)

$exe = Join-Path $work 'TrackVice.exe'
$out = Join-Path $work 'install.out'
$err = Join-Path $work 'install.err'

Invoke-WebRequest -Uri $Download -OutFile $exe -UseBasicParsing

$hash = (Get-FileHash -Path $exe -Algorithm SHA256).Hash
if ($hash -ne $Sha256) {
    Remove-Item -Path $work -Recurse -Force
    throw "TrackVice.exe has SHA-256 $hash, expected $Sha256. Update `$Sha256 after uploading a new TrackVice.exe."
}

$arguments = '/agent-install', '/upload', "`"$Upload`"", '/keep', $KeepHours
if ($MaxDays) {
    $arguments += '/max-days', $MaxDays
}
if ($Alert) {
    $arguments += '/alert', "`"$Alert`""
}
if ($UpdateKey) {
    $arguments += '/update-key', $UpdateKey
}
if ($UpdateLink) {
    $arguments += '/update', "`"$UpdateLink`""
}
$process = Start-Process -FilePath $exe -ArgumentList $arguments -Wait -PassThru -RedirectStandardOutput $out -RedirectStandardError $err

$message = (Get-Content -Path $out, $err -ErrorAction SilentlyContinue | Where-Object { $_.Trim() }) -join ' '
Remove-Item -Path $work -Recurse -Force -ErrorAction SilentlyContinue

if ($process.ExitCode -ne 0) {
    throw "TrackVice.exe /agent-install returned $($process.ExitCode). $message"
}
Write-Output $message
exit 0
