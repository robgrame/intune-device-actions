# Architectural improvements — `intune-wipe-api`

> Analisi prodotta il **2026-05-27** a valle dello split web/worker, storage e
> App Service Plan (commit `95d61d7`). Fonte: rubber-duck architectural review
> agent + sintesi/raccomandazioni.
>
> **Scope:** miglioramenti di design, resilienza, sicurezza architetturale,
> osservabilità, deployment, scalabilità. Non sono code review né style fixes.

---

## Valutazione complessiva

La soluzione è già architetturalmente solida:

- Split `funcWeb` / `funcProc` su due Function App Linux EP1 dedicate
- Due UAMI separate (`uamiWeb` no-Graph, `uami` worker Graph-consented)
- Due storage account separati (web runtime vs proc runtime+ledger+queue)
- RBAC scoped (Queue Data Message Sender solo sulla coda)
- mTLS Required, cert↔device binding, anti-replay, allow-list group, ownership
  match, ledger di idempotenza
- `AppRoleGuard` come fail-closed in-code

**I gap residui non sono controlli di sicurezza di base — sono maturità
operativa, durabilità dell'audit, governance del deployment, visibilità di
stato, e resilienza delle operazioni distruttive.**

---

## Roadmap sintetica

| Horizon | Focus | Items |
|---|---|---|
| **1 (Now / pre-prod)** | Audit & operatività | #4 audit non-sampled · #5 alerting · #9 CI/CD OIDC · #2 rimozione Function Key client · #6 idempotenza Graph esplicita · #13 signing client PS |
| **2 (Next)** | Maturità funzionale | #7 status API · #11 Graph throttling/Polly · #3 CA trust lifecycle Key Vault · #12 server-side Intune resolution · #10 deployment slots |
| **3 (Later)** | Hardening enterprise | #1 APIM/App Gateway WAF edge · #8 private endpoint storage+function · #14 PIM/Azure Policy/operator governance |

**Top-3 ROI suggeriti per partire:**

1. **#4 + #5 audit durabile + alerting** — costo basso, copre il blind spot operativo più grosso.
2. **#9 CI/CD con post-deploy boundary checks** — l'isolamento dipende da config, va verificato in continuo.
3. **#6 idempotenza Graph esplicita** (`Submitting` state) — unica ambiguità di design destructive rimasta, effort S/M.

---

# Proposte dettagliate

## 1. Edge controllato davanti alla Function App pubblica

### Current state
`funcWeb` è pubblica su `*.azurewebsites.net`, con App Service mTLS Required, Function Key auth, validazione certificate in-code, replay protection e permessi enqueue-only.

### Gap / risk
La Function App stessa è il boundary internet-facing. Nessun WAF/rate-limit di prima classe, nessun throttling per-device, nessuna policy API centralizzata, e la Function Key va distribuita ai client.

### Proposta
- **Azure API Management** o **Application Gateway WAF v2** in fronte.
- Backend Function App raggiungibile solo via **Private Endpoint** o access restriction strette.
- Edge termina/valida il client cert.
- Backend si fida del cert forwarded solo dal private edge.
- Function Key diventa secret backend-only.
- (APIM) rate-limit per thumbprint device, request size limits, audit headers, OpenAPI import, versioning policy.
- (App Gateway) WAF policy, mutual auth, private backend, custom domain.

### Effort / impact
**L.** Aggiunge edge infra, custom domain, cert management, networking privato, policy lifecycle. Costo: APIM Premium/Standard v2 o App Gateway WAF significativamente più caro del Function-only.

### Priorità
**Should** per uso enterprise. Il modello attuale è accettabile per pilot controllato; una API distruttiva merita un edge esplicito prima del rollout broad.

---

## 2. Rimuovere la Function Key client-distribuita dal trust model

### Current state
Il client PowerShell invia `x-functions-key`. README e client si aspettano la key distribuita ai device.

### Gap / risk
La Function Key diventa un bearer secret condiviso. Se leakata da package Intune, log, copie locali script o gestione operatore, è difficile da scoping per device e da ruotare senza redeploy client.

### Proposta
- APIM/App Gateway in fronte chiama il backend con la key.
- Client autentica solo con mTLS.
- Eventuali APIM subscription keys per deployment ring, ma mai raw function key embedded nel client.
- Function Keys e secret edge/backend in **Key Vault**.
- Rotazione documentata zero-downtime dual-key.

### Effort / impact
**M** se accoppiato a APIM/App Gateway; **S** per solo Key Vault + docs rotazione. Costo legato alla scelta edge; Key Vault trascurabile.

### Priorità
**Must** prima della distribuzione production large-scale.

---

## 3. Certificate trust lifecycle gestito, non parameter-driven

