<#
.SYNOPSIS
    Remediation: set the Intune Device Actions Function App endpoint env vars.

.DESCRIPTION
    Sets the machine-scope env vars to the values pinned below, using the
    capability-neutral INTUNE_ACTIONS_* namespace:
        INTUNE_ACTIONS_API_URL      = $ExpectedApiUrl
        INTUNE_ACTIONS_FUNCTION_KEY = $ExpectedFunctionKey

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

# Certificate selectors used by the client to locate its mTLS client
# cert in Cert:\LocalMachine\My. Leave any of these EMPTY ('') to opt out
# of pinning that selector via env var.
$ExpectedCertThumbprint  = ''
$ExpectedCertSubjectLike = ''
$ExpectedCertIssuerLike  = '*MSLABS-SUBCA01*'
# =============================================================================

$UrlVar       = 'INTUNE_ACTIONS_API_URL'
$KeyVar       = 'INTUNE_ACTIONS_FUNCTION_KEY'
$CertThumbVar   = 'INTUNE_ACTIONS_CERT_THUMBPRINT'
$CertSubjectVar = 'INTUNE_ACTIONS_CERT_SUBJECT_LIKE'
$CertIssuerVar  = 'INTUNE_ACTIONS_CERT_ISSUER_LIKE'
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
    [Environment]::SetEnvironmentVariable($UrlVar, $ExpectedApiUrl,      'Machine')
    [Environment]::SetEnvironmentVariable($KeyVar, $ExpectedFunctionKey, 'Machine')
    Write-Log ("Set {0} and {1}." -f $UrlVar, $KeyVar)

    # For cert selectors: empty ExpectedX means "do not pin via env" — in
    # that case we DELETE the env var so the client falls back to
    # config.json. This lets ops opt a selector out of remediation later
    # without leaving a stale value behind.
    foreach ($pair in @(
        @{ Var = $CertThumbVar  ; Expected = $ExpectedCertThumbprint  },
        @{ Var = $CertSubjectVar; Expected = $ExpectedCertSubjectLike },
        @{ Var = $CertIssuerVar ; Expected = $ExpectedCertIssuerLike  }
    )) {
        if ($pair.Expected) {
            [Environment]::SetEnvironmentVariable($pair.Var, $pair.Expected, 'Machine')
            Write-Log ("Set {0}." -f $pair.Var)
        } else {
            [Environment]::SetEnvironmentVariable($pair.Var, $null, 'Machine')
            Write-Log ("Removed {0} (empty expected value)." -f $pair.Var)
        }
    }

    # Verify the values read back correctly.
    $writtenUrl     = [Environment]::GetEnvironmentVariable($UrlVar, 'Machine')
    $writtenKey     = [Environment]::GetEnvironmentVariable($KeyVar, 'Machine')
    $writtenThumb   = [Environment]::GetEnvironmentVariable($CertThumbVar, 'Machine')
    $writtenSubject = [Environment]::GetEnvironmentVariable($CertSubjectVar, 'Machine')
    $writtenIssuer  = [Environment]::GetEnvironmentVariable($CertIssuerVar, 'Machine')
    Write-Log ("Read-back snapshot: URL='{0}', FunctionKey={1} {2}, Thumbprint={3}, SubjectLike={4}, IssuerLike={5}" -f `
        $writtenUrl,
        (Format-SafeValue $writtenKey -Secret),
        (Get-SecretFingerprint $writtenKey),
        (Format-SafeValue $writtenThumb),
        (Format-SafeValue $writtenSubject),
        (Format-SafeValue $writtenIssuer))
    if ($writtenUrl -ne $ExpectedApiUrl) {
        Write-Log ("FAIL: {0} read-back '{1}' does not match expected." -f $UrlVar, $writtenUrl)
        exit 1
    }
    if ($writtenKey -ne $ExpectedFunctionKey) {
        Write-Log ("FAIL: {0} read-back length does not match expected length={1}." -f $KeyVar, $ExpectedFunctionKey.Length)
        exit 1
    }

    Write-Log "OK: URL + key + cert selectors written to Machine env (INTUNE_ACTIONS_*)."
    exit 0
} catch {
    Write-Log "ERROR: $($_.Exception.Message)"
    exit 1
}
