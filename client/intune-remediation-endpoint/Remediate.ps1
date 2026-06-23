<#
.SYNOPSIS
    Remediation: set IntuneWipeClient Function App endpoint env vars.

.DESCRIPTION
    Sets the machine-scope env vars to the values pinned below, using the
    capability-neutral INTUNE_ACTIONS_* namespace and, in lockstep, the legacy
    INTUNE_WIPE_* names for backward compatibility with client scripts not yet
    migrated:
        INTUNE_ACTIONS_API_URL      (+ legacy INTUNE_WIPE_API_URL)      = $ExpectedApiUrl
        INTUNE_ACTIONS_FUNCTION_KEY (+ legacy INTUNE_WIPE_FUNCTION_KEY) = $ExpectedFunctionKey

    Keep these constants in lockstep with Detect.ps1.

.NOTES
    Context: SYSTEM (Proactive Remediation), 64-bit.
    Exit 0 -> remediation succeeded. Non-zero -> failed.

    SECURITY: $ExpectedFunctionKey is a secret. Replace the placeholder
    below ONLY in the script body you paste into the Intune admin
    centre — never commit the real value to source control.
#>

[CmdletBinding()]
param()

# === EDIT BEFORE UPLOADING TO INTUNE =========================================
$ExpectedApiUrl      = 'https://devact-web-dev.azurewebsites.net/api/actions'
$ExpectedFunctionKey = '__REPLACE_WITH_REAL_FUNCTION_KEY__'

# Certificate selectors used by the wipe client to locate its mTLS client
# cert in Cert:\LocalMachine\My. Leave any of these EMPTY ('') to opt out
# of pinning that selector via env var.
$ExpectedCertThumbprint  = ''
$ExpectedCertSubjectLike = ''
$ExpectedCertIssuerLike  = '*MSLABS-SUBCA01*'
# =============================================================================

$UrlVarNames     = @('INTUNE_ACTIONS_API_URL',           'INTUNE_WIPE_API_URL')
$KeyVarNames     = @('INTUNE_ACTIONS_FUNCTION_KEY',      'INTUNE_WIPE_FUNCTION_KEY')
$CertThumbVars   = @('INTUNE_ACTIONS_CERT_THUMBPRINT',   'INTUNE_WIPE_CERT_THUMBPRINT')
$CertSubjectVars = @('INTUNE_ACTIONS_CERT_SUBJECT_LIKE', 'INTUNE_WIPE_CERT_SUBJECT_LIKE')
$CertIssuerVars  = @('INTUNE_ACTIONS_CERT_ISSUER_LIKE',  'INTUNE_WIPE_CERT_ISSUER_LIKE')
$LogDir         = Join-Path $env:ProgramData 'IntuneWipeClient\Logs'
$LogFile        = Join-Path $LogDir 'intune-remediation-endpoint-remediate.log'

function Write-Log {
    param([string] $Message)
    if (-not $Message) { return }
    if (-not (Test-Path $LogDir)) {
        New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
    }
    $line = "[{0}] {1}" -f (Get-Date -Format o), ($Message -replace "[\r\n]+", ' ')
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

try {
    Write-Log "Remediation started."
    Write-Log ("Expected config: URL='{0}', FunctionKey={1} {2}, Thumbprint={3}, SubjectLike={4}, IssuerLike={5}" -f `
        $ExpectedApiUrl,
        (Format-SafeValue $ExpectedFunctionKey -Secret),
        (Get-SecretFingerprint $ExpectedFunctionKey),
        (Format-SafeValue $ExpectedCertThumbprint),
        (Format-SafeValue $ExpectedCertSubjectLike),
        (Format-SafeValue $ExpectedCertIssuerLike))
    foreach ($n in $UrlVarNames) { [Environment]::SetEnvironmentVariable($n, $ExpectedApiUrl,      'Machine') }
    foreach ($n in $KeyVarNames) { [Environment]::SetEnvironmentVariable($n, $ExpectedFunctionKey, 'Machine') }
    Write-Log ("Set {0} and {1}." -f ($UrlVarNames -join '/'), ($KeyVarNames -join '/'))

    # For cert selectors: empty ExpectedX means "do not pin via env" — in
    # that case we DELETE the env vars so the wipe client falls back to
    # config.json. This lets ops opt a selector out of remediation later
    # without leaving a stale value behind.
    foreach ($pair in @(
        @{ Vars = $CertThumbVars  ; Expected = $ExpectedCertThumbprint  },
        @{ Vars = $CertSubjectVars; Expected = $ExpectedCertSubjectLike },
        @{ Vars = $CertIssuerVars ; Expected = $ExpectedCertIssuerLike  }
    )) {
        foreach ($n in $pair.Vars) {
            if ($pair.Expected) {
                [Environment]::SetEnvironmentVariable($n, $pair.Expected, 'Machine')
            } else {
                [Environment]::SetEnvironmentVariable($n, $null, 'Machine')
            }
        }
        if ($pair.Expected) {
            Write-Log ("Set {0}." -f ($pair.Vars -join '/'))
        } else {
            Write-Log ("Removed {0} (empty expected value)." -f ($pair.Vars -join '/'))
        }
    }

    # Verify the canonical (first) names read back correctly.
    $writtenUrl     = [Environment]::GetEnvironmentVariable($UrlVarNames[0], 'Machine')
    $writtenKey     = [Environment]::GetEnvironmentVariable($KeyVarNames[0], 'Machine')
    $writtenThumb   = [Environment]::GetEnvironmentVariable($CertThumbVars[0], 'Machine')
    $writtenSubject = [Environment]::GetEnvironmentVariable($CertSubjectVars[0], 'Machine')
    $writtenIssuer  = [Environment]::GetEnvironmentVariable($CertIssuerVars[0], 'Machine')
    Write-Log ("Read-back snapshot: URL='{0}', FunctionKey={1} {2}, Thumbprint={3}, SubjectLike={4}, IssuerLike={5}" -f `
        $writtenUrl,
        (Format-SafeValue $writtenKey -Secret),
        (Get-SecretFingerprint $writtenKey),
        (Format-SafeValue $writtenThumb),
        (Format-SafeValue $writtenSubject),
        (Format-SafeValue $writtenIssuer))
    if ($writtenUrl -ne $ExpectedApiUrl) {
        Write-Log ("FAIL: {0} read-back '{1}' does not match expected." -f $UrlVarNames[0], $writtenUrl)
        exit 1
    }
    if ($writtenKey -ne $ExpectedFunctionKey) {
        Write-Log ("FAIL: {0} read-back length does not match expected length={1}." -f $KeyVarNames[0], $ExpectedFunctionKey.Length)
        exit 1
    }

    Write-Log "OK: URL + key + cert selectors written to Machine env (INTUNE_ACTIONS_* + legacy INTUNE_WIPE_*)."
    exit 0
} catch {
    Write-Log "ERROR: $($_.Exception.Message)"
    exit 1
}
