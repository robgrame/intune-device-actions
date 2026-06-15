<#
.SYNOPSIS
    Detection script for the IntuneWipeClient ApiUrl override.

.DESCRIPTION
    Reports the device as non-compliant if the machine-scope environment
    variable INTUNE_WIPE_API_URL is missing or does not match the
    $ExpectedApiUrl constant below. The companion Remediate.ps1 sets the
    variable to the expected value.

    Wiring rationale (avoid repackaging the .intunewin):
        Invoke-WipeFromTask.ps1 and Watch-WipeStatus.ps1 honour
        INTUNE_WIPE_API_URL (Machine scope) and use it instead of the
        ApiUrl baked into config.json by Install.ps1. Repointing the
        Function App is therefore a single edit in this script + Intune
        remediation re-run, with zero client re-deployment.

.NOTES
    Author : Intune Device Actions
    Context: SYSTEM (Proactive Remediation), 64-bit.
    Exit 0 -> compliant. Exit 1 -> non-compliant (Remediate will run).
#>

[CmdletBinding()]
param()

# Single source of truth for the Function App (Web role) base URL.
# Change this when the Function App is repointed; republish the remediation.
$ExpectedApiUrl = 'https://devact-web-dev.azurewebsites.net/api/actions'
$EnvVarName     = 'INTUNE_WIPE_API_URL'

function Write-OneLine {
    param([string] $Message)
    if ($Message) { Write-Host ($Message -replace "[\r\n]+", ' ') }
}

try {
    $current = [Environment]::GetEnvironmentVariable($EnvVarName, 'Machine')
    if (-not $current) {
        Write-OneLine "REMEDIATE: machine env $EnvVarName not set; expected '$ExpectedApiUrl'."
        exit 1
    }
    $trimmed = $current.Trim()
    if ($trimmed -ne $ExpectedApiUrl) {
        Write-OneLine "REMEDIATE: machine env $EnvVarName is '$trimmed', expected '$ExpectedApiUrl'."
        exit 1
    }

    Write-OneLine "OK: $EnvVarName = $ExpectedApiUrl."
    exit 0
} catch {
    Write-OneLine "ERROR: $($_.Exception.Message)"
    exit 1
}
