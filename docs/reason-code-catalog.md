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

> **Event vs reason**: gate denials (wave, device group, user group, group-check
> failures) all emit the single event `action.schedule.gate-denied`; the value
> below appears in the `scheduleGateReason` property. A couple of pre-gate
> resolution failures additionally emit a dedicated `action.denied.*` event
> (noted in the table).

| Family | Code | Meaning | Emitted as | UI text |
|---|---|---|---|---|
| denied | `denied:not-enrolled-in-wave` | Device is outside the allowed wave | `action.schedule.gate-denied` (reason) | Device fuori dalla wave corrente |
| denied | `denied:device-not-in-allowed-group` | Device is not in the allowed Entra group | `action.schedule.gate-denied` (reason) | Device non nel gruppo autorizzato |
| denied | `denied:user-not-in-allowed-group` | Caller user is not in the allowed Entra group | `action.schedule.gate-denied` (reason) | Utente non nel gruppo autorizzato |
| denied | `denied:group-check-failed` | Device group check threw (fail-closed) | `action.schedule.gate-denied` (reason) | Verifica gruppo fallita |
| denied | `denied:user-group-check-failed` | User group check threw (fail-closed) | `action.schedule.gate-denied` (reason) | Verifica gruppo utente fallita |
| denied | `denied:device-resolve-failed` | Device object id resolution failed | `action.denied.device-resolve-failed` + `action.schedule.gate-denied` | Risoluzione device fallita |
| denied | `denied:device-not-in-entra` | Entra device lookup returned empty | `action.denied.device-not-in-entra` | Device non registrato in Entra ID |
| deferred | (schedule defer) | Wave exists but is not yet due | `action.schedule.gated` (+ `scheduleScheduledAtUtc`) | Fuori dalla wave corrente, riprova più tardi |
| failed | (graph error) | Capability execution failed | `action.failed` | Errore durante l'esecuzione |
| failed | (timeout) | Execution timed out | `action.poll-timeout` | Timeout di esecuzione |

> Legacy: `BitLockerRotateRunner` still performs its own group gating and emits
> `action.denied.not-in-allowed-group` with reason `denied:not-in-allowed-group`
> — it has NOT been migrated to the central gate pipeline yet (see rubber-duck
> finding). New capabilities must use the central gates, not a private path.

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

