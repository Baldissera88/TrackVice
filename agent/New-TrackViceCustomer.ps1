#Requires -Version 5.1
#Requires -Modules Az.Accounts, Az.Storage

<#
.SYNOPSIS
    Sets up the storage for the TrackVice agent for a new customer and writes a filled platform script.

.DESCRIPTION
    Does what TrackVice > Devices > New customer does, without Intune:
    creates the resource group and the storage account when they are missing (StorageV2, Standard_LRS,
    HTTPS only, TLS 1.2, no anonymous blob access, account key access on), the private containers
    trackvice and trackvice-agent, and a lifecycle rule that deletes sessions/ and logs/ after
    RetentionDays. It uploads TrackVice.exe to trackvice-agent, makes the upload, viewer and download
    links, and writes a copy of Install-TrackViceAgent.ps1 with the values filled in.

    An existing storage account is used as it is. Other lifecycle rules on it are kept.
    The account key is only used to sign the links; it is not written anywhere.

    The output object holds the links. The viewer link can read every session: paste it into
    TrackVice > Devices with the TrackVice agent, and keep it like a password.

.PARAMETER CustomerName
    The customer, used for the default storage account name and the script's file name.

.PARAMETER SubscriptionId
    The Azure subscription for the storage account.

.PARAMETER TenantId
    Optional. The tenant of the subscription. Without it the current Az sign-in is used, or you sign in.

.PARAMETER ResourceGroupName
    The resource group. It is created in Location when it does not exist.

.PARAMETER StorageAccountName
    3 to 24 lowercase letters and digits, unique in all of Azure. Default: the customer name and trackvice.

.PARAMETER Location
    The region for a new resource group and storage account. Default: westeurope.

.PARAMETER RetentionDays
    Sessions and logs older than this are deleted. Default: 30.

.PARAMETER LinkDays
    How long the links are valid. Default: 365.

.PARAMETER ExePath
    The TrackVice.exe the devices download. Default: TrackVice.exe in the folder above this script.

.PARAMETER Alert
    Optional. A Teams workflow link for alerts. It goes into $Alert.

.PARAMETER MaxAgeHours
    Only devices enrolled within this many hours install the agent. Default: 24.

.PARAMETER UpdateKey
    Optional. The public key from TrackVice > Settings > Publish agent update. With it, $UpdateKey and
    $UpdateLink are filled in, and the agents install the versions you publish to trackvice-agent.

.PARAMETER OutputPath
    Where to write the filled script. Default: Install-TrackViceAgent-<customer>.ps1 in the current folder.

.EXAMPLE
    .\New-TrackViceCustomer.ps1 -CustomerName Contoso -SubscriptionId 4f6c2e1a-9b3d-4c7e-8a51-2d9f0b6e3c71 -ResourceGroupName rg-trackvice
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CustomerName,

    [Parameter(Mandatory = $true)]
    [string]$SubscriptionId,

    [string]$TenantId,

    [Parameter(Mandatory = $true)]
    [string]$ResourceGroupName,

    [ValidatePattern('^[a-z0-9]{3,24}$')]
    [string]$StorageAccountName,

    [string]$Location = 'westeurope',

    [ValidateRange(1, 3650)]
    [int]$RetentionDays = 30,

    [ValidateRange(1, 3650)]
    [int]$LinkDays = 365,

    [string]$ExePath = (Join-Path (Split-Path -Path $PSScriptRoot -Parent) 'TrackVice.exe'),

    [string]$Alert = '',

    [ValidateRange(0, 720)]
    [int]$MaxAgeHours = 24,

    [string]$UpdateKey = '',

    [string]$TemplatePath = (Join-Path $PSScriptRoot 'Install-TrackViceAgent.ps1'),

    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$sessionsContainer = 'trackvice'
$agentContainer = 'trackvice-agent'
$ruleName = 'trackvice-cleanup'

function Get-SuggestedName {
    param([string]$Name)
    $head = ($Name.ToLowerInvariant() -replace '[^a-z0-9]', '')
    if (-not $head) { $head = 'agents' }
    $tail = 'trackvice'
    if ($head.Length + $tail.Length -gt 24) { $head = $head.Substring(0, 24 - $tail.Length) }
    return $head + $tail
}

