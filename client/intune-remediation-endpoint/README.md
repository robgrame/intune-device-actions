# intune-remediation-endpoint

Proactive Remediation pair that pins the **Function App (Web role) base
URL and Function key** consumed by the client
(`intune-remediation-schedule\*`, the packaged task client) to
**machine-scope environment variables**, **without having to repackage the
`.intunewin`**.

| Env var (Machine scope) | Holds |
|---|---|
| `INTUNE_ACTIONS_API_URL` | Full Function App actions endpoint, e.g. `https://devact-web-dev.azurewebsites.net/api/actions` |
| `INTUNE_ACTIONS_FUNCTION_KEY` | Function-level or host key for the `actions` endpoint |
| `INTUNE_ACTIONS_CERT_THUMBPRINT` | (optional) Exact SHA-1 thumbprint of the mTLS client cert |
| `INTUNE_ACTIONS_CERT_SUBJECT_LIKE` | (optional) `-like` wildcard on cert Subject (`*Microsoft Intune MDM Device CA*`) |
| `INTUNE_ACTIONS_CERT_ISSUER_LIKE` | (optional) `;`-separated wildcards on cert Issuer (OR semantics) |

`Remediate.ps1` writes the capability-neutral `INTUNE_ACTIONS_*` names;
consumers read the same names.

Set any cert selector's `$ExpectedX` to empty string `''` in
`Detect.ps1` + `Remediate.ps1` to opt that selector out of remediation
(the client will then fall back to the value in `config.json`).
Setting an empty value causes `Remediate.ps1` to **delete** the env var
so a previously-pinned value cannot leak through.

## Why

Originally `Install.ps1` was invoked from the Intune Win32 install
command with `-ApiUrl` **and** `-FunctionKey` baked into the command
line. That had two drawbacks:

1. Rotating the key / repointing the URL meant editing the install
   command, repackaging the `.intunewin`, and re-deploying the Win32 app
   to every device.
2. The Function key ended up in the Intune Management Extension log
   (`IntuneManagementExtension.log`) verbatim.

With this remediation, `Install.ps1` is invoked **without** secrets
(both parameters are now optional). The remediation provisions both env
vars on every targeted device. The wipe scripts always prefer the env
vars over `config.json`, so any subsequent rotation is a single edit in
`Detect.ps1` + `Remediate.ps1` and an Intune remediation re-run.

## Files

- `Detect.ps1` — exits `1` if either env var is missing or does not
  match `$ExpectedApiUrl` / `$ExpectedFunctionKey`, `0` otherwise.
- `Remediate.ps1` — sets both env vars and verifies the read-back.
  Writes a persistent log to:
  `%ProgramData%\IntuneWipeClient\Logs\intune-remediation-endpoint-remediate.log`

## Security

`$ExpectedFunctionKey` is a **secret**. Source-controlled scripts ship
with the placeholder `__REPLACE_WITH_REAL_FUNCTION_KEY__`; replace it
**only** in the script body you paste into the Intune admin centre.
Intune encrypts script content at rest and in transit. Once written,
the env var lives in `HKLM:\SYSTEM\CurrentControlSet\Control\Session
Manager\Environment` — readable by local users, same threat model as
the previous `config.json` under `%ProgramData%` (no security
downgrade).

## Deploy

1. Edit `$ExpectedApiUrl` and `$ExpectedFunctionKey` in **both**
   `Detect.ps1` and `Remediate.ps1` with the real values.
2. Intune admin centre → Devices → Scripts and remediations → Add.
3. Upload `Detect.ps1` + `Remediate.ps1`.
4. Settings: run as **SYSTEM**, 64-bit PowerShell, no signature check.
5. Assign to the same device group as the wipe `.intunewin`.
6. Schedule: every 24 h. The wipe SYSTEM task spawns a fresh
   `powershell.exe` per run and therefore picks up the latest Machine
   env values on every invocation.

## Choosing the right certificate selector

`Get-ClientCertificate` (in `DeviceIdentity.psm1`) applies the active
selectors in AND:

1. `LocalMachine\My` (then `CurrentUser\My`, but the wipe task runs as
   **SYSTEM** so only `LocalMachine\My` matters in practice)
2. `HasPrivateKey` + within `NotBefore`/`NotAfter`
3. **EKU** `1.3.6.1.5.5.7.3.2` (clientAuth)
4. `IssuerLike` — if set, at least one `;`-separated wildcard must match
5. `Thumbprint` exact match, **else** `SubjectLike` wildcard, **else**
   the cert with the longest `NotAfter` remaining

### Gotcha: empty Subject (SAN-only certs)

Enterprise auto-enrolled device certs frequently have an **empty
Subject DN** (the device identity lives in the SAN-DNS / SAN-UPN). If
you set `INTUNE_ACTIONS_CERT_SUBJECT_LIKE` to a non-empty pattern, those
certs are filtered out — including the one you actually need. Symptom:
`Client certificate not found (with Client Authentication EKU and
private key)` even though a perfectly valid cert sits in
`LocalMachine\My`.

### Recommended values

- **Single enterprise CA** (typical):
  ```powershell
  $ExpectedCertThumbprint  = ''
  $ExpectedCertSubjectLike = ''
  $ExpectedCertIssuerLike  = '*MSLABS-SUBCA01*'
  ```
- **Multiple acceptable CAs** (migration, partner CA, etc.):
  ```powershell
  $ExpectedCertIssuerLike  = '*MSLABS-SUBCA01*;*MSLABS-ADCS*'
  ```
- **Single device pinning (lab / repro)**: hard-pin the thumbprint;
  zero ambiguity but you'll need to re-pin on renewal:
  ```powershell
  $ExpectedCertThumbprint  = '09BFC7AD8B69917B65A7FE476321AAFAA3FB6E9C'
  $ExpectedCertSubjectLike = ''
  $ExpectedCertIssuerLike  = ''
  ```

**Never** combine an Issuer-like enterprise CA pattern with a Subject
pattern modelled on Intune MDM certs (`*Microsoft Intune MDM Device
CA*`) — Intune MDM certs are issued by `CN=Microsoft Intune MDM Device
CA`, so the AND of `IssuerLike '*MSLABS*'` and `SubjectLike '*Intune
MDM*'` is the empty set.

## Migration from -ApiUrl / -FunctionKey install args

Existing devices still have `ApiUrl` + `FunctionKey` written to
`%ProgramData%\IntuneWipeClient\config.json` from the old install
command. They will keep working — the env-var overrides only take
precedence when set. The Intune Win32 install command has been
simplified to:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden ^
    -File ".\Install.ps1" ^
    -StatusPollIntervalSeconds 5 -StatusPollMaxMinutes 30
```

(no `-ApiUrl`, no `-FunctionKey`, no `-Certificate*`). All five values
are now provisioned exclusively through the env vars set by this
remediation.
