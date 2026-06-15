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
# =============================================================================

$UrlVarName = 'INTUNE_WIPE_API_URL'
$KeyVarName = 'INTUNE_WIPE_FUNCTION_KEY'

function Write-OneLine {
    param([string] $Message)
    if ($Message) { Write-Host ($Message -replace "[\r\n]+", ' ') }
}

try {
    $currentUrl = [Environment]::GetEnvironmentVariable($UrlVarName, 'Machine')
    if (-not $currentUrl -or $currentUrl.Trim() -ne $ExpectedApiUrl) {
        Write-OneLine "REMEDIATE: $UrlVarName missing or mismatched (expected '$ExpectedApiUrl')."
        exit 1
    }

    $currentKey = [Environment]::GetEnvironmentVariable($KeyVarName, 'Machine')
    if (-not $currentKey -or $currentKey.Trim() -ne $ExpectedFunctionKey) {
        # Never echo the key contents — only its presence / length.
        $obs = if ($currentKey) { "len=$($currentKey.Trim().Length)" } else { 'missing' }
        Write-OneLine "REMEDIATE: $KeyVarName mismatched ($obs vs expected len=$($ExpectedFunctionKey.Length))."
        exit 1
    }

    Write-OneLine "OK: endpoint env vars match expected URL + key (len=$($ExpectedFunctionKey.Length))."
    exit 0
} catch {
    Write-OneLine "ERROR: $($_.Exception.Message)"
    exit 1
}
