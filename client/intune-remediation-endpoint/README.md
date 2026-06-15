# intune-remediation-endpoint

Proactive Remediation pair that pins the **Function App (Web role) base
URL and Function key** consumed by the IntuneWipeClient
(`Invoke-WipeFromTask.ps1`, `Watch-WipeStatus.ps1`,
`intune-remediation-schedule\*`) to **machine-scope environment
variables**, **without having to repackage the `.intunewin`**.

| Env var (Machine scope) | Holds |
|---|---|
| `INTUNE_WIPE_API_URL` | Full Function App actions endpoint, e.g. `https://devact-web-dev.azurewebsites.net/api/actions` |
| `INTUNE_WIPE_FUNCTION_KEY` | Function-level or host key for the `actions` endpoint |
| `INTUNE_WIPE_CERT_THUMBPRINT` | (optional) Exact SHA-1 thumbprint of the mTLS client cert |
| `INTUNE_WIPE_CERT_SUBJECT_LIKE` | (optional) `-like` wildcard on cert Subject (`*Microsoft Intune MDM Device CA*`) |
| `INTUNE_WIPE_CERT_ISSUER_LIKE` | (optional) `;`-separated wildcards on cert Issuer (OR semantics) |

Set any cert selector's `$ExpectedX` to empty string `''` in
`Detect.ps1` + `Remediate.ps1` to opt that selector out of remediation
(the wipe client will then fall back to the value in `config.json`).
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

## Migration from -ApiUrl / -FunctionKey install args

Existing devices still have `ApiUrl` + `FunctionKey` written to
`%ProgramData%\IntuneWipeClient\config.json` from the old install
command. They will keep working — the env-var overrides only take
precedence when set. Once this remediation has reached the fleet you
can simplify the Intune Win32 install command to:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden ^
    -File ".\Install.ps1" ^
    -CertificateSubjectLike "*Microsoft Intune MDM Device CA*"
```

(no `-ApiUrl`, no `-FunctionKey`).
