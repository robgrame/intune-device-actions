#requires -Version 5.1
<#
.SYNOPSIS
    Registers the AD object cleanup hybrid worker as a Windows Scheduled Task
    that runs continuously under a service account.

.DESCRIPTION
    Creates (or updates) a scheduled task that launches
    Start-AdObjectCleanupWorker.ps1 at startup and keeps it running. The worker
    loops on the Service Bus queue; if the process exits the task restarts it.

    Run this on the domain-joined hybrid server (RSAT ActiveDirectory required)
    as an administrator. The task runs as the account you supply, which must:
      * be able to read/delete the target computer objects in AD, and
      * be able to read the SAS key file.

.PARAMETER Namespace
    Service Bus namespace (host or short name).

.PARAMETER Queue
    Queue name (default 'ad-object-cleanup').

.PARAMETER SasKeyName
    Listen SAS rule name (default 'worker-listen').

.PARAMETER SasKeyFile
    Path to the file holding the SAS key (protect with NTFS ACL).

.PARAMETER SearchBase
    OU subtree DN to scope deletions to (STRONGLY recommended).

.PARAMETER ExclusionNames
    Computer names to never delete.

.PARAMETER MaxDeletePerMessage
    Per-message delete cap (default 5).

.PARAMETER RunAsUser
    Service account (DOMAIN\user) the task runs as. Prompted for a password.

.PARAMETER TaskName
    Scheduled task name (default 'IntuneDeviceActions-AdObjectCleanup').

.PARAMETER LogPath
    Log file path (default C:\ProgramData\idactions\ad-object-cleanup.log).

.PARAMETER WhatIfWorker
    Install the worker in dry-run mode (adds -WhatIf to the worker command).

.EXAMPLE
    .\Install-AdObjectCleanupWorker.ps1 -Namespace idactions-sb-dev `
        -SasKeyFile C:\ProgramData\idactions\sb.key `
        -SearchBase 'OU=Workstations,DC=corp,DC=contoso,DC=com' `
        -RunAsUser 'CORP\svc-adcleanup'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Namespace,
    [string] $Queue = 'ad-object-cleanup',
    [string] $SasKeyName = 'worker-listen',
    [Parameter(Mandatory = $true)] [string] $SasKeyFile,
    [string] $SearchBase,
    [string[]] $ExclusionNames = @(),
    [int] $MaxDeletePerMessage = 5,
    [Parameter(Mandatory = $true)] [string] $RunAsUser,
    [string] $TaskName = 'IntuneDeviceActions-AdObjectCleanup',
    [string] $LogPath = 'C:\ProgramData\idactions\ad-object-cleanup.log',
    [switch] $WhatIfWorker
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$workerScript = Join-Path $scriptDir 'Start-AdObjectCleanupWorker.ps1'
if (-not (Test-Path $workerScript)) { throw "Worker script not found: $workerScript" }

$logDir = Split-Path -Parent $LogPath
if ($logDir -and -not (Test-Path $logDir)) { New-Item -ItemType Directory -Force -Path $logDir | Out-Null }

# Build the pwsh/powershell argument string.
$argList = @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$workerScript`"",
    '-Namespace', $Namespace,
    '-Queue', $Queue,
    '-SasKeyName', $SasKeyName,
    '-SasKeyFile', "`"$SasKeyFile`"",
    '-MaxDeletePerMessage', $MaxDeletePerMessage,
    '-LogPath', "`"$LogPath`""
)
if ($SearchBase)      { $argList += @('-SearchBase', "`"$SearchBase`"") }
if ($ExclusionNames.Count -gt 0) { $argList += @('-ExclusionNames', ($ExclusionNames -join ',')) }
if ($WhatIfWorker)    { $argList += '-WhatIf' }

$psCmd = Get-Command powershell.exe -ErrorAction SilentlyContinue
$psExe = if ($psCmd) { $psCmd.Source } else { 'powershell.exe' }

$action = New-ScheduledTaskAction -Execute $psExe -Argument ($argList -join ' ')
$trigger = New-ScheduledTaskTrigger -AtStartup
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero)

$cred = Get-Credential -UserName $RunAsUser -Message "Password for the worker service account $RunAsUser"
$password = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto(
    [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($cred.Password))

if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Write-Host "Updating existing task '$TaskName'..."
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings `
    -User $RunAsUser -Password $password -RunLevel Highest -Description 'IntuneDeviceActions on-prem AD object cleanup worker' | Out-Null

Write-Host "Scheduled task '$TaskName' registered. Starting now..."
Start-ScheduledTask -TaskName $TaskName
Write-Host 'Done. Tail the log at:' $LogPath
