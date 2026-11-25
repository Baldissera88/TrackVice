namespace TrackVice
{
    // Copies of agent/Install-TrackViceAgent.ps1 and agent/Install-TrackViceAgent-Links.ps1, so TrackVice can fill them in
    // without the repository. A test checks that they are the same as the files.
    public static class AgentScriptTemplate
    {
        // The platform script that holds only the customer code.
        public const string Text = @"#Requires -RunAsAdministrator

# Installs the TrackVice agent on a device, so its deployment can be followed from another computer.
# Assign it in Intune as a platform script: system context, no signature check, 64-bit PowerShell.
# The customer code is made by TrackVice. It holds the links and settings, and the key the published TrackVice is signed with.

$Customer = ''   # The customer code from TrackVice > Devices > Add customer

$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 downloads much more slowly while it draws a progress bar.
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Customer = $Customer.Trim()
if (-not $Customer) {
    throw 'Fill in $Customer at the top of the script with the customer code from TrackVice.'
}
if (-not $Customer.StartsWith('tv1:')) {
    # A customer name is short; a customer code is far longer, and a link has ://.
    if ($Customer.Length -lt 60 -and $Customer -notmatch '://') {
        throw ""`$Customer holds '$Customer', which looks like a customer name. Paste the customer code from TrackVice > Settings > Customers > Customer code..., which starts with tv1:""
    }
    throw 'This is not a TrackVice customer code. It starts with tv1:'
}
try {
    $text = $Customer.Substring(4).Replace('-', '+').Replace('_', '/')
    while ($text.Length % 4) {
        $text += '='
    }
    $code = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($text)) | ConvertFrom-Json
}
catch {
    throw 'The customer code could not be read. Copy it again from TrackVice.'
}
if (-not $code.u -or -not $code.a -or -not $code.k) {
    throw 'The customer code has no upload link, download link or update key. Copy it again from TrackVice.'
}

# Without this, a device that was enrolled long ago also installs the agent when the script is assigned to it.
$MaxAgeHours = [int]$code.m
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
                Write-Output ""The name $HostName did not resolve within $Seconds seconds. The download is tried anyway.""
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
                throw ""TrackVice.exe could not be downloaded from $HostName after 5 attempts: $reason""
            }
            $delay = $delays[$attempt - 1]
            Write-Output ""Download attempt $attempt from $HostName failed: $reason Trying again in $delay seconds.""
            Start-Sleep -Seconds $delay
        }
    }
}

$agentTask = Get-ScheduledTask -TaskName 'TrackVice Agent' -ErrorAction SilentlyContinue
$agentRegistered = $agentTask -and $agentTask.State -ne 'Disabled'
$useInstalled = $false
if (Test-Path -Path $agentExe) {
    if ($agentRegistered) {
        $state = if ($agentTask.State -eq 'Running') { 'installed and running' } else { 'installed and registered' }
        Write-Output ""The TrackVice agent $(Get-AgentVersion) is already $state. Nothing to do.""
        exit 0
    }
    # The exe is there but its task is not: register it again from the installed exe, without a download.
    Write-Output 'The TrackVice agent is installed but its scheduled task is missing. It is registered again.'
    $useInstalled = $true
}

# A blob in the agent container: the name goes before the ? of the container link.
function Get-BlobLink {
    param([string]$Container, [string]$Blob)
    $parts = $Container.Split([char[]]'?', 2)
    $link = $parts[0].TrimEnd('/') + '/' + $Blob
    if ($parts.Count -gt 1) {
        $link += '?' + $parts[1]
    }
    return $link
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

$json = Join-Path $work 'latest.json'
$exe = Join-Path $work 'TrackVice.exe'
$out = Join-Path $work 'install.out'
$err = Join-Path $work 'install.err'

try {
    if ($useInstalled) {
        $exe = $agentExe
        $version = Get-AgentVersion
    }
    else {
        try {
            $storageLink = Get-BlobLink -Container $code.a -Blob 'agent/latest.json'
            $storageHost = ([Uri]$storageLink).Host
            $null = Wait-ForHost -HostName $storageHost
            Invoke-WithRetry -HostName $storageHost -Path $json -Action { Invoke-WebRequest -Uri $storageLink -OutFile $json -UseBasicParsing }
        }
        catch {
            throw ""agent/latest.json could not be downloaded: $($_.Exception.Message) Publish TrackVice from TrackVice > Settings > Your storage, and check that the device can reach the storage account.""
        }
        $raw = [IO.File]::ReadAllText($json, [Text.Encoding]::UTF8)
        $manifest = $raw | ConvertFrom-Json

        # The published time is taken as the text in the file, since PowerShell 7 turns it into a date.
        $version = [string]$manifest.version
        $sha256 = ([string]$manifest.sha256).ToUpperInvariant()
        $size = [long]$manifest.size
        $blob = [string]$manifest.blob
        $published = [regex]::Match($raw, '""published""\s*:\s*""([^""]*)""').Groups[1].Value

        if ($version -notmatch '^\d{1,9}(\.\d{1,9}){0,3}(-[0-9A-Za-z][0-9A-Za-z.]*)?$' -or $blob -ne ('agent/TrackVice-' + $version + '.exe')) {
            throw ""agent/latest.json names $blob for version $version. It is refused.""
        }
        if ($sha256 -notmatch '^[0-9A-F]{64}$' -or $size -le 0 -or $size -gt 200MB) {
            throw 'agent/latest.json has no valid SHA-256 or size. It is refused.'
        }

        # The same text TrackVice signs when it publishes a version.
        $payload = 'TrackVice agent update', ('version=' + $version), ('sha256=' + $sha256), ('size=' + $size.ToString([Globalization.CultureInfo]::InvariantCulture)), ('blob=' + $blob), ('published=' + $published), ''
        $bytes = [Text.Encoding]::UTF8.GetBytes($payload -join ""`n"")

        $valid = $false
        $rsa = New-Object -TypeName System.Security.Cryptography.RSACryptoServiceProvider
        try {
            $rsa.ImportCspBlob([Convert]::FromBase64String($code.k))
            $valid = $rsa.VerifyData($bytes, 'SHA256', [Convert]::FromBase64String([string]$manifest.signature))
        }
        catch {
            $valid = $false
        }
        $rsa.Clear()
        if (-not $valid) {
            throw 'agent/latest.json is not signed with the update key in the customer code. It is refused.'
        }

        $blobLink = Get-BlobLink -Container $code.a -Blob $blob
        Invoke-WithRetry -HostName $storageHost -Path $exe -Action { Invoke-WebRequest -Uri $blobLink -OutFile $exe -UseBasicParsing }

        $length = (Get-Item -Path $exe).Length
        $hash = (Get-FileHash -Path $exe -Algorithm SHA256).Hash
        if ($length -ne $size -or $hash -ne $sha256) {
            throw ""$blob has $length bytes and SHA-256 $hash, expected $size bytes and $sha256. It is refused.""
        }
    }

    $arguments = '/agent-install', '/customer', ""`""$Customer`""""
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -Wait -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
    $message = (Get-Content -Path $out, $err -ErrorAction SilentlyContinue | Where-Object { $_.Trim() }) -join ' '
}
finally {
    Remove-Item -Path $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($process.ExitCode -ne 0) {
    throw ""TrackVice.exe /agent-install returned $($process.ExitCode). $message""
}
Write-Output ""TrackVice $version. $message""
exit 0
";

        // The earlier script with the download link, SHA-256 and upload link, for setups made before customer codes.
        public const string Links = @"#Requires -RunAsAdministrator

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
                Write-Output ""The name $HostName did not resolve within $Seconds seconds. The download is tried anyway.""
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
                throw ""TrackVice.exe could not be downloaded from $HostName after 5 attempts: $reason""
            }
            $delay = $delays[$attempt - 1]
            Write-Output ""Download attempt $attempt from $HostName failed: $reason Trying again in $delay seconds.""
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
        Write-Output ""The TrackVice agent $(Get-AgentVersion) is already $state. Nothing to do.""
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
    $null = Wait-ForHost -HostName $downloadHost
    Invoke-WithRetry -HostName $downloadHost -Path $exe -Action { Invoke-WebRequest -Uri $Download -OutFile $exe -UseBasicParsing }
}

$hash = (Get-FileHash -Path $exe -Algorithm SHA256).Hash
if ($hash -ne $Sha256) {
    Remove-Item -Path $work -Recurse -Force
    throw ""TrackVice.exe has SHA-256 $hash, expected $Sha256. Update `$Sha256 after uploading a new TrackVice.exe.""
}

$arguments = '/agent-install', '/upload', ""`""$Upload`"""", '/keep', $KeepHours
if ($MaxDays) {
    $arguments += '/max-days', $MaxDays
}
if ($Alert) {
    $arguments += '/alert', ""`""$Alert`""""
}
if ($UpdateKey) {
    $arguments += '/update-key', $UpdateKey
}
if ($UpdateLink) {
    $arguments += '/update', ""`""$UpdateLink`""""
}
$process = Start-Process -FilePath $exe -ArgumentList $arguments -Wait -PassThru -RedirectStandardOutput $out -RedirectStandardError $err

$message = (Get-Content -Path $out, $err -ErrorAction SilentlyContinue | Where-Object { $_.Trim() }) -join ' '
Remove-Item -Path $work -Recurse -Force -ErrorAction SilentlyContinue

if ($process.ExitCode -ne 0) {
    throw ""TrackVice.exe /agent-install returned $($process.ExitCode). $message""
}
Write-Output $message
exit 0
";

        // The script for a share instead of Azure storage: the same values, and TrackVice.exe is copied from the share.
        public const string Share = @"#Requires -RunAsAdministrator

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
                Write-Output ""The name $HostName did not resolve within $Seconds seconds. The download is tried anyway.""
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
                throw ""TrackVice.exe could not be downloaded from $HostName after 5 attempts: $reason""
            }
            $delay = $delays[$attempt - 1]
            Write-Output ""Download attempt $attempt from $HostName failed: $reason Trying again in $delay seconds.""
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
        Write-Output ""The TrackVice agent $(Get-AgentVersion) is already $state. Nothing to do.""
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
    throw ""TrackVice.exe has SHA-256 $hash, expected $Sha256. Update `$Sha256 after uploading a new TrackVice.exe.""
}

$arguments = '/agent-install', '/upload', ""`""$Upload`"""", '/keep', $KeepHours
if ($MaxDays) {
    $arguments += '/max-days', $MaxDays
}
if ($Alert) {
    $arguments += '/alert', ""`""$Alert`""""
}
if ($UpdateKey) {
    $arguments += '/update-key', $UpdateKey
}
if ($UpdateLink) {
    $arguments += '/update', ""`""$UpdateLink`""""
}
$process = Start-Process -FilePath $exe -ArgumentList $arguments -Wait -PassThru -RedirectStandardOutput $out -RedirectStandardError $err

$message = (Get-Content -Path $out, $err -ErrorAction SilentlyContinue | Where-Object { $_.Trim() }) -join ' '
Remove-Item -Path $work -Recurse -Force -ErrorAction SilentlyContinue

if ($process.ExitCode -ne 0) {
    throw ""TrackVice.exe /agent-install returned $($process.ExitCode). $message""
}
Write-Output $message
exit 0
";
    }
}
