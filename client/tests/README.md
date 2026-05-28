# Client unit tests

Pester v5 hermetic unit tests for the PowerShell client.

## What's covered

`DeviceIdentity.Tests.ps1` — 26 tests against `client\DeviceIdentity.psm1`:

| Function | Scenarios |
|---|---|
| `Get-EntraDeviceId` | happy path, dsregcmd failure, missing DeviceId line |
| `Get-MdmEnrollmentId` | missing reg key, DeviceClientId present, fallback to subkey name, non-GUID subkey filtered, non-Intune ProviderID skipped |
| `Get-IntuneManagedDeviceId` | IME registry hit, ManagedDeviceId fallback, log-file parse fallback, $null when nothing found, ignore non-GUID values |
| `Get-ClientCertificate` | thumb match (case-insensitive), EKU filter, expiration filter, no-private-key filter, single + multi-pattern issuer wildcards, longest-living tiebreak, SubjectLike filter, no-match throws |
| Safe variants | all four return `'n/a'` instead of throwing |

All registry / log / dsregcmd / cert-store calls are mocked — the suite runs
on any host (no Intune/Entra enrollment required) and on CI runners.

## Run locally

```powershell
# Install Pester 5.x on first run (CurrentUser scope, no admin needed)
.\client\tests\Invoke-Tests.ps1            # default verbosity = Detailed
.\client\tests\Invoke-Tests.ps1 -Output Normal
.\client\tests\Invoke-Tests.ps1 -Output Diagnostic
```

The runner exits with `FailedCount` so any CI step will fail the build on
the first red test.

## Run directly with Pester

```powershell
Invoke-Pester -Path .\client\tests\DeviceIdentity.Tests.ps1 -Output Detailed
```

## Adding new tests

Drop another `*.Tests.ps1` next to `DeviceIdentity.Tests.ps1` — the runner
discovers everything under `client\tests\`.

When testing functions defined inside `DeviceIdentity.psm1`, wrap the test
body in `InModuleScope DeviceIdentity { ... }` so `Mock` can intercept the
cmdlets the module calls (Pester 5 isolates module scope from file scope).