### Current state
Trusted CA thumbprint/base64 sono parametri Bicep / app settings. Revocation configurabile ma disabilitato di default. Rotazione CA manuale.

### Gap / risk
SCEP/PKCS CA rollover è facile da sbagliare. Pin stale → outage; trust anchor dimenticato → trust silenziosamente esteso. Online revocation può fallire dal sandbox.

### Proposta
- CA certs/thumbprint in **Key Vault** come versioned secrets/certificates.
- App Settings con Key Vault references.
- Trust overlapping `current` + `next` CA.
- Auto-check expiry CA, alert su:
  - CA in scadenza 30/60/90 gg
  - spike cert-validation failures
  - revocation-check failures
- Runbook documentato per rollover.

### Effort / impact
**M.** Richiede Key Vault, RBAC, runbook, monitoring. Costo basso.

### Priorità
**Should.** Fail-closed attuale è buono, ma la sopravvivenza in produzione dipende da rotazione CA prevedibile.

---

## 4. Audit durabile e disabilitazione sampling per eventi security-critical

### Current state
Audit principalmente `ILogger` con prefisso `AUDIT`. Application Insights workspace-backed, retention Log Analytics 30 giorni. `host.json` con sampling AppInsights attivo.

### Gap / risk
Operazione distruttiva → serve audit durabile, queryable, non-sampled. Sampling può droppare trace. 30 giorni probabilmente troppo poco per security/compliance/investigation.

### Proposta
- Emettere eventi strutturati come `customEvents`:
  - `wipe.request.accepted`
  - `wipe.denied.cert`
  - `wipe.denied.replay`
  - `wipe.denied.group`
  - `wipe.graph.issued`
  - `wipe.graph.failed`
  - `wipe.poisoned`
- **Disabilitare sampling** per eventi audit/security.
- Estendere retention LA o export verso:
  - Storage Account immutable blob (time-based retention)
  - Event Hub / Sentinel se in uso
- AppInsights Workbook con:
  - wipe per status
  - denies per reason
  - cert validation failures
  - Graph errors
  - queue backlog
  - poison messages
  - role mismatch
- Query KQL baseline nel repo.

### Effort / impact
**M.** Aggiunge monitoring asset e gestione retention. Costo: incremento Log Analytics/storage modesto a volumi attesi.

### Priorità
**Must** per production. Operazione distruttiva → audit deve essere durabile e non-sampled.

---

## 5. Alerting e runbook per failure modes

### Current state
Queue retries con default Functions. README menziona poison queue ma nessun alert Bicep-defined, poison handler, workbook o reprocessor.

### Gap / risk
Throttling Graph, worker down, config rotta, poison accumulati → operatori scoprono solo da segnalazione utente.

### Proposta
Alert operativi:
- Storage Queue message count sopra soglia
- Poison queue message count > 0
- Worker Function failures
- Spike Graph 429/5xx
- Spike Graph 4xx permanenti
- Spike cert validation failures
- Spike replay failures
- Occurrence `app-role-mismatch`
- Nessuna execution worker successful nel window atteso
- Ledger `Reserved` oltre threshold

Reprocessor / runbook manuale:
- Ispezione poison message
- Re-validazione device + group
- Decisione replay / mark failed / suppress
- Preservare audit trail

### Effort / impact
**M.** Bicep alert rules, action groups, KQL, runbook. Costo basso-medio.

### Priorità
**Must.** Sistema async → necessita visibilità esplicita.

---

## 6. Semantica idempotenza esplicita sui side-effect Graph

### Current state
Worker prenota ledger entry → chiama Graph wipe → marca `Issued`. Se ledger è `Reserved` con stesso correlation id, processing prosegue.

### Gap / risk
Finestra di ambiguità su operazione distruttiva:
1. Worker reserve ledger
2. Worker chiama Graph wipe
3. Graph accetta
4. Worker crasha/timeout prima di `MarkIssuedAsync`
5. Queue retry
6. Ledger ancora `Reserved` → wipe Graph richiamata

Seconda wipe probabilmente innocua in pratica, ma il sistema non ha exactly-once semantics su side-effect esterno.

### Proposta
Scegliere e documentare strategia esplicita:

- **Conservative**: stato `Submitting`/`Attempted` immediatamente prima della Graph call. Se retry vede `Attempted` → non richiama Graph automaticamente, va a reconciliation manuale.
- **Operational**: accettare esplicitamente "at-least-once Graph POST" e provare che doppio wipe è safe.
- **Mature**: Durable Functions orchestration per lifecycle tracking, comunque trattare Graph call come side-effect non idempotente.

Aggiungere stato `Stuck/Unknown` al ledger per attempts ambigui.

