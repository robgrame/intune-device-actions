# AD Object Cleanup — .NET Worker Service (recommended, production)

Event-driven .NET Worker Service that deletes stale on-premises Active Directory
**computer objects** on behalf of the Intune rename capability. This is the
**recommended production** option; a lightweight PowerShell alternative lives in
[`../AdObjectCleanup`](../AdObjectCleanup/README.md).

## Why this over the PowerShell worker

* **Event-driven** — a `ServiceBusProcessor` invokes the handler the moment a
  message arrives (no polling loop / no scheduled-task restarts).
* **First-class Windows Service** — hosted via
  `Microsoft.Extensions.Hosting.WindowsServices`; proper start/stop, logging,
  crash recovery.
* **Robust settlement** — Complete / Abandon / Dead-letter are native SB
  operations, and poison messages are dead-lettered (not silently dropped).
* Uses `System.DirectoryServices` (LDAP) directly — no RSAT PowerShell module
  dependency.

## Architecture

```
Azure (Rename capability)                 On-prem (domain-joined Windows Service)
  enqueue AdCleanupMessage ─▶ [ ad-object-cleanup queue ] ─▶ ServiceBusProcessor
  (Function App OR runbook)                                   ProcessMessageAsync
                                                              System.DirectoryServices
                                                              DirectoryEntry.DeleteTree()
                                                              Complete / Abandon / DeadLetter
```

The wire contract (`Models/AdCleanupMessage.cs`) mirrors
`src/Capabilities.Rename/Models/AdCleanupMessage.cs`. Application property
`messageType == "ad-object-cleanup"` is asserted.

## Requirements

* Windows Server **joined to the domain** whose objects you clean.
* .NET 10 runtime (or publish self-contained — see below).
* Outbound HTTPS (443) to `<namespace>.servicebus.windows.net`.
* A service account with permission to delete the target computer objects, and
  read access to the SB credential.

## Guardrails (fail-safe by default)

| Guardrail | Setting | Behaviour |
|-----------|---------|-----------|
| Dry-run | `AdCleanup:DryRun` (default **true**) | Resolves + logs what *would* be deleted; deletes nothing. Set `false` to arm. |
| OU scope | `AdCleanup:SearchBase` | Only objects under this DN can be deleted. **Strongly recommended.** |
| Exclusion list | `AdCleanup:ExclusionNames` | Named objects are never deleted (case-insensitive). |
| Delete cap | `AdCleanup:MaxDeletePerMessage` (default 5) | A message resolving to more objects is **dead-lettered** for human review, deleting nothing. |

Message handling: **success → Complete**, **transient AD fault (DC unreachable)
→ Abandon** (redelivered), **poison** (bad JSON / wrong `messageType` / blank
`targetName` / cap exceeded) → **Dead-letter**.

## Configuration

Bind from `appsettings.json`, environment variables (`AdCleanup__SearchBase=…`),
or command line. **Do not commit secrets** — prefer environment variables or a
protected `appsettings.Production.json` (git-ignored) for the SB credential.

Authentication (pick one):

* **Listen SAS connection string** (simplest for on-prem):
  `AdCleanup:ConnectionString = "Endpoint=sb://<ns>.servicebus.windows.net/;SharedAccessKeyName=worker-listen;SharedAccessKey=<key>;EntityPath=ad-object-cleanup"`
* **Azure AD service principal**: set `FullyQualifiedNamespace` + `TenantId` +
  `ClientId` + `ClientSecret` (the SP needs *Azure Service Bus Data Receiver* on
  the queue).
* **DefaultAzureCredential**: set only `FullyQualifiedNamespace` (uses env / VM
  identity / az login).

Get the Listen SAS key provisioned by `infra/ad-object-cleanup.bicep`:

```bash
az servicebus queue authorization-rule keys list \
  --resource-group <rg> --namespace-name <ns> \
  --queue-name ad-object-cleanup --name worker-listen \
  --query primaryConnectionString -o tsv
```

## Build & test locally

```powershell
dotnet build .\AdObjectCleanup.Service.csproj -c Release

# Interactive dry-run (console): reads appsettings.json (DryRun=true by default)
$env:AdCleanup__ConnectionString = '<listen-connection-string>'
$env:AdCleanup__SearchBase       = 'OU=Workstations,DC=corp,DC=contoso,DC=com'
dotnet run --project .\AdObjectCleanup.Service.csproj -c Release
```

## Publish & install as a Windows Service

```powershell
# Framework-dependent (requires .NET 10 runtime on the target):
dotnet publish .\AdObjectCleanup.Service.csproj -c Release -o C:\idactions\ad-cleanup

# …or self-contained (no runtime needed on the target):
dotnet publish .\AdObjectCleanup.Service.csproj -c Release -r win-x64 --self-contained true -o C:\idactions\ad-cleanup

# Register the service (run elevated). Put secrets in env vars, not the CLI:
New-Service -Name 'IntuneDeviceActions-AdObjectCleanup' `
  -BinaryPathName 'C:\idactions\ad-cleanup\AdObjectCleanup.Service.exe' `
  -DisplayName 'Intune Device Actions - AD Object Cleanup' `
  -StartupType Automatic

# Run as a domain service account with delete rights (recommended):
sc.exe config IntuneDeviceActions-AdObjectCleanup obj= 'CORP\svc-adcleanup' password= '<password>'

Start-Service IntuneDeviceActions-AdObjectCleanup
```

Set the SB credential + guardrails for the service via machine environment
variables or `appsettings.Production.json` next to the exe. Flip
`AdCleanup:DryRun` to `false` only after validating the dry-run log.

## Related Azure config

| Key | Default | Meaning |
|-----|---------|---------|
| `Rename:AdNameCleanupMode` | `queue` | `queue` = enqueue to this worker; `graph` = legacy Entra ServerAd shadow delete. |
| `ServiceBus:AdCleanupQueue` | `ad-object-cleanup` | Queue name the capability sends to. |
