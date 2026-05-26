# Intune Device Self-Wipe API

[![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Azure Functions](https://img.shields.io/badge/Azure_Functions-isolated-0062AD?logo=azurefunctions&logoColor=white)](https://learn.microsoft.com/azure/azure-functions/)
[![Bicep](https://img.shields.io/badge/IaC-Bicep-1E5DBE?logo=azurepipelines&logoColor=white)](https://learn.microsoft.com/azure/azure-resource-manager/bicep/)
[![License](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

> Soluzione serverless end-to-end per consentire ad un dispositivo Windows gestito da Intune di richiedere in autonomia il proprio **wipe (factory reset)**, con difesa in profondità: certificato dispositivo Intune (mTLS), allow-list nativa via gruppo Entra ID, validazione di ownership, esecuzione asincrona via coda e audit completo.

## Indice

- [Architettura](#architettura)
- [Componenti](#componenti)
- [Controlli di sicurezza](#controlli-di-sicurezza-in-profondit%C3%A0)
- [Permessi Microsoft Graph](#permessi-microsoft-graph)
- [Quickstart deploy](#quickstart-deploy)
- [Uso del client PowerShell](#uso-del-client-powershell-51)
- [API](#api)
- [Configurazione](#configurazione)
- [Osservabilità & audit](#osservabilità--audit)
- [Struttura del repository](#struttura-del-repository)
- [Roadmap](#roadmap)
- [Licenza](#licenza)

## Architettura

```
  PS 5.1 client (Intune Win32)
        │
        │  HTTPS + mTLS (cert dispositivo SCEP/PKCS)
        ▼
  ┌────────────────────────┐        ┌──────────────────────┐
  │  Function "WipeRequest"│  ───▶  │  Storage Queue       │
  │  (HTTP, pubblica)      │        │  "wipe-requests"     │
  └────────────────────────┘        └──────────┬───────────┘
                                                │
                                                ▼
                              ┌──────────────────────────────┐
                              │  Function "WipeProcessor"    │
                              │  (queue trigger, interna)    │
                              └───────────────┬──────────────┘
                                              │  Managed Identity
                                              ▼
                              ┌──────────────────────────────┐
                              │   Microsoft Graph            │
                              │   ├─ resolve device          │
                              │   ├─ checkMemberGroups       │
                              │   ├─ verify ownership        │
                              │   └─ POST .../wipe           │
                              └──────────────────────────────┘
```

L'endpoint pubblico fa **solo** validazione (certificato + payload) e
accodamento. L'esecuzione effettiva avviene in un secondo processo,
disaccoppiato e ritentabile, che applica i controlli di autorizzazione
e chiama Microsoft Graph.

## Componenti

| # | Componente | Ruolo |
|---|---|---|
| 1 | **`Invoke-DeviceWipe.ps1`** (PowerShell 5.1) | Raccoglie identità device, mostra UI WinForms di conferma (irreversibilità + ~90 min di indisponibilità + parola `WIPE` da digitare), invoca l'API in mTLS. |
| 2 | **`WipeRequest`** (HTTP Function) | Valida cert client (issuer/thumbprint), valida payload, accoda messaggio, risponde `202 Accepted` con `correlationId`. |
| 3 | **Azure Storage Queue** `wipe-requests` | Disaccoppia ricezione ed esecuzione; retry automatici, dead-letter su `wipe-requests-poison`. |
| 4 | **`WipeProcessor`** (Queue trigger, non esposta) | Risolve device Entra, verifica membership gruppo, verifica ownership Intune↔Entra, esegue `POST /deviceManagement/managedDevices/{id}/wipe`. |
| 5 | **User-Assigned Managed Identity** | Identità unica per Storage e Graph. Nessun secret. |
| 6 | **Application Insights** | Audit completo con `correlationId`. |

## Controlli di sicurezza in profondità

Una richiesta deve superare **tutti** questi controlli, nell'ordine:

1. **Function key** sull'HTTP call (`x-functions-key`)
2. **Client certificate** valido, emesso dalla CA Intune SCEP/PKCS autorizzata (validazione issuer e/o thumbprint allow-list)
3. **Payload ben formato** (tre GUID validi)
4. **Device presente** nell'Entra ID del tenant
5. **Device membro** (anche transitivo) del gruppo di sicurezza Entra autorizzato → allow-list nativa, integrabile con membership dinamica
6. **Ownership match**: `managedDevice.azureADDeviceId` deve uguagliare l'`entraDeviceId` dichiarato
7. **Solo allora** viene chiamata l'API di wipe Microsoft Graph

Inoltre: HTTPS-only, TLS 1.2 minimo, `clientCertEnabled = true`,
Managed Identity con permessi minimi, nessuna credenziale in codice.

## Permessi Microsoft Graph

Assegnati come **application permissions** alla Managed Identity (richiede consent admin):

- `DeviceManagementManagedDevices.PrivilegedOperations.All`
- `DeviceManagementManagedDevices.Read.All`
- `Device.Read.All`

## Quickstart deploy

### Prerequisiti

- Azure subscription + permessi `Owner` o `Contributor` + `User Access Administrator` sul RG (per le role assignment)
- Tenant con Intune e CA SCEP/PKCS che emette certificati ai dispositivi
- `az` CLI ≥ 2.60, `dotnet` SDK 10
- Un gruppo di sicurezza Entra ID che conterrà i device autorizzati al self-wipe

### 1. Crea il gruppo Entra (se non esiste)

```pwsh
$groupId = az ad group create `
  --display-name 'Intune-Wipe-Authorized' `
  --mail-nickname 'IntuneWipeAuthorized' `
  --description 'Devices authorized to request self-wipe' `
  --query id -o tsv
```

### 2. Deploy infrastruttura

```pwsh
az group create -n rg-intwipe-dev -l westeurope
az deployment group create `
  -g rg-intwipe-dev `
  -f infra/main.bicep `
  -p infra/main.parameters.json `
  -p allowedGroupId=$groupId
```

Output utili: `functionAppName`, `uamiPrincipalId`, `storageAccount`, `wipeQueueName`.

### 3. Concedi i permessi Graph alla Managed Identity

```pwsh
$principalId = '<uamiPrincipalId dall''output>'
$graphSpId   = az ad sp list --filter "appId eq '00000003-0000-0000-c000-000000000000'" --query "[0].id" -o tsv

foreach ($r in @(
  'DeviceManagementManagedDevices.PrivilegedOperations.All',
  'DeviceManagementManagedDevices.Read.All',
  'Device.Read.All'
)) {
  $rid = az ad sp show --id $graphSpId --query "appRoles[?value=='$r'].id | [0]" -o tsv
  $body = "{`"principalId`":`"$principalId`",`"resourceId`":`"$graphSpId`",`"appRoleId`":`"$rid`"}"
  $tmp = New-TemporaryFile; $body | Set-Content -Encoding ascii $tmp
  az rest --method POST `
    --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$principalId/appRoleAssignments" `
    --headers "Content-Type=application/json" --body "@$($tmp.FullName)"
  Remove-Item $tmp
}
```

### 4. Pubblica il codice

```pwsh
cd src
dotnet publish -c Release -o ./publish
Compress-Archive -Path ./publish/* -DestinationPath ./publish.zip -Force
az functionapp deployment source config-zip `
  -g rg-intwipe-dev -n <functionAppName> --src ./publish.zip
```

### 5. Aggiungi device al gruppo allow-list

```pwsh
$deviceObjId = az ad device list --filter "deviceId eq '<entraDeviceId>'" --query "[0].id" -o tsv
az ad group member add --group $groupId --member-id $deviceObjId
```

## Uso del client PowerShell 5.1

Distribuibile via Intune (Win32 app, esecuzione in contesto SYSTEM) oppure
eseguibile manualmente:

```powershell
.\client\Invoke-DeviceWipe.ps1 `
  -ApiUrl       'https://<func>.azurewebsites.net/api/wipe' `
  -CertificateSubjectLike '*Intune MDM Device CA*' `
  -FunctionKey  '<function-key>'
```

Lo script:

1. Legge l'**Entra Device Id** da `dsregcmd /status`
2. Legge l'**Intune Device Id** (`DeviceClientId`) dal registro
   `HKLM:\SOFTWARE\Microsoft\Enrollments\*`
3. Mostra una finestra WinForms con:
   - intestazione rossa di warning
   - dettagli del dispositivo
   - avviso esplicito di irreversibilità e ~90 minuti di downtime
   - checkbox di consapevolezza obbligatoria
   - input testuale che richiede di digitare `WIPE` per abilitare il bottone
4. Sceglie il certificato dispositivo da `Cert:\LocalMachine\My`
5. Invoca l'API con `Invoke-RestMethod -Certificate` e mostra il
   `correlationId` per riferimento al supporto

Usa `-Silent` per scenari unattended (test).

## API

### `POST /api/wipe`

Headers obbligatori:

- `x-functions-key: <function-key>`
- `Content-Type: application/json`
- mutual TLS con certificato dispositivo Intune

Body:

```json
{
  "deviceName": "DESKTOP-ABC",
  "entraDeviceId": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
  "intuneDeviceId": "yyyyyyyy-yyyy-yyyy-yyyy-yyyyyyyyyyyy"
}
```

Risposta `202 Accepted`:

```json
{
  "status": "queued",
  "message": "wipe request accepted and queued",
  "correlationId": "..."
}
```

Codici di errore:

| Code | Significato |
|------|-------------|
| 400 | Payload non valido (campi mancanti o non GUID) |
| 401 | Certificato client mancante o non valido |
| 502 | Errore upstream Microsoft Graph (riconciliato via retry coda) |

## Configurazione

Tutte le impostazioni sono app settings della Function App:

| Setting | Default | Descrizione |
|---|---|---|
| `Queue__WipeQueueName` | `wipe-requests` | Nome coda |
| `ClientCert__AllowedIssuer` | `CN=Intune SCEP CA` | DN issuer cert dispositivo |
| `ClientCert__AllowedThumbprints` | _(vuoto)_ | CSV thumbprint allow-list (opzionale) |
| `ClientCert__RequireClientCert` | `true` | Impone cert client |
| `Wipe__AllowedGroupId` | _(obbligatorio)_ | ObjectId gruppo Entra |
| `Wipe__KeepEnrollmentData` | `false` | Mantiene enrollment Intune |
| `Wipe__KeepUserData` | `false` | Mantiene dati utente |
| `Graph__TenantId` | tenant corrente | Tenant Graph |

## Osservabilità & audit

Application Insights raccoglie ogni richiesta con `correlationId`. Esempi KQL:

```kql
// Tutti i wipe richiesti nelle ultime 24h
traces
| where timestamp > ago(24h)
| where message startswith "AUDIT"
| project timestamp, message, customDimensions

// Wipe negati per device fuori dal gruppo
traces
| where message contains "device-not-in-allowed-group"
| project timestamp, message
```

## Struttura del repository

```
.
├── azure.yaml                          # (opzionale) per azd
├── infra/
│   ├── main.bicep                      # Function App, Storage+Queue, UAMI, AI, RBAC
│   └── main.parameters.json
├── src/                                # .NET 10 isolated worker
│   ├── Program.cs
│   ├── Functions/
│   │   ├── WipeRequestFunction.cs      # HTTP, valida + accoda
│   │   └── WipeProcessorFunction.cs    # Queue trigger, esegue wipe
│   ├── Services/
│   │   ├── ClientCertValidator.cs
│   │   └── GraphWipeService.cs
│   └── Models/
│       ├── WipeRequest.cs
│       └── WipeQueueMessage.cs
├── client/
│   └── Invoke-DeviceWipe.ps1           # PS 5.1 con UI WinForms
└── docs/
    └── Presentazione-Soluzione-Intune-Self-Wipe.eml
```

## Roadmap

- [ ] Validazione cert via `chain.Build()` con root CA pinning
- [ ] Notifica esito (Teams webhook / email) al termine del wipe
- [ ] Endpoint `GET /api/wipe/{correlationId}` per consultare stato
- [ ] APIM davanti alla Function con rate-limit per device
- [ ] Workflow GitHub Actions per CI/CD

## Licenza

[MIT](LICENSE) © Roberto Gramellini

---

> ⚠️ **Avviso.** Il wipe Intune è un'operazione **distruttiva e irreversibile**.
> Distribuisci questa soluzione solo dopo aver validato la propria CA SCEP,
> il gruppo Entra di allow-list e i meccanismi di approvazione/audit interni.
