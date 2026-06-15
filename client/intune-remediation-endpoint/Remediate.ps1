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
# =============================================================================

$UrlVarName = 'INTUNE_WIPE_API_URL'
$KeyVarName = 'INTUNE_WIPE_FUNCTION_KEY'

function Write-OneLine {
    param([string] $Message)
    if ($Message) { Write-Host ($Message -replace "[\r\n]+", ' ') }
}

try {
    [Environment]::SetEnvironmentVariable($UrlVarName, $ExpectedApiUrl,      'Machine')
    [Environment]::SetEnvironmentVariable($KeyVarName, $ExpectedFunctionKey, 'Machine')

    $writtenUrl = [Environment]::GetEnvironmentVariable($UrlVarName, 'Machine')
    $writtenKey = [Environment]::GetEnvironmentVariable($KeyVarName, 'Machine')
    if ($writtenUrl -ne $ExpectedApiUrl) {
        Write-OneLine "FAIL: $UrlVarName read-back '$writtenUrl' does not match expected."
        exit 1
    }
    if ($writtenKey -ne $ExpectedFunctionKey) {
        Write-OneLine "FAIL: $KeyVarName read-back length=$(($writtenKey | Measure-Object -Character).Characters) does not match expected length=$($ExpectedFunctionKey.Length)."
        exit 1
    }

    Write-OneLine "OK: $UrlVarName set; $KeyVarName set (len=$($ExpectedFunctionKey.Length))."
    exit 0
} catch {
    Write-OneLine "ERROR: $($_.Exception.Message)"
    exit 1
}
