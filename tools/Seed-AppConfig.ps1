#Requires -Version 7.0
<#
.SYNOPSIS
    Seeds Azure App Configuration key-values for IntuneDeviceActions.

.DESCRIPTION
    Populates the App Configuration store with all settings that the Function
    Apps read at startup. Uses 'az appconfig kv set' (data-plane CLI) which
    works regardless of ARM proxy / disableLocalAuth settings.

    The caller must have 'App Configuration Data Owner' on the target store.

    Safe to re-run (idempotent upserts).

.PARAMETER ResourceGroup
    Target resource group containing the App Configuration store.

.PARAMETER NamePrefix
    Resource naming prefix (e.g. 'devact').

.PARAMETER NameSuffix
    Resource naming suffix (e.g. 'dev').

.PARAMETER ParametersFile
    Bicep parameters file to read non-infra parameter values from.
    Default: infra\main-public.parameters.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ResourceGroup,
    [Parameter(Mandatory)] [string] $NamePrefix,
    [string] $NameSuffix = '',
    [string] $ParametersFile
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $ParametersFile) {
    $ParametersFile = Join-Path $repoRoot 'infra' 'main-public.parameters.json'
}

# ── Helpers ──────────────────────────────────────────────────────────────────
function Set-KV {
    param([string]$Key, [string]$Value, [string]$Label = '')
    $args_ = @('appconfig', 'kv', 'set', '-n', $storeName, '--key', $Key, '--value', $Value, '--yes', '-o', 'none')
    if ($Label) { $args_ += '--label'; $args_ += $Label }
    az @args_ 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Warning "  [WARN] Failed to set $Key (label=$Label)" }
    else { Write-Host "  [OK]  $Key$(if($Label){" [$Label]"})" }
}

# ── Read parameters file ─────────────────────────────────────────────────────
Write-Host "`n==> Reading parameters from $ParametersFile"
$paramsJson = Get-Content $ParametersFile -Raw | ConvertFrom-Json
$p = $paramsJson.parameters

function Get-Param([string]$name, $default = '') {
    if ($p.PSObject.Properties[$name]) { return $p.$name.value }
    return $default
}

# ── Resolve resource names ───────────────────────────────────────────────────
$sep = if ($NameSuffix) { '-' } else { '' }
$storeName       = "${NamePrefix}-appcfg-${NameSuffix}"
$sbNamespaceName = "${NamePrefix}-sb-${NameSuffix}"
$storageProcName = "${NamePrefix}stp${NameSuffix}"

Write-Host "`n==> Resolving infrastructure resources"
Write-Host "    App Config store: $storeName"
Write-Host "    Service Bus:      $sbNamespaceName"
Write-Host "    Storage (proc):   $storageProcName"

# Resolve UAMI client IDs
Write-Host "`n==> Resolving managed identity client IDs"
$uamis = @{
    web       = "${NamePrefix}-uami-web-${NameSuffix}"
    proc      = "${NamePrefix}-uami-${NameSuffix}"
    wipe      = "${NamePrefix}-uami-wipe-${NameSuffix}"
    autopilot = "${NamePrefix}-uami-autopilot-${NameSuffix}"
    bitlocker = "${NamePrefix}-uami-bitlocker-${NameSuffix}"
    rename    = "${NamePrefix}-uami-rename-${NameSuffix}"
}
$clientIds = @{}
foreach ($role in $uamis.Keys) {
    $cid = az identity show -g $ResourceGroup -n $uamis[$role] --query clientId -o tsv 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "  [WARN] UAMI $($uamis[$role]) not found, skipping role $role"
        continue
    }
    $clientIds[$role] = $cid
    Write-Host "  [OK]  $role -> $cid"
}

# ── Read Bicep parameter values ──────────────────────────────────────────────
$graphTenantId            = Get-Param 'graphTenantId' (az account show --query tenantId -o tsv 2>&1)
$allowedGroupId           = Get-Param 'allowedGroupId'
$bitlockerAllowedGroupId  = Get-Param 'bitlockerAllowedGroupId' $allowedGroupId
$keepEnrollmentData       = Get-Param 'keepEnrollmentData' 'false'
$keepUserData             = Get-Param 'keepUserData' 'false'

