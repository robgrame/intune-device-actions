# IntuneWipeClient — Win32 app package

End-to-end packaging of the self-service wipe client (`Invoke-DeviceWipe.ps1`)
for distribution via Microsoft Intune as a Win32 LOB application.

## What gets installed on the client

| Path | Purpose |
|---|---|
| `%ProgramFiles%\IntuneWipeClient\Invoke-DeviceWipe.ps1` | The wipe script (calls the API). |
| `%ProgramFiles%\IntuneWipeClient\WipeConfirmationDialog.ps1` | Shared WinForms confirmation dialog. |
| `%ProgramFiles%\IntuneWipeClient\Launch-Wipe.ps1` | Shortcut launcher: reads config + invokes the wipe script. |
| `%ProgramFiles%\IntuneWipeClient\config.json` | API URL + function key (ACL = SYSTEM + Administrators only). |
| `%ProgramData%\Microsoft\Windows\Start Menu\Programs\Migrazione a MODERN.lnk` | All-users Start Menu shortcut. |
| `%PUBLIC%\Desktop\Migrazione a MODERN.lnk` | Public Desktop shortcut (visible to every user). |
| `HKLM:\SOFTWARE\MSLABS\IntuneWipeClient` | Detection registry key (`Version`, `ProductCode`, `InstallDir`). |

## Build

```powershell
cd C:\Users\robgrame\source\repos\intune-wipe-api\client\intune-win32-package
.\Build-IntuneWinPackage.ps1
# -> dist\IntuneWipeClient.intunewin
```

The build script syncs the canonical wipe scripts from `..\` so there is a
single source of truth, stamps `Detect.ps1` with the version in
`source\version.txt`, downloads `IntuneWinAppUtil.exe` on first run, and
produces `dist\IntuneWipeClient.intunewin`.

## Publish to Intune

```powershell
.\Publish-ToIntune.ps1 `
    -AssignToGroupId "<entra group object id>"   # optional
```

> **No more `-ApiUrl` / `-FunctionKey` in the install command line.**
> Both values are now provisioned via the companion Proactive
> Remediation [`intune-remediation-endpoint`](../intune-remediation-endpoint/),
> which writes them as machine-scope env vars
> (`INTUNE_WIPE_API_URL`, `INTUNE_WIPE_FUNCTION_KEY`). The wipe scripts
> read those env vars in preference to `config.json`, so URL repointing
> and key rotation no longer require repackaging the `.intunewin`.

Authenticates interactively to Microsoft Graph via the
[`IntuneWin32App`](https://github.com/MSEndpointMgr/IntuneWin32App) module
(installed to `CurrentUser` scope on first run). The publish call is
idempotent: an existing app with the same display name is removed and
re-published.

### What gets configured in Intune

- **Install command**:
  `powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ".\Install.ps1" -StatusPollIntervalSeconds 5 -StatusPollMaxMinutes 30`
- **Uninstall command**:
  `powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ".\Uninstall.ps1"`
- **Install behaviour**: System
- **Detection**: registry value
  `HKLM\SOFTWARE\MSLABS\IntuneWipeClient\Version` equals the current version.
- **Requirements**: Windows 10 1809+, all architectures.
- **Restart behaviour**: suppress.

> Backward compatibility: `Install.ps1` still accepts `-ApiUrl` and
> `-FunctionKey` as optional parameters for environments not yet
> running the endpoint remediation.

## Operational notes

- The function key is treated as a secret: it is written to `config.json`
  with an ACL that disables inheritance and grants Full Control only to
  `SYSTEM` and the local `Administrators` group, so a standard user
  cannot read it. The launcher (Start Menu shortcut) therefore needs to
  run elevated — in typical Intune-managed deployments the primary user
  is a local admin on their device. If your end users are standard
  users, switch the secret to Key Vault retrieval at script start.
- Logs:
  - `Install.ps1` / `Uninstall.ps1` → `%ProgramData%\IntuneWipeClient\Logs\`
  - `Launch-Wipe.ps1` → `%LOCALAPPDATA%\IntuneWipeClient\Logs\`
- Audit events are emitted by the API into Application Insights as
  `wipe.*` and surfaced by the observability portal
  (https://intwipe-portal-qupxwx6egkr3e.azurewebsites.net/).

## Versioning

Bump `source\version.txt` before each rebuild. The build script
re-stamps `Detect.ps1` automatically, and `Publish-ToIntune.ps1`
passes the same value as `-AppVersion`.
