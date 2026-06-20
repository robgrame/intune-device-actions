# Reason code catalog

This document defines the canonical reason codes used by the centralized
gating pipeline and by the user-facing UX.

Rules:

- **event name** = what happened
- **reason code** = why it happened
- **UI text** = how we explain it to a human

Reason codes are grouped by family:

- `denied:*` = permanent policy block
- `deferred:*` = not executable now, retry later
- `failed:*` = execution failure

## Core mapping

| Family | Code | Meaning | Typical event | UI text |
|---|---|---|---|---|
| denied | `denied:not-enrolled-in-wave` | Device is outside the allowed wave | `action.schedule.gate-denied` | Device fuori dalla wave corrente |
| denied | `denied:device-not-in-allowed-group` | Device is not in the allowed Entra group | `action.denied.device-not-in-allowed-group` | Device non nel gruppo autorizzato |
| denied | `denied:user-not-in-allowed-group` | Caller user is not in the allowed Entra group | `action.denied.user-not-in-allowed-group` | Utente non nel gruppo autorizzato |
| denied | `denied:device-not-in-entra` | Device binding / Entra lookup failed | `action.denied.device-not-in-entra` | Device non registrato in Entra ID |
| denied | `denied:ownership-mismatch` | Certificate/device ownership mismatch | `action.denied.ownership-mismatch` | Il certificato non corrisponde al device |
| denied | `denied:rate-limited` | Request rejected by throttling | `action.denied.rate-limited` | Troppe richieste, riprova più tardi |
| denied | `denied:device-resolve-failed` | Device lookup failed before dispatch | `action.denied.device-resolve-failed` | Risoluzione device fallita |
| denied | `denied:managed-device-resolve-failed` | Managed device lookup failed | `action.denied.managed-device-resolve-failed` | Device gestito non trovato |
| denied | `denied:already-issued` | Same request already issued | `action.denied.already-issued` | Azione già emessa |
| denied | `denied:in-progress-elsewhere` | Another operation is already running | `action.denied.in-progress-elsewhere` | Azione già in corso |
| denied | `denied:missing-hardware-hash` | Hardware hash is missing | `action.denied.missing-hardware-hash` | Hardware hash mancante |
| denied | `denied:missing-serial` | Device serial is missing | `action.denied.missing-serial` | Seriale mancante |
| denied | `denied:name-collision` | Requested name collides with another device | `action.denied.name-collision` | Collisione nome device |
| deferred | `deferred:wave-not-open` | The wave exists but is not open yet | `action.schedule.gated` | Fuori dalla wave corrente |
| deferred | `deferred:maintenance-window` | Execution is deferred by maintenance policy | `action.schedule.gated` | Finestra di manutenzione non attiva |
| failed | `failed:graph-error` | Graph call failed | `action.failed` | Errore durante l'esecuzione |
| failed | `failed:timeout` | Execution timed out | `action.poll-timeout` | Timeout di esecuzione |
| failed | `failed:transient-retry-exhausted` | Retries were exhausted | `action.failed` | Ritentativi esauriti |

## UI guidance

### Denied

Use a direct message: the operator or user must understand that the request
will not proceed until policy changes.

### Deferred

Use a waiting message: the request may become valid later.

### Failed

Use a diagnostic message: execution started, but the capability could not be
completed.

## Extensibility rules

When adding a new gate:

1. pick the family first (`denied`, `deferred`, or `failed`)
2. assign a stable reason code
3. map it to a stable event name
4. update client and portal text separately

Do not create capability-specific reason taxonomies for cross-cutting gates.

