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

# --- Configuration (resolved from env vars or config.json) -------------------
# Capability-neutral env var resolution:
# - New namespace:    INTUNE_ACTIONS_*
# - Legacy namespace: INTUNE_WIPE_*  (backward compatibility)
# No tenant URL is hardcoded: the base URL comes from the same env vars the
# intune-remediation-endpoint remediation sets, or from the installed client
# config.json. If none of those provide a URL the script logs and skips
# (compliant) instead of calling a baked-in endpoint.
$ApiBaseUrl    = $env:INTUNE_ACTIONS_API_URL
if (-not $ApiBaseUrl) { $ApiBaseUrl = $env:INTUNE_WIPE_API_URL }
if (-not $ApiBaseUrl) {
    try {
        $programFiles64 = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
        $configPath = Join-Path $programFiles64 'IntuneWipeClient\config.json'
        if (Test-Path $configPath) {
            $clientConfig = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
            if ($clientConfig.ApiUrl) { $ApiBaseUrl = $clientConfig.ApiUrl }
        }
    } catch { }
}
# INTUNE_*_API_URL / config.json ApiUrl usually point to /api/actions. For
# schedule polling we need the app base URL only.
if ($ApiBaseUrl) {
    $ApiBaseUrl = $ApiBaseUrl.TrimEnd('/')
    if ($ApiBaseUrl -match '/api/actions$') {
        $ApiBaseUrl = $ApiBaseUrl.Substring(0, $ApiBaseUrl.Length - '/api/actions'.Length)
    }
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

function Format-SafeValue {
    param([string]$Value, [switch]$Secret)
    if (-not $Value) { return '(empty)' }
    if ($Secret) { return "len=$($Value.Length)" }
    return $Value
}

function Get-SecretFingerprint {
    param([string]$Value)
    if (-not $Value) { return '(empty)' }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
    $hash = [System.Security.Cryptography.SHA256]::Create().ComputeHash($bytes)
    return ('sha256={0}' -f ([BitConverter]::ToString($hash).Replace('-', '').Substring(0, 16)))
}

function Get-ShortcutPaths {
    return @(
        (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'Intune Device Actions.lnk'),
        (Join-Path (Join-Path ([Environment]::GetFolderPath('CommonStartMenu')) 'Programs') 'Intune Device Actions.lnk')
    )
}

Write-Log "Detect started. ApiBaseUrl='$ApiBaseUrl'"
Write-Log ("Config snapshot: URL='{0}', FunctionKey={1} {2}, Thumbprint={3}, SubjectLike={4}, IssuerLike={5}, ExcludeIssuerLike={6}" -f `
    $ApiBaseUrl,
    (Format-SafeValue $FunctionKey -Secret),
    (Get-SecretFingerprint $FunctionKey),
    (Format-SafeValue $CertThumbprint),
    (Format-SafeValue $CertSubjectLike),
    (Format-SafeValue $CertIssuerLike),
    (Format-SafeValue $CertExcludeIssuerLike))
if ($FunctionKey) {
    Write-Log "Using function key from environment (length=$($FunctionKey.Trim().Length))."
} else {
    Write-Log "Function key missing in environment (INTUNE_ACTIONS_FUNCTION_KEY / INTUNE_WIPE_FUNCTION_KEY). API call may return 401."
}

if (-not $ApiBaseUrl) {
    Write-Log "No API base URL resolved (INTUNE_ACTIONS_API_URL / INTUNE_WIPE_API_URL / config.json ApiUrl all empty). Cannot poll schedule endpoint. Compliant (skip)."
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
    Write-Log "Calling schedule function: GET $uri"
    $response = Invoke-RestMethod -Uri $uri -Certificate $cert -Headers $headers -Method GET -TimeoutSec 30
    $manifest = $response  # $null on 204 No Content
        if ($manifest) {
            $manifest | ConvertTo-Json -Depth 10 | Set-Content -Path $CacheFile -Encoding UTF8
            Write-Log ("Fetched schedule manifest from API and refreshed cache. waveId={0}, name={1}, isImmediate={2}, scheduledAtUtc={3}, status={4}" -f `
                $manifest.waveId,
                $manifest.name,
                $manifest.isImmediate,
                $manifest.scheduledAtUtc,
                $manifest.status)
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
    $hasShortcut = @(Get-ShortcutPaths | Where-Object { Test-Path $_ })
    foreach ($shortcutPath in (Get-ShortcutPaths)) {
        Write-Log ("Shortcut path check: '{0}' exists={1}" -f $shortcutPath, (Test-Path $shortcutPath))
    }
    Write-Log ("Shortcut flag check: '{0}' exists={1}" -f $ShortcutFlag, (Test-Path $ShortcutFlag))
    if ($hasShortcut) {
        Write-Log "No scheduled wave for this device, but shortcut(s) exist. Non-compliant - remediation needed to remove them."
        exit 1
    }
    Write-Log "No scheduled wave for this device (204) and no shortcut exists. Compliant."
    exit 0
}

$existingShortcuts = @(Get-ShortcutPaths | Where-Object { Test-Path $_ })
$hasFlag = Test-Path $ShortcutFlag
foreach ($shortcutPath in (Get-ShortcutPaths)) {
    Write-Log ("Shortcut path check: '{0}' exists={1}" -f $shortcutPath, (Test-Path $shortcutPath))
}
Write-Log ("Shortcut flag check: '{0}' exists={1}" -f $ShortcutFlag, $hasFlag)
Write-Log ("Shortcut state snapshot: flag={0}, existingShortcutCount={1}" -f $hasFlag, ($existingShortcuts.Count))

if ($manifest.isImmediate -eq $true) {
    if ($existingShortcuts.Count -gt 0 -and $hasFlag) {
        Write-Log "Active wave '$($manifest.name)' and shortcut already present. Compliant."
        exit 0
    }
    Write-Log "Active wave '$($manifest.name)' (scheduledAtUtc: $($manifest.scheduledAtUtc), waveId: $($manifest.waveId)). Non-compliant - remediation needed to create shortcut."
    exit 1
} else {
    if ($existingShortcuts.Count -gt 0 -or $hasFlag) {
        Write-Log "Wave '$($manifest.name)' is not active, but shortcut/flag still exists. Non-compliant - remediation needed to remove shortcut."
        exit 1
    }
    Write-Log "Wave '$($manifest.name)' scheduled for $($manifest.scheduledAtUtc) - not yet active and no shortcut exists. Compliant."
    exit 0
}
