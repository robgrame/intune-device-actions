# Wave-based Desktop Shortcut (Proactive Remediation)

Questa Proactive Remediation di Intune mostra l'icona sul desktop dell'utente
**solo quando il device appartiene a una wave attiva** (startDate raggiunta).

## Componenti

| File | Contesto | Scopo |
|------|----------|-------|
| `Detect.ps1` | SYSTEM | Polling periodico su `/api/schedule/me` per verificare se il device e' in una wave attiva |
| `Remediate.ps1` | SYSTEM | Crea lo shortcut sul Public Desktop + scrive flag |

## Flusso

```
[Intune Proactive Remediation - ogni N ore]
    |
    v
Detect.ps1 --> GET /api/schedule/me (mTLS)
    |
    |-- Nessuna wave attiva --> exit 0 (compliant, noop)
    |-- Wave attiva (startDate <= now) --> exit 1 (non-compliant)
         |
         v
    Remediate.ps1 --> Crea shortcut sul Public Desktop
                  --> Scrive flag (shortcut-created.flag)
                  --> exit 0
    |
    [Prossimo ciclo Detect.ps1]
    |-- Flag esiste --> exit 0 (compliant, shortcut gia' presente)
```

## Configurazione

### Variabili d'ambiente (opzionali)

| Variabile | Default | Descrizione |
|-----------|---------|-------------|
| `INTUNE_ACTIONS_API_URL` | `https://devact-web-dev.azurewebsites.net` | Base URL dell'API |
| `INTUNE_ACTIONS_CERT_THUMBPRINT` | (auto-detect) | Thumbprint del certificato client |

### Personalizzazione shortcut

In `Remediate.ps1` modificare:
- `$ShortcutTarget` - percorso dell'eseguibile/script da lanciare
- `$ShortcutIcon` - percorso dell'icona (.ico)
- `$ShortcutName` - nome del file .lnk
- `$ShortcutDesc` - tooltip

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
