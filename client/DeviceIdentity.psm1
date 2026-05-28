<#
.SYNOPSIS
    Canonical device identity helpers for the Intune Wipe client.

.DESCRIPTION
    Functions in this module resolve device identifiers and the client
    certificate used for mTLS, with strict (throwing) variants intended for
    the production flow and "Safe" variants intended for the user-mode
    confirmation dialog (which must never throw — it returns 'n/a' instead).

    The module is dot-source-free so it can be loaded by:
      - client\Invoke-DeviceWipe.ps1                              (dev / standalone)
      - client\intune-win32-package\source\Invoke-DeviceWipe.ps1  (Win32 packaged copy)
      - client\intune-win32-package\source\Launch-Wipe.ps1        (user-mode dialog)

    All registry, log, dsregcmd, and certificate store accesses go through
    PowerShell cmdlets (Get-ChildItem, Get-ItemProperty, Test-Path,
    Select-String) that can be mocked by Pester for unit testing — see
    client\tests\DeviceIdentity.Tests.ps1.

.NOTES
    PS 5.1 compatible. No external dependencies.
#>

Set-StrictMode -Version Latest

# --- Strict (throwing) variants used by the production wipe flow --------------

function Get-EntraDeviceId {
    <#
    .SYNOPSIS
        Returns the Entra (Azure AD) device id GUID for the local machine.
    .OUTPUTS
        [string] GUID (36 chars, hex with dashes).
    .NOTES
        Wraps `dsregcmd /status`. Throws on failure to extract the DeviceId.
    #>
    [CmdletBinding()]
    param()

    $out = & dsregcmd.exe /status 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $out) { throw "dsregcmd failed" }
    $line = $out | Where-Object { $_ -match '^\s*DeviceId\s*:\s*([0-9a-fA-F-]{36})' } | Select-Object -First 1
    if ($line -match '([0-9a-fA-F-]{36})') { return $Matches[1] }
    throw "EntraDeviceId not found (device not Entra joined/registered?)"
}

function Get-MdmEnrollmentId {
    <#
    .SYNOPSIS
        Returns the MDM EnrollmentId / DeviceClientId from registry.
    .DESCRIPTION
        This is NOT the Intune managedDevice.id used by Graph; it is kept
        for diagnostic display and audit trail. The backend resolves the
        real managedDevice.id from EntraDeviceId.
    .OUTPUTS
        [string] GUID — DeviceClientId value when present, else the enrollment
        key GUID.
    #>
    [CmdletBinding()]
    param()

    $root = 'HKLM:\SOFTWARE\Microsoft\Enrollments'
    if (-not (Test-Path $root)) { throw "Enrollments key not found" }
    foreach ($e in (Get-ChildItem $root -ErrorAction SilentlyContinue |
                    Where-Object { $_.PSChildName -match '^[0-9A-Fa-f-]{36}$' })) {
        $p = Get-ItemProperty $e.PSPath -ErrorAction SilentlyContinue
        if (-not $p) { continue }
        $hasProvider = ($p.PSObject.Properties.Name -contains 'ProviderID')
        $hasUpn      = ($p.PSObject.Properties.Name -contains 'UPN')
        if ($hasProvider -and $p.ProviderID -eq 'MS DM Server' -and $hasUpn -and $p.UPN) {
            if ($p.PSObject.Properties.Name -contains 'DeviceClientId' -and $p.DeviceClientId) {
                return [string]$p.DeviceClientId
            }
            return [string]$e.PSChildName
        }
    }
    throw "Intune enrollment not found (device not enrolled in Intune?)"
}

function Get-IntuneManagedDeviceId {
    <#
    .SYNOPSIS
        Best-effort lookup of the real Intune managedDevice.id (Graph resource id).
    .DESCRIPTION
        Tries (in order):
          1. HKLM:\SOFTWARE\Microsoft\IntuneManagementExtension registry —
             values IntuneDeviceId / ManagedDeviceId / DeviceId.
          2. $env:ProgramData\Microsoft\IntuneManagementExtension\Logs\
             IntuneManagementExtension.log — Select-String against
             "Intune Device Id : <GUID>" pattern.
        Returns $null when not derivable locally. The backend resolves the
        real id from EntraDeviceId regardless, so this is purely for display
        and operator diagnostics.
    .OUTPUTS
        [string] GUID, or $null.
    #>
    [CmdletBinding()]
    param()

    $imeKey = 'HKLM:\SOFTWARE\Microsoft\IntuneManagementExtension'
    if (Test-Path $imeKey) {
        $p = Get-ItemProperty $imeKey -ErrorAction SilentlyContinue
        if ($p) {
            foreach ($n in 'IntuneDeviceId','ManagedDeviceId','DeviceId') {
                if ($p.PSObject.Properties.Name -contains $n -and $p.$n -match '^[0-9a-fA-F-]{36}$') {
                    return [string]$p.$n
                }
            }
        }
    }

    $logPath = Join-Path $env:ProgramData 'Microsoft\IntuneManagementExtension\Logs\IntuneManagementExtension.log'
    if (Test-Path $logPath) {
        try {
            $hits = Select-String -Path $logPath -Pattern 'Intune\s*Device\s*Id\D+([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})' -ErrorAction SilentlyContinue
            $last = $hits | Select-Object -Last 1
            if ($last) { return [string]$last.Matches[0].Groups[1].Value }
        } catch { }
    }

    return $null
}

