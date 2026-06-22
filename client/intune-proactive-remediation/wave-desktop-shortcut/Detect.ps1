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
# Backward-compatible env var resolution:
# - New namespace: INTUNE_ACTIONS_*
# - Legacy namespace: INTUNE_WIPE_*
$ApiBaseUrl    = $env:INTUNE_ACTIONS_API_URL
if (-not $ApiBaseUrl) { $ApiBaseUrl = $env:INTUNE_WIPE_API_URL }
if (-not $ApiBaseUrl) { $ApiBaseUrl = 'https://devact-web-dev.azurewebsites.net' }
# Legacy INTUNE_WIPE_API_URL usually points to /api/actions. For schedule polling
# we need the app base URL only.
$ApiBaseUrl = $ApiBaseUrl.TrimEnd('/')
if ($ApiBaseUrl -match '/api/actions$') {
    $ApiBaseUrl = $ApiBaseUrl.Substring(0, $ApiBaseUrl.Length - '/api/actions'.Length)
}
$SchedulePath  = '/api/schedule/me?actionType=wipe'
$FunctionKey = $env:INTUNE_ACTIONS_FUNCTION_KEY
if (-not $FunctionKey) { $FunctionKey = $env:INTUNE_WIPE_FUNCTION_KEY }
$CertThumbprint = $env:INTUNE_ACTIONS_CERT_THUMBPRINT
if (-not $CertThumbprint) { $CertThumbprint = $env:INTUNE_WIPE_CERT_THUMBPRINT }
$CertSubjectLike = $env:INTUNE_ACTIONS_CERT_SUBJECT_LIKE
if (-not $CertSubjectLike) { $CertSubjectLike = $env:INTUNE_WIPE_CERT_SUBJECT_LIKE }
$CertIssuerLike  = $env:INTUNE_ACTIONS_CERT_ISSUER_LIKE
if (-not $CertIssuerLike) { $CertIssuerLike = $env:INTUNE_WIPE_CERT_ISSUER_LIKE }
$CertExcludeIssuerLike = $env:INTUNE_ACTIONS_CERT_EXCLUDE_ISSUER_LIKE
if (-not $CertExcludeIssuerLike) { $CertExcludeIssuerLike = $env:INTUNE_WIPE_CERT_EXCLUDE_ISSUER_LIKE }
if (-not $CertExcludeIssuerLike) { $CertExcludeIssuerLike = '*Intune*MDM*' }
$CacheDir      = Join-Path $env:ProgramData 'IntuneWipeClient'
$CacheFile     = Join-Path $CacheDir 'wave-schedule.json'
$CacheTtlHours = 4
$ShortcutFlag  = Join-Path $CacheDir 'shortcut-created.flag'
$LogDir        = Join-Path $CacheDir 'Logs'
$LogFile       = Join-Path $LogDir 'wave-desktop-shortcut-detect.log'

if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Path $LogDir -Force | Out-Null }

function Write-Log {
    param([Parameter(Mandatory = $true)][string]$Message)
    $line = "[{0}] {1}" -f (Get-Date -Format o), $Message
    $line | Out-File -FilePath $LogFile -Append -Encoding utf8
    Write-Host $line
}

Write-Log "Detect started. ApiBaseUrl='$ApiBaseUrl'"
if ($FunctionKey) {
    Write-Log "Using function key from environment (length=$($FunctionKey.Trim().Length))."
} else {
    Write-Log "Function key missing in environment (INTUNE_ACTIONS_FUNCTION_KEY / INTUNE_WIPE_FUNCTION_KEY). API call may return 401."
}

# --- If shortcut was already created, we're compliant (no need to remediate again)
if (Test-Path $ShortcutFlag) {
    Write-Log "Shortcut already created (flag exists). Compliant."
    exit 0
}

