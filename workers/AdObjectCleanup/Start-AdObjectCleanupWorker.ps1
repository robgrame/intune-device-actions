#requires -Version 5.1
<#
.SYNOPSIS
    On-prem hybrid worker that deletes stale Active Directory computer objects
    on behalf of the Intune rename capability.

.DESCRIPTION
    The rename capability (running in Azure) cannot reach on-premises Active
    Directory. Before it issues an Intune `setDeviceName`, it publishes an
    `ad-object-cleanup` message to a dedicated Azure Service Bus queue. This
    worker — deployed on a domain-joined server that CAN reach a Domain
    Controller and has RSAT ActiveDirectory installed — pulls those messages
    (peek-lock via the Service Bus REST API + a Listen SAS token, so no Azure
    SDK is required) and runs `Remove-ADComputer` for every computer object
    whose name matches the target rename name.

    Contract (owned by the capability, see
    src/Capabilities.Rename/Models/AdCleanupMessage.cs):
        {
          "schemaVersion": "1",
          "correlationId": "....",
          "targetName": "PC-NEW",         # delete AD computer(s) with this name
          "sourceDeviceName": "PC-OLD",
          "entraDeviceId": "....",
          "intuneDeviceId": "....",
          "serialNumber": "....",
          "requestedUtc": "2024-..Z"
        }
    Application property `messageType` == "ad-object-cleanup" is asserted.

    Guardrails (fail-safe by default):
      * -WhatIf / DryRun mode does everything EXCEPT the actual delete.
      * -SearchBase constrains deletions to a specific OU subtree — objects
        outside it are never touched.
      * -ExclusionNames protects named objects (e.g. servers) from deletion.
      * -MaxDeletePerMessage caps how many objects a single message may delete
        (a message that would delete more is dead-lettered, not executed).

    Message handling:
      * success            -> Complete (message removed from the queue).
      * transient failure  -> Abandon (redelivered; e.g. DC unreachable).
      * invalid/poison msg -> logged + Completed so it does not poison-loop
                              (bad schema, blank target name, cap exceeded).

.PARAMETER Namespace
    Service Bus namespace host, e.g. 'idactions-sb-dev' (without
    '.servicebus.windows.net') or the full FQDN.

.PARAMETER Queue
    Queue name (default 'ad-object-cleanup').

.PARAMETER SasKeyName
    Name of the SAS authorization rule with Listen (default 'worker-listen').

.PARAMETER SasKey
    The SAS primary/secondary key. Prefer -SasKeyFile or the
    IDACTIONS_AD_CLEANUP_SASKEY environment variable over passing on the CLI.

.PARAMETER SasKeyFile
    Path to a file whose single line is the SAS key (protect with NTFS ACL).

.PARAMETER SearchBase
    Distinguished name of the OU subtree to scope deletions to. STRONGLY
    recommended. When omitted the whole domain is searched (still bounded by
    the exclusion list + cap).

.PARAMETER ExclusionNames
    Computer names that must never be deleted (case-insensitive).

.PARAMETER MaxDeletePerMessage
    Max AD objects a single message may delete (default 5).

.PARAMETER WhatIf
    Dry-run: resolve + log the objects that WOULD be deleted, but do not delete.

.PARAMETER RunOnce
    Process a single receive cycle then exit (used by tests / scheduled task
    single-shot). Without it the worker loops until Ctrl+C / stop.

.PARAMETER MaxIdleLoops
    With -RunOnce omitted, exit after this many consecutive empty receives
    (0 = never exit on idle; default 0).

.PARAMETER LogPath
    Optional path to append a text log to (in addition to stdout).

.EXAMPLE
    .\Start-AdObjectCleanupWorker.ps1 -Namespace idactions-sb-dev `
        -SearchBase 'OU=Workstations,DC=corp,DC=contoso,DC=com' `
        -SasKeyFile C:\ProgramData\idactions\sb.key -WhatIf

.NOTES
    Requires the ActiveDirectory module (RSAT). Author: rename capability.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string] $Namespace,

    [string] $Queue = 'ad-object-cleanup',

    [string] $SasKeyName = 'worker-listen',

    [string] $SasKey,

    [string] $SasKeyFile,

    [string] $SearchBase,

    [string[]] $ExclusionNames = @(),

    [int] $MaxDeletePerMessage = 5,

    [switch] $RunOnce,

    [int] $MaxIdleLoops = 0,

    [int] $ReceiveTimeoutSeconds = 30,

    [string] $LogPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Logging
