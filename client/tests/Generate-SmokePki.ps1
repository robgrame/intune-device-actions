<#
.SYNOPSIS
    Generates a 2-level mini-PKI (root + leaf) for smoke-testing the mTLS-protected
    Function Apps in dev. Never run against prod parameters.

.DESCRIPTION
    Output (under client/tests/smoke-pki/, git-ignored):
      - root.cer        public DER of the test root CA (paste into Bicep params)
      - root.thumbprint plain-text root thumbprint (uppercase hex, no spaces)
      - leaf.pfx        PKCS#12 of the leaf — private key included, password = "smoke"
      - leaf.cer        public DER of the leaf (CN = <EntraDeviceId GUID>)
      - leaf.thumbprint plain-text leaf thumbprint
      - meta.json       { entraDeviceId, leafThumbprint, rootThumbprint, rootCertBase64 }

    The leaf has:
      - EKU = Client Authentication (1.3.6.1.5.5.7.3.2)   [required by RequireClientAuthEku=true]
      - Subject CN = a GUID                                [satisfies DeviceIdBinding=Auto SubjectCN path]
      - Issuer = root                                      [creates a 2-element chain so the
                                                            TrustedCaThumbprints check has a non-leaf to look at]

.PARAMETER EntraDeviceId
    Optional GUID to use as the leaf CN. Generated automatically if omitted.

.PARAMETER PfxPassword
    Password to protect the leaf PFX. Default 'smoke'. Plain-text by design — this is
    a throwaway test cert; the threat model is "anyone who has the PFX can authenticate
    as this device". Rotate by re-running the script (regenerates root and leaf).

.EXAMPLE
    .\Generate-SmokePki.ps1
    .\Generate-SmokePki.ps1 -EntraDeviceId 11111111-2222-3333-4444-555555555555

.NOTES
    After running, copy the printed Bicep-parameter snippet into
    infra/main.parameters.json and run a Bicep redeploy so the apps trust the root.
#>
[CmdletBinding()]
param(
    [Parameter()] [Guid]   $EntraDeviceId = [Guid]::NewGuid(),
    [Parameter()] [string] $PfxPassword   = 'smoke'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$outDir = Join-Path $PSScriptRoot 'smoke-pki'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

Write-Host "==> Generating test root CA (CN=IDActions-SMOKE-ROOT, 2 years)"
$root = New-SelfSignedCertificate `
    -Type Custom `
    -Subject 'CN=IDActions-SMOKE-ROOT' `
    -KeyExportPolicy Exportable `
    -KeyAlgorithm RSA `
    -KeyLength 2048 `
    -HashAlgorithm SHA256 `
    -NotAfter (Get-Date).AddYears(2) `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -KeyUsage CertSign, CRLSign, DigitalSignature `
    -TextExtension @('2.5.29.19={text}CA=true&pathlength=0')

Write-Host "    root thumbprint = $($root.Thumbprint)"

Write-Host "==> Generating leaf cert signed by the test root (CN=$EntraDeviceId, 1 year)"
$leaf = New-SelfSignedCertificate `
    -Type Custom `
    -Subject "CN=$EntraDeviceId" `
    -Signer $root `
    -KeyExportPolicy Exportable `
    -KeyAlgorithm RSA `
    -KeyLength 2048 `
    -HashAlgorithm SHA256 `
    -NotAfter (Get-Date).AddYears(1) `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -KeyUsage DigitalSignature, KeyEncipherment `
    -TextExtension @(
        '2.5.29.37={text}1.3.6.1.5.5.7.3.2',
        "2.5.29.17={text}DNS=$EntraDeviceId"
    )

Write-Host "    leaf thumbprint = $($leaf.Thumbprint)"

# --- Export DER (public-only) for root + leaf -----------------------------
$rootCerPath  = Join-Path $outDir 'root.cer'
$leafCerPath  = Join-Path $outDir 'leaf.cer'
[System.IO.File]::WriteAllBytes($rootCerPath, $root.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
[System.IO.File]::WriteAllBytes($leafCerPath, $leaf.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))

# --- Export PFX (private key included) for the leaf ------------------------
$leafPfxPath = Join-Path $outDir 'leaf.pfx'
$pwd = ConvertTo-SecureString -String $PfxPassword -AsPlainText -Force
Export-PfxCertificate -Cert "Cert:\CurrentUser\My\$($leaf.Thumbprint)" -FilePath $leafPfxPath -Password $pwd | Out-Null

# --- Plain-text helpers --------------------------------------------------
Set-Content -Path (Join-Path $outDir 'root.thumbprint') -Value $root.Thumbprint -Encoding ascii
Set-Content -Path (Join-Path $outDir 'leaf.thumbprint') -Value $leaf.Thumbprint -Encoding ascii

# Base64 of the DER root (single line, no PEM markers) — paste-ready for Bicep params.
$rootB64 = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($rootCerPath))

# --- meta.json so Invoke-SmokeTest.ps1 can default everything -----------
$meta = [ordered]@{
    entraDeviceId  = $EntraDeviceId.ToString()
    leafThumbprint = $leaf.Thumbprint
    rootThumbprint = $root.Thumbprint
    rootCertBase64 = $rootB64
    leafPfxPath    = $leafPfxPath
    pfxPassword    = $PfxPassword
    notBefore      = $leaf.NotBefore.ToString('o')
    notAfter       = $leaf.NotAfter.ToString('o')
}
$metaPath = Join-Path $outDir 'meta.json'
$meta | ConvertTo-Json | Set-Content -Path $metaPath -Encoding utf8

# --- Cleanup: certs live in Cert:\CurrentUser\My; remove them so the user's
#     personal store doesn't accumulate throw-away CAs across runs. The PFX
#     on disk is the source of truth for the leaf.
Remove-Item -Path "Cert:\CurrentUser\My\$($leaf.Thumbprint)" -ErrorAction SilentlyContinue
Remove-Item -Path "Cert:\CurrentUser\My\$($root.Thumbprint)" -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "==> DONE. Files written to: $outDir"
Write-Host ""
Write-Host "Update infra\main.parameters.json with the snippet below (APPEND the new"
Write-Host "values to the existing ones, comma-separated, so MSLABS continues to be trusted):"
Write-Host ""
Write-Host "    trustedCaThumbprints:             {existing},$($root.Thumbprint)"
Write-Host "    trustedRootCertificatesBase64:    {existing},$rootB64"
Write-Host ""
Write-Host "Then redeploy: az deployment group create -g rg-idactions-dev "
Write-Host "    --template-file infra\main.bicep --parameters infra\main.parameters.json"
Write-Host ""
Write-Host "Smoke test (status, non-destructive):  .\Invoke-SmokeTest.ps1 -Mode Status"
Write-Host "Smoke test (request, queues a wipe):   .\Invoke-SmokeTest.ps1 -Mode Request"
