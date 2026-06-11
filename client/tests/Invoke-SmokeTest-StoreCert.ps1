<#
.SYNOPSIS
    E2E smoke test using a client cert loaded from the Windows certificate store
    (LocalMachine\My) by thumbprint, instead of the smoke-PKI PFX.

.DESCRIPTION
    Non-destructive round-trip:
      1. GET  /api/actions/status/<random>      → expect 404 (mTLS path proven)
      2. POST /api/actions {actionType,...}      → expect 202 (Service Bus enqueue)
      3. GET  /api/actions/status/<corr>         → poll until terminal (denied:* / failed:*)

    Wipe is excluded by default (defaults to bitlocker-rotate). The action runs
    against a fictitious EntraDeviceId so Graph will 404 downstream and the
    runner writes a terminal denial row — no real device is touched.

.PARAMETER FunctionAppHost
    FQDN of the Web Function App. Defaults to the public-network dev app.

.PARAMETER CertThumbprint
    Client cert thumbprint in LocalMachine\My. Must be reachable via mTLS chain
    against ClientCert:TrustedRootCertificates / TrustedIntermediateCertificates.

.PARAMETER CertStoreLocation
    Cert store location. Defaults to LocalMachine. Use CurrentUser if the cert
    lives in the user store.

.PARAMETER ActionType
    bitlocker-rotate | device-rename | autopilot-register. NOT wipe.

.PARAMETER ResourceGroup / FunctionAppName
    Used to fetch the function key via az CLI when -FunctionKey is omitted.

.EXAMPLE
    # default: bitlocker-rotate round-trip against idactions-web-dev
    .\Invoke-SmokeTest-StoreCert.ps1 -CertThumbprint B83225C63108B394EFD50D8511386D7DDC90210F

.NOTES
    Requires PowerShell 7.x (uses Invoke-WebRequest -Certificate). LocalMachine
    cert access usually requires an elevated shell.
#>
[CmdletBinding()]
param(
    [Parameter()] [string] $FunctionAppHost   = 'idactions-web-dev.azurewebsites.net',
    [Parameter(Mandatory)] [string] $CertThumbprint,
    [Parameter()] [ValidateSet('LocalMachine','CurrentUser')] [string] $CertStoreLocation = 'LocalMachine',
    [Parameter()] [ValidateSet('bitlocker-rotate','device-rename','autopilot-register')] [string] $ActionType = 'bitlocker-rotate',
    [Parameter()] [string] $FunctionKey,
    [Parameter()] [string] $ResourceGroup    = 'RG-INTUNE-DEVICEACTIONS',
    [Parameter()] [string] $FunctionAppName  = 'idactions-web-dev',
    [Parameter()] [string] $BoundEntraDeviceId,
    [Parameter()] [int]    $PollTimeoutSec   = 90
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "PowerShell 7+ required (Invoke-WebRequest -Certificate). Current: $($PSVersionTable.PSVersion)"
}

if ($ActionType -eq 'wipe') {
    throw "wipe is intentionally excluded from this smoke test."
}

$thumb = ($CertThumbprint -replace '[^0-9a-fA-F]', '').ToUpperInvariant()
$storePath = "Cert:\$CertStoreLocation\My\$thumb"
Write-Host "==> Loading cert $thumb from $storePath"
if (-not (Test-Path $storePath)) { throw "Cert not found at $storePath" }
$leaf = Get-Item $storePath
if (-not $leaf.HasPrivateKey) { throw "Cert $thumb has no private key — cannot mTLS-authenticate." }
Write-Host "    Subject : $($leaf.Subject)"
Write-Host "    Issuer  : $($leaf.Issuer)"
Write-Host "    NotAfter: $($leaf.NotAfter)"

if (-not $FunctionKey) {
    Write-Host "==> Fetching function key for $FunctionAppName via az CLI..."
    $FunctionKey = az functionapp keys list -g $ResourceGroup -n $FunctionAppName --query 'functionKeys.default' -o tsv 2>$null
    if (-not $FunctionKey) {
        $FunctionKey = az functionapp keys list -g $ResourceGroup -n $FunctionAppName --query 'masterKey' -o tsv
    }
    if (-not $FunctionKey) { throw "Could not retrieve function key. Pass -FunctionKey explicitly." }
}

function New-AuthHeaders {
    @{
        'x-functions-key'      = $FunctionKey
        'X-Request-Timestamp'  = (Get-Date).ToUniversalTime().ToString('o')
        'X-Request-Nonce'      = [Guid]::NewGuid().ToString()
    }
}