# ---------------------------------------------------------------------------
function Write-WorkerLog {
    param(
        [ValidateSet('INFO', 'WARN', 'ERROR', 'DEBUG')] [string] $Level = 'INFO',
        [Parameter(Mandatory = $true)] [string] $Message,
        [hashtable] $Data
    )
    $ts = (Get-Date).ToUniversalTime().ToString('o')
    $line = "[$ts] [$Level] $Message"
    if ($Data) {
        $kv = ($Data.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ' '
        $line = "$line $kv"
    }
    switch ($Level) {
        'ERROR' { Write-Host $line -ForegroundColor Red }
        'WARN'  { Write-Host $line -ForegroundColor Yellow }
        'DEBUG' { Write-Verbose $line }
        default { Write-Host $line }
    }
    if ($LogPath) {
        try { Add-Content -Path $LogPath -Value $line -Encoding UTF8 } catch { }
    }
}

# ---------------------------------------------------------------------------
# Service Bus REST helpers (peek-lock via SAS)
# ---------------------------------------------------------------------------
function Resolve-SbHost {
    param([string] $Ns)
    if ($Ns -like '*.*') { return $Ns }
    return "$Ns.servicebus.windows.net"
}

function New-SbSasToken {
    param(
        [Parameter(Mandatory = $true)] [string] $ResourceUri,
        [Parameter(Mandatory = $true)] [string] $KeyName,
        [Parameter(Mandatory = $true)] [string] $Key,
        [int] $TtlSeconds = 3600
    )
    $epoch = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() + $TtlSeconds
    $encodedUri = [Uri]::EscapeDataString($ResourceUri.ToLowerInvariant())
    $stringToSign = "$encodedUri`n$epoch"
    $hmac = [System.Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($Key))
    try {
        $sig = [Convert]::ToBase64String($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($stringToSign)))
    } finally {
        $hmac.Dispose()
    }
    $encodedSig = [Uri]::EscapeDataString($sig)
    return "SharedAccessSignature sr=$encodedUri&sig=$encodedSig&se=$epoch&skn=$KeyName"
}

