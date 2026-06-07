#requires -Version 7.2
<#
.SYNOPSIS
    Azure Automation Runbook (PowerShell 7.2) — device-rename capability
    executor with functional parity to
    IntuneDeviceActions.Capabilities.Rename.Runners.RenameActionRunner.

.DESCRIPTION
    Pipeline (LOOKUP + Graph):
        0. Payload validation — serial mandatory; intuneDeviceId mandatory
        1. Idempotency reservation (auto-rearm + 24h rolling rate limit)
        2. LOOKUP — GET the customer-internal CMDB / asset-management REST
           endpoint with the serial number; the response carries the
           canonical new name (`newName` JSON property by default).
        3. Collision check — query Entra /devices?$filter=displayName eq …
           (Entra does NOT enforce uniqueness on device displayName, unlike
           on-prem AD). Behaviour controlled by Rename:OnCollision
           Automation variable (block | warn). Skip when the resolved name
           matches the device's current name.
        4. POST Graph /deviceManagement/managedDevices/{id}/setDeviceName
           → Intune queues the rename for the next MDM sync. Windows
           requires a reboot to complete the change.

    Required Automation Account variables (created by main.bicep when
    enableRunbookVariant=true):
        - Rename:Endpoint             — customer CMDB URL (supports {serial} placeholder)
        - Rename:AuthHeaderName       — auth header name (default X-Api-Key)
        - Rename:AuthHeaderValue      — auth header value (recommend Key Vault reference)
        - Rename:NewNameJsonPath      — response property holding the new name (default newName)
        - Rename:OnCollision          — block | warn (default block)

    All helpers live in _lib/RunbookCore.ps1 which is concatenated in front
    of this file by tools/Deploy-IntuneDeviceActions.ps1 at publish time.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)] [object]$WebhookData,
    [Parameter(Mandatory = $false)] [string]$EnvelopeJson
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

# >>> RBC-LIB-INSERTION-POINT <<<

$rawJson = if ($EnvelopeJson) { $EnvelopeJson }
           elseif ($WebhookData) { [string]$WebhookData.RequestBody }
           else { throw 'Runbook requires either -WebhookData or -EnvelopeJson.' }

$env = ConvertFrom-RbcEnvelope -Json $rawJson
foreach ($req in @('correlationId','intuneDeviceId')) {
    $value = $env[(($req.Substring(0,1).ToUpperInvariant()) + $req.Substring(1))]
    if ([string]::IsNullOrWhiteSpace([string]$value)) { throw "Envelope missing required field '$req'." }
}

$ctx = New-RbcContext `
    -ActionType     'device-rename' `
    -CorrelationId  $env.CorrelationId `
    -IntuneDeviceId $env.IntuneDeviceId `
    -EntraDeviceId  $env.EntraDeviceId `
    -DeviceName     $env.DeviceName `
    -ForceRearm     $env.ForceRearm

Write-RbcInfo "device-rename runbook starting" @{
    corr = $ctx.CorrelationId; intune = $ctx.IntuneDeviceId; device = $ctx.DeviceName
}

Disable-AzContextAutosave -Scope Process | Out-Null
$null = Connect-AzAccount -Identity -ErrorAction Stop

# ─── 0) Payload validation: serial required ─────────────────────────────────
$serial = Get-RbcExtra -Extras $env.Extras -Name 'serialNumber'
if ([string]::IsNullOrWhiteSpace($serial)) {
    Write-RbcAudit -EventName $script:RbcAudit.RenameDeniedMissingSerial -Context $ctx -Level 'Warning'
    Write-RbcTerminalStatus -Context $ctx -State 'denied:missing-serial'
    return
}
$serial = $serial.Trim()

