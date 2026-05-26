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
