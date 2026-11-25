#Requires -RunAsAdministrator

# Installs the TrackVice agent on a device, so its deployment can be followed from another computer.
# Assign it in Intune as a platform script: system context, no signature check, 64-bit PowerShell.
# For a share (UNC path) instead of Azure storage, made by TrackVice > New customer. docs/AGENT.md explains it.

$Download  = ''   # TrackVice.exe on the share: \\server\share\folder\agent\TrackVice.exe. The device reads it as its computer account
$Sha256    = ''   # SHA-256 of that TrackVice.exe, from Get-FileHash
$Upload    = ''   # Where the agent uploads: the share folder, \\server\share\folder. The device writes there as its computer account
$KeepHours = 48   # The agent removes itself this many hours after the deployment is done, and at most $MaxDays days after it was installed
$MaxDays   = 30   # The agent removes itself this many days after it was installed, also when the deployment is not done
$MaxAgeHours = 0  # Only install on a device enrolled within this many hours. 0 installs on every device
$Alert     = ''   # Optional: a Teams workflow or other webhook link. The agent posts failures and the end of the deployment there
$UpdateKey = ''   # Optional: the public key from TrackVice > Settings > Publish agent update. With it the agent installs new versions you publish
$UpdateLink = ''  # Optional, with $UpdateKey: where the agent reads published versions: the share folder, which has agent\latest.json

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

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

# The agent is installed when TrackVice.exe is in Program Files and its scheduled task is registered.
$programFiles = $env:ProgramW6432
if (-not $programFiles) {
    $programFiles = $env:ProgramFiles
}
$agentExe = Join-Path (Join-Path $programFiles 'TrackVice Agent') 'TrackVice.exe'

function Get-AgentVersion {
    $registered = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\TrackVice\Agent' -Name 'Version' -ErrorAction SilentlyContinue).Version
    if ($registered) {
        return [string]$registered
    }
    return [string](Get-Item -Path $agentExe).VersionInfo.ProductVersion
}

# Waits until the name of the host resolves, so a device that has just got its network does not fail on the first download.
function Wait-ForHost {
    param([string]$HostName, [int]$Seconds = 180)
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ($true) {
        try {
            $null = [Net.Dns]::GetHostAddresses($HostName)
            return $true
        }
        catch {
            if ((Get-Date) -ge $deadline) {
                Write-Output "The name $HostName did not resolve within $Seconds seconds. The download is tried anyway."
                return $false
            }
            Start-Sleep -Seconds 5
        }
    }
}

# Runs a download up to 5 times, with 5, 15, 30 and 60 seconds between the attempts. The last failure ends the script with exit code 1.
function Invoke-WithRetry {
    param([scriptblock]$Action, [string]$HostName, [string]$Path)
    $delays = 5, 15, 30, 60
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            & $Action
            return
        }
        catch {
            $reason = $_.Exception.Message
            Remove-Item -Path $Path -Force -ErrorAction SilentlyContinue
            if ($attempt -eq 5) {
                throw "TrackVice.exe could not be downloaded from $HostName after 5 attempts: $reason"
            }
            $delay = $delays[$attempt - 1]
            Write-Output "Download attempt $attempt from $HostName failed: $reason Trying again in $delay seconds."
            Start-Sleep -Seconds $delay
        }
    }
}

$agentTask = Get-ScheduledTask -TaskName 'TrackVice Agent' -ErrorAction SilentlyContinue
$agentRegistered = $agentTask -and $agentTask.State -ne 'Disabled'
$useInstalled = $false
if (Test-Path -Path $agentExe) {
    # Whatever version is installed is kept: the agent updates itself, and an older exe is never put over a newer one.
    if ($agentRegistered) {
        $state = if ($agentTask.State -eq 'Running') { 'installed and running' } else { 'installed and registered' }
        Write-Output "The TrackVice agent $(Get-AgentVersion) is already $state. Nothing to do."
        exit 0
    }
    # The exe is there but its task is not: register it again from the installed exe, without a download.
    Write-Output 'The TrackVice agent is installed but its scheduled task is missing. It is registered again.'
    $useInstalled = $true
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

if ($useInstalled) {
    $exe = $agentExe
}
else {
    $downloadHost = ([Uri]$Download).Host
    if ($downloadHost) {
        $null = Wait-ForHost -HostName $downloadHost
    }
    Invoke-WithRetry -HostName $downloadHost -Path $exe -Action { Copy-Item -LiteralPath $Download -Destination $exe -Force }
}

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