# ─── 1) Idempotency reservation ─────────────────────────────────────────────
$reserve = Reserve-RbcLedger -Context $ctx -IntuneDeviceId $ctx.IntuneDeviceId `
                              -CorrelationId $ctx.CorrelationId -ForceRearm $ctx.ForceRearm
$state = [string]$reserve.State
$entry = $reserve.Entry

if ($state -eq 'RateLimited') {
    Write-RbcAudit -EventName $script:RbcAudit.DeniedRateLimited -Context $ctx -Level 'Warning' -Props @{
        recentActionsInWindow     = $reserve.RecentActionsInWindow
        maxActionsPerDevicePerDay = $reserve.MaxActionsPerDevicePerDay
    }
    Write-RbcTerminalStatus -Context $ctx -State 'denied:rate-limited'
    return
}
if ([string]$reserve.Rearmed -ne 'None') {
    $rearmEvent = switch ([string]$reserve.Rearmed) {
        'AfterSuccess'     { $script:RbcAudit.LedgerRearmedAfterSuccess }
        'AfterFailure'     { $script:RbcAudit.LedgerRearmedAfterFailure }
        'AfterPollTimeout' { $script:RbcAudit.LedgerRearmedAfterTimeout }
        'Forced'           { $script:RbcAudit.LedgerRearmedForced }
        default            { $script:RbcAudit.LedgerRearmedAfterSuccess }
    }
    Write-RbcAudit -EventName $rearmEvent -Context $ctx -Props @{
        actionSequence        = [int]$entry.ActionSequence
        previousTerminalState = ([string]$entry.LastTerminalState ?? '(unknown)')
        rearmReason           = [string]$reserve.Rearmed
    }
}
if ($state -eq 'Issued') {
    Write-RbcAudit -EventName $script:RbcAudit.ActionAlreadyIssued -Context $ctx -Props @{
        originalCorrelationId = [string]$entry.CorrelationId
        actionSequence        = [int]$entry.ActionSequence
    }
    Write-RbcTerminalStatus -Context $ctx -State 'denied:already-issued'
    return
}
if ($state -eq 'Reserved' -and [string]$entry.CorrelationId -ne $ctx.CorrelationId) {
    Write-RbcAudit -EventName $script:RbcAudit.ActionInProgressElsewhere -Context $ctx -Level 'Warning' -Props @{
        originalCorrelationId = [string]$entry.CorrelationId
    }
    Write-RbcTerminalStatus -Context $ctx -State 'denied:in-progress-elsewhere'
    return
}

# ─── 2) LOOKUP — customer CMDB returns the canonical new name ───────────────
$endpointTpl = Get-AutomationVariable -Name 'Rename:Endpoint' -ErrorAction SilentlyContinue
if ([string]::IsNullOrWhiteSpace($endpointTpl)) {
    # Configuration error → permanent. Don't throw to the host (which would
    # retry forever); mark the ledger failed and emit a terminal status so
    # the rate-limiter doesn't keep eating attempts.
    Set-RbcLedgerOutcome -Context $ctx -IntuneDeviceId $ctx.IntuneDeviceId -CorrelationId $ctx.CorrelationId -Outcome 'Failed' -FailureReason "config-error:missing-Rename:Endpoint" | Out-Null
    Write-RbcAudit -EventName $script:RbcAudit.RenameLookupFailedPermanent -Context $ctx -Level 'Error' -Props @{
        serial = $serial; reason = 'config-error'; missingVariable = 'Rename:Endpoint'
    }
    Write-RbcTerminalStatus -Context $ctx -State 'failed:config-error' -ManagedDeviceId $ctx.IntuneDeviceId
    return
}
$authHeaderName  = (Get-AutomationVariable -Name 'Rename:AuthHeaderName'  -ErrorAction SilentlyContinue) ?? 'X-Api-Key'
$authHeaderValue =  Get-AutomationVariable -Name 'Rename:AuthHeaderValue' -ErrorAction SilentlyContinue
$nameJsonPath    = (Get-AutomationVariable -Name 'Rename:NewNameJsonPath' -ErrorAction SilentlyContinue) ?? 'newName'
$onCollision     = ((Get-AutomationVariable -Name 'Rename:OnCollision'    -ErrorAction SilentlyContinue) ?? 'block').ToLowerInvariant()

$encoded = [System.Uri]::EscapeDataString($serial)
$lookupUri = if ($endpointTpl -match '\{serial\}') {
    $endpointTpl -replace '\{serial\}', $encoded
} else {
    if ($endpointTpl.EndsWith('/')) { "$endpointTpl$encoded" } else { "$endpointTpl/$encoded" }
}

$lookupHeaders = @{
    Accept            = 'application/json'
    'X-Correlation-Id' = $ctx.CorrelationId
}
if (-not [string]::IsNullOrWhiteSpace($authHeaderName) -and -not [string]::IsNullOrWhiteSpace($authHeaderValue)) {
    $lookupHeaders[$authHeaderName] = $authHeaderValue
}

$newName = $null
try {
    $resp = Invoke-RbcRest -Method 'GET' -Uri $lookupUri -Headers $lookupHeaders -TimeoutSec 30
    $status = [int]$resp.StatusCode
    if ($status -ge 200 -and $status -lt 300) {
        # JSON parsing / property extraction failures are PERMANENT (malformed
        # CMDB response — retrying won't fix it). Isolate them from the outer
        # catch so a bad payload doesn't get retried forever.
        try {
            $obj = $resp.Content
            if ($obj -is [System.Collections.IDictionary]) {
                foreach ($k in $obj.Keys) {
                    if ([string]::Equals([string]$k, $nameJsonPath, [System.StringComparison]::OrdinalIgnoreCase)) {
                        $newName = [string]$obj[$k]; break
                    }
                }
            } elseif ($obj) {
                $p = $obj.PSObject.Properties | Where-Object { $_.Name -ieq $nameJsonPath } | Select-Object -First 1
                if ($p) { $newName = [string]$p.Value }
            }
        } catch {
            Set-RbcLedgerOutcome -Context $ctx -IntuneDeviceId $ctx.IntuneDeviceId -CorrelationId $ctx.CorrelationId -Outcome 'Failed' -FailureReason "malformed-response:$($_.Exception.Message)" | Out-Null
            Write-RbcAudit -EventName $script:RbcAudit.RenameLookupFailedPermanent -Context $ctx -Level 'Error' -Exception $_.Exception -Props @{
                serial = $serial; httpStatus = $status; reason = 'malformed-response'
            }
            Write-RbcTerminalStatus -Context $ctx -State 'failed:lookup-permanent' -ManagedDeviceId $ctx.IntuneDeviceId
            return
        }
        if ([string]::IsNullOrWhiteSpace($newName)) {
            Set-RbcLedgerOutcome -Context $ctx -IntuneDeviceId $ctx.IntuneDeviceId -CorrelationId $ctx.CorrelationId -Outcome 'Failed' -FailureReason "missing-or-empty-property:$nameJsonPath" | Out-Null
            Write-RbcAudit -EventName $script:RbcAudit.RenameLookupFailedPermanent -Context $ctx -Level 'Error' -Props @{
                serial = $serial; httpStatus = $status; reason = "missing-or-empty-property:$nameJsonPath"
            }
            Write-RbcTerminalStatus -Context $ctx -State 'failed:lookup-permanent' -ManagedDeviceId $ctx.IntuneDeviceId
            return
        }
        $newName = $newName.Trim()
        Write-RbcAudit -EventName $script:RbcAudit.RenameLookupIssued -Context $ctx -Props @{
            serial = $serial; newName = $newName; httpStatus = $status
        }
    } elseif ($status -eq 404) {
        Set-RbcLedgerOutcome -Context $ctx -IntuneDeviceId $ctx.IntuneDeviceId -CorrelationId $ctx.CorrelationId -Outcome 'Failed' -FailureReason "lookup-not-found" | Out-Null
        Write-RbcAudit -EventName $script:RbcAudit.RenameLookupNotFound -Context $ctx -Level 'Warning' -Props @{
            serial = $serial; httpStatus = $status
        }
        Write-RbcTerminalStatus -Context $ctx -State 'failed:lookup-not-found' -ManagedDeviceId $ctx.IntuneDeviceId
        return
    } else {
        $kind = ConvertTo-RbcErrorKind -StatusCode $status
        if ($kind -eq 'Permanent') {
            Set-RbcLedgerOutcome -Context $ctx -IntuneDeviceId $ctx.IntuneDeviceId -CorrelationId $ctx.CorrelationId -Outcome 'Failed' -FailureReason "http-$status" | Out-Null
            Write-RbcAudit -EventName $script:RbcAudit.RenameLookupFailedPermanent -Context $ctx -Level 'Error' -Props @{
                serial = $serial; httpStatus = $status
            }
            Write-RbcTerminalStatus -Context $ctx -State 'failed:lookup-permanent' -ManagedDeviceId $ctx.IntuneDeviceId
            return
        }
        Write-RbcAudit -EventName $script:RbcAudit.RenameLookupTransientError -Context $ctx -Level 'Warning' -Props @{
            serial = $serial; httpStatus = $status
        }
        throw "Customer rename lookup returned transient outcome (status=$status)."
    }
} catch {
    if ($_.Exception -is [RbcGraphError]) { throw }   # let typed re-throws bubble
    if ($newName) { } else {
        # network / DNS / TLS — transient
        Write-RbcAudit -EventName $script:RbcAudit.RenameLookupTransientError -Context $ctx -Level 'Warning' -Exception $_.Exception -Props @{
            serial = $serial
        }
        throw
    }
}

# ─── 3) Collision check — Entra + Intune ────────────────────────────────────
# Entra does NOT enforce uniqueness on device displayName (unlike on-prem AD).
# Probe BOTH directories — Entra (devices?$filter=displayName eq ...) AND
# Intune (managedDevices?$filter=deviceName eq ...) — because in-flight renames
# applied via another channel (manual portal action, a parallel runner instance
# that already issued setDeviceName, an Autopilot deployment profile rename)
# may live on the Intune side before propagating up to Entra at the next MDM sync.
$escaped       = $newName.Replace("'", "''")
$entraFilter   = [System.Uri]::EscapeDataString("displayName eq '$escaped'")
$entraSelect   = [System.Uri]::EscapeDataString('id,deviceId,displayName,accountEnabled')
$entraColUri   = "https://graph.microsoft.com/v1.0/devices?`$filter=$entraFilter&`$select=$entraSelect&`$top=25"
$intuneFilter  = [System.Uri]::EscapeDataString("deviceName eq '$escaped'")
$intuneSelect  = [System.Uri]::EscapeDataString('id,azureADDeviceId,deviceName')
$intuneColUri  = "https://graph.microsoft.com/v1.0/deviceManagement/managedDevices?`$filter=$intuneFilter&`$select=$intuneSelect&`$top=25"