# --- Find client certificate -------------------------------------------------
function Get-ClientCertificate {
    param(
        [string]$Thumbprint,
        [string]$SubjectLike,
        [string]$IssuerLike,
        [string]$ExcludeIssuerLike
    )

    $thumb = if ($Thumbprint) { $Thumbprint.Trim().ToUpper().Replace(' ', '') } else { $null }
    $issuerPatterns = @()
    $excludeIssuerPatterns = @()
    if ($IssuerLike) {
        $issuerPatterns = @($IssuerLike -split ';' |
                            ForEach-Object { $_.Trim() } |
                            Where-Object { $_ })
    }
    if ($ExcludeIssuerLike) {
        $excludeIssuerPatterns = @($ExcludeIssuerLike -split ';' |
                                   ForEach-Object { $_.Trim() } |
                                   Where-Object { $_ })
    }

    $candidates = Get-ChildItem Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
        Where-Object {
            $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) -and $_.NotBefore -le (Get-Date)
        } |
        Where-Object {
            $ekus = $_.EnhancedKeyUsageList
            (-not $ekus) -or ($ekus | Where-Object { $_.ObjectId -eq '1.3.6.1.5.5.7.3.2' })
        }

    if ($issuerPatterns.Count -gt 0) {
        $candidates = $candidates | Where-Object {
            $issuer = $_.Issuer
            foreach ($pattern in $issuerPatterns) {
                if ($issuer -like $pattern) { return $true }
            }
            return $false
        }
    }
    if ($excludeIssuerPatterns.Count -gt 0) {
        $candidates = $candidates | Where-Object {
            $issuer = $_.Issuer
            foreach ($pattern in $excludeIssuerPatterns) {
                if ($issuer -like $pattern) { return $false }
            }
            return $true
        }
    }

    if ($thumb) {
        return $candidates | Where-Object { $_.Thumbprint -eq $thumb } | Select-Object -First 1
    }
    if ($SubjectLike) {
        return $candidates | Where-Object { $_.Subject -like $SubjectLike } |
            Sort-Object NotAfter -Descending |
            Select-Object -First 1
    }
    return $candidates | Sort-Object NotAfter -Descending | Select-Object -First 1
}

$cert = Get-ClientCertificate `
    -Thumbprint $CertThumbprint `
    -SubjectLike $CertSubjectLike `
    -IssuerLike $CertIssuerLike `
    -ExcludeIssuerLike $CertExcludeIssuerLike

if (-not $cert) {
    Write-Log "No valid client certificate found (thumbprint='$CertThumbprint', subjectLike='$CertSubjectLike', issuerLike='$CertIssuerLike', excludeIssuerLike='$CertExcludeIssuerLike'). Cannot poll API. Compliant (skip)."
    exit 0
}
Write-Log "Using client certificate thumbprint='$($cert.Thumbprint)' subject='$($cert.Subject)' issuer='$($cert.Issuer)' notAfter='$($cert.NotAfter.ToString('o'))'."

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
    Write-Log "Using cached schedule manifest."
} else {
    try {
    $uri = "$ApiBaseUrl$SchedulePath"
    $headers = @{}
    if ($FunctionKey) { $headers['x-functions-key'] = $FunctionKey }
    $response = Invoke-RestMethod -Uri $uri -Certificate $cert -Headers $headers -Method GET -TimeoutSec 30
    $manifest = $response  # $null on 204 No Content
        if ($manifest) {
            $manifest | ConvertTo-Json -Depth 10 | Set-Content -Path $CacheFile -Encoding UTF8
            Write-Log "Fetched schedule manifest from API and refreshed cache."
        } else {
            # 204 - no wave; clear stale cache
            if (Test-Path $CacheFile) { Remove-Item $CacheFile -Force }
            Write-Log "API returned 204/no manifest. Cleared cache."
        }
    } catch {
        Write-Log "API call failed: $($_.Exception.Message). Using stale cache or skipping."
        if (Test-Path $CacheFile) {
            $raw = Get-Content $CacheFile -Raw
            $manifest = if ($raw) { $raw | ConvertFrom-Json } else { $null }
            Write-Log "Loaded stale cache fallback."
        } else {
            Write-Log "No cache available. Compliant (skip)."
            exit 0
        }
    }
}

# --- Evaluate wave activation ------------------------------------------------
# The server returns a single DeviceScheduleSnapshot JSON object with:
#   waveId, name, actionType, scheduledAtUtc, status, isImmediate, description, generatedAtUtc
# If no wave applies, the server returns 204 (empty response / $null manifest).

if (-not $manifest) {
    Write-Log "No scheduled wave for this device (204). Compliant."
    exit 0
}

if ($manifest.isImmediate -eq $true) {
    Write-Log "Active wave '$($manifest.name)' (scheduledAtUtc: $($manifest.scheduledAtUtc), waveId: $($manifest.waveId)). Non-compliant - remediation needed."
    exit 1
} else {
    Write-Log "Wave '$($manifest.name)' scheduled for $($manifest.scheduledAtUtc) - not yet active. Compliant."
    exit 0
}