function ConvertTo-Literal {
    param([string]$Value)
    return "'" + ($Value -replace "'", "''") + "'"
}

# Replaces the value on the line that sets $Name, and keeps the comment after it.
function Set-ScriptValue {
    param([string]$Text, [string]$Name, [string]$Literal)
    $pattern = '(?m)^(\$' + [regex]::Escape($Name) + '\s*=\s*)(''(?:[^'']|'''')*''|-?\d+)'
    $found = [regex]::Matches($Text, $pattern)
    if ($found.Count -ne 1) {
        throw "The template has no line that sets `$$Name. Use the Install-TrackViceAgent.ps1 of the same TrackVice version."
    }
    $value = $found[0].Groups[2]
    return $Text.Substring(0, $value.Index) + $Literal + $Text.Substring($value.Index + $value.Length)
}

if (-not $StorageAccountName) { $StorageAccountName = Get-SuggestedName -Name $CustomerName }
if (-not $OutputPath) {
    $safeName = $CustomerName -replace '[^A-Za-z0-9_-]', ''
    $OutputPath = Join-Path (Get-Location).Path ('Install-TrackViceAgent-' + $safeName + '.ps1')
}
if (-not (Test-Path -Path $ExePath -PathType Leaf)) { throw "TrackVice.exe was not found at $ExePath. Give it with -ExePath." }
if (-not (Test-Path -Path $TemplatePath -PathType Leaf)) { throw "Install-TrackViceAgent.ps1 was not found at $TemplatePath. Give it with -TemplatePath." }
# .NET file methods do not know the current PowerShell folder, so the paths are made full.
$ExePath = (Resolve-Path -Path $ExePath).ProviderPath
$TemplatePath = (Resolve-Path -Path $TemplatePath).ProviderPath
$OutputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
if ($Alert -and $Alert -notmatch '^https://') { throw 'The alert link must start with https://' }

# Sign in to Azure and pick the subscription.
if ($TenantId) {
    Connect-AzAccount -Tenant $TenantId -Subscription $SubscriptionId | Out-Null
}
elseif (-not (Get-AzContext)) {
    Connect-AzAccount -Subscription $SubscriptionId | Out-Null
}
Set-AzContext -Subscription $SubscriptionId | Out-Null

# Resource group, with the REST API through Az.Accounts, so Az.Resources is not needed.
$groupPath = "/subscriptions/$SubscriptionId/resourcegroups/$ResourceGroupName" + '?api-version=2021-04-01'
$response = Invoke-AzRestMethod -Path $groupPath -Method GET
if ($response.StatusCode -eq 404) {
    Write-Verbose "Creating the resource group $ResourceGroupName in $Location."
    $response = Invoke-AzRestMethod -Path $groupPath -Method PUT -Payload (@{ location = $Location } | ConvertTo-Json)
    if ($response.StatusCode -notin 200, 201) {
        throw "The resource group $ResourceGroupName could not be created (HTTP $($response.StatusCode)). $($response.Content)"
    }
}
elseif ($response.StatusCode -ne 200) {
    throw "The resource group $ResourceGroupName could not be read (HTTP $($response.StatusCode)). $($response.Content)"
}

# Storage account.
$account = Get-AzStorageAccount -ResourceGroupName $ResourceGroupName -Name $StorageAccountName -ErrorAction SilentlyContinue
if (-not $account) {
    $availability = Get-AzStorageAccountNameAvailability -Name $StorageAccountName
    if (-not $availability.NameAvailable) {
        throw "The storage account name $StorageAccountName cannot be used: $($availability.Message) Give another one with -StorageAccountName."
    }
    Write-Verbose "Creating the storage account $StorageAccountName."
    $accountParameters = @{
        ResourceGroupName      = $ResourceGroupName
        Name                   = $StorageAccountName
        Location               = $Location
        SkuName                = 'Standard_LRS'
        Kind                   = 'StorageV2'
        EnableHttpsTrafficOnly = $true
        MinimumTlsVersion      = 'TLS1_2'
        AllowBlobPublicAccess  = $false
        AllowSharedKeyAccess   = $true
    }
    $account = New-AzStorageAccount @accountParameters
}
$context = $account.Context

# Containers. A container that is there already is left as it is.
foreach ($name in $sessionsContainer, $agentContainer) {
    if (-not (Get-AzStorageContainer -Name $name -Context $context -ErrorAction SilentlyContinue)) {
        Write-Verbose "Creating the container $name."
        New-AzStorageContainer -Name $name -Context $context -Permission Off | Out-Null
    }
}

# Lifecycle rule. Rules the account has already are kept; one with the same name is replaced.
$action = Add-AzStorageAccountManagementPolicyAction -BaseBlobAction Delete -DaysAfterModificationGreaterThan $RetentionDays
$filter = New-AzStorageAccountManagementPolicyFilter -PrefixMatch "$sessionsContainer/sessions/", "$sessionsContainer/logs/" -BlobType blockBlob
$rules = @()
$policy = Get-AzStorageAccountManagementPolicy -ResourceGroupName $ResourceGroupName -StorageAccountName $StorageAccountName -ErrorAction SilentlyContinue
if ($policy -and $policy.Rules) {
    $rules += @($policy.Rules | Where-Object { $_.Name -ne $ruleName })
}
$rules += New-AzStorageAccountManagementPolicyRule -Name $ruleName -Action $action -Filter $filter
Set-AzStorageAccountManagementPolicy -ResourceGroupName $ResourceGroupName -StorageAccountName $StorageAccountName -Rule $rules | Out-Null

# TrackVice.exe and its hash.
Write-Verbose "Uploading $ExePath to $agentContainer."
Set-AzStorageBlobContent -File $ExePath -Container $agentContainer -Blob 'TrackVice.exe' -Context $context -Force | Out-Null
$sha256 = (Get-FileHash -Path $ExePath -Algorithm SHA256).Hash

# Links. The start is a few minutes back, so a device whose clock is slightly behind can use them at once.
$start = (Get-Date).ToUniversalTime().AddMinutes(-15)
$expiry = (Get-Date).ToUniversalTime().AddDays($LinkDays)
$sas = @{ StartTime = $start; ExpiryTime = $expiry; Protocol = 'HttpsOnly'; Context = $context; FullUri = $true }
$uploadLink = New-AzStorageContainerSASToken -Name $sessionsContainer -Permission 'cw' @sas
$viewerLink = New-AzStorageContainerSASToken -Name $sessionsContainer -Permission 'rl' @sas
$downloadLink = New-AzStorageBlobSASToken -Container $agentContainer -Blob 'TrackVice.exe' -Permission 'r' @sas
$updateLink = ''
if ($UpdateKey) {
    $updateLink = New-AzStorageContainerSASToken -Name $agentContainer -Permission 'r' @sas
}

# The filled platform script.
$scriptText = [IO.File]::ReadAllText($TemplatePath)
$scriptText = Set-ScriptValue -Text $scriptText -Name 'Download' -Literal (ConvertTo-Literal $downloadLink)
$scriptText = Set-ScriptValue -Text $scriptText -Name 'Sha256' -Literal (ConvertTo-Literal $sha256)
$scriptText = Set-ScriptValue -Text $scriptText -Name 'Upload' -Literal (ConvertTo-Literal $uploadLink)
$scriptText = Set-ScriptValue -Text $scriptText -Name 'MaxAgeHours' -Literal ([string]$MaxAgeHours)
$scriptText = Set-ScriptValue -Text $scriptText -Name 'Alert' -Literal (ConvertTo-Literal $Alert)
$scriptText = Set-ScriptValue -Text $scriptText -Name 'UpdateKey' -Literal (ConvertTo-Literal $UpdateKey)
$scriptText = Set-ScriptValue -Text $scriptText -Name 'UpdateLink' -Literal (ConvertTo-Literal $updateLink)
[IO.File]::WriteAllText($OutputPath, $scriptText, (New-Object -TypeName System.Text.UTF8Encoding -ArgumentList $false))

[pscustomobject]@{
    Customer       = $CustomerName
    StorageAccount = $StorageAccountName
    ResourceGroup  = $ResourceGroupName
    Sha256         = $sha256
    UploadLink     = $uploadLink
    ViewerLink     = $viewerLink
    DownloadLink   = $downloadLink
    UpdateLink     = $updateLink
    LinksExpire    = $expiry
    Script         = $OutputPath
}