$collisions = @()
try {
    $entraPage = Invoke-RbcGraphApi -Method GET -Uri $entraColUri
    foreach ($d in @($entraPage.value)) {
        if ($ctx.EntraDeviceId -and $d.deviceId -and ([string]::Equals([string]$d.deviceId, [string]$ctx.EntraDeviceId, [System.StringComparison]::OrdinalIgnoreCase))) {
            continue
        }
        $collisions += [pscustomobject]@{
            source         = 'entra'
            displayName    = $d.displayName
            id             = $d.deviceId
            accountEnabled = $d.accountEnabled
        }
    }
    $intunePage = Invoke-RbcGraphApi -Method GET -Uri $intuneColUri
    foreach ($d in @($intunePage.value)) {
        if ($ctx.IntuneDeviceId -and $d.id -and ([string]::Equals([string]$d.id, [string]$ctx.IntuneDeviceId, [System.StringComparison]::OrdinalIgnoreCase))) {
            continue
        }
        $collisions += [pscustomobject]@{
            source         = 'intune'
            displayName    = $d.deviceName
            id             = $d.azureADDeviceId
            accountEnabled = $null
        }
    }
} catch [RbcGraphError] {
    $err = $_.Exception
    Write-RbcAudit -EventName $script:RbcAudit.RenameCollisionCheckFailed -Context $ctx -Level 'Warning' -Exception $err -Props @{ newName = $newName }
    if ($err.Kind -eq 'Transient') { throw }
    Set-RbcLedgerOutcome -Context $ctx -IntuneDeviceId $ctx.IntuneDeviceId -CorrelationId $ctx.CorrelationId -Outcome 'Failed' -FailureReason "collision-check-failed:$($err.Message)" | Out-Null
    Write-RbcTerminalStatus -Context $ctx -State 'failed:collision-check' -ManagedDeviceId $ctx.IntuneDeviceId
    return
}

