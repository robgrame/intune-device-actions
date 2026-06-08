# IntuneDeviceActions — Roadmap remediation banking-grade

> **Scopo.** Sizing realistico dell'effort necessario per chiudere i 10 gap
> banking-grade emersi dalla security-review (`docs/security-compliance-banking.md`
> Parte 2) e arrivare a uno stato "completamente compliant" agli standard
> finanziari europei (PCI-DSS v4.0, ISO 27001:2022, EBA/GL/2019/04, DORA,
> Banca d'Italia 285/13).
>
> **Riferimento.** Tutte le stime sono **person-day** per un senior backend
> .NET/Azure con esperienza Function App + Bicep + Service Bus. Includono
> codice + IaC + test unitari + smoke deploy in un environment di dev.
> **Non includono** assessment indipendente (PCI QSA, ente ISO, pentest)
> né formalizzazione documentale lato cliente.
>
> **Punto di partenza.** Commit `e9622fd` (HIGH chiuso: mTLS admin + actor
> verificato). Effort già speso per il fix HIGH: ~1 ora.

---

## Sintesi: 3 sprint da 4-6 settimane totali

| Sprint | Settimane | Focus | Output |
|---|---|---|---|
| **S1 — Quick wins** | 1 | #1 anti-replay distribuito, #5a revocation default-on, #7 log retention, #9 trust-anchor strict, #10 separation host key | Audit baseline conforme |
| **S2 — Key & audit posture** | 2 | #3 Key Vault HSM, #6 audit WORM/immutable, #4a Storage CMK | Custody + tamper-evidence banking-grade |
| **S3 — Frontiera regolatoria** | 2-3 | #2 HTTP Message Signatures, #5b OCSP stapling APIM, #8 Hybrid Worker AAD-auth, #4b Service Bus Premium CMK | DORA/EBA full alignment |

**Totale**: 5-6 settimane (1 senior) per arrivare a stato fully-compliant
sui 10 gap. Quick wins sbloccano già un audit baseline in 1 settimana.

---

## Dettaglio per gap

### Gap #1 — Anti-replay distribuito

| | |
|---|---|
| **Effort** | 2-3 person-day |
| **Rischio** | Basso (pattern noto) |
| **Dipendenze** | Nessuna |
| **Costo infra** | Azure Table: ~zero (riusa storage esistente); Redis Cache Basic: ~$15/mese |
| **Breaking change** | Nessuno (transparent per client) |
| **Approccio** | Sostituire `IMemoryCache` in `ReplayProtector.cs` con `Azure.Data.Tables` client; PK = giorno UTC (per TTL via rolling cleanup), RK = nonce. Cleanup job giornaliero o Timer Function. |

### Gap #2 — HTTP Message Signatures (RFC 9421)

| | |
|---|---|
| **Effort** | 5-8 person-day |
| **Rischio** | **Medio-alto** — librerie .NET poco mature (`HttpMessageSignatures` community), client PS 5.1 challenging per crypto API |
| **Dipendenze** | Decisione strategica: tenere il client PS 5.1 o upgrade a PS 7? PS 7 ha `System.Security.Cryptography` completo. |
| **Costo infra** | Nessuno |
| **Breaking change** | **Sì** — contratto API nuovo, serve versioning `/api/v2/actions` o feature flag transitorio |
| **Approccio** | Server: middleware che verifica `Signature-Input` + `Signature` (HMAC della chiave pubblica del cert client su `@method @path digest`). Client: firmare con la chiave privata del cert macchina. |

### Gap #3 — Key Vault HSM-backed per secret operativi

| | |
|---|---|
| **Effort** | 2-3 person-day |
| **Rischio** | Basso |
| **Dipendenze** | Decisione cliente: KV Premium (FIPS 140-2 L2) sufficiente o serve Managed HSM (FIPS 140-3 L3)? |
| **Costo infra** | KV Premium: ~$1/mese + $0.03/10k ops. Managed HSM: ~$3.50/ora ≈ $2520/mese |
| **Breaking change** | Nessuno (App Configuration supporta KV references nativamente) |
| **Approccio** | Provisionare KV Premium + soft-delete + purge protection + RBAC; migrare `Rename:AuthHeaderValue`, `RunbookBridge:Routes:*` e altri segreti in KV; sostituire i valori App Config con `{"uri":"https://kv.../secrets/<name>"}`. |

### Gap #4 — Customer-Managed Keys (CMK) at-rest

| | |
|---|---|
| **Effort** | 3-5 person-day (storage); +3 giorni se anche Service Bus |
| **Rischio** | Medio (rotazione CMK su Storage richiede rebind di tutti gli account) |
| **Dipendenze** | **#3 deve essere completato prima** (CMK key vive in KV); Service Bus CMK richiede tier Premium |
| **Costo infra** | Storage CMK: ~zero overhead. **Service Bus Premium**: ~$670/mese (vs Standard $10/mese) ⚠️ |
| **Breaking change** | Nessuno (encryption è transparent al codice) |
| **Approccio (#4a Storage)** | Sui 6 Storage Account: `encryption.keySource = Microsoft.KeyVault`, `keyVaultProperties` con la KV key di #3, UAMI con `Key Vault Crypto Service Encryption User`. **Test**: rotation manuale + verifica re-encryption. |
| **Approccio (#4b SB Premium)** | Upgrade Service Bus a Premium SKU + abilitare CMK. **Valutare cost-benefit**: l'audit BdI/EBA non sempre richiede CMK su SB; spesso accettato Microsoft-managed se storage è CMK. |

### Gap #5 — Revocation check + OCSP

| | |
|---|---|
| **Effort (5a default-on)** | 0.5 person-day |
| **Effort (5b OCSP stapling all'edge)** | 5-7 person-day (richiede APIM/App Gateway) |
| **Rischio** | Medio (latenza CA in critical path: serve cache OCSP + timeout robusti) |
| **Dipendenze** | CA Intune SCEP/PKCS deve avere CRL/OCSP esposti (di solito sì). APIM Standard v2: ~$700/mese |
| **Breaking change** | Solo se cert revocati venivano usati erroneamente (5a). |
| **Approccio (5a)** | Cambiare default in `infra/*.parameters.json`: `ClientCert:CheckRevocation=true`, `RevocationMode=Online`, `RevocationFlag=ExcludeRoot`. Smoke test con cert dispositivo valido. |
| **Approccio (5b)** | Posare APIM davanti alla Function App (variante hardened); abilitare `validate-client-certificate` policy con OCSP stapling. Già nella roadmap del README. |

### Gap #6 — Audit tamper-resistance (WORM)

| | |
|---|---|
| **Effort** | 5-7 person-day |
| **Rischio** | Medio (perdita query Azure Table — Kusto su Log Analytics è ok ma diverso; serve refactor view operative) |
| **Dipendenze** | Decisione cliente: retention policy 5/7/10 anni? (impatta costo storage) |
| **Costo infra** | Storage immutable: ~$0.018/GB-mese × volume audit × anni. Per 50 GB×7 anni ≈ $6/mese |
| **Breaking change** | Sì sui consumer del table `auditevents` (portal, query SecOps). Va offerto periodo di doppia scrittura. |
| **Approccio** | Opzione A (consigliata): export App Insights → Storage Blob con `immutableStorageWithVersioning` + policy 7y; nuovo sink `AuditAppendBlobSink` per la tabella locale. Opzione B: refactor `AuditTableSink` → blob append-only; UAMI di scrittura distinta da UAMI di lettura (no delete). |

### Gap #7 — Log retention esplicita

| | |
|---|---|
| **Effort** | 1 person-day |
| **Rischio** | Basso |
| **Dipendenze** | Può essere unificato con #6 (stessa policy 5-7 anni WORM su Storage export) |
| **Costo infra** | Coperto da #6 |
| **Breaking change** | Nessuno |
| **Approccio** | Bicep: `retentionInDays = 365` su Log Analytics workspace + App Insights linked; aggiungere `dataExport` rules verso lo Storage immutable. |

### Gap #8 — Automation Account hardening

| | |
|---|---|
| **Effort** | 8-12 person-day |
| **Rischio** | **Alto** — Hybrid Worker = nuova VM/VMSS da gestire (patching, hardening, monitoring); refactor del `RunbookBridge` per AAD-auth job submission |
| **Dipendenze** | Decisione strategica: la variante runbook è essenziale in prod o è solo demo? In molti contesti bancari, runbook viene **rimossa** in favore della sola variante Function. |
| **Costo infra** | Hybrid Worker VM (B2s): ~$30/mese. Patching/AV/SIEM: +ops overhead |
| **Breaking change** | Sì — sparisce il webhook URL come API contract. |
| **Approccio** | (1) `disableLocalAuth=true` su AA; (2) provisionare Hybrid Worker su VNet con PE; (3) refactor `RunbookBridgeForwarder` → `Az.Automation` SDK job submission con token UAMI; (4) firewall rules + NSG. **Alternativa più semplice**: rimuovere la variante runbook in produzione, tenerla solo per dev. |

### Gap #9 — TrustedCaCertificates legacy strict

| | |
|---|---|
| **Effort** | 0.5-1 person-day |
| **Rischio** | Basso (potenziale breaking change per chi usa il path legacy con intermedi — segnalare in CHANGELOG) |
| **Dipendenze** | Nessuna |
| **Costo infra** | Nessuno |
| **Breaking change** | Sì se cliente ha intermedi nel campo legacy — va migrato a `TrustedIntermediateCertificates` |
| **Approccio** | In `ClientCertValidator.cs` ctor: per ogni cert in `TrustedCaCertificates`, verificare `cert.SubjectName == cert.IssuerName` (self-signed); altrimenti `throw InvalidOperationException` con messaggio "use TrustedIntermediateCertificates instead". Test unitario con cert intermediate. |

### Gap #10 — Separation host key admin

| | |
|---|---|
| **Effort** | 1-2 person-day |
| **Rischio** | Basso |
| **Dipendenze** | Nessuna |
| **Costo infra** | Nessuno |
| **Breaking change** | Solo per gli script SecOps esistenti che usano la default key (vanno riconfigurati per usare la `admin` key) |
| **Approccio** | (a) Cambiare `[HttpTrigger(AuthorizationLevel.Function, …)]` di `ActionLedgerAdminFunction` in un attribute custom che valida una host-key dedicata; **oppure** (b) creare 4ª Function App `idactions-admin-*` con solo gli endpoint admin, Private-Endpoint-only — più costoso ma più pulito. |

---

## Tabella effort + costo riassuntiva

| Gap | Effort (pd) | Costo infra/mese aggiunto | Risk | Sprint |
|---|---|---|---|---|
| #1 anti-replay distribuito | 2-3 | ~$0-15 | Low | S1 |
| #2 HTTP Message Sig. | 5-8 | $0 | **High** | S3 |
| #3 Key Vault HSM | 2-3 | $1-2520 | Low | S2 |
| #4a Storage CMK | 3-5 | ~$0 | Med | S2 |
| #4b SB Premium CMK | 3 | **+$660** | Med | S3 (opt) |
| #5a Revocation default-on | 0.5 | $0 | Low | S1 |
| #5b OCSP stapling APIM | 5-7 | +$700 | Med | S3 (opt) |
| #6 Audit WORM | 5-7 | ~$6-30 | Med | S2 |
| #7 Log retention | 1 | (incl. #6) | Low | S1 |
| #8 AA Hybrid Worker AAD | 8-12 | +$30-100 | **High** | S3 (or drop runbook) |
| #9 TrustedCa strict | 0.5-1 | $0 | Low | S1 |
| #10 Separation host key | 1-2 | $0 | Low | S1 |
| **TOTALE realistico** | **36-54 pd** | **+$30-3300/mese** | | |

> **NB sui costi**: i due grandi voci ($660 SB Premium, $700 APIM, $2520 HSM)
> sono **opzionali** in funzione del livello di compliance richiesto. Una
> deployment "EBA-conforme senza FIPS L3" può cavarsela con KV Premium ($1/mese)
> + Storage CMK (gratuita) + immutable audit ($6/mese) = ~$10-50/mese aggiuntivi.

---

## Phasing consigliato

### Sprint 1 (1 settimana, ~1 person-week)
**Quick wins, baseline audit-pass.**

- #1 anti-replay distribuito (Table)
- #5a revocation default-on
- #7 log retention esplicita 365gg
- #9 TrustedCa strict
- #10 separation host key admin

**Outcome**: superi un audit baseline (ISO 27001 controls + PCI-DSS technical baseline).
Costo infra aggiuntivo: <$20/mese.

### Sprint 2 (2 settimane, ~2 person-weeks)
**Custody + integrity banking-grade.**

- #3 Key Vault Premium (HSM L2)
- #4a Storage CMK
- #6 Audit immutable WORM 7y + UAMI write/read split

**Outcome**: PCI-DSS 3.5/3.7/10.5 + ISO A.12.4.2 + EBA §3.4.5 coperti.
Costo infra aggiuntivo: ~$10/mese.

### Sprint 3 (2-3 settimane, ~2-3 person-weeks)
**Frontiera regolatoria DORA/EBA full + opzioni costose.**

- #2 HTTP Message Signatures (richiede decisione PS 5.1 vs PS 7 sul client)
- #5b OCSP stapling via APIM (richiede APIM nel perimetro)
- #8 Hybrid Worker + AAD-auth job submission **OPPURE** rimuovere runbook variant in prod
- #4b Service Bus Premium CMK **OPPURE** accettare residual Microsoft-managed (decisione cliente)

**Outcome**: DORA art. 9 §4 + EBA/GL/2019/04 full alignment.
Costo infra aggiuntivo: $30/mese (minimal) → $2-3k/mese (maximal con APIM+SB Premium+HSM L3).

---

## Decisioni cliente bloccanti

Prima di partire con S2 servono 3 decisioni dal team Risk/Compliance:

1. **Livello FIPS richiesto**: 140-2 L2 (KV Premium, ~$1/mese) oppure 140-3 L3 (Managed HSM, ~$2500/mese)?
2. **Retention audit log**: 5, 7 o 10 anni? (impatta costo storage immutable e mappatura BdI 285/13)
3. **Runbook variant in prod**: mantenere con Hybrid Worker (+$30/mese ops overhead) o solo via Function (più semplice, perde la "demo plug-in" path)?

Prima di partire con S3 (gap #2) serve la 4ª decisione:

4. **Client PS 5.1 → PS 7?** PS 5.1 ha API crypto limitate per HTTP Message Signatures. PS 7 è strongly recommended ma molti endpoint bancari sono ancora su Windows Server 2016/2019 con PS 5.1 nativo.

---

## Effort di assessment (out of scope per noi)

Costi una-tantum lato cliente non inclusi nelle stime sopra:

- **Penetration test** banking-grade: $8k-25k
- **PCI QSA assessment**: $15k-40k (dipende dal scope)
- **ISO 27001 surveillance/certification audit**: variabile per accreditation body
- **DPIA GDPR** (se device identifiers correlabili a persona): 3-5 person-day legali
- **Threat model formale** (es. STRIDE workshop): 5-10 person-day team

---

## Manutenzione ricorrente

Una volta chiusi i 10 gap, l'overhead operativo aumenta di:

- **Key rotation** (KV HSM keys, CMK): 0.5 pd/trimestre
- **Cert lifecycle** (operator certs allow-list, CA trust anchor): 0.5 pd/anno
- **Hybrid Worker patching** (se #8 implementato): 0.5 pd/mese
- **Log retention pruning policy review**: 0.5 pd/anno
- **Re-test post-Microsoft platform changes** (App Service, KV, Storage breaking changes): 1-2 pd/anno
- **TOTALE ops**: ~10-15 pd/anno aggiuntivi

---

## TL;DR

- **5-6 settimane** di lavoro tecnico (1 senior) per chiudere tutti i 10 gap
- **1 settimana** per arrivare a baseline audit-pass (5 quick wins su 10)
- **Costo infra**: $10-50/mese (minimal) → $2-3k/mese (full FIPS L3 + APIM + SB Premium)
- **3-4 decisioni strategiche** dal team Risk/Compliance prima di partire con S2/S3
- **Assessment esterno**: 1-2 mesi calendar time aggiuntivi, costi una-tantum a parte
