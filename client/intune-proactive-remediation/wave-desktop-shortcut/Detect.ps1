<#
.SYNOPSIS
    Proactive Remediation - Detect script.
    Polls the IntuneDeviceActions API /api/schedule/me?actionType=wipe endpoint
    to determine whether this device belongs to an active wipe wave (isImmediate=true).

.DESCRIPTION
    Runs as SYSTEM via Intune Proactive Remediation on a periodic schedule.
    - Calls /api/schedule/me?actionType=wipe with the device's client certificate.
    - The server returns a DeviceScheduleSnapshot (200) or 204 (no wave).
    - If isImmediate is true (wave has fired), exits 1 (non-compliant) so
      Intune triggers the Remediate script that creates the desktop shortcut.
    - If 204 or isImmediate is false, exits 0 (compliant).

    The schedule manifest caches locally to avoid repeated calls on every run.

.NOTES
    Context: SYSTEM
    Requires: client certificate in LocalMachine\My matching the API thumbprint.
#>

$ErrorActionPreference = 'Stop'

# --- Configuration (override via env vars or hardcode for your tenant) -------
$ApiBaseUrl    = $env:INTUNE_ACTIONS_API_URL
if (-not $ApiBaseUrl) { $ApiBaseUrl = 'https://devact-web-dev.azurewebsites.net' }
$SchedulePath  = '/api/schedule/me?actionType=wipe'
$CacheDir      = Join-Path $env:ProgramData 'IntuneWipeClient'
$CacheFile     = Join-Path $CacheDir 'wave-schedule.json'
$CacheTtlHours = 4
$ShortcutFlag  = Join-Path $CacheDir 'shortcut-created.flag'

# --- If shortcut was already created, we're compliant (no need to remediate again)
if (Test-Path $ShortcutFlag) {
    Write-Host "Shortcut already created (flag exists). Compliant."
    exit 0
}

# --- Find client certificate -------------------------------------------------
$CertThumbprint = $env:INTUNE_ACTIONS_CERT_THUMBPRINT
if (-not $CertThumbprint) {
    # Auto-detect: find cert issued by our known CA
    $cert = Get-ChildItem Cert:\LocalMachine\My |
        Where-Object { $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
} else {
    $cert = Get-ChildItem Cert:\LocalMachine\My\$CertThumbprint -ErrorAction SilentlyContinue
}

if (-not $cert) {
    Write-Host "No valid client certificate found. Cannot poll API. Compliant (skip)."
    exit 0
}

# --- Check cache freshness ---------------------------------------------------
if (-not (Test-Path $CacheDir)) { New-Item -ItemType Directory -Path $CacheDir -Force | Out-Null }

$useCache = $false
if (Test-Path $CacheFile) {
    $cacheAge = (Get-Date) - (Get-Item $CacheFile).LastWriteTime
    if ($cacheAge.TotalHours -lt $CacheTtlHours) {
        $useCache = $true
    }
}

# --- Poll API or read cache --------------------------------------------------
if ($useCache) {
    $raw = Get-Content $CacheFile -Raw
    $manifest = if ($raw) { $raw | ConvertFrom-Json } else { $null }
} else {
    try {
        $uri = "$ApiBaseUrl$SchedulePath"
        $response = Invoke-RestMethod -Uri $uri -Certificate $cert -Method GET -TimeoutSec 30
        $manifest = $response  # $null on 204 No Content
        if ($manifest) {
            $manifest | ConvertTo-Json -Depth 10 | Set-Content -Path $CacheFile -Encoding UTF8
        } else {
            # 204 - no wave; clear stale cache
            if (Test-Path $CacheFile) { Remove-Item $CacheFile -Force }
        }
    } catch {
        Write-Host "API call failed: $($_.Exception.Message). Using stale cache or skipping."
        if (Test-Path $CacheFile) {
            $raw = Get-Content $CacheFile -Raw
            $manifest = if ($raw) { $raw | ConvertFrom-Json } else { $null }
        } else {
            Write-Host "No cache available. Compliant (skip)."
            exit 0
        }
    }
}

# --- Evaluate wave activation ------------------------------------------------
# The server returns a single DeviceScheduleSnapshot JSON object with:
#   waveId, name, actionType, scheduledAtUtc, status, isImmediate, description, generatedAtUtc
# If no wave applies, the server returns 204 (empty response / $null manifest).

if (-not $manifest) {
    Write-Host "No scheduled wave for this device (204). Compliant."
    exit 0
}

if ($manifest.isImmediate -eq $true) {
    Write-Host "Active wave '$($manifest.name)' (scheduledAtUtc: $($manifest.scheduledAtUtc), waveId: $($manifest.waveId)). Non-compliant - remediation needed."
    exit 1
} else {
    Write-Host "Wave '$($manifest.name)' scheduled for $($manifest.scheduledAtUtc) - not yet active. Compliant."
    exit 0
}
