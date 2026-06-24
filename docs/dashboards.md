# Operator Dashboards (cruscotto) — architettura

L'osservabilità della pipeline IntuneDeviceActions è divisa in **tre artefatti**
con responsabilità nette:

| Componente | Repo | Cosa fa | Note |
|---|---|---|---|
| **Dashboard API** | `intune-wipe-api` (questo repo) — `src/Web/Functions/DashboardFunction.cs` + `src/Web/Dashboard/DashboardTelemetryService.cs` | Endpoint JSON readonly + 1 POST di remediation, ospitati sul Web Function App (`idactions-web-<suffix>`). Sorgenti: ServiceBus admin, Ledger blob enumeration, KQL su App Insights. | È quello che il portale consuma. mTLS + Function key + cert allow-list. |
| **Operator portal (UI)** | **`intune-wipe-web`** (repo separato) — deployato su `idactions-portal` (App Service Linux B1, .NET 10) | Pagina HTML/JS che chiama gli endpoint qui sopra, mostra il "flusso di energia" stile Prius, timeline trace per correlationId, bottone Reset ledger. | Auth: Easy Auth / AAD. |
| **Grafana** | `intune-wipe-api` — `infra/dashboard-grafana.bicep` + `infra/grafana/intunedeviceactions-dashboard.json` | Esplorazione storica + alerting time-series + condivisione via URL. | Risorsa Azure separata SKU Essentials (~$10/mo). |

> **Storia**: una prima versione della dashboard era ospitata come pagina
> HTML embedded nel Web Function App. È stata rimossa: il portale operatore
> esiste già in `intune-wipe-web` ed è il posto giusto per la UI. Il Web
> Function App resta **solo** un'API readonly.

## Dashboard API — endpoint esposti

Tutti gli endpoint sono sotto `https://idactions-web-<suffix>.azurewebsites.net/api/dashboard/*`
e richiedono Function key + cert mTLS dell'operatore.

| Verb | Route | Risposta |
|---|---|---|
| `GET` | `/api/dashboard/data` | `DashboardSnapshot` — code SB, ledger summary + top stuck, diagnostics (poller heartbeat, freshness capability), warnings. |
| `GET` | `/api/dashboard/trace?corr={guid}` | `RequestTrace` — timeline di eventi App Insights per quel correlationId + summary ledger + `Recommendation` (severity, title, detail, actionKind ∈ {`reset-ledger`,`open-app-insights`,`open-azure-portal`,`none`}). |
| `GET` | `/api/dashboard/device?q={hostname-or-intuneId}&take=25` | Lista delle ultime N richieste viste in App Insights per quel device. |
| `POST` | `/api/dashboard/actions/reset-ledger` | Body `{"intuneDeviceId":"…","reason":"…"}`. Archivia l'entry corrente sotto `_archive/` + libera il device. Gated da `Idempotency:AdminApiEnabled=true`. |

DTOs autoritativi: vedi `src/Web/Dashboard/DashboardTelemetryService.cs`
(public records in fondo al file).

## Configurazione

App Configuration keys lette dal Web Function App:

```
Dashboard:Enabled                    = true
Dashboard:LogsWorkspaceId            = <LAW customerId GUID>   # richiesto per /trace e /device
Dashboard:AllowedCertThumbprints     = <thumb1>,<thumb2>       # opzionale; fallback ad Idempotency:AdminCertThumbprints
Idempotency:AdminApiEnabled          = true                    # richiesto per /actions/reset-ledger
Idempotency:AdminCertThumbprints     = <thumb-operatore>       # serve anche al portale per il reset
EventGrid:Enabled                    = true                    # abilita fanout near-realtime eventi audit
EventGrid:AuditTopicEndpoint         = https://<topic>.<region>-1.eventgrid.azure.net/api/events
```

Per recuperare il LAW customer ID:
```powershell
$ws = az monitor app-insights component show -g RG-INTUNE-DEVICEACTIONS -n idactions-ai-dev --query workspaceResourceId -o tsv
az monitor log-analytics workspace show --ids $ws --query customerId -o tsv
```

## RBAC richiesto sulla Web UAMI (`idactions-uami-web-dev`)

