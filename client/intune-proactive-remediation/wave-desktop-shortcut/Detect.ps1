<#
.SYNOPSIS
    Proactive Remediation - Detect script.
    Polls the IntuneDeviceActions API /api/schedule/me endpoint to determine
    whether this device belongs to an active wave (startDate <= now).

.DESCRIPTION
    Runs as SYSTEM via Intune Proactive Remediation on a periodic schedule.
    - Calls /api/schedule/me with the device's client certificate.
    - If the response contains an active wave (startDate in the past), exits 1
      (non-compliant) so Intune triggers the Remediate script.
    - If no active wave or startDate is in the future, exits 0 (compliant).

    The schedule manifest caches locally to avoid repeated calls on every run.

.NOTES
    Context: SYSTEM
    Requires: client certificate in LocalMachine\My matching the API thumbprint.
#>

$ErrorActionPreference = 'Stop'

# --- Configuration (override via env vars or hardcode for your tenant) -------
$ApiBaseUrl    = $env:INTUNE_ACTIONS_API_URL
if (-not $ApiBaseUrl) { $ApiBaseUrl = 'https://devact-web-dev.azurewebsites.net' }
$SchedulePath  = '/api/schedule/me'
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
    $manifest = Get-Content $CacheFile -Raw | ConvertFrom-Json
} else {
    try {
        $uri = "$ApiBaseUrl$SchedulePath"
        $response = Invoke-RestMethod -Uri $uri -Certificate $cert -Method GET -TimeoutSec 30
        $manifest = $response
        $manifest | ConvertTo-Json -Depth 10 | Set-Content -Path $CacheFile -Encoding UTF8
    } catch {
        Write-Host "API call failed: $($_.Exception.Message). Using stale cache or skipping."
        if (Test-Path $CacheFile) {
            $manifest = Get-Content $CacheFile -Raw | ConvertFrom-Json
        } else {
            Write-Host "No cache available. Compliant (skip)."
            exit 0
        }
    }
}

# --- Evaluate wave activation ------------------------------------------------
$now = Get-Date

# The manifest contains scheduled actions; look for a wipe wave with startDate <= now
$activeWave = $null
if ($manifest.waves) {
    $activeWave = $manifest.waves |
        Where-Object { $_.startDate -and [DateTime]::Parse($_.startDate) -le $now } |
        Sort-Object { [DateTime]::Parse($_.startDate) } -Descending |
        Select-Object -First 1
} elseif ($manifest.startDate) {
    # Simple format: single schedule entry
    if ([DateTime]::Parse($manifest.startDate) -le $now) {
        $activeWave = $manifest
    }
}

if ($activeWave) {
    Write-Host "Active wave found (startDate: $($activeWave.startDate)). Non-compliant - remediation needed."
    exit 1
} else {
    Write-Host "No active wave for this device. Compliant."
    exit 0
}
