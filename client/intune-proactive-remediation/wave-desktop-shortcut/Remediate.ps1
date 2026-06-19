<#
.SYNOPSIS
    Proactive Remediation - Remediate script.
    Creates the Intune Device Actions shortcut on the user's desktop.

.DESCRIPTION
    Triggered by Intune when Detect.ps1 exits 1 (device is in an active wave).
    - Creates a desktop shortcut (.lnk) pointing to the wipe confirmation tool.
    - Writes a flag file so Detect.ps1 knows remediation is complete.

.NOTES
    Context: SYSTEM (creates shortcut in Public Desktop so all users see it).
    The shortcut target can be:
      - A local script path (deployed via Win32 app / LOB)
      - A URL to launch the self-service portal
    Customize $ShortcutTarget and $ShortcutIcon below.
#>

$ErrorActionPreference = 'Stop'

# --- Configuration -----------------------------------------------------------
$ShortcutName   = 'Intune Device Actions.lnk'
$ShortcutTarget = Join-Path $env:ProgramData 'IntuneWipeClient\Launch-Wipe.cmd'
$ShortcutIcon   = Join-Path $env:ProgramData 'IntuneWipeClient\icon.ico'
$ShortcutDesc   = 'Avvia la procedura di Device Wipe programmata'
$PublicDesktop  = [Environment]::GetFolderPath('CommonDesktopDirectory')
$CacheDir       = Join-Path $env:ProgramData 'IntuneWipeClient'
$FlagFile       = Join-Path $CacheDir 'shortcut-created.flag'

# --- Create shortcut ---------------------------------------------------------
$shortcutPath = Join-Path $PublicDesktop $ShortcutName

try {
    $wshShell = New-Object -ComObject WScript.Shell
    $shortcut = $wshShell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath  = $ShortcutTarget
    $shortcut.Description = $ShortcutDesc
    $shortcut.WorkingDirectory = Split-Path $ShortcutTarget -Parent

    if (Test-Path $ShortcutIcon) {
        $shortcut.IconLocation = "$ShortcutIcon,0"
    }

    $shortcut.Save()
    Write-Host "Shortcut created: $shortcutPath"
} catch {
    Write-Host "Failed to create shortcut: $($_.Exception.Message)"
    exit 1
}

# --- Write flag so Detect.ps1 knows we're done ------------------------------
if (-not (Test-Path $CacheDir)) { New-Item -ItemType Directory -Path $CacheDir -Force | Out-Null }
Set-Content -Path $FlagFile -Value (Get-Date -Format 'o') -Encoding UTF8
Write-Host "Flag written: $FlagFile"

exit 0