function Invoke-Status {
    param([string]$Corr, [string]$Hint)
    $uri = "https://$FunctionAppHost/api/actions/status/$Corr"
    Write-Host "==> GET $uri  ($Hint)"
    try {
        $resp = Invoke-WebRequest -Uri $uri -Method Get -Headers (New-AuthHeaders) -Certificate $leaf -UseBasicParsing -SslProtocol Tls12
        Write-Host "    HTTP $($resp.StatusCode) :: $($resp.Content)" -ForegroundColor Green
        return [pscustomobject]@{ Status = [int]$resp.StatusCode; Body = $resp.Content }
    }
    catch {
        $sc  = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { -1 }
        $msg = if ($_.ErrorDetails)       { $_.ErrorDetails.Message }               else { $_.Exception.Message }
        $color = if ($sc -in 404, 200) { 'Yellow' } else { 'Red' }
        Write-Host "    HTTP $sc :: $msg" -ForegroundColor $color
        return [pscustomobject]@{ Status = $sc; Body = $msg }
    }
}

# Fictitious device identifiers — Graph will 404 downstream (expected).
# If -BoundEntraDeviceId is supplied (matches a ClientCert:ThumbprintToDeviceMap
# entry on the server), we use it so the cert↔device binding check passes.
$fakeEntra  = if ($BoundEntraDeviceId) { $BoundEntraDeviceId } else { [Guid]::NewGuid().ToString() }
$fakeIntune = [Guid]::NewGuid().ToString()
$fakeName   = "smoke-$($fakeEntra.Substring(0,8))"

Write-Host ""
Write-Host "── STEP 1/3: Status on unknown correlationId (expect 404) ───────"
$r1 = Invoke-Status -Corr ([Guid]::NewGuid().ToString('N')) -Hint 'expect 404'
if ($r1.Status -ne 404) {
    if ($r1.Status -in 401, 403) {
        throw "STEP 1 failed: HTTP $($r1.Status). mTLS auth rejected the cert. Check thumbprint allow-list / chain trust."
    }
    throw "STEP 1 failed: expected 404, got $($r1.Status)"
}

Write-Host ""
Write-Host "── STEP 2/3: POST /api/actions actionType=$ActionType (expect 202) ───"
$uri  = "https://$FunctionAppHost/api/actions"
$bodyObj = [ordered]@{
    actionType     = $ActionType
    deviceName     = $fakeName
    entraDeviceId  = $fakeEntra
    intuneDeviceId = $fakeIntune
}
if ($ActionType -eq 'device-rename') { $bodyObj['newDeviceName'] = "${fakeName}-renamed" }
$body = $bodyObj | ConvertTo-Json -Compress
$hdrs = New-AuthHeaders
$hdrs['Content-Type'] = 'application/json'
Write-Host "==> POST $uri"
Write-Host "    body: $body"
try {
    $postResp = Invoke-WebRequest -Uri $uri -Method Post -Headers $hdrs -Certificate $leaf -Body $body -UseBasicParsing -SslProtocol Tls12
}
catch {
    $sc  = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { -1 }
    $msg = if ($_.ErrorDetails)       { $_.ErrorDetails.Message }               else { $_.Exception.Message }
    Write-Host "    HTTP $sc :: $msg" -ForegroundColor Red
    throw "STEP 2 failed: HTTP $sc"
}
if ([int]$postResp.StatusCode -ne 202) { throw "STEP 2 failed: expected 202, got $($postResp.StatusCode)" }
$postObj = $postResp.Content | ConvertFrom-Json
$corr = $postObj.correlationId
Write-Host "    HTTP 202  corr=$corr" -ForegroundColor Green

Write-Host ""
Write-Host "── STEP 3/3: Poll status until terminal (timeout ${PollTimeoutSec}s) ──"
$deadline = (Get-Date).AddSeconds($PollTimeoutSec)
$r3 = $null
do {
    Start-Sleep -Seconds 5
    $r3 = Invoke-Status -Corr $corr -Hint 'polling…'
    if ($r3.Status -eq 200) {
        $snap = $r3.Body | ConvertFrom-Json
        if ($snap.terminal -eq $true) { break }
        Write-Host "    (state=$($snap.state) terminal=$($snap.terminal) — keep polling)" -ForegroundColor DarkGray
    }
} while ((Get-Date) -lt $deadline)

if (-not $r3 -or $r3.Status -ne 200) {
    throw "STEP 3 failed: expected 200 within ${PollTimeoutSec}s (last=$($r3.Status))."
}
$snap = $r3.Body | ConvertFrom-Json

Write-Host ""
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host " SMOKE TEST PASSED — full pipeline validated end-to-end" -ForegroundColor Green
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host "  action      = $ActionType"
Write-Host "  corr        = $($snap.correlationId)"
Write-Host "  device      = $($snap.deviceName)"
Write-Host "  state       = $($snap.state)"
Write-Host "  terminal    = $($snap.terminal)"
Write-Host "  issuedAt    = $($snap.issuedAt)"
Write-Host "  lastPolled  = $($snap.lastPolledAt)"
if ($snap.terminal -ne $true) {
    Write-Warning "Status row is non-terminal — expected denial path to mark terminal=true."
}
if ($snap.state -notlike 'denied:*' -and $snap.state -notlike 'failed:*') {
    Write-Warning "Unexpected state '$($snap.state)' — expected denied:* / failed:* for fictitious device."
}