- `Monitoring Reader` sul namespace Service Bus (overview code).
- `Storage Blob Data Reader` sullo storage `idactionsstpdev` container `action-ledger`.
- `Log Analytics Reader` sul workspace `idactions-law-dev` (`/trace` e `/device`).

## Recommendation engine (mappature)

Implementato in `DashboardTelemetryService.Recommend()`. Schema:

| Pattern di eventi | Severity | Action consigliata |
|---|---|---|
| `action.already-issued` come ultimo evento + ledger Issued senza terminale | warn | **reset-ledger** (caso FC1DSK005) |
| `*.action.failed` o exception | error | **open-app-insights** sul correlationId |
| `*.action.completed` come ultimo evento | ok | none — il comando è in Graph |
| `*.action.consumed` recente (<5 min) senza terminale | warn | wait |
| `*.action.consumed` vecchio senza terminale | error | **open-app-insights** (runner crashato) |
| Eventi mancanti (es. `received` ma niente `forwarded`) | warn | **open-azure-portal** sulla coda upstream |

## Opzione Grafana (analisi storica)

Standalone — `infra/dashboard-grafana.bicep` crea
`Microsoft.Dashboard/grafana@2024-10-01` SKU Essentials con role assignment
`Monitoring Reader` + `Reader` sull'intero **resource group** (più
`Storage Blob Data Reader` sul ledger SA). Import dei JSON in `infra/grafana/`:

| Dashboard JSON | UID | Cosa mostra |
|---|---|---|
| `intunedeviceactions-dashboard.json` | _(assegnato all'import)_ | Cruscotto operativo: SB backlog, runner, ledger bloccati, intake HTTP, latenza Graph. |
| `infrastructure-health-dashboard.json` | `idactions-infra-health` | **Salute infrastrutturale** di tutto lo stamp: Function App (response time, 5xx, requests, esecuzioni, memoria), Storage (availability, transactions, latenza, throttling, **PNA via Azure Resource Graph**), Service Bus (errori/throttle/throughput), App Configuration, Event Grid (publish/dead-letter), Automation (runbook jobs per stato), e disponibilità end-to-end (App Insights). |

### Infrastructure Health dashboard

Portabile tra stamp: nessun resource ID hardcoded. Quattro variabili `constant`
(nascoste) parametrizzano tutto — `subscription`, `resourceGroup`, `prefix`
(default `devact`), `suffix` (default `dev`). I `resourceUri` dei pannelli Azure
Monitor sono costruiti per interpolazione (`${prefix}-web-${suffix}`,
`${prefix}stw${suffix}`, …). Per un altro stamp basta cambiare le variabili in
fase di import.

> **Sorgente di verità**: non editare il JSON a mano. Modifica
> `infra/grafana/gen_infra_dashboard.py` e rigeneralo con
> `python infra/grafana/gen_infra_dashboard.py`. Quando aggiungi una capability,
> aggiungi la sua entry in `WEB_SITES` / `STORAGE` nel generatore.

Import:
1. Deploy del modulo Grafana (vedi header di `infra/dashboard-grafana.bicep`).
2. Nel portale Grafana aggiungi il data source **Azure Monitor** (auto via MSI).
3. Importa il JSON; mappa `DS_AZURE` (metriche + Resource Graph) e `DS_AI`
   (Log Analytics/App Insights) sul data source Azure Monitor.

Il pannello **Public Network Access (PNA) per storage account** rispecchia lo
scheduled check orario: se uno storage compare con `publicNetworkAccess =
Disabled`, un job esterno l'ha disabilitato e va riabilitato.

## Path Event Grid (near-realtime)

`infra/main.bicep` e `infra/main-public.bicep` ora provisionano (flag
`enableEventGridAuditStream=true`):

- custom topic Event Grid per stream audit (`action.*`, `wipe.*`, `autopilot.*`, …);
- role assignment `EventGrid Data Sender` a tutte le UAMI dei Function App;
- wiring runtime via app settings `EventGrid__Enabled` + `EventGrid__AuditTopicEndpoint`;
- subscription webhook opzionale verso il portale (`eventGridDashboardWebhookEndpoint`)
  con retry + dead-letter su blob container `eventgrid-deadletter`.

In pratica: ogni `AuditService.TrackEvent(...)` continua a scrivere su App
Insights + tabella audit, e in più pubblica lo stesso evento sul topic Event Grid.
