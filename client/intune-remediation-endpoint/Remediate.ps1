<#
.SYNOPSIS
    Remediation: set IntuneWipeClient Function App endpoint env vars.

.DESCRIPTION
    Sets both machine-scope env vars to the values pinned below:
        INTUNE_WIPE_API_URL       = $ExpectedApiUrl
        INTUNE_WIPE_FUNCTION_KEY  = $ExpectedFunctionKey

    Keep these two constants in lockstep with Detect.ps1.

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
$ExpectedCertSubjectLike = '*Microsoft Intune MDM Device CA*'
$ExpectedCertIssuerLike  = '*MSLABS-SUBCA01*;*MSLABS-ADCS*'
# =============================================================================

$UrlVarName     = 'INTUNE_WIPE_API_URL'
$KeyVarName     = 'INTUNE_WIPE_FUNCTION_KEY'
$CertThumbVar   = 'INTUNE_WIPE_CERT_THUMBPRINT'
$CertSubjectVar = 'INTUNE_WIPE_CERT_SUBJECT_LIKE'
$CertIssuerVar  = 'INTUNE_WIPE_CERT_ISSUER_LIKE'
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

try {
    Write-Log "Remediation started."
    [Environment]::SetEnvironmentVariable($UrlVarName, $ExpectedApiUrl,      'Machine')
    [Environment]::SetEnvironmentVariable($KeyVarName, $ExpectedFunctionKey, 'Machine')
    Write-Log "Set $UrlVarName and $KeyVarName."

    # For cert selectors: empty ExpectedX means "do not pin via env" — in
    # that case we DELETE the env var so the wipe client falls back to
    # config.json. This lets ops opt a selector out of remediation later
    # without leaving a stale value behind.
    foreach ($pair in @(
        @{ Var = $CertThumbVar  ; Expected = $ExpectedCertThumbprint  },
        @{ Var = $CertSubjectVar; Expected = $ExpectedCertSubjectLike },
        @{ Var = $CertIssuerVar ; Expected = $ExpectedCertIssuerLike  }
    )) {
        if ($pair.Expected) {
            [Environment]::SetEnvironmentVariable($pair.Var, $pair.Expected, 'Machine')
            Write-Log "Set $($pair.Var)."
        } else {
            [Environment]::SetEnvironmentVariable($pair.Var, $null, 'Machine')
            Write-Log "Removed $($pair.Var) (empty expected value)."
        }
    }

    $writtenUrl = [Environment]::GetEnvironmentVariable($UrlVarName, 'Machine')
    $writtenKey = [Environment]::GetEnvironmentVariable($KeyVarName, 'Machine')
    if ($writtenUrl -ne $ExpectedApiUrl) {
        Write-Log "FAIL: $UrlVarName read-back '$writtenUrl' does not match expected."
        exit 1
    }
    if ($writtenKey -ne $ExpectedFunctionKey) {
        Write-Log "FAIL: $KeyVarName read-back length does not match expected length=$($ExpectedFunctionKey.Length)."
        exit 1
    }

    Write-Log "OK: URL + key + cert selectors written to Machine env."
    exit 0
} catch {
    Write-Log "ERROR: $($_.Exception.Message)"
    exit 1
}
