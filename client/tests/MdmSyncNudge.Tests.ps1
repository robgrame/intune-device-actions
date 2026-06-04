#requires -Version 5.1
<#
.SYNOPSIS
    Pester v5 unit tests for client\MdmSyncNudge.psm1.

.DESCRIPTION
    Validates the local MDM sync nudge fallback chain (PushLaunch ->
    EnterpriseMgmt Schedule* tasks -> opt-in scheduled reboot). All external
    dependencies (Get-ScheduledTask, Start-ScheduledTask, Start-RebootCountdown)
    are mocked so the tests are hermetic and never touch the real task
    scheduler nor schedule any actual reboot.

    Run with:
        powershell.exe -NoProfile -File .\client\tests\Invoke-Tests.ps1
#>

BeforeAll {
    $script:ModulePath = Resolve-Path (Join-Path $PSScriptRoot '..\MdmSyncNudge.psm1')
    Import-Module $script:ModulePath -Force -DisableNameChecking
}

AfterAll {
    Remove-Module MdmSyncNudge -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------------------
Describe 'Invoke-MdmSyncNudge' {

    It 'returns ok=true and method=PushLaunch when the PushLaunch task is found and starts' {
        InModuleScope MdmSyncNudge {
            Mock Get-ScheduledTask {
                [pscustomobject]@{
                    TaskName = 'PushLaunch'
                    TaskPath = '\Microsoft\Windows\EnterpriseMgmt\AAAA-BBBB\'
                }
            } -ParameterFilter { $TaskPath -eq '\Microsoft\Windows\EnterpriseMgmt\*' }
            Mock Start-ScheduledTask { }

            $r = Invoke-MdmSyncNudge
            $r.ok        | Should -BeTrue
            $r.method    | Should -Be 'PushLaunch'
            $r.taskCount | Should -Be 1
            Assert-MockCalled Start-ScheduledTask -Times 1 -Exactly
        }
    }

    It 'starts every PushLaunch task when several enrollment folders exist' {
        InModuleScope MdmSyncNudge {
            Mock Get-ScheduledTask {
                @(
                    [pscustomobject]@{ TaskName = 'PushLaunch'; TaskPath = '\Microsoft\Windows\EnterpriseMgmt\AAAA-BBBB\' }
                    [pscustomobject]@{ TaskName = 'PushLaunch'; TaskPath = '\Microsoft\Windows\EnterpriseMgmt\CCCC-DDDD\' }
                )
            } -ParameterFilter { $TaskPath -eq '\Microsoft\Windows\EnterpriseMgmt\*' }
            Mock Start-ScheduledTask { }

            $r = Invoke-MdmSyncNudge
            $r.ok        | Should -BeTrue
            $r.method    | Should -Be 'PushLaunch'
            $r.taskCount | Should -Be 2
            Assert-MockCalled Start-ScheduledTask -Times 2 -Exactly
        }
    }

    It 'falls back to Schedule* tasks when PushLaunch is absent' {
        InModuleScope MdmSyncNudge {
            # First call (PushLaunch filter) -> none. Second call (Schedule*) -> one.
            Mock Get-ScheduledTask {
                if ($NamePattern -eq 'PushLaunch') { ,@() }
                else {
                    [pscustomobject]@{
                        TaskName = 'Schedule #3 created by enrollment client'
                        TaskPath = '\Microsoft\Windows\EnterpriseMgmt\AAAA-BBBB\'
                    }
                }
            }
            # Stub the public function. NOTE: must use Write-Output -NoEnumerate
            # to match the real Get-EnterpriseMgmtTasks contract, otherwise an
            # empty @() gets pipeline-unrolled into $null and breaks `.Count`
            # under Set-StrictMode -Version Latest.
            Mock Get-EnterpriseMgmtTasks {
                if ($NamePattern -eq 'PushLaunch') {
                    Write-Output -NoEnumerate ([object[]]@())
                } else {
                    Write-Output -NoEnumerate ([object[]]@(
                        [pscustomobject]@{
                            TaskName = 'Schedule #3 created by enrollment client'
                            TaskPath = '\Microsoft\Windows\EnterpriseMgmt\AAAA-BBBB\'
                        }
                    ))
                }
            }
            Mock Start-ScheduledTask { }

            $r = Invoke-MdmSyncNudge
            $r.ok        | Should -BeTrue
            $r.method    | Should -Be 'EnterpriseMgmtTask'
            $r.taskCount | Should -Be 1
        }
    }

    It 'returns ok=false / method=none when no task is found and reboot fallback is disabled' {
        InModuleScope MdmSyncNudge {
            Mock Get-EnterpriseMgmtTasks { ,@() }
            Mock Start-ScheduledTask { }
            Mock Start-RebootCountdown { $true } # must NOT be called when fallback is disabled

            $r = Invoke-MdmSyncNudge   # AllowRebootFallback not set
            $r.ok        | Should -BeFalse
            $r.method    | Should -Be 'none'
            $r.taskCount | Should -Be 0
            Assert-MockCalled Start-ScheduledTask   -Times 0 -Exactly
            Assert-MockCalled Start-RebootCountdown -Times 0 -Exactly
            # Verify the layer-3 entry was recorded with reason 'opt-in-disabled'
            $layer3 = $r.attempts | Where-Object { $_.layer -eq 3 }
            $layer3.details.reason | Should -Be 'opt-in-disabled'
        }
    }

    It 'invokes Start-RebootCountdown with the clamped (minimum 30s) delay when reboot fallback is allowed' {
        InModuleScope MdmSyncNudge {
            Mock Get-EnterpriseMgmtTasks { ,@() }
            $script:capturedDelay   = $null
            $script:capturedMessage = $null
            Mock Start-RebootCountdown {
                $script:capturedDelay   = $DelaySeconds
                $script:capturedMessage = $Message
                $true
            }

            $r = Invoke-MdmSyncNudge -AllowRebootFallback -RebootDelaySeconds 5
            $r.ok        | Should -BeTrue
            $r.method    | Should -Be 'ScheduledReboot'
            # 5 was below the 30s floor: must be clamped up to 30.
            $script:capturedDelay | Should -Be 30
            ($r.attempts | Where-Object { $_.layer -eq 3 }).details.delaySeconds | Should -Be 30
            $script:capturedMessage | Should -Not -BeNullOrEmpty
            Assert-MockCalled Start-RebootCountdown -Times 1 -Exactly
        }
    }

    It 'clamps an unreasonably large RebootDelaySeconds to 600 (10 minutes)' {
        InModuleScope MdmSyncNudge {
            Mock Get-EnterpriseMgmtTasks { ,@() }
            $script:capturedDelay = $null
            Mock Start-RebootCountdown {
                $script:capturedDelay = $DelaySeconds
                $true
            }

            $r = Invoke-MdmSyncNudge -AllowRebootFallback -RebootDelaySeconds 99999
            $r.method | Should -Be 'ScheduledReboot'
            $script:capturedDelay | Should -Be 600
            ($r.attempts | Where-Object { $_.layer -eq 3 }).details.delaySeconds | Should -Be 600
        }
    }

    It 'records a layer-3 failure when Start-RebootCountdown returns $false (shutdown.exe non-zero exit)' {
        InModuleScope MdmSyncNudge {
            Mock Get-EnterpriseMgmtTasks { ,@() }
            Mock Start-RebootCountdown { $false }

            $r = Invoke-MdmSyncNudge -AllowRebootFallback
            $r.ok     | Should -BeFalse
            $r.method | Should -Be 'none'
            $layer3 = $r.attempts | Where-Object { $_.layer -eq 3 }
            $layer3.ok                | Should -BeFalse
            $layer3.details.reason    | Should -Be 'shutdown-nonzero-exit'
        }
    }

    It 'records per-task failures and still succeeds overall if at least one task starts' {
        InModuleScope MdmSyncNudge {
            Mock Get-EnterpriseMgmtTasks {
                if ($NamePattern -eq 'PushLaunch') {
                    @(
                        [pscustomobject]@{ TaskName = 'PushLaunch'; TaskPath = '\Microsoft\Windows\EnterpriseMgmt\AAA\' }
                        [pscustomobject]@{ TaskName = 'PushLaunch'; TaskPath = '\Microsoft\Windows\EnterpriseMgmt\BBB\' }
                    )
                } else { @() }
            }
            # First Start-ScheduledTask call throws (BBB), second succeeds (AAA).
            $script:i = 0
            Mock Start-ScheduledTask {
                $script:i++
                if ($script:i -eq 1) { throw 'Access is denied' }
            }

            $r = Invoke-MdmSyncNudge
            $r.ok        | Should -BeTrue
            $r.method    | Should -Be 'PushLaunch'
            $r.taskCount | Should -Be 1   # one of two
            $layer1 = $r.attempts | Where-Object { $_.layer -eq 1 }
            $layer1.details.triggered | Should -Be 1
            $layer1.details.total     | Should -Be 2
        }
    }
}

# ---------------------------------------------------------------------------
Describe 'Get-EnterpriseMgmtTasks' {

    It 'returns an empty array when Get-ScheduledTask throws' {
        InModuleScope MdmSyncNudge {
            Mock Get-ScheduledTask { throw 'Access denied' }
            $r = Get-EnterpriseMgmtTasks -NamePattern 'PushLaunch'
            # NOTE: `$r | Should -BeOfType [object[]]` is a silent no-op when
            # $r is an empty array (pipeline emits 0 items, Should is never
            # called). Use `-is` + .Count instead, which work safely under
            # Set-StrictMode -Version Latest only when $r is actually an array.
            ($r -is [array]) | Should -BeTrue
            $r.Count         | Should -Be 0
        }
    }

    It 'normalizes a single-object result into a 1-element array' {
        InModuleScope MdmSyncNudge {
            Mock Get-ScheduledTask {
                [pscustomobject]@{
                    TaskName = 'PushLaunch'
                    TaskPath = '\Microsoft\Windows\EnterpriseMgmt\AAA\'
                }
            }
            $r = Get-EnterpriseMgmtTasks -NamePattern 'PushLaunch'
            $r.Count | Should -Be 1
        }
    }

    It 'filters out tasks whose name does not match the pattern' {
        InModuleScope MdmSyncNudge {
            Mock Get-ScheduledTask {
                @(
                    [pscustomobject]@{ TaskName = 'PushLaunch';            TaskPath = '\Microsoft\Windows\EnterpriseMgmt\A\' }
                    [pscustomobject]@{ TaskName = 'Login';                 TaskPath = '\Microsoft\Windows\EnterpriseMgmt\A\' }
                    [pscustomobject]@{ TaskName = 'Schedule #1 created..'; TaskPath = '\Microsoft\Windows\EnterpriseMgmt\A\' }
                )
            }
            (Get-EnterpriseMgmtTasks -NamePattern 'PushLaunch').Count | Should -Be 1
            (Get-EnterpriseMgmtTasks -NamePattern 'Schedule*').Count  | Should -Be 1
            (Get-EnterpriseMgmtTasks -NamePattern '*').Count          | Should -Be 3
        }
    }
}