if ($collisions.Count -gt 0) {
    $detail = (($collisions | ForEach-Object {
        $suffix = if ($_.accountEnabled -eq $false) { '(disabled)' } else { '' }
        "$($_.source):$($_.displayName)@$($_.id)$suffix"
    }) -join ',')
    Write-RbcAudit -EventName $script:RbcAudit.RenameCollisionDetected -Context $ctx -Level 'Warning' -Props @{
        newName          = $newName
        collisions       = $detail
        collisionCount   = $collisions.Count
        entraCollisions  = (@($collisions | Where-Object { $_.source -eq 'entra'  }).Count)
        intuneCollisions = (@($collisions | Where-Object { $_.source -eq 'intune' }).Count)
        policy           = $onCollision
    }
    if ($onCollision -eq 'block') {
        Set-RbcLedgerOutcome -Context $ctx -IntuneDeviceId $ctx.IntuneDeviceId -CorrelationId $ctx.CorrelationId -Outcome 'Failed' -FailureReason "name-collision:$($collisions.Count)" | Out-Null
        Write-RbcAudit -EventName $script:RbcAudit.RenameCollisionBlocked -Context $ctx -Level 'Error' -Props @{
            newName = $newName; collisions = $detail
        }
        Write-RbcTerminalStatus -Context $ctx -State 'denied:name-collision' -ManagedDeviceId $ctx.IntuneDeviceId
        return
    }
    # policy=warn → proceed
}

