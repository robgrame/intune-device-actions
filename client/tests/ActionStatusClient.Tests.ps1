#requires -Version 5.1

BeforeAll {
    $script:ModulePath = Resolve-Path (Join-Path $PSScriptRoot '..\ActionStatusClient.psm1')
    Import-Module $script:ModulePath -Force -DisableNameChecking
}

AfterAll {
    Remove-Module ActionStatusClient -Force -ErrorAction SilentlyContinue
}

Describe 'Get-ActionStatusUrl' {
    It 'appends /status/{correlationId} to the canonical actions endpoint' {
        InModuleScope ActionStatusClient {
            Get-ActionStatusUrl -ApiUrl 'https://host/api/actions/' -CorrelationId 'abc-123' |
                Should -Be 'https://host/api/actions/status/abc-123'
        }
    }
}

Describe 'Resolve-ActionStatusMonitoringOptions' {
    It 'defaults to 5 seconds and 30 minutes' {
        InModuleScope ActionStatusClient {
            $opts = Resolve-ActionStatusMonitoringOptions
            $opts.IntervalSeconds | Should -Be 5
            $opts.MaxMinutes | Should -Be 30
        }
    }

    It 'reads values from config when explicit parameters are absent' {
        InModuleScope ActionStatusClient {
            $cfg = [pscustomobject]@{
                StatusPollIntervalSeconds = 9
                StatusPollMaxMinutes = 44
            }
            $opts = Resolve-ActionStatusMonitoringOptions -Config $cfg
            $opts.IntervalSeconds | Should -Be 9
            $opts.MaxMinutes | Should -Be 44
        }
    }

    It 'prefers explicit parameters over config values' {
        InModuleScope ActionStatusClient {
            $cfg = [pscustomobject]@{
                StatusPollIntervalSeconds = 9
                StatusPollMaxMinutes = 44
            }
            $opts = Resolve-ActionStatusMonitoringOptions -Config $cfg -IntervalSeconds 5 -MaxMinutes 30
            $opts.IntervalSeconds | Should -Be 5
            $opts.MaxMinutes | Should -Be 30
        }
    }
}

Describe 'Wait-ActionStatus' {
    It 'returns terminal status and preserves the default 5-second cadence contract' {
        InModuleScope ActionStatusClient {
            $updates = New-Object System.Collections.Generic.List[object]
            $calls = 0

            Mock Invoke-RestMethod {
                $script:calls++
                if ($script:calls -eq 1) {
                    return [pscustomobject]@{ state = 'pending'; terminal = $false }
                }
                return [pscustomobject]@{ state = 'done'; terminal = $true }
            }

            Mock Start-Sleep {}

            $result = Wait-ActionStatus `
                -ApiUrl 'https://host/api/actions' `
                -CorrelationId 'corr-1' `
                -Certificate ([pscustomobject]@{ Thumbprint = 'thumb' }) `
                -FunctionKey 'key' `
                -IntervalSeconds 5 `
                -MaxMinutes 1 `
                -OnUpdate { param($u) [void]$updates.Add($u) }

            $result.LocalState | Should -Be 'terminal'
            ([string]$result.Snapshot.state) | Should -Be 'done'
            $updates.Count | Should -Be 2
            Assert-MockCalled Start-Sleep -Times 1 -Exactly -ParameterFilter { $Seconds -eq 5 }
        }
    }
}
