# Intune Device Self-Wipe API

Soluzione end-to-end per consentire ad un client (PS 5.1) di richiedere un wipe Intune del proprio device, autenticandosi con certificato distribuito da Intune.

## Componenti

1. **Azure Function (.NET 8 isolated, Flex Consumption)** — endpoint `POST /api/wipe`
   - Riceve `{ deviceName, entraDeviceId, intuneDeviceId }`
   - Valida il certificato client (header `X-ARR-ClientCert`) contro un allow-list di thumbprint o issuer
   - Verifica che il device sia nell'allow-list (Azure Table Storage)
   - Chiama Microsoft Graph (Managed Identity) per:
     - Verificare che `managedDevices/{intuneDeviceId}.azureADDeviceId == entraDeviceId`
     - Invocare `POST /deviceManagement/managedDevices/{id}/wipe`
   - Logga audit su Application Insights

2. **Azure Table Storage `allowedDevices`**
   - PartitionKey = tenant; RowKey = `entraDeviceId`
   - Properties: deviceName, intuneDeviceId, enabled, notes, addedBy, addedAt

3. **User-Assigned Managed Identity**
   - Permessi Graph (application):
     - `DeviceManagementManagedDevices.PrivilegedOperations.All`
     - `DeviceManagementManagedDevices.Read.All`
     - `Device.Read.All`

4. **Client PS 5.1 script** `Invoke-DeviceWipe.ps1`
   - Recupera Entra Device Id da `dsregcmd /status`
   - Recupera Intune Device Id da registry `HKLM:\SOFTWARE\Microsoft\Enrollments\*`
   - Recupera nome device
   - Carica certificato dispositivo (per thumbprint o subject) da `Cert:\LocalMachine\My`
   - Invoca l'API con `Invoke-RestMethod -Certificate`

## File principali

- `infra/main.bicep` + moduli (function app, storage, managed identity, app insights, role assignments)
- `src/IntuneWipeApi.csproj` + `Program.cs` + `Functions/WipeFunction.cs`
- `src/Services/ClientCertValidator.cs`
- `src/Services/DeviceAllowListService.cs`
- `src/Services/GraphWipeService.cs`
- `src/Models/WipeRequest.cs`
- `client/Invoke-DeviceWipe.ps1`
- `azure.yaml` per `azd up`
- `README.md` con istruzioni di deploy e configurazione Graph permissions

## Stato corrente (2026-05-28)

**Backend**: deployed su `intwipe-web-qupxwx6egkr3e` + `intwipe-proc-qupxwx6egkr3e` (RG `rg-intwipe-dev`).
- Wipe pipeline: HTTP (mTLS) → queue → worker → Graph wipe.
- **Post-wipe fallback** (best-effort): syncDevice (+60s) → rebootNow (+60s); opt-out via `Wipe:SyncFallbackDelaySeconds=0` / `Wipe:RebootFallbackDelaySeconds=0`.
- **Audit persistence**: dual-write App Insights `customEvents` + Azure Table `auditevents` su `storageProc` (PartitionKey=correlationId, RowKey=ticks_guid). Retention illimitata, query in Storage Explorer.

**Client v1.0.11** (intunewin rebuilt, da ri-pubblicare su Intune):
- Modulo `DeviceIdentity.psm1` condiviso (test-coverage Pester 26/26).
- Fix strict-mode su `Get-ClientCertificate` (single-pattern `-split` → array).
- Sync+reboot fallback lato server (nessun cambio client necessario per beneficiarne).

**TODO operativo**:
- Republish `IntuneWipeClient.intunewin` v1.0.11 su Intune (Publish-ToIntune.ps1).
- (Opzionale) Aggiungere lifecycle policy alla `auditevents` table se serve cap di retention.
- (Roadmap) Event Grid custom topic per fanout notifiche/SIEM, e system topic su Key Vault quando ci sposteremo trusted CA lì.
