# MdmSyncNudge.psm1
#
# Local MDM sync nudge. Forces the Intune Management Extension (IME) / OMA-DM
# stack to fetch pending commands NOW instead of waiting for the next scheduled
# check-in (default Intune cadence: every 8 hours, with PushLaunch best-effort
# fallback). Used after the wipe API returns 202 Accepted to collapse the
# end-to-end latency seen by the user from "tens of minutes" to "tens of
# seconds".
#
# Fallback chain (executed in order, first success wins):
#
#   1. PushLaunch scheduled task
#      \Microsoft\Windows\EnterpriseMgmt\<EnrollmentId>\PushLaunch is the
#      canonical Win10 1607+ task that triggers an OMA-DM session, equivalent
#      to clicking 'Sync' in Settings > Accounts > Access work or school.
#
#   2. Any other Schedule* task under the same EnterpriseMgmt enrollment node
#      (older builds and some co-managed devices register tasks with names
#      like 'Schedule #1 created by enrollment client' instead of PushLaunch).
#
#   3. (Opt-in) Scheduled reboot via `shutdown /r /t <seconds>`. At boot the
#      management agent always performs a check-in, so this is the deterministic
#      last-resort path. Off by default — the caller must pass
#      `-AllowRebootFallback` because rebooting an end-user device without
#      explicit consent is a worse UX than waiting a few extra minutes.
#
# Designed for SYSTEM-context invocation (so it can enumerate and start the
# EnterpriseMgmt tasks, which non-admin users cannot see). Returns a structured
# hashtable describing what was attempted; it never throws — failures are
# reported in the return value so the caller can log them.

Set-StrictMode -Version Latest

function Get-EnterpriseMgmtTasks {
    <#
    .SYNOPSIS
        Returns scheduled tasks registered under \Microsoft\Windows\EnterpriseMgmt\*
        that can be used as MDM sync triggers.
    .DESCRIPTION
        Filters to PushLaunch and Schedule* names. Excludes the SmsRouter and
        report-only tasks. Safe to call from SYSTEM; non-SYSTEM callers will
        typically see an empty set because the parent folder is ACL'd to
        SYSTEM / TrustedInstaller.
    .PARAMETER NamePattern
        Wildcard pattern used to filter the task name. Default 'PushLaunch'
        (layer 1). Layer 2 uses 'Schedule*'.
    #>
    [CmdletBinding()]
    param([string] $NamePattern = 'PushLaunch')

    try {
        $tasks = Get-ScheduledTask -TaskPath '\Microsoft\Windows\EnterpriseMgmt\*' -ErrorAction Stop |
                 Where-Object { $_.TaskName -like $NamePattern }
        if ($null -eq $tasks) {
            # -NoEnumerate prevents pipeline unrolling so the caller sees an
            # actual [object[]] array (even when empty), not $null.
            Write-Output -NoEnumerate ([object[]]@())
            return
        }
        # Force array shape (Where-Object may return a scalar when only one
        # item matches) and emit without unrolling.
        Write-Output -NoEnumerate ([object[]]@($tasks))
        return
    } catch {
        Write-Verbose ("Get-EnterpriseMgmtTasks pattern={0} failed: {1}" -f $NamePattern, $_.Exception.Message)
        Write-Output -NoEnumerate ([object[]]@())
        return
    }
}

function Start-RebootCountdown {
    <#
    .SYNOPSIS
        Schedules a system reboot via shutdown.exe with a user-facing message.
    .DESCRIPTION
        Thin wrapper so the rest of the module can be unit-tested by mocking
        this single function instead of mocking the external shutdown.exe
        executable (which Pester can mock but is brittle across versions).
    .OUTPUTS
        [bool] $true on success (shutdown.exe exit code 0), $false otherwise.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)] [int]    $DelaySeconds,
        [Parameter(Mandatory)] [string] $Message
    )
    # /f forces close of open apps so we don't get stuck on a "Save?" prompt
    # /c is limited to 512 chars by shutdown.exe.
    $msg = if ($Message.Length -gt 510) { $Message.Substring(0, 510) } else { $Message }
    & shutdown.exe /r /t $DelaySeconds /f /c $msg 2>&1 | Out-Null
    return ($LASTEXITCODE -eq 0)
}

