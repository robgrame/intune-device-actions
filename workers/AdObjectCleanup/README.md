# AD Object Cleanup — on-prem hybrid worker

The Intune **rename** capability runs in Azure and cannot reach on-premises
Active Directory. Before it issues an Intune `setDeviceName`, it must remove any
stale on-prem AD **computer object** that already carries the *target* (new)
name, otherwise the next Entra Connect / MDM sync collides and the hybrid rename
fails.

Because the deletion must happen against a Domain Controller, it is delegated to
this **hybrid worker**: a domain-joined server that

1. reads `ad-object-cleanup` messages from an Azure **Service Bus queue**
   (peek-lock via the Service Bus **REST API** + a **Listen SAS token** — no
   Azure SDK required), and
2. runs `Remove-ADComputer` for the object(s) named `targetName`, with
   guardrails.

```
Azure                                   On-prem (domain-joined)
┌──────────────────────┐   AdCleanup    ┌───────────────────────────────┐
│ Rename capability    │─── message ───▶│ ad-object-cleanup queue        │
│ (Rename Function App)│   (queue mode) │                                │
└──────────────────────┘                │  Start-AdObjectCleanupWorker   │
                                        │   peek-lock (REST + SAS)       │
                                        │   Get-ADComputer -Filter Name  │
                                        │   Remove-ADComputer (guarded)  │
                                        │   Complete / Abandon           │
                                        └───────────────────────────────┘
```

## Message contract

Owned by the capability
(`src/Capabilities.Rename/Models/AdCleanupMessage.cs`), published only when
`Rename:AdNameCleanupMode = queue` (the default):

```jsonc
{
  "schemaVersion": "1",
  "correlationId": "….",
  "targetName": "PC-NEW",       // delete AD computer(s) with this name
  "sourceDeviceName": "PC-OLD",
  "entraDeviceId": "….",
  "intuneDeviceId": "….",
  "serialNumber": "….",
  "requestedUtc": "2024-..Z"
}
```

The Service Bus application property `messageType == "ad-object-cleanup"` is
asserted by the worker.

## Requirements

* Windows Server (or Win10/11) **joined to the domain** whose objects you clean.
* **RSAT ActiveDirectory** PowerShell module
  (`Add-WindowsCapability -Online -Name Rsat.ActiveDirectory.DS-LDS.Tools~~~~0.0.1.0`).
* Outbound HTTPS (443) to `<namespace>.servicebus.windows.net`.
* A service account with permission to delete the target computer objects.
* A **Listen** SAS rule on the queue (see `infra/ad-object-cleanup.bicep`).

## Guardrails (fail-safe by default)

| Guardrail | Parameter | Behaviour |
|-----------|-----------|-----------|
| Dry-run | `-WhatIf` | Resolves + logs the objects that *would* be deleted, deletes nothing. |
| OU scope | `-SearchBase` | Only objects under this DN can be deleted. **Strongly recommended.** |
| Exclusion list | `-ExclusionNames` | Named objects are never deleted (case-insensitive). |
| Delete cap | `-MaxDeletePerMessage` (default 5) | A message resolving to *more* objects is **dropped without deleting** for human review. |

Message handling: **success → Complete**, **transient failure (DC unreachable)
→ Abandon** (redelivered), **poison message** (bad schema / blank name / cap
exceeded) → logged and **Completed** so it does not poison-loop.

## Quick start (interactive test)

```powershell
# Dry-run against a single OU, process one message then exit:
.\Start-AdObjectCleanupWorker.ps1 `
    -Namespace idactions-sb-dev `
    -Queue ad-object-cleanup `
    -SasKeyName worker-listen `
    -SasKeyFile C:\ProgramData\idactions\sb.key `
    -SearchBase 'OU=Workstations,DC=corp,DC=contoso,DC=com' `
    -ExclusionNames 'DC01','FILE01' `
    -MaxDeletePerMessage 3 `
    -WhatIf -RunOnce -Verbose
```

Provide the SAS key via (in order of precedence) `-SasKey`, `-SasKeyFile`, or the
`IDACTIONS_AD_CLEANUP_SASKEY` environment variable. Prefer the file/env options
so the key is not in your shell history.

## Run as a service (scheduled task)

```powershell
.\Install-AdObjectCleanupWorker.ps1 `
    -Namespace idactions-sb-dev `
    -SasKeyFile C:\ProgramData\idactions\sb.key `
    -SearchBase 'OU=Workstations,DC=corp,DC=contoso,DC=com' `
    -ExclusionNames 'DC01','FILE01' `
    -RunAsUser 'CORP\svc-adcleanup'
```

This registers `IntuneDeviceActions-AdObjectCleanup`, running the worker at
startup and auto-restarting it. Add `-WhatIfWorker` to install in dry-run mode
first. Logs default to `C:\ProgramData\idactions\ad-object-cleanup.log`.

## Provisioning the queue + SAS rule

See `infra/ad-object-cleanup.bicep` (additive module):

* creates the `ad-object-cleanup` queue,
* adds a `worker-listen` **Listen** authorization rule (the SAS key this worker
  uses),
* grants the Rename Function App's UAMI the **Azure Service Bus Data Sender**
  role so the capability can enqueue.

Retrieve the Listen key after deployment:

```bash
az servicebus queue authorization-rule keys list \
  --resource-group <rg> --namespace-name <ns> \
  --queue-name ad-object-cleanup --name worker-listen \
  --query primaryKey -o tsv > sb.key
```

## Related configuration (Azure side)

| Key | Default | Meaning |
|-----|---------|---------|
| `Rename:AdNameCleanupMode` | `queue` | `queue` = enqueue to this worker; `graph` = legacy Entra ServerAd shadow delete. |
| `Rename:AdNameCleanup` | `enabled` | Master switch for AD-name cleanup. |
| `ServiceBus:AdCleanupQueue` | `ad-object-cleanup` | Queue name the capability sends to. |

The equivalent runbook (`runbooks/Invoke-DeviceRename.runbook.ps1`) enqueues the
same message when `Rename:AdNameCleanupMode = queue`.
