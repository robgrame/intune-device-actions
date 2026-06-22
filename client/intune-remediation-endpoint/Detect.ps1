<#
.SYNOPSIS
    Detection: IntuneWipeClient Function App endpoint (URL + key) env vars.

.DESCRIPTION
    Reports the device as non-compliant if either of the machine-scope
    env vars below is missing / blank / different from the expected
    value. The companion Remediate.ps1 sets both.

        INTUNE_WIPE_API_URL       <- expected URL
        INTUNE_WIPE_FUNCTION_KEY  <- expected Function-level / host key

    The wipe scripts (Invoke-WipeFromTask.ps1, Watch-WipeStatus.ps1,
    intune-remediation-schedule/*) read these env vars in preference to
    the values in config.json, so the Function App can be repointed and
    the key rotated WITHOUT repackaging the .intunewin.

.NOTES
    Context: SYSTEM (Proactive Remediation), 64-bit.
    Exit 0 -> compliant. Exit 1 -> non-compliant (Remediate will run).

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
# of pinning that selector via env var (the value from config.json — or
# from a previously-set env var — will be left untouched).
$ExpectedCertThumbprint  = ''
$ExpectedCertSubjectLike = ''
$ExpectedCertIssuerLike  = '*MSLABS-SUBCA01*'
# =============================================================================

$UrlVarName     = 'INTUNE_WIPE_API_URL'
$KeyVarName     = 'INTUNE_WIPE_FUNCTION_KEY'
$CertThumbVar   = 'INTUNE_WIPE_CERT_THUMBPRINT'
$CertSubjectVar = 'INTUNE_WIPE_CERT_SUBJECT_LIKE'
$CertIssuerVar  = 'INTUNE_WIPE_CERT_ISSUER_LIKE'

function Write-OneLine {
    param([string] $Message)
    if ($Message) { Write-Host ($Message -replace "[\r\n]+", ' ') }
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
    Write-OneLine ("Expected config snapshot: URL='{0}', FunctionKey={1} {2}, Thumbprint={3}, SubjectLike={4}, IssuerLike={5}" -f `
        $ExpectedApiUrl,
        (Format-SafeValue $ExpectedFunctionKey -Secret),
        (Get-SecretFingerprint $ExpectedFunctionKey),
        (Format-SafeValue $ExpectedCertThumbprint),
        (Format-SafeValue $ExpectedCertSubjectLike),
        (Format-SafeValue $ExpectedCertIssuerLike))
    $currentUrl = [Environment]::GetEnvironmentVariable($UrlVarName, 'Machine')
    $currentKey = [Environment]::GetEnvironmentVariable($KeyVarName, 'Machine')
    $currentThumb = [Environment]::GetEnvironmentVariable($CertThumbVar, 'Machine')
    $currentSubject = [Environment]::GetEnvironmentVariable($CertSubjectVar, 'Machine')
    $currentIssuer = [Environment]::GetEnvironmentVariable($CertIssuerVar, 'Machine')
    Write-OneLine ("Current env snapshot: URL='{0}', FunctionKey={1} {2}, Thumbprint={3}, SubjectLike={4}, IssuerLike={5}" -f `
        (Format-SafeValue $currentUrl),
        (Format-SafeValue $currentKey -Secret),
        (Get-SecretFingerprint $currentKey),
        (Format-SafeValue $currentThumb),
        (Format-SafeValue $currentSubject),
        (Format-SafeValue $currentIssuer))
    if (-not $currentUrl -or $currentUrl.Trim() -ne $ExpectedApiUrl) {
        Write-OneLine "REMEDIATE: $UrlVarName missing or mismatched (expected '$ExpectedApiUrl')."
        exit 1
    }

    if (-not $currentKey -or $currentKey.Trim() -ne $ExpectedFunctionKey) {
        $obs = if ($currentKey) { "len=$($currentKey.Trim().Length)" } else { 'missing' }
        Write-OneLine "REMEDIATE: $KeyVarName mismatched ($obs vs expected len=$($ExpectedFunctionKey.Length))."
        exit 1
    }

    foreach ($pair in @(
        @{ Var = $CertThumbVar  ; Expected = $ExpectedCertThumbprint  },
        @{ Var = $CertSubjectVar; Expected = $ExpectedCertSubjectLike },
        @{ Var = $CertIssuerVar ; Expected = $ExpectedCertIssuerLike  }
    )) {
        if (-not $pair.Expected) { continue }  # opted out
        $current = [Environment]::GetEnvironmentVariable($pair.Var, 'Machine')
        if (-not $current -or $current.Trim() -ne $pair.Expected) {
            Write-OneLine ("REMEDIATE: {0} missing or mismatched (expected '{1}')." -f $pair.Var, $pair.Expected)
            exit 1
        }
    }

    Write-OneLine "OK: endpoint env vars (URL + key + cert selectors) match expected."
    exit 0
} catch {
    Write-OneLine "ERROR: $($_.Exception.Message)"
    exit 1
}