function Invoke-MdmSyncNudge {
    <#
    .SYNOPSIS
        Forces a local MDM check-in via the best available method.
    .PARAMETER AllowRebootFallback
        Enable the last-resort scheduled-reboot fallback when no sync task can
        be triggered. Default: $false.
    .PARAMETER RebootDelaySeconds
        Seconds passed to `shutdown /r /t <n>`. Default: 60. Min 30, max 600
        (10 minutes) — values outside that range are clamped.
    .PARAMETER RebootMessage
        User-facing message broadcast on the scheduled reboot. Localized in
        Italian by default to match the rest of the UI.
    .OUTPUTS
        [hashtable] with keys:
          ok          : [bool] $true if at least one layer succeeded
          method      : [string] 'PushLaunch' | 'EnterpriseMgmtTask' | 'ScheduledReboot' | 'none'
          taskCount   : [int]    number of tasks triggered (layers 1/2 only)
          attempts    : [array]  list of per-layer details
                                  @{ layer; name; ok; details }
    .EXAMPLE
        $r = Invoke-MdmSyncNudge -Verbose
        if ($r.ok) { "Triggered $($r.method) ($($r.taskCount) task(s))" }
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [switch] $AllowRebootFallback,
        [int]    $RebootDelaySeconds = 60,
        [string] $RebootMessage = 'Reset aziendale: il dispositivo verra'' riavviato per applicare il comando Intune.'
    )

    $attempts = New-Object System.Collections.ArrayList

    # ---- Layer 1: PushLaunch ------------------------------------------------
    # Get-EnterpriseMgmtTasks uses Write-Output -NoEnumerate so this assignment
    # always receives an [object[]] (possibly empty), never $null. We do NOT
    # wrap with @(...) because that would re-wrap an empty array into a
    # 1-element array containing the empty array.
    $pushTasks = Get-EnterpriseMgmtTasks -NamePattern 'PushLaunch'
    if ($pushTasks.Count -gt 0) {
        $okCount = 0
        $perTask = New-Object System.Collections.ArrayList
        foreach ($t in $pushTasks) {
            try {
                # Use the string path/name signature instead of -InputObject
                # so that tests can substitute pscustomobject stand-ins for
                # the real [CimInstance] objects returned by Get-ScheduledTask
                # (Start-ScheduledTask's -InputObject is strongly typed).
                Start-ScheduledTask -TaskPath $t.TaskPath -TaskName $t.TaskName -ErrorAction Stop
                $okCount++
                [void]$perTask.Add(@{ taskPath = $t.TaskPath; taskName = $t.TaskName; ok = $true })
            } catch {
                [void]$perTask.Add(@{ taskPath = $t.TaskPath; taskName = $t.TaskName; ok = $false; error = $_.Exception.Message })
            }
        }
        [void]$attempts.Add(@{
            layer    = 1
            name     = 'PushLaunch'
            ok       = ($okCount -gt 0)
            details  = @{ triggered = $okCount; total = $pushTasks.Count; perTask = @($perTask) }
        })
        if ($okCount -gt 0) {
            return @{ ok = $true; method = 'PushLaunch'; taskCount = $okCount; attempts = @($attempts) }
        }
    } else {
        [void]$attempts.Add(@{ layer = 1; name = 'PushLaunch'; ok = $false; details = @{ reason = 'no-task-found' } })
    }

    # ---- Layer 2: any Schedule* task under EnterpriseMgmt -------------------
    $scheduleTasks = Get-EnterpriseMgmtTasks -NamePattern 'Schedule*'
    if ($scheduleTasks.Count -gt 0) {
        $okCount = 0
        $perTask = New-Object System.Collections.ArrayList
        foreach ($t in $scheduleTasks) {
            try {
                Start-ScheduledTask -TaskPath $t.TaskPath -TaskName $t.TaskName -ErrorAction Stop
                $okCount++
                [void]$perTask.Add(@{ taskPath = $t.TaskPath; taskName = $t.TaskName; ok = $true })
            } catch {
                [void]$perTask.Add(@{ taskPath = $t.TaskPath; taskName = $t.TaskName; ok = $false; error = $_.Exception.Message })
            }
        }
        [void]$attempts.Add(@{
            layer    = 2
            name     = 'EnterpriseMgmtTask'
            ok       = ($okCount -gt 0)
            details  = @{ triggered = $okCount; total = $scheduleTasks.Count; perTask = @($perTask) }
        })
        if ($okCount -gt 0) {
            return @{ ok = $true; method = 'EnterpriseMgmtTask'; taskCount = $okCount; attempts = @($attempts) }
        }
    } else {
        [void]$attempts.Add(@{ layer = 2; name = 'EnterpriseMgmtTask'; ok = $false; details = @{ reason = 'no-task-found' } })
    }

    # ---- Layer 3: scheduled reboot (opt-in) ---------------------------------
    if ($AllowRebootFallback) {
        $delay = [int][math]::Min(600, [math]::Max(30, $RebootDelaySeconds))
        try {
            $rebootOk = Start-RebootCountdown -DelaySeconds $delay -Message $RebootMessage
            if ($rebootOk) {
                [void]$attempts.Add(@{
                    layer   = 3
                    name    = 'ScheduledReboot'
                    ok      = $true
                    details = @{ delaySeconds = $delay; message = $RebootMessage }
                })
                return @{ ok = $true; method = 'ScheduledReboot'; taskCount = 0; attempts = @($attempts) }
            } else {
                [void]$attempts.Add(@{
                    layer   = 3
                    name    = 'ScheduledReboot'
                    ok      = $false
                    details = @{ delaySeconds = $delay; reason = 'shutdown-nonzero-exit' }
                })
            }
        } catch {
            [void]$attempts.Add(@{
                layer   = 3
                name    = 'ScheduledReboot'
                ok      = $false
                details = @{ error = $_.Exception.Message }
            })
        }
    } else {
        [void]$attempts.Add(@{ layer = 3; name = 'ScheduledReboot'; ok = $false; details = @{ reason = 'opt-in-disabled' } })
    }

    return @{ ok = $false; method = 'none'; taskCount = 0; attempts = @($attempts) }
}

Export-ModuleMember -Function Invoke-MdmSyncNudge, Get-EnterpriseMgmtTasks, Start-RebootCountdown