$actionRequestsQueueName  = Get-Param 'actionRequestsQueueName' 'action-requests'
$actionDispatchQueueName  = Get-Param 'actionDispatchQueueName' 'action-dispatch'
$wipeActionQueueName      = Get-Param 'wipeActionQueueName' 'wipe-action'
$autopilotActionQueueName = Get-Param 'autopilotActionQueueName' 'autopilot-action'
$bitlockerActionQueueName = Get-Param 'bitlockerActionQueueName' 'bitlocker-action'
$renameActionQueueName    = Get-Param 'renameActionQueueName' 'rename-action'

$ledgerContainerName      = Get-Param 'ledgerContainerName' 'action-ledger'
$auditTableName           = Get-Param 'auditTableName' 'auditevents'
$actionStatusTableName    = Get-Param 'actionStatusTableName' 'actionstatus'
$actionStatusPollerCron   = Get-Param 'actionStatusPollerCron' '*/5 * * * * *'
$actionStatusPollMaxAge   = Get-Param 'actionStatusPollMaxAgeHours' '24'

$idempotencyMaxPerDay     = Get-Param 'idempotencyMaxActionsPerDay' '5'
$idempotencyRearmHours    = Get-Param 'idempotencyRearmGracePeriodHours' '48'
$idempotencyAllowRearm    = Get-Param 'idempotencyAllowForceRearm' 'true'

$enableEventGrid          = Get-Param 'enableEventGridAuditStream' 'true'
$eventGridTopicName       = Get-Param 'eventGridAuditTopicName' ''
$eventGridEndpoint        = ''
if ($enableEventGrid -eq 'true' -or $enableEventGrid -eq $true) {
    $egName = if ($eventGridTopicName) { $eventGridTopicName } else { "${NamePrefix}-eg-audit-${NameSuffix}" }
    $eventGridEndpoint = az eventgrid topic show -g $ResourceGroup -n $egName --query endpoint -o tsv 2>&1
    if ($LASTEXITCODE -ne 0) { $eventGridEndpoint = ''; Write-Warning "  [WARN] Event Grid topic not found" }
}

$trustedCaThumbprints     = Get-Param 'trustedCaThumbprints' ''
$trustedRootCerts         = Get-Param 'trustedRootCertificatesBase64' ''
$trustedIntCerts          = Get-Param 'trustedIntermediateCertificatesBase64' ''
$trustedCaCerts           = Get-Param 'trustedCaCertificatesBase64' ''
$allowedLeafThumbs        = Get-Param 'allowedLeafThumbprints' ''
$checkRevocation          = Get-Param 'checkRevocation' 'false'
$revocationMode           = Get-Param 'revocationMode' 'Online'
$revocationFlag           = Get-Param 'revocationFlag' 'ExcludeRoot'
$requireClientAuthEku     = Get-Param 'requireClientAuthEku' 'true'
$deviceIdBindingClaim     = Get-Param 'deviceIdBindingClaim' 'Auto'
$thumbprintToDeviceMap    = Get-Param 'clientCertThumbprintToDeviceMap' ''
$maxTimestampSkew         = Get-Param 'maxTimestampSkewSeconds' '300'

$renameEndpoint           = Get-Param 'renameEndpoint' ''
$renameAuthHeaderName     = Get-Param 'renameAuthHeaderName' 'X-Api-Key'
$renameNewNameJsonPath    = Get-Param 'renameNewNameJsonPath' 'newName'
$renameOnCollision        = Get-Param 'renameOnCollision' 'block'

# ── Seed: Sentinel ───────────────────────────────────────────────────────────
Write-Host "`n==> Seeding Sentinel key"
Set-KV 'Sentinel' '1'

