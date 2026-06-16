# Secure Boot 2023 Certificate Verification

Verificare la presenza dei certificati Secure Boot 2023 aggiornati su dispositivi Windows prima della scadenza (giugno 2026).

**References**: KB5068202, KB5080921

---

## Comandi PowerShell di Verifica

Esegui questi comandi in **PowerShell Administrator** uno dopo l'altro:

### 1. DB: Windows UEFI CA 2023 (RICHIESTO)
```powershell
[System.Text.Encoding]::ASCII.GetString((Get-SecureBootUEFI db).bytes) -match "Windows UEFI CA 2023"
```
**Output**: `True` = ✓ Presente | `False` = ✗ Mancante

---

### 2. KEK: Microsoft Corporation KEK 2K CA 2023 (RICHIESTO)
```powershell
[System.Text.Encoding]::ASCII.GetString((Get-SecureBootUEFI kek).bytes) -match "Microsoft Corporation KEK 2K CA 2023"
```
**Output**: `True` = ✓ Presente | `False` = ✗ Mancante

---

### 3. DB: Microsoft Option ROM UEFI CA 2023 (OPZIONALE)
```powershell
[System.Text.Encoding]::ASCII.GetString((Get-SecureBootUEFI db).bytes) -match "Microsoft Option ROM UEFI CA 2023"
```
**Output**: `True` = ✓ Presente | `False` = ✗ Mancante (non critico)

---

### 4. DB: Microsoft UEFI CA 2023 (RICHIESTO)
```powershell
[System.Text.Encoding]::ASCII.GetString((Get-SecureBootUEFI db).bytes) -match "Microsoft UEFI CA 2023"
```
**Output**: `True` = ✓ Presente | `False` = ✗ Mancante

---

## Interpretazione dei Risultati

| Certificato | Tipo | Risultato Atteso | Note |
|---|---|---|---|
| Windows UEFI CA 2023 | DB | True | Richiesto |
| Microsoft Corporation KEK 2K CA 2023 | KEK | True | Richiesto |
| Microsoft UEFI CA 2023 | DB | True | Richiesto |
| Microsoft Option ROM UEFI CA 2023 | DB | True | Opzionale (alcuni device non lo hanno) |

### Device Aggiornato
✓ **PASS**: Tutti i 3 certificati richiesti presenti (+ opzionale se presente)

### Device NON Aggiornato
✗ **FAIL**: Uno o più certificati richiesti mancanti → Richiede remediation

---

## Remediation

Se uno o più certificati sono mancanti:

1. Imposta il flag di update nel registro:
   ```powershell
   Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot' -Name 'AvailableUpdates' -Value 0x5944 -Type DWord -Force
   ```

2. Triggera lo scheduled task (se disponibile):
   ```powershell
   Start-ScheduledTask -TaskName 'Secure-Boot-Update' -TaskPath '\Microsoft\Windows\'
   ```

3. Riavvia il device:
   ```powershell
   Restart-Computer
   ```

4. Riesegui i comandi di verifica per confermare l'aggiornamento

---

## Integration con Intune

Per automazione su scala con Intune Proactive Remediation, vedi:
- `client/intune-remediation-endpoint/` (quando disponibile)
