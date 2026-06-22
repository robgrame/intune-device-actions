<#
.SYNOPSIS
    Proactive Remediation - Remediate script.
    Creates the Intune Device Actions shortcuts on Start Menu + Public Desktop.

.DESCRIPTION
    Triggered by Intune when Detect.ps1 exits 1 (device is in an active wave).
    - Creates Start Menu + Public Desktop shortcuts (.lnk) pointing to the wipe confirmation tool.
    - Writes a flag file so Detect.ps1 knows remediation is complete.

.NOTES
    Context: SYSTEM (creates shortcuts in all-users Start Menu + Public Desktop).
    The shortcut target can be:
      - A local script path (deployed via Win32 app / LOB)
      - A URL to launch the self-service portal
    Customize $ShortcutTarget and $ShortcutIcon below.
#>

$ErrorActionPreference = 'Stop'

# --- Configuration -----------------------------------------------------------
$ShortcutName   = 'Intune Device Actions.lnk'
$ProgramFiles64 = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$InstallDir     = Join-Path $ProgramFiles64 'IntuneWipeClient'
$ShortcutTarget = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$ShortcutArgs   = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$InstallDir\Launch-Wipe.ps1`""
$ShortcutIcon   = Join-Path $env:ProgramData 'IntuneWipeClient\icon.ico'
$ShortcutDesc   = 'Avvia la procedura di Device Wipe programmata'
$PublicDesktop  = [Environment]::GetFolderPath('CommonDesktopDirectory')
$AllUsersStart  = [Environment]::GetFolderPath('CommonStartMenu')
$StartMenuPrograms = Join-Path $AllUsersStart 'Programs'
$CacheDir       = Join-Path $env:ProgramData 'IntuneWipeClient'
$FlagFile       = Join-Path $CacheDir 'shortcut-created.flag'
$LogDir         = Join-Path $CacheDir 'Logs'
$LogFile        = Join-Path $LogDir 'wave-desktop-shortcut-remediate.log'

if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Path $LogDir -Force | Out-Null }

function Write-Log {
    param([Parameter(Mandatory = $true)][string]$Message)
    $line = "[{0}] {1}" -f (Get-Date -Format o), $Message
    $line | Out-File -FilePath $LogFile -Append -Encoding utf8
    Write-Host $line
}

Write-Log "Remediation started. PublicDesktop='$PublicDesktop' StartMenuPrograms='$StartMenuPrograms'"
Write-Log "Shortcut target='$ShortcutTarget' args='$ShortcutArgs'"

if (-not (Test-Path (Join-Path $InstallDir 'Launch-Wipe.ps1'))) {
    Write-Log "ERROR: Missing launcher script at '$InstallDir\\Launch-Wipe.ps1'."
    exit 1
}

# --- Create shortcuts ---------------------------------------------------------
try {
    $wshShell = New-Object -ComObject WScript.Shell

    foreach ($folder in @($PublicDesktop, $StartMenuPrograms)) {
        if (-not (Test-Path $folder)) {
            New-Item -ItemType Directory -Path $folder -Force | Out-Null
            Write-Log "Created folder: $folder"
        }

        $shortcutPath = Join-Path $folder $ShortcutName
        $shortcut = $wshShell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath  = $ShortcutTarget
        $shortcut.Arguments   = $ShortcutArgs
        $shortcut.Description = $ShortcutDesc
        $shortcut.WorkingDirectory = $InstallDir

        if (Test-Path $ShortcutIcon) {
            $shortcut.IconLocation = "$ShortcutIcon,0"
        }

        $shortcut.Save()
        Write-Log "Shortcut created: $shortcutPath"
    }
} catch {
    Write-Log "ERROR: Failed to create shortcut(s): $($_.Exception.Message)"
    exit 1
}

# --- Write flag so Detect.ps1 knows we're done ------------------------------
if (-not (Test-Path $CacheDir)) { New-Item -ItemType Directory -Path $CacheDir -Force | Out-Null }
Set-Content -Path $FlagFile -Value (Get-Date -Format 'o') -Encoding UTF8
Write-Log "Flag written: $FlagFile"

exit 0