### Effort / impact
**M.** Code + runbook update. Costo trascurabile (più alto se Durable Functions).

### Priorità
**Should**, potenzialmente **Must** in base al risk appetite. Per una API distruttiva, l'handling ambiguo va reso esplicito.

---

## 7. Status API e modello di stato durabile della richiesta

### Current state
`POST /api/wipe` → `202 Accepted` con `correlationId`. Nessun endpoint per il client o support desk per sapere se denied, issued, failed, poisoned, stuck.

### Gap / risk
Il client sa solo "in coda". Support deve cercare manualmente nei log per `correlationId`. Utente non distingue "queued" da "denied later" da "Graph failed" da "wipe issued".

### Proposta
Request status store:
- Table Storage / Cosmos / structured blob keyed by `correlationId`.
- Stati: `Accepted/Denied/Authorized/Reserved/Issued/FailedPermanent/Retrying/Poisoned/Unknown`.
- `GET /api/wipe/{correlationId}` con stesso mTLS/device binding.
- **Non** dare al `funcWeb` accesso broad al ledger: o status table separato con read-only per web, o status function dedicata low-priv.

### Effort / impact
**M.** Data model + status writer + endpoint + client. Costo molto basso.

### Priorità
**Should.** Migliora materialmente supportabilità e UX.

---

## 8. Hardening rete su storage e worker

### Current state
Entrambi gli storage hanno `publicNetworkAccess: Enabled`. `funcProc` non è intended-public ma site infra è comunque esposta. Storage `Standard_LRS`.

### Gap / risk
Isolation RBAC/identità è forte ma layer rete è permissivo. Public network access aumenta esposizione a misconfig e credential abuse.

### Proposta
Per production:
- Storage account dietro Private Endpoint
- Functions con VNet integration
- Disable `publicNetworkAccess` su entrambi gli storage se compatibile
- Restringere public access su `funcProc`
- SCM/Kudu restrictions
- Considerare Private Endpoint anche su `funcProc`
- Azure Policy che nega drift public storage/network
- ZRS/GZRS dove serve resilienza regionale

### Effort / impact
**L.** Private networking + DNS + VNet aumenta complessità deploy. Costo: PE, VNet, DNS, redundancy SKU più alti.

### Priorità
**Should** production / **Nice-to-have** pilot.

---

## 9. CI/CD reale e deployment governance

### Current state
README documenta deploy manuale (`az deployment group create`, `dotnet publish`, `Compress-Archive`, `az functionapp deployment source config-zip`). Nessun GitHub Actions workflow nel repo. Stesso zip su entrambe le app.

### Gap / risk
Deploy manuale → drift + provenance debole. Per un'operazione distruttiva deve essere chiaro chi ha deployato cosa, quale artifact gira, se infra/config sono drift.

### Proposta
GitHub Actions / Azure DevOps:
- **OIDC** federation a Azure (no publish credentials)
- Build/test/publish una volta sola
- Artifact firmato o hash-pinned
- Deploy stesso immutable zip a entrambe le app
- `bicep what-if` pre-deploy
- Post-deploy verification:
  - App__Role settings
  - disabled function selectors
  - UAMI assignments
  - **assert web UAMI non ha Graph permissions**
  - storage RBAC scopes
- Environment dev/test/prod
- Approval per prod
- Drift detection scheduled
- Disable local/basic publishing dove fattibile

### Effort / impact
**M.** Pipeline + federated identity + approval + validation scripts. Costo basso.

### Priorità
**Must** prima di production.

---

## 10. Deployment slots blue/green

### Current state
Deploy sostituisce package direttamente su entrambe le app.

### Gap / risk
Deploy difettoso può rompere endpoint pubblico o worker. Poiché stesso package è deployato a entrambe, problema deploy può colpire entrambi i ruoli.

### Proposta
- Staging slot per `funcWeb` e `funcProc`
- Warm-up + health-check per ruolo:
  - web: validazione cert path + enqueue
  - proc: read queue + resolve Graph dependencies in dry-run/test
- Swap
- Rollback path all'artifact precedente

**Attenzione:** staging worker NON deve processare coda production a meno che sia explicitly intended.

### Effort / impact
**M.** Slot config + deployment choreography. Costo: overhead slot resource su Premium.

### Priorità
**Should.** Particolarmente utile dato che stesso package va a 2 app con behavior config-selected.

---

## 11. Graph throttling e controllo coda

### Current state
Errori Graph classificati transient/permanent. Transient → throw → default queue retry. Nessun `host.json` queue settings, concurrency limit, o Polly esplicito.

### Gap / risk
Retry default troppo blunt. Burst di wipe può amplificare throttling Graph. Mancano visibility timeout, max dequeue count, concurrency tuning espliciti.

