<#
.SYNOPSIS
    Remediation: set the IntuneWipeClient ApiUrl override env var.

.DESCRIPTION
    Sets the machine-scope environment variable INTUNE_WIPE_API_URL to
    the $ExpectedApiUrl constant below. The wipe scripts
    (Invoke-WipeFromTask.ps1, Watch-WipeStatus.ps1, intune-remediation-
    schedule\*) read this variable and use it instead of the ApiUrl
    baked into config.json at install time.

    Keep $ExpectedApiUrl in sync with Detect.ps1.

.NOTES
    Context: SYSTEM (Proactive Remediation), 64-bit.
    Exit 0 -> remediation succeeded. Non-zero -> failed.
#>

[CmdletBinding()]
param()

$ExpectedApiUrl = 'https://devact-web-dev.azurewebsites.net/api/actions'
$EnvVarName     = 'INTUNE_WIPE_API_URL'

function Write-OneLine {
    param([string] $Message)
    if ($Message) { Write-Host ($Message -replace "[\r\n]+", ' ') }
}

try {
    [Environment]::SetEnvironmentVariable($EnvVarName, $ExpectedApiUrl, 'Machine')

    # Verify the write took effect against the registry-backed Machine
    # scope (the value will only appear in $env: for NEW processes).
    $written = [Environment]::GetEnvironmentVariable($EnvVarName, 'Machine')
    if ($written -ne $ExpectedApiUrl) {
        Write-OneLine "FAIL: tried to set $EnvVarName but read-back returned '$written'."
        exit 1
    }

    Write-OneLine "OK: set machine env $EnvVarName = $ExpectedApiUrl."
    exit 0
} catch {
    Write-OneLine "ERROR: $($_.Exception.Message)"
    exit 1
}
