#requires -Version 5.1
<#
.SYNOPSIS
    Pester test runner for the Intune Wipe client PS1 scripts.

.DESCRIPTION
    Ensures Pester >= 5.0 is available (installs to CurrentUser scope on demand)
    and runs all *.Tests.ps1 files under client\tests.

    Exits with the Pester FailedCount so it is CI-friendly:
        $env:GITHUB_ACTIONS / Azure DevOps will mark the step failed on any non-zero exit.

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\client\tests\Invoke-Tests.ps1

.EXAMPLE
    .\client\tests\Invoke-Tests.ps1 -Output Diagnostic
#>
[CmdletBinding()]
param(
    [ValidateSet('None','Normal','Detailed','Diagnostic')]
    [string]$Output = 'Detailed'
)

$ErrorActionPreference = 'Stop'

Write-Host '==> Ensuring Pester 5.x is available ...' -ForegroundColor Cyan
$pester = Get-Module -ListAvailable -Name Pester |
          Where-Object { $_.Version -ge [version]'5.0.0' } |
          Sort-Object Version -Descending | Select-Object -First 1
if (-not $pester) {
    Write-Host '    Installing Pester 5.x to CurrentUser scope ...' -ForegroundColor Yellow
    Install-Module -Name Pester -MinimumVersion 5.5.0 -Force -SkipPublisherCheck -Scope CurrentUser
    $pester = Get-Module -ListAvailable -Name Pester |
              Where-Object { $_.Version -ge [version]'5.0.0' } |
              Sort-Object Version -Descending | Select-Object -First 1
}
Write-Host ("    Using Pester {0} from {1}" -f $pester.Version, $pester.ModuleBase) -ForegroundColor Green
Import-Module $pester.Path -Force

$cfg = New-PesterConfiguration
$cfg.Run.Path        = $PSScriptRoot
$cfg.Output.Verbosity = $Output
$cfg.Run.PassThru    = $true

$result = Invoke-Pester -Configuration $cfg

$color = if ($result.FailedCount -eq 0) { 'Green' } else { 'Red' }
Write-Host ''
Write-Host ("==> Passed: {0}  Failed: {1}  Skipped: {2}  Duration: {3}s" -f `
    $result.PassedCount, $result.FailedCount, $result.SkippedCount,
    [math]::Round($result.Duration.TotalSeconds, 2)) -ForegroundColor $color

exit $result.FailedCount
