<#
.SYNOPSIS
    Issues a device-rename request against the intune-device-actions API.

.DESCRIPTION
    Posts a `device-rename` action to the mTLS-protected `/api/v2/actions`
    endpoint. The API forwards to a dedicated Rename Function App which calls
    the customer-internal REST endpoint with the device serial number and the
    desired new name.

    The serial number can be sourced from local hardware (default) or passed
    explicitly via -SerialNumber.

.PARAMETER ApiBaseUrl
    Full URL of the actions endpoint, e.g. https://idactions-web.azurewebsites.net/api/v2/actions

.PARAMETER ClientCertThumbprint
    SHA1 thumbprint of the client certificate enrolled in the API's allow-list
    (matches one of the `trustedCaThumbprints` configured in bicep).

.PARAMETER NewName
    Desired new device name. The customer endpoint is the authority for
    validation/normalization.

.PARAMETER SerialNumber
    Hardware serial number to send. When omitted, the script reads the local
    BIOS serial via WMI (Win32_BIOS).

.PARAMETER IntuneDeviceId
    Intune managed-device id. Optional — when omitted, the API derives it from
    the client certificate / device claims.

.PARAMETER EntraDeviceId
    Entra (Azure AD) device id. Optional — same fallback as IntuneDeviceId.

.EXAMPLE
    .\Invoke-RenameDevice.ps1 `
        -ApiBaseUrl 'https://idactions-web.azurewebsites.net/api/v2/actions' `
        -ClientCertThumbprint 'AB12CD34EF56...' `
        -NewName 'WS-CONTOSO-101'

.EXAMPLE
    .\Invoke-RenameDevice.ps1 -ApiBaseUrl ... -ClientCertThumbprint ... `
        -NewName 'WS-DEV-007' -SerialNumber 'PF3X9ABC'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ApiBaseUrl,
    [Parameter(Mandatory)] [string] $ClientCertThumbprint,
    [Parameter(Mandatory)] [string] $NewName,
    [string] $SerialNumber,
    [string] $IntuneDeviceId,
    [string] $EntraDeviceId
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $SerialNumber) {
    $bios = Get-CimInstance -ClassName Win32_BIOS -ErrorAction Stop
    $SerialNumber = ($bios.SerialNumber ?? '').Trim()
    if (-not $SerialNumber) {
        throw "Could not read BIOS serial number; pass -SerialNumber explicitly."
    }
    Write-Verbose "Resolved local serial: $SerialNumber"
}

$cert = Get-Item -Path "Cert:\LocalMachine\My\$ClientCertThumbprint" -ErrorAction SilentlyContinue
if (-not $cert) {
    $cert = Get-Item -Path "Cert:\CurrentUser\My\$ClientCertThumbprint" -ErrorAction SilentlyContinue
}
if (-not $cert) {
    throw "Client certificate $ClientCertThumbprint not found in LocalMachine\My or CurrentUser\My."
}

$deviceName = $env:COMPUTERNAME
$body = [ordered]@{
    actionType  = 'device-rename'
    deviceName  = $deviceName
    rename      = [ordered]@{
        serialNumber = $SerialNumber
        newName      = $NewName
    }
}
if ($IntuneDeviceId) { $body.intuneDeviceId = $IntuneDeviceId }
if ($EntraDeviceId)  { $body.entraDeviceId  = $EntraDeviceId }

$json = $body | ConvertTo-Json -Depth 6 -Compress
Write-Verbose "POST $ApiBaseUrl"
Write-Verbose "Body: $json"

$resp = Invoke-RestMethod -Method Post -Uri $ApiBaseUrl `
    -Body $json -ContentType 'application/json' `
    -Certificate $cert -ErrorAction Stop

$resp | ConvertTo-Json -Depth 6