function Get-ClientCertificate {
    <#
    .SYNOPSIS
        Selects the mTLS client certificate from the local cert stores.
    .DESCRIPTION
        Looks in LocalMachine\My first, then CurrentUser\My. Filters by:
          - private key present
          - valid date range
          - Client Authentication EKU (1.3.6.1.5.5.7.3.2)
          - optional Thumbprint (exact match) OR SubjectLike (wildcard)
          - optional IssuerLike: semicolon-separated wildcard list — cert
            matches if ANY pattern matches its issuer (multi-CA support).
        When no Thumb/SubjectLike provided, returns the certificate with the
        longest NotAfter remaining.
    .PARAMETER Thumb
        Exact SHA-1 thumbprint (case-insensitive).
    .PARAMETER SubjectLike
        Wildcard pattern matched against the Subject DN.
    .PARAMETER IssuerLike
        Semicolon-separated list of wildcard patterns matched against the
        Issuer DN (e.g. "*MSLABS-SUBCA01*;*MSLABS-ADCS*"). At least one must
        match (when supplied).
    .OUTPUTS
        [System.Security.Cryptography.X509Certificates.X509Certificate2]
    #>
    [CmdletBinding()]
    param(
        [string]$Thumb,
        [string]$SubjectLike,
        [string]$IssuerLike
    )

    $issuerPatterns = @()
    if ($IssuerLike) {
        $issuerPatterns = @($IssuerLike -split ';' |
                            ForEach-Object { $_.Trim() } |
                            Where-Object { $_ })
    }

    foreach ($s in @('Cert:\LocalMachine\My','Cert:\CurrentUser\My')) {
        $certs = Get-ChildItem $s -ErrorAction SilentlyContinue |
                 Where-Object { $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) -and $_.NotBefore -le (Get-Date) }

        $certs = $certs | Where-Object {
            $ekus = $_.EnhancedKeyUsageList
            (-not $ekus) -or ($ekus | Where-Object { $_.ObjectId -eq '1.3.6.1.5.5.7.3.2' })
        }

        if ($issuerPatterns.Count -gt 0) {
            $certs = $certs | Where-Object {
                $issuer = $_.Issuer
                $matched = $false
                foreach ($pat in $issuerPatterns) {
                    if ($issuer -like $pat) { $matched = $true; break }
                }
                $matched
            }
        }

        if ($Thumb) {
            $c = $certs | Where-Object Thumbprint -eq $Thumb.ToUpper() | Select-Object -First 1
        } elseif ($SubjectLike) {
            $c = $certs | Where-Object { $_.Subject -like $SubjectLike } |
                 Sort-Object NotAfter -Descending | Select-Object -First 1
        } else {
            $c = $certs | Sort-Object NotAfter -Descending | Select-Object -First 1
        }
        if ($c) { return $c }
    }
    throw "Client certificate not found (with Client Authentication EKU and private key)"
}

# --- Safe (non-throwing) variants used by the user-mode dialog ---------------

function Get-EntraDeviceIdSafe {
    try { return (Get-EntraDeviceId) } catch { return 'n/a' }
}

function Get-MdmEnrollmentIdSafe {
    try { return (Get-MdmEnrollmentId) } catch { return 'n/a' }
}

function Get-IntuneManagedDeviceIdSafe {
    try {
        $v = Get-IntuneManagedDeviceId
        if ($v) { return $v }
    } catch { }
    return 'n/a'
}

Export-ModuleMember -Function `
    Get-EntraDeviceId, `
    Get-MdmEnrollmentId, `
    Get-IntuneManagedDeviceId, `
    Get-ClientCertificate, `
    Get-EntraDeviceIdSafe, `
    Get-MdmEnrollmentIdSafe, `
    Get-IntuneManagedDeviceIdSafe