function Receive-SbPeekLockMessage {
    <#
      POST .../messages/head?timeout=N  (peek-lock).
      Returns $null on empty (204), otherwise a PSCustomObject:
        { Body, BrokerProperties, ApplicationProperties, LockLocation }
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $SbHost,
        [Parameter(Mandatory = $true)] [string] $QueueName,
        [Parameter(Mandatory = $true)] [string] $SasToken,
        [int] $TimeoutSeconds = 30
    )
    $uri = "https://$SbHost/$QueueName/messages/head?timeout=$TimeoutSeconds"
    $headers = @{ Authorization = $SasToken }
    try {
        $resp = Invoke-WebRequest -Method Post -Uri $uri -Headers $headers `
            -ContentType 'application/json' -UseBasicParsing -TimeoutSec ($TimeoutSeconds + 20)
    } catch {
        $status = $null
        if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
        throw [PSCustomObject]@{ Transient = $true; Status = $status; Inner = $_ }
    }

    if ($resp.StatusCode -eq 204) { return $null }  # queue empty

    $lockLocation = $resp.Headers['Location']
    if ($lockLocation -is [array]) { $lockLocation = $lockLocation[0] }

    $brokerRaw = $resp.Headers['BrokerProperties']
    if ($brokerRaw -is [array]) { $brokerRaw = $brokerRaw[0] }
    $broker = $null
    if ($brokerRaw) { $broker = $brokerRaw | ConvertFrom-Json }

    $appProps = @{}
    foreach ($h in $resp.Headers.Keys) {
        if ($h -in @('Location', 'BrokerProperties', 'Content-Type', 'Content-Length', 'Date', 'Server', 'Transfer-Encoding', 'Strict-Transport-Security')) { continue }
        $appProps[$h] = ($resp.Headers[$h] | Select-Object -First 1)
    }

    return [PSCustomObject]@{
        Body                  = $resp.Content
        BrokerProperties      = $broker
        ApplicationProperties = $appProps
        LockLocation          = $lockLocation
    }
}

function Complete-SbMessage {
    param([Parameter(Mandatory = $true)] [string] $LockLocation, [Parameter(Mandatory = $true)] [string] $SasToken)
    Invoke-WebRequest -Method Delete -Uri $LockLocation -Headers @{ Authorization = $SasToken } -UseBasicParsing -TimeoutSec 30 | Out-Null
}

function Abandon-SbMessage {
    param([Parameter(Mandatory = $true)] [string] $LockLocation, [Parameter(Mandatory = $true)] [string] $SasToken)
    # PUT on the lock URI releases the lock -> message is redelivered.
    Invoke-WebRequest -Method Put -Uri $LockLocation -Headers @{ Authorization = $SasToken } -UseBasicParsing -TimeoutSec 30 | Out-Null
}

# ---------------------------------------------------------------------------
# AD deletion (guardrailed)
# ---------------------------------------------------------------------------
function Invoke-AdComputerCleanup {
    <#
      Resolves computer objects named == TargetName (scoped by SearchBase),
      applies the exclusion list + cap, and deletes them unless -WhatIf.
      Returns a result object; throws only on a TRANSIENT AD failure.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $TargetName,
        [string] $SearchBase,
        [string[]] $ExclusionNames = @(),
        [int] $MaxDelete = 5,
        [switch] $DryRun
    )

    $adParams = @{ Filter = "Name -eq '$TargetName'"; ErrorAction = 'Stop' }
    if ($SearchBase) { $adParams['SearchBase'] = $SearchBase }

    try {
        $found = @(Get-ADComputer @adParams -Properties DistinguishedName, Name)
    } catch {
        # AD unreachable / RPC errors are transient — let the caller abandon.
        throw [PSCustomObject]@{ Transient = $true; Inner = $_ }
    }

    $exclusion = @($ExclusionNames | Where-Object { $_ } | ForEach-Object { $_.ToLowerInvariant() })
    $toDelete = @($found | Where-Object { $exclusion -notcontains $_.Name.ToLowerInvariant() })
    $excluded = @($found | Where-Object { $exclusion -contains $_.Name.ToLowerInvariant() })

    $result = [PSCustomObject]@{
        Found      = $found.Count
        Excluded   = $excluded.Count
        Deleted    = 0
        Skipped    = 0
        DryRun     = [bool]$DryRun
        CapExceeded = $false
        Objects    = @()
    }

    if ($toDelete.Count -eq 0) { return $result }

    if ($toDelete.Count -gt $MaxDelete) {
        $result.CapExceeded = $true
        return $result
    }

    foreach ($obj in $toDelete) {
        $dn = $obj.DistinguishedName
        if ($DryRun) {
            $result.Objects += "WOULDDELETE:$dn"
            $result.Skipped++
            continue
        }
        try {
            Remove-ADComputer -Identity $dn -Confirm:$false -ErrorAction Stop
            $result.Objects += "DELETED:$dn"
            $result.Deleted++
        } catch {
            throw [PSCustomObject]@{ Transient = $true; Inner = $_; PartialResult = $result }
        }
    }
    return $result
}

# ---------------------------------------------------------------------------
# Message processing
# ---------------------------------------------------------------------------
function Test-IsTransient {
    param($ErrorObject)
    if ($null -eq $ErrorObject) { return $false }
    if ($ErrorObject.PSObject.Properties.Name -contains 'Transient') { return [bool]$ErrorObject.Transient }
    return $false
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------
if (-not (Get-Module -ListAvailable -Name ActiveDirectory)) {
    Write-WorkerLog -Level ERROR -Message 'ActiveDirectory module not available. Install RSAT: Active Directory tools.'
    exit 2
}
Import-Module ActiveDirectory -ErrorAction Stop

# Resolve SAS key (file > param > env)
if (-not $SasKey) {
    if ($SasKeyFile) {
        if (-not (Test-Path $SasKeyFile)) { Write-WorkerLog -Level ERROR -Message "SasKeyFile not found: $SasKeyFile"; exit 2 }
        $SasKey = (Get-Content -Path $SasKeyFile -Raw).Trim()
    } elseif ($env:IDACTIONS_AD_CLEANUP_SASKEY) {
        $SasKey = $env:IDACTIONS_AD_CLEANUP_SASKEY
    }
}
if (-not $SasKey) { Write-WorkerLog -Level ERROR -Message 'No SAS key provided (-SasKey / -SasKeyFile / IDACTIONS_AD_CLEANUP_SASKEY).'; exit 2 }

$sbHost = Resolve-SbHost -Ns $Namespace
$resourceUri = "https://$sbHost/$Queue"

Write-WorkerLog -Level INFO -Message 'AD object cleanup worker starting' -Data @{
    host = $sbHost; queue = $Queue; searchBase = $(if ($SearchBase) { $SearchBase } else { '(domain)' })
    maxDelete = $MaxDeletePerMessage; whatIf = [bool]$WhatIfPreference; runOnce = [bool]$RunOnce
}

$idleLoops = 0
$stop = $false
while (-not $stop) {
    # Fresh SAS per loop is cheap and avoids expiry on long-running workers.
    $sas = New-SbSasToken -ResourceUri $resourceUri -KeyName $SasKeyName -Key $SasKey -TtlSeconds 3600

    $msg = $null
    try {
        $msg = Receive-SbPeekLockMessage -SbHost $sbHost -QueueName $Queue -SasToken $sas -TimeoutSeconds $ReceiveTimeoutSeconds
    } catch {
        $inner = $_.TargetObject
        if (Test-IsTransient $inner) {
            Write-WorkerLog -Level WARN -Message 'Transient receive error; backing off 5s' -Data @{ status = $inner.Status }
        } else {
            Write-WorkerLog -Level ERROR -Message "Receive failed: $($_.Exception.Message)"
        }
        Start-Sleep -Seconds 5
        if ($RunOnce) { break }
        continue
    }

    if ($null -eq $msg) {
        $idleLoops++
        Write-WorkerLog -Level DEBUG -Message 'Queue empty'
        if ($RunOnce) { break }
        if ($MaxIdleLoops -gt 0 -and $idleLoops -ge $MaxIdleLoops) { Write-WorkerLog -Level INFO -Message 'Idle limit reached; exiting'; break }
        continue
    }
    $idleLoops = 0

    $corr = $null
    try {
        $payload = $msg.Body | ConvertFrom-Json -ErrorAction Stop
        $corr = $payload.correlationId
        $msgType = $null
        if ($msg.ApplicationProperties.ContainsKey('messageType')) { $msgType = $msg.ApplicationProperties['messageType'] }
        if (-not $msgType -and $msg.BrokerProperties) { $msgType = $msg.BrokerProperties.Label }

        # ---- Contract validation (poison -> Complete/drop, do not loop) ----
        if ($msgType -and $msgType -ne 'ad-object-cleanup') {
            Write-WorkerLog -Level WARN -Message 'Dropping message with unexpected messageType' -Data @{ messageType = $msgType; correlationId = $corr }
            Complete-SbMessage -LockLocation $msg.LockLocation -SasToken $sas
            if ($RunOnce) { break } else { continue }
        }
        if (-not $payload.targetName -or [string]::IsNullOrWhiteSpace($payload.targetName)) {
            Write-WorkerLog -Level WARN -Message 'Dropping message with blank targetName' -Data @{ correlationId = $corr }
            Complete-SbMessage -LockLocation $msg.LockLocation -SasToken $sas
            if ($RunOnce) { break } else { continue }
        }

        $target = $payload.targetName.Trim()
        Write-WorkerLog -Level INFO -Message 'Processing cleanup message' -Data @{ correlationId = $corr; targetName = $target; source = $payload.sourceDeviceName }

        $res = Invoke-AdComputerCleanup -TargetName $target -SearchBase $SearchBase `
            -ExclusionNames $ExclusionNames -MaxDelete $MaxDeletePerMessage -DryRun:($WhatIfPreference)

        if ($res.CapExceeded) {
            # Refuse + drop (do NOT delete a suspiciously large set) — a human
            # must investigate. Completing avoids a poison loop.
            Write-WorkerLog -Level ERROR -Message 'Delete cap exceeded; dropping message WITHOUT deleting' -Data @{ correlationId = $corr; targetName = $target; found = $res.Found; maxDelete = $MaxDeletePerMessage }
            Complete-SbMessage -LockLocation $msg.LockLocation -SasToken $sas
            if ($RunOnce) { break } else { continue }
        }

        Write-WorkerLog -Level INFO -Message 'Cleanup complete' -Data @{
            correlationId = $corr; targetName = $target; found = $res.Found
            deleted = $res.Deleted; excluded = $res.Excluded; skipped = $res.Skipped; dryRun = $res.DryRun
            objects = ($res.Objects -join ';')
        }
        Complete-SbMessage -LockLocation $msg.LockLocation -SasToken $sas
    } catch {
        $inner = $_.TargetObject
        if (Test-IsTransient $inner) {
            Write-WorkerLog -Level WARN -Message 'Transient AD failure; abandoning message for retry' -Data @{ correlationId = $corr; error = $inner.Inner.Exception.Message }
            try { Abandon-SbMessage -LockLocation $msg.LockLocation -SasToken $sas } catch { Write-WorkerLog -Level ERROR -Message "Abandon failed: $($_.Exception.Message)" }
        } else {
            Write-WorkerLog -Level ERROR -Message "Unhandled processing error; abandoning: $($_.Exception.Message)" -Data @{ correlationId = $corr }
            try { Abandon-SbMessage -LockLocation $msg.LockLocation -SasToken $sas } catch { }
        }
    }

    if ($RunOnce) { $stop = $true }
}

Write-WorkerLog -Level INFO -Message 'AD object cleanup worker stopped'
