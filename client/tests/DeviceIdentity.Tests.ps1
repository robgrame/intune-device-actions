#requires -Version 5.1
<#
.SYNOPSIS
    Pester v5 unit tests for client\DeviceIdentity.psm1.

.DESCRIPTION
    All external dependencies (registry, log files, dsregcmd, cert store) are
    mocked via Pester so the tests are hermetic and can run on any host —
    including dev machines that are not Entra/Intune enrolled, or CI runners.

    Run with:
        powershell.exe -NoProfile -File .\client\tests\Invoke-Tests.ps1
    or directly:
        Invoke-Pester -Path .\client\tests\DeviceIdentity.Tests.ps1 -Output Detailed
#>

BeforeAll {
    $script:ModulePath = Resolve-Path (Join-Path $PSScriptRoot '..\DeviceIdentity.psm1')
    Import-Module $script:ModulePath -Force -DisableNameChecking
}

AfterAll {
    Remove-Module DeviceIdentity -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------------------
Describe 'Get-EntraDeviceId' {

    It 'returns the GUID found in dsregcmd output' {
        InModuleScope DeviceIdentity {
            Mock dsregcmd.exe {
                $global:LASTEXITCODE = 0
                @(
                    '+----------------------------------------------------------------------+'
                    '|                Device State                                          |'
                    '+----------------------------------------------------------------------+'
                    '             AzureAdJoined : YES'
                    '          EnterpriseJoined : NO'
                    '              DomainJoined : NO'
                    '                  DeviceId : 01b0de08-d540-4a02-beb8-067da555f345'
                    '                Thumbprint : ABCDEF...'
                )
            }
            Get-EntraDeviceId | Should -Be '01b0de08-d540-4a02-beb8-067da555f345'
        }
    }

    It 'throws when dsregcmd exits non-zero' {
        InModuleScope DeviceIdentity {
            Mock dsregcmd.exe { $global:LASTEXITCODE = 1; $null }
            { Get-EntraDeviceId } | Should -Throw -ExpectedMessage '*dsregcmd failed*'
        }
    }

    It 'throws when output contains no DeviceId line' {
        InModuleScope DeviceIdentity {
            Mock dsregcmd.exe {
                $global:LASTEXITCODE = 0
                @('+--- Device State ---+', 'AzureAdJoined : NO', 'DomainJoined : YES')
            }
            { Get-EntraDeviceId } | Should -Throw -ExpectedMessage '*EntraDeviceId not found*'
        }
    }
}

# ---------------------------------------------------------------------------
Describe 'Get-MdmEnrollmentId' {

    It 'throws when the Enrollments key is missing' {
        InModuleScope DeviceIdentity {
            Mock Test-Path { $false } -ParameterFilter { $Path -eq 'HKLM:\SOFTWARE\Microsoft\Enrollments' }
            { Get-MdmEnrollmentId } | Should -Throw -ExpectedMessage '*Enrollments key not found*'
        }
    }

    It 'returns DeviceClientId when present on the matching enrollment' {
        InModuleScope DeviceIdentity {
            Mock Test-Path { $true } -ParameterFilter { $Path -eq 'HKLM:\SOFTWARE\Microsoft\Enrollments' }
            Mock Get-ChildItem {
                [pscustomobject]@{
                    PSChildName = 'AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE'
                    PSPath      = 'HKLM:\SOFTWARE\Microsoft\Enrollments\AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE'
                }
            } -ParameterFilter { "$Path" -eq 'HKLM:\SOFTWARE\Microsoft\Enrollments' }
            Mock Get-ItemProperty {
                [pscustomobject]@{
                    ProviderID     = 'MS DM Server'
                    UPN            = 'user@example.com'
                    DeviceClientId = '16A44AED-644C-49E1-80E4-6E5780B5EAA4'
                }
            }
            Get-MdmEnrollmentId | Should -Be '16A44AED-644C-49E1-80E4-6E5780B5EAA4'
        }
    }

    It 'falls back to the enrollment key GUID when DeviceClientId is absent' {
        InModuleScope DeviceIdentity {
            Mock Test-Path { $true } -ParameterFilter { $Path -eq 'HKLM:\SOFTWARE\Microsoft\Enrollments' }
            Mock Get-ChildItem {
                [pscustomobject]@{
                    PSChildName = 'AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE'
                    PSPath      = 'HKLM:\SOFTWARE\Microsoft\Enrollments\AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE'
                }
            } -ParameterFilter { "$Path" -eq 'HKLM:\SOFTWARE\Microsoft\Enrollments' }
            Mock Get-ItemProperty {
                [pscustomobject]@{
                    ProviderID = 'MS DM Server'
                    UPN        = 'user@example.com'
                }
            }
            Get-MdmEnrollmentId | Should -Be 'AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE'
        }
    }

    It 'skips non-GUID subkeys (e.g. EnrollmentType)' {
        InModuleScope DeviceIdentity {
            Mock Test-Path { $true } -ParameterFilter { $Path -eq 'HKLM:\SOFTWARE\Microsoft\Enrollments' }
            Mock Get-ChildItem {
                @(
                    [pscustomobject]@{ PSChildName = 'EnrollmentType'; PSPath = 'x' }
                    [pscustomobject]@{
                        PSChildName = 'AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE'
                        PSPath      = 'HKLM:\SOFTWARE\Microsoft\Enrollments\AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE'
                    }
                )
            } -ParameterFilter { "$Path" -eq 'HKLM:\SOFTWARE\Microsoft\Enrollments' }
            Mock Get-ItemProperty {
                [pscustomobject]@{ ProviderID='MS DM Server'; UPN='u@x'; DeviceClientId='16A44AED-644C-49E1-80E4-6E5780B5EAA4' }
            }
            Get-MdmEnrollmentId | Should -Be '16A44AED-644C-49E1-80E4-6E5780B5EAA4'
        }
    }

    It 'skips enrollments whose ProviderID is not MS DM Server' {
        InModuleScope DeviceIdentity {
            Mock Test-Path { $true } -ParameterFilter { $Path -eq 'HKLM:\SOFTWARE\Microsoft\Enrollments' }
            Mock Get-ChildItem {
                [pscustomobject]@{
                    PSChildName = 'AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE'
                    PSPath      = 'HKLM:\...\AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE'
                }
            } -ParameterFilter { "$Path" -eq 'HKLM:\SOFTWARE\Microsoft\Enrollments' }
            Mock Get-ItemProperty {
                [pscustomobject]@{ ProviderID = 'SomeOtherMDM'; UPN = 'u@x'; DeviceClientId = '...' }
            }
            { Get-MdmEnrollmentId } | Should -Throw -ExpectedMessage '*Intune enrollment not found*'
        }
    }
}

# ---------------------------------------------------------------------------
Describe 'Get-IntuneManagedDeviceId' {

    It 'returns the IntuneDeviceId from the IME registry key when present' {
        InModuleScope DeviceIdentity {
            Mock Test-Path { $true } -ParameterFilter { $Path -eq 'HKLM:\SOFTWARE\Microsoft\IntuneManagementExtension' }
            Mock Test-Path { $false }
            Mock Get-ItemProperty {
                [pscustomobject]@{
                    IntuneDeviceId  = 'a8fa102a-1e88-4e71-8df0-37a09d570a72'
                    ManagedDeviceId = 'should-not-be-used'
                }
            }
            Get-IntuneManagedDeviceId | Should -Be 'a8fa102a-1e88-4e71-8df0-37a09d570a72'
        }
    }

    It 'falls back to ManagedDeviceId when IntuneDeviceId missing' {
        InModuleScope DeviceIdentity {
            Mock Test-Path { $true } -ParameterFilter { $Path -eq 'HKLM:\SOFTWARE\Microsoft\IntuneManagementExtension' }
            Mock Test-Path { $false }
            Mock Get-ItemProperty {
                [pscustomobject]@{ ManagedDeviceId = 'a8fa102a-1e88-4e71-8df0-37a09d570a72' }
            }
            Get-IntuneManagedDeviceId | Should -Be 'a8fa102a-1e88-4e71-8df0-37a09d570a72'
        }
    }

    It 'parses managedDevice.id from the IME log file when registry has nothing' {
        InModuleScope DeviceIdentity {
            Mock Test-Path { $false } -ParameterFilter { $Path -eq 'HKLM:\SOFTWARE\Microsoft\IntuneManagementExtension' }
            Mock Test-Path { $true }
            $fakeMatch = [pscustomobject]@{
                Matches = @( [pscustomobject]@{ Groups = @( $null, [pscustomobject]@{ Value = 'a8fa102a-1e88-4e71-8df0-37a09d570a72' } ) } )
            }
            Mock Select-String { $fakeMatch }
            Get-IntuneManagedDeviceId | Should -Be 'a8fa102a-1e88-4e71-8df0-37a09d570a72'
        }
    }

    It 'returns $null when neither registry nor log yield a GUID' {
        InModuleScope DeviceIdentity {
            Mock Test-Path { $false }
            Get-IntuneManagedDeviceId | Should -BeNullOrEmpty
        }
    }

    It 'ignores registry values that are not GUID-shaped' {
        InModuleScope DeviceIdentity {
            Mock Test-Path { $true } -ParameterFilter { $Path -eq 'HKLM:\SOFTWARE\Microsoft\IntuneManagementExtension' }
            Mock Test-Path { $false }
            Mock Get-ItemProperty { [pscustomobject]@{ IntuneDeviceId = 'not-a-guid' } }
            Get-IntuneManagedDeviceId | Should -BeNullOrEmpty
        }
    }
}

# ---------------------------------------------------------------------------
Describe 'Get-ClientCertificate' {

    BeforeAll {
        # Define the helper *inside* the module's scope so it is visible from
        # every InModuleScope block below (Pester v5 isolates module scope
        # from file scope, so a plain BeforeAll function would be unreachable).
        InModuleScope DeviceIdentity {
            function script:New-FakeCert {
                param(
                    [string]$Thumb     = '27042182AAB835797735EE942C2C9EFA3ADDDC2B',
                    [string]$Subject   = 'CN=FC1DSK005.mslabs.local',
                    [string]$Issuer    = 'CN=MSLABS-SUBCA01, DC=mslabs, DC=local',
                    [datetime]$NotBefore = (Get-Date).AddDays(-30),
                    [datetime]$NotAfter  = (Get-Date).AddDays(365),
                    [bool]$HasPrivateKey = $true,
                    [bool]$HasClientAuthEku = $true
                )
                $ekus = if ($HasClientAuthEku) {
                    @([pscustomobject]@{ ObjectId = '1.3.6.1.5.5.7.3.2'; FriendlyName = 'Client Authentication' })
                } else {
                    @([pscustomobject]@{ ObjectId = '1.3.6.1.5.5.7.3.1'; FriendlyName = 'Server Authentication' })
                }
                [pscustomobject]@{
                    Thumbprint           = $Thumb
                    Subject              = $Subject
                    Issuer               = $Issuer
                    NotBefore            = $NotBefore
                    NotAfter             = $NotAfter
                    HasPrivateKey        = $HasPrivateKey
                    EnhancedKeyUsageList = $ekus
                }
            }
        }
    }

    It 'returns the matching cert by exact thumbprint (case-insensitive)' {
        InModuleScope DeviceIdentity {
            Mock Get-ChildItem {
                @(
                    New-FakeCert -Thumb 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'
                    New-FakeCert -Thumb '27042182AAB835797735EE942C2C9EFA3ADDDC2B'
                )
            } -ParameterFilter { "$Path" -like 'Cert:\*' }
            $c = Get-ClientCertificate -Thumb '27042182aab835797735ee942c2c9efa3adddc2b'
            $c.Thumbprint | Should -Be '27042182AAB835797735EE942C2C9EFA3ADDDC2B'
        }
    }

    It 'filters out certs missing the Client Authentication EKU' {
        InModuleScope DeviceIdentity {
            Mock Get-ChildItem {
                @( New-FakeCert -HasClientAuthEku $false )
            } -ParameterFilter { "$Path" -like 'Cert:\*' }
            { Get-ClientCertificate } | Should -Throw -ExpectedMessage '*Client certificate not found*'
        }
    }

    It 'filters out expired certs' {
        InModuleScope DeviceIdentity {
            Mock Get-ChildItem {
                @( New-FakeCert -NotAfter (Get-Date).AddDays(-1) )
            } -ParameterFilter { "$Path" -like 'Cert:\*' }
            { Get-ClientCertificate } | Should -Throw -ExpectedMessage '*Client certificate not found*'
        }
    }

    It 'filters out certs without a private key' {
        InModuleScope DeviceIdentity {
            Mock Get-ChildItem {
                @( New-FakeCert -HasPrivateKey $false )
            } -ParameterFilter { "$Path" -like 'Cert:\*' }
            { Get-ClientCertificate } | Should -Throw -ExpectedMessage '*Client certificate not found*'
        }
    }

    It 'applies single-pattern IssuerLike filter' {
        InModuleScope DeviceIdentity {
            Mock Get-ChildItem {
                @(
                    New-FakeCert -Thumb 'AAAA' -Issuer 'CN=Other-CA'
                    New-FakeCert -Thumb 'BBBB' -Issuer 'CN=MSLABS-SUBCA01, DC=mslabs'
                )
            } -ParameterFilter { "$Path" -like 'Cert:\*' }
            $c = Get-ClientCertificate -IssuerLike '*MSLABS-SUBCA01*'
            $c.Thumbprint | Should -Be 'BBBB'
        }
    }

    It 'applies multi-pattern IssuerLike filter (semicolon-separated, OR semantics)' {
        InModuleScope DeviceIdentity {
            Mock Get-ChildItem {
                @(
                    New-FakeCert -Thumb 'AAAA' -Issuer 'CN=Acme-Root'
                    New-FakeCert -Thumb 'BBBB' -Issuer 'CN=MSLABS-ADCS, DC=mslabs'
                )
            } -ParameterFilter { "$Path" -like 'Cert:\*' }
            $c = Get-ClientCertificate -IssuerLike '*MSLABS-SUBCA01*;*MSLABS-ADCS*'
            $c.Thumbprint | Should -Be 'BBBB'
        }
    }

    It 'returns the longest-living cert when no Thumb/Subject specified' {
        InModuleScope DeviceIdentity {
            $old = New-FakeCert -Thumb 'OLD'  -NotAfter (Get-Date).AddDays(10)
            $new = New-FakeCert -Thumb 'NEW'  -NotAfter (Get-Date).AddDays(500)
            Mock Get-ChildItem { @($old, $new) } -ParameterFilter { "$Path" -like 'Cert:\*' }
            (Get-ClientCertificate).Thumbprint | Should -Be 'NEW'
        }
    }

    It 'matches by SubjectLike wildcard when Thumb not supplied' {
        InModuleScope DeviceIdentity {
            Mock Get-ChildItem {
                @(
                    New-FakeCert -Thumb 'A1' -Subject 'CN=other.host'
                    New-FakeCert -Thumb 'B2' -Subject 'CN=FC1DSK005.mslabs.local'
                )
            } -ParameterFilter { "$Path" -like 'Cert:\*' }
            (Get-ClientCertificate -SubjectLike '*FC1DSK005*').Thumbprint | Should -Be 'B2'
        }
    }

    It 'throws when no cert satisfies all filters' {
        InModuleScope DeviceIdentity {
            Mock Get-ChildItem { @() } -ParameterFilter { "$Path" -like 'Cert:\*' }
            { Get-ClientCertificate -Thumb 'ZZZZ' } | Should -Throw -ExpectedMessage '*Client certificate not found*'
        }
    }
}

# ---------------------------------------------------------------------------
Describe 'Safe variants' {

    It 'Get-EntraDeviceIdSafe returns "n/a" instead of throwing' {
        InModuleScope DeviceIdentity {
            Mock dsregcmd.exe { $global:LASTEXITCODE = 1; $null }
            Get-EntraDeviceIdSafe | Should -Be 'n/a'
        }
    }

    It 'Get-MdmEnrollmentIdSafe returns "n/a" when registry missing' {
        InModuleScope DeviceIdentity {
            Mock Test-Path { $false }
            Get-MdmEnrollmentIdSafe | Should -Be 'n/a'
        }
    }

    It 'Get-IntuneManagedDeviceIdSafe returns "n/a" when nothing derivable' {
        InModuleScope DeviceIdentity {
            Mock Test-Path { $false }
            Get-IntuneManagedDeviceIdSafe | Should -Be 'n/a'
        }
    }

    It 'Get-IntuneManagedDeviceIdSafe returns the GUID when registry has it' {
        InModuleScope DeviceIdentity {
            Mock Test-Path { $true } -ParameterFilter { $Path -eq 'HKLM:\SOFTWARE\Microsoft\IntuneManagementExtension' }
            Mock Test-Path { $false }
            Mock Get-ItemProperty { [pscustomobject]@{ IntuneDeviceId = 'a8fa102a-1e88-4e71-8df0-37a09d570a72' } }
            Get-IntuneManagedDeviceIdSafe | Should -Be 'a8fa102a-1e88-4e71-8df0-37a09d570a72'
        }
    }
}