### Proposta
- `host.json` queue: `maxDequeueCount`, `visibilityTimeout`, `batchSize`, `newBatchThreshold`
- Concurrency cap per evitare burst Graph
- Retry policy che onora `Retry-After`
- Jittered exponential backoff
- Circuit breaker su sustained 429/5xx
- Metriche retry count, dequeue count, throttling

### Effort / impact
**M.** Code/config + load testing. Costo trascurabile.

### Priorità
**Should.** Modello attuale ok per low volume; ma destructive ops devono fallire predicibilmente sotto throttling.

---

## 12. Lifecycle tenant/device

### Current state
Client manda `entraDeviceId` + `intuneDeviceId`. Worker verifica `managedDevice.azureADDeviceId == entraDeviceId`. Lookup fail → log denial/permanent failure.

### Gap / risk
Edge case lifecycle creano false negative:
- Device recently enrolled non ancora indicizzati
- Stale Intune enrollment registry values
- Multiple managedDevice per un Entra device
- Hybrid join / registered differences
- Retired/disposed
- Device rename mismatch
- Intune lag dopo wipe/retire

### Proposta
Spostare resolution authority server-side:
- `intuneDeviceId` come hint, non lookup primario
- Resolve Intune managed devices via `azureADDeviceId`
- Gestione record multipli: seleziona per enrollment state / last sync / management agent
- Retry-with-delay per "not found" probabile eventual consistency
- Stati terminali espliciti: `NotInIntune`, `MultipleCandidates`, `RetiredOrDisposed`, `RecentlyEnrolledPending`
- Includere stati in status API/support view

### Effort / impact
**M/L** in base a complessità Graph query e test coverage. Costo trascurabile.

### Priorità
**Should.** Riduce friction support ed evita reject di device legit per lifecycle timing.

---

## 13. Client distribution, signing, telemetry

### Current state
Client PS 5.1 raccoglie identity, seleziona cert, UI di conferma, chiama API. Accetta `FunctionKey` come parametro. Nessun script signing, hash verification, version telemetry, client event logging.

### Gap / risk
Client è parte del trust boundary. Se modificato, downgraded, misconfigurato → leak secret, wrong cert, poor support data.

### Proposta
- Sign script PS con cert enterprise code-signing
- Distribuire come Intune Win32 app con hash/version pinnati
- Client version in `User-Agent` o custom header
- Log client su Windows Event Log
- Niente Function Key embedded nel package
- Pin expected API host / custom domain
- Optional dry-run health check (cert/identity selection senza enqueue)
- Script integrity verification se packaged con helper

### Effort / impact
**M.** Code-signing process + Intune packaging + release discipline. Costo basso se code-signing infra già presente.

### Priorità
**Should.** Backend sicuro può essere undermined da client distribution debole.

---

## 14. Controlli privileged-operator e supply-chain

### Current state
Architettura separa fortemente identità web/worker. Ma operatori Azure con sufficient App Service/RBAC possono alterare app settings, swappare identità, deploy zip malizioso, cambiare `App__Role`, assegnare Graph permissions sbagliate.

### Gap / risk
Insider/operator e supply-chain risk non pienamente coperti da runtime identity isolation.

### Proposta
- Ruoli separati: app deployers / infra deployers / Graph consent admins / security auditors
- **PIM elevation** per prod changes
- Azure Policy:
  - web UAMI **non** deve avere Graph app roles
  - storage public access restrictions
  - required diagnostic settings
  - required managed identity
- Resource locks su risorse critiche dove pratico
- Disable basic auth / FTP / local publishing
- Solo CI/CD deployment
- Artifact hash/version in app settings, loggato all'avvio
- Alert su:
  - cambio app settings
  - cambio identità
  - cambio role assignments
  - cambio Graph app role assignments

### Effort / impact
**M/L** in base a maturità governance tenant. Costo basso-medio (process + policy).

### Priorità
**Should** per enterprise production. Il rischio residuo più alto è probabilmente operatore over-privileged, non attaccante esterno.

---

## Note di stato

Già implementati e quindi **fuori scope** di questo documento:
- Split web/worker in due Function App separate
- Split UAMI (web no-Graph, worker Graph-consented)
- Split storage account (web runtime vs proc runtime+ledger+queue)
- RBAC scoped (Queue Data Message Sender sulla singola coda)
- Split App Service Plan (due Linux EP1 dedicati)
- `AppRoleGuard` fail-closed in-code come defense-in-depth
- Migrazione Windows → Linux per i due Function App

Riferimenti commit: `658ec83`, `9ba7d3d`, `96e6363`, `95d61d7`.
