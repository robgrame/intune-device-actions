# Wave-based Desktop Shortcut (Proactive Remediation)

Questa Proactive Remediation di Intune mostra l'icona sul desktop dell'utente
**solo quando il device appartiene a una wave wipe attiva** (la wave ha fired,
cioe' `isImmediate=true` nella risposta del server).

## Architettura

Non esiste infrastruttura server dedicata. La feature sfrutta il
**WipeScheduleProvider** gia' presente: il client interroga
`GET /api/schedule/me?actionType=wipe` e valuta il campo `isImmediate`
della `DeviceScheduleSnapshot` restituita.

## Componenti

| File | Contesto | Scopo |
|------|----------|-------|
| `Detect.ps1` | SYSTEM | Polling periodico su `/api/schedule/me?actionType=wipe` per verificare se il device e' in una wave attiva |
| `Remediate.ps1` | SYSTEM | Crea gli shortcut su Start Menu (all-users) + Public Desktop, poi scrive flag |

## Flusso

```
[Intune Proactive Remediation - ogni N ore]
    |
    v
Detect.ps1 --> GET /api/schedule/me?actionType=wipe (mTLS)
    |
    |-- 204 No Content (nessuna wave) --> exit 0 (compliant, noop)
    |-- 200 + isImmediate=false       --> exit 0 (wave futura, noop)
    |-- 200 + isImmediate=true        --> exit 1 (non-compliant)
         |
         v
    Remediate.ps1 --> Crea shortcut su Start Menu + Public Desktop
                  --> Scrive flag (shortcut-created.flag)
                  --> exit 0
    |
    [Prossimo ciclo Detect.ps1]
    |-- Flag esiste --> exit 0 (compliant, shortcut gia' presente)
```

## Risposta server (DeviceScheduleSnapshot)

```json
{
  "waveId": "a1b2c3d4-...",
  "name": "Wave 1 - Pilot",
  "actionType": "wipe",
  "scheduledAtUtc": "2026-07-01T08:00:00Z",
  "status": "scheduled",
  "isImmediate": true,
  "description": "...",
  "generatedAtUtc": "2026-06-19T14:00:00Z"
}
```

Se il device non e' in nessuna wave, il server risponde `204 No Content`.

## Configurazione

### Variabili d'ambiente (opzionali)

| Variabile | Default | Descrizione |
|-----------|---------|-------------|
| `INTUNE_ACTIONS_API_URL` | `https://devact-web-dev.azurewebsites.net` | Base URL dell'API |
| `INTUNE_ACTIONS_CERT_THUMBPRINT` | (auto-detect) | Thumbprint del certificato client |

### Personalizzazione shortcut

In `Remediate.ps1` modificare:
- `$ShortcutTarget` / `$ShortcutArgs` - comando da lanciare (default: `powershell.exe -File "<ProgramFiles64>\\IntuneWipeClient\\Launch-Wipe.ps1"`)
- `$ShortcutIcon` - percorso dell'icona (.ico)
- `$ShortcutName` - nome del file .lnk
- `$ShortcutDesc` - tooltip

### Logging remediation

`Remediate.ps1` scrive un log persistente in:

`%ProgramData%\IntuneWipeClient\Logs\wave-desktop-shortcut-remediate.log`

`Detect.ps1` scrive un log persistente in:

`%ProgramData%\IntuneWipeClient\Logs\wave-desktop-shortcut-detect.log`

In aggiunta, l'esecuzione Intune resta visibile nei log dell'agent:

`C:\ProgramData\Microsoft\IntuneManagementExtension\Logs\IntuneManagementExtension.log`

### Cache

Il risultato dell'API viene cachato in `%ProgramData%\IntuneWipeClient\wave-schedule.json`
per 4 ore (configurabile via `$CacheTtlHours` in Detect.ps1).

## Setup in Intune

1. **Proactive Remediation** > Create script package
2. Detection script: `Detect.ps1`
3. Remediation script: `Remediate.ps1`
4. Run as: **System**
5. Schedule: ogni 4-8 ore (allineato al cache TTL)
6. Assign al gruppo di device target