# ─── 4) Graph setDeviceName ─────────────────────────────────────────────────
$setUri  = "https://graph.microsoft.com/v1.0/deviceManagement/managedDevices/$([System.Uri]::EscapeDataString($ctx.IntuneDeviceId))/setDeviceName"
$setBody = @{ deviceName = $newName }
try {
    [void](Invoke-RbcGraphApi -Method POST -Uri $setUri -Body $setBody)
    [void](Set-RbcLedgerOutcome -Context $ctx -IntuneDeviceId $ctx.IntuneDeviceId -CorrelationId $ctx.CorrelationId -Outcome 'Issued')
    Write-RbcAudit -EventName $script:RbcAudit.RenameSetNameIssued -Context $ctx -Props @{
        serial  = $serial
        newName = $newName
    }
    # No probe is registered for "device-rename" in src/Proc/Program.cs; mark
    # the row terminal-issued now rather than leaving a pending row that will
    # never resolve (Intune queues the rename for the next MDM sync, and there
    # is no first-party probe for setDeviceName completion).
    Write-RbcTerminalStatus -Context $ctx -State 'issued' -ManagedDeviceId $ctx.IntuneDeviceId

    Write-Output (([ordered]@{
        correlationId = $ctx.CorrelationId
        actionType    = 'device-rename'
        state         = 'issued'
        terminal      = $false
        serialNumber  = $serial
        newName       = $newName
        source        = 'runbook'
    } | ConvertTo-Json -Compress))
}
catch [RbcGraphError] {
    $err = $_.Exception
    if ($err.Kind -eq 'Permanent') {
        [void](Set-RbcLedgerOutcome -Context $ctx -IntuneDeviceId $ctx.IntuneDeviceId -CorrelationId $ctx.CorrelationId -Outcome 'Failed' -FailureReason $err.Message)
        Write-RbcAudit -EventName $script:RbcAudit.RenameSetNameFailedPermanent -Context $ctx -Level 'Error' -Exception $err -Props @{
            serial = $serial; newName = $newName
        }
        Write-RbcTerminalStatus -Context $ctx -State 'failed:permanent' -ManagedDeviceId $ctx.IntuneDeviceId
        return
    }
    Write-RbcAudit -EventName $script:RbcAudit.RenameSetNameTransientError -Context $ctx -Level 'Warning' -Exception $err -Props @{
        serial = $serial; newName = $newName
    }
    throw
}