# ── Seed: Shared settings (no label) ────────────────────────────────────────
Write-Host "`n==> Seeding shared settings"
Set-KV 'Audit:TableName'                         $auditTableName
Set-KV 'Audit:StorageAccount'                    $storageProcName
Set-KV 'ActionStatus:TableName'                  $actionStatusTableName
Set-KV 'ActionStatus:PollMaxAgeHours'            "$actionStatusPollMaxAge"
Set-KV 'ActionStatusPoller:CronExpression'        $actionStatusPollerCron
Set-KV 'Idempotency:BlobContainer'               $ledgerContainerName
Set-KV 'Idempotency:StorageAccount'              $storageProcName
Set-KV 'Idempotency:AllowForceRearm'             "$idempotencyAllowRearm"
Set-KV 'Idempotency:MaxActionsPerDevicePerDay'   "$idempotencyMaxPerDay"
Set-KV 'Idempotency:RearmGracePeriodHours'       "$idempotencyRearmHours"
Set-KV 'Idempotency:AdminApiEnabled'             'true'
Set-KV 'EventGrid:Enabled'                       "$enableEventGrid"
Set-KV 'EventGrid:AuditTopicEndpoint'            "$eventGridEndpoint"
Set-KV 'ServiceBus:fullyQualifiedNamespace'      "${sbNamespaceName}.servicebus.windows.net"
Set-KV 'ServiceBus:credential'                   'managedidentity'
Set-KV 'ServiceBus:ActionRequestsQueue'          $actionRequestsQueueName
Set-KV 'ServiceBus:ActionDispatchQueue'           $actionDispatchQueueName
Set-KV 'ServiceBus:WipeActionQueue'              $wipeActionQueueName
Set-KV 'ServiceBus:AutopilotActionQueue'         $autopilotActionQueueName
Set-KV 'ServiceBus:BitLockerActionQueue'         $bitlockerActionQueueName
Set-KV 'ServiceBus:RenameActionQueue'            $renameActionQueueName
Set-KV 'Graph:TenantId'                          $graphTenantId
Set-KV 'Wipe:AllowedGroupId'                     $allowedGroupId
Set-KV 'Wipe:KeepEnrollmentData'                 "$keepEnrollmentData"
Set-KV 'Wipe:KeepUserData'                       "$keepUserData"
Set-KV 'BitLocker:AllowedGroupId'                $bitlockerAllowedGroupId
Set-KV 'ClientCert:RequireClientCert'            'true'
Set-KV 'ClientCert:TrustForwardedHeader'         'true'
Set-KV 'ClientCert:TrustedCaThumbprints'         $trustedCaThumbprints
Set-KV 'ClientCert:TrustedRootCertificates'      $trustedRootCerts
Set-KV 'ClientCert:TrustedIntermediateCertificates' $trustedIntCerts
Set-KV 'ClientCert:TrustedCaCertificates'        $trustedCaCerts
Set-KV 'ClientCert:AllowedLeafThumbprints'       $allowedLeafThumbs
Set-KV 'ClientCert:CheckRevocation'              "$checkRevocation"
Set-KV 'ClientCert:RevocationMode'               $revocationMode
Set-KV 'ClientCert:RevocationFlag'               $revocationFlag
Set-KV 'ClientCert:RequireClientAuthEku'         "$requireClientAuthEku"
Set-KV 'ClientCert:DeviceIdBindingClaim'         $deviceIdBindingClaim
Set-KV 'ClientCert:ThumbprintToDeviceMap'        $thumbprintToDeviceMap
Set-KV 'Replay:MaxTimestampSkewSeconds'          "$maxTimestampSkew"
Set-KV 'Actions:AllowedTypes'                    'wipe'

# ── Seed: Per-role settings (labeled) ────────────────────────────────────────
$roles = @(
    @{ role = 'web';       uamiKey = 'web';       hasGraph = $false }
    @{ role = 'proc';      uamiKey = 'proc';      hasGraph = $true  }
    @{ role = 'wipe';      uamiKey = 'wipe';      hasGraph = $true  }
    @{ role = 'autopilot'; uamiKey = 'autopilot'; hasGraph = $true  }
    @{ role = 'bitlocker'; uamiKey = 'bitlocker'; hasGraph = $true  }
    @{ role = 'rename';    uamiKey = 'rename';    hasGraph = $true  }
)

foreach ($r in $roles) {
    $role = $r.role
    if (-not $clientIds.ContainsKey($r.uamiKey)) { continue }
    $cid = $clientIds[$r.uamiKey]

    Write-Host "`n==> Seeding per-role settings [$role]"
    Set-KV 'App:Role'            $role -Label $role
    Set-KV 'ServiceBus:clientId' $cid  -Label $role

    if ($r.hasGraph) {
        Set-KV 'Graph:ManagedIdentityClientId' $cid -Label $role
    }

    if ($role -ne 'web' -and $role -ne 'proc') {
        Set-KV 'Idempotency:AdminApiEnabled' 'false' -Label $role
    }

    if ($role -eq 'rename') {
        Set-KV 'Rename:Endpoint'        $renameEndpoint       -Label $role
        Set-KV 'Rename:AuthHeaderName'   $renameAuthHeaderName -Label $role
        Set-KV 'Rename:NewNameJsonPath'  $renameNewNameJsonPath -Label $role
        Set-KV 'Rename:OnCollision'      $renameOnCollision    -Label $role
        Set-KV 'Rename:TimeoutSeconds'   '30'                  -Label $role
    }
}

Write-Host "`n==> App Configuration seed complete!`n"
