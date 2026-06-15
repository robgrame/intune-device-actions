# intune-remediation-apiurl

Proactive Remediation pair that pins the **Function App (Web role) base
URL** consumed by the IntuneWipeClient (`Invoke-WipeFromTask.ps1`,
`Watch-WipeStatus.ps1`, `intune-remediation-schedule\*`) to a
**machine-scope environment variable** `INTUNE_WIPE_API_URL`, **without
having to repackage the `.intunewin`**.

## Why

`Install.ps1` writes `ApiUrl` into `%ProgramData%\IntuneWipeClient\config.json`
at install time. Changing the Function App URL (e.g. moving from
`idactions-web-dev` to `devact-web-dev`) would normally require:

- editing the `Install.ps1` invocation,
- repackaging the `.intunewin`,
- re-deploying the Win32 app to thousands of devices.

This remediation lets you **swap the URL with a one-line edit and a
remediation re-run**, with zero client re-deployment. The wipe scripts
read `[Environment]::GetEnvironmentVariable('INTUNE_WIPE_API_URL',
'Machine')` and use it in place of `$cfg.ApiUrl` when present.

## Files

- `Detect.ps1` — exits `1` if the machine env var is missing or differs
  from `$ExpectedApiUrl`, `0` otherwise.
- `Remediate.ps1` — sets the env var to `$ExpectedApiUrl` and verifies
  the read-back from the Machine scope.

Both scripts share the same `$ExpectedApiUrl` constant; **edit both** in
lockstep when repointing.

## Deploy

1. Edit `$ExpectedApiUrl` in `Detect.ps1` and `Remediate.ps1`.
2. Intune admin centre → Devices → Scripts and remediations → Add.
3. Upload `Detect.ps1` + `Remediate.ps1`.
4. Settings: run as **SYSTEM**, 64-bit PowerShell, no signature check.
5. Assign to the same device group as the wipe `.intunewin`.
6. Schedule: every 24 h is plenty (the env var is sticky across reboots).

> Existing processes will not see the new value: the wipe SYSTEM task
> spawns a fresh `powershell.exe` per run and therefore reads the latest
> Machine value on every invocation.
