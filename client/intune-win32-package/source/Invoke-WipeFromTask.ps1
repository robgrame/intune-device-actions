#requires -Version 5.1
<#
.SYNOPSIS
    Scheduled-task wrapper that runs as SYSTEM and performs the actual API
    call using the machine certificate.
.DESCRIPTION
    The end-user shortcut (Launch-Wipe.ps1) collects the confirmation in
    the user's interactive session, then triggers the scheduled task
    '\IntuneWipeClient\InvokeWipe' which executes this script as SYSTEM.
    Running as SYSTEM is required to access the private key of the SCEP /
    PKCS device certificate in Cert:\LocalMachine\My.

    This script:
      1. Reads C:\Program Files\IntuneWipeClient\config.json (ACL'd to
         SYSTEM + Administrators).
      2. Invokes Invoke-DeviceWipe.ps1 -Silent.
      3. Persists the outcome to %ProgramData%\IntuneWipeClient\last-result.json
         so the user-context launcher can show success/failure.
    Logs to %ProgramData%\IntuneWipeClient\Logs.
.PARAMETER SelfTest
    When set, the wrapper writes a self-test marker to
    %ProgramData%\IntuneWipeClient\selftest.json and exits 0 without
    contacting the API. Install.ps1 calls this right after registering the
    scheduled task to verify that the SYSTEM-context invocation path works
    end-to-end on the target device (AppLocker / WDAC / Constrained Language
    mode would surface here, not at the user's first wipe attempt).
#>
[CmdletBinding()]
param(
    [switch] $SelfTest
)

# ABSOLUTE FIRST INSTRUCTION: drop a "wrapper-reached" marker via direct
# .NET IO (bypasses the PS provider stack which WDAC in ConstrainedLanguage
# mode can disable). If you ever see this file present but last-result.json
# missing, you know the wrapper PROCESS started but failed before our
# managed sentinel write.
try {
    $emergencyDir = Join-Path $env:ProgramData 'IntuneWipeClient'
    if (-not [System.IO.Directory]::Exists($emergencyDir)) {
        [void][System.IO.Directory]::CreateDirectory($emergencyDir)
    }
    $emergencyPath = Join-Path $emergencyDir 'wrapper-reached.txt'
    $emergencyLine = ("{0} pid={1} selftest={2} user={3} psver={4} lang={5}" -f `
        (Get-Date).ToUniversalTime().ToString('o'), $PID, [bool]$SelfTest, `
        [System.Security.Principal.WindowsIdentity]::GetCurrent().Name, `
        $PSVersionTable.PSVersion, $ExecutionContext.SessionState.LanguageMode)
    [System.IO.File]::AppendAllText($emergencyPath, $emergencyLine + [Environment]::NewLine)
} catch { }

$ErrorActionPreference = 'Stop'

$ProgramFiles64 = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$InstallDir   = Join-Path $ProgramFiles64 'IntuneWipeClient'
$ConfigPath   = Join-Path $InstallDir       'config.json'
$WipeScript   = Join-Path $InstallDir       'Invoke-DeviceWipe.ps1'
$DataDir      = Join-Path $env:ProgramData  'IntuneWipeClient'
$LogDir       = Join-Path $DataDir          'Logs'
$ResultPath   = Join-Path $DataDir          'last-result.json'

# SelfTest fast-path: prove the SYSTEM-context invocation works on this
# device without touching the API or the device certificate. Install.ps1
# calls this right after registering the scheduled task; the absence of the
# marker post-install is the SOLE diagnostic needed to conclude the device
# blocks our SYSTEM-context invocation.
if ($SelfTest) {
    try {
        if (-not [System.IO.Directory]::Exists($DataDir)) {
            [void][System.IO.Directory]::CreateDirectory($DataDir)
        }
        $marker = [ordered]@{
            ok            = $true
            ts            = (Get-Date).ToUniversalTime().ToString('o')
            user          = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
            pid           = $PID
            psVersion     = $PSVersionTable.PSVersion.ToString()
            languageMode  = "$($ExecutionContext.SessionState.LanguageMode)"
            installDir    = $InstallDir
            configExists  = (Test-Path $ConfigPath)
            wipeExists    = (Test-Path $WipeScript)
        }
        $json = ([pscustomobject]$marker) | ConvertTo-Json -Depth 4
        $selfTestPath = Join-Path $DataDir 'selftest.json'
        [System.IO.File]::WriteAllText($selfTestPath, $json)
        exit 0
    } catch {
        try {
            $errPath = Join-Path $DataDir 'selftest-error.txt'
            [System.IO.File]::WriteAllText($errPath, "$(Get-Date -Format o) $($_.Exception.Message)`r`n$($_.ScriptStackTrace)")
        } catch { }
        exit 7
    }
}

# Earliest possible breadcrumb: drop a "running" sentinel BEFORE attempting
# Start-Transcript so that any subsequent crash (including transcript
# initialization failures or hot-path early throws) still leaves the user-mode
# launcher with an actionable result file instead of the generic
# "Stato non disponibile" dialog. Any later Write-Result call overwrites this.
try {
    if (-not [System.IO.Directory]::Exists($DataDir)) {
        [void][System.IO.Directory]::CreateDirectory($DataDir)
    }
    $earlySentinel = [ordered]@{
        status        = 'error'
        message       = 'Task wrapper started but did not reach the wipe step (early crash before Start-Transcript or in setup). Inspect the latest Task_*.log under C:\ProgramData\IntuneWipeClient\Logs and C:\ProgramData\IntuneWipeClient\wrapper-reached.txt.'
        correlationId = $null
        ts            = (Get-Date).ToUniversalTime().ToString('o')
        kind          = 'task-wrapper-early-crash'
    }
    $earlyJson = ([pscustomobject]$earlySentinel) | ConvertTo-Json -Depth 6
    # Use .NET IO directly (bypasses PS provider that WDAC ConstrainedLanguage
    # can block).
    [System.IO.File]::WriteAllText($ResultPath, $earlyJson)
} catch {
    # Best-effort only; if even this fails we cannot help the launcher.
}

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$LogFile = Join-Path $LogDir ("Task_{0:yyyyMMdd_HHmmss}.log" -f (Get-Date))
try {
    Start-Transcript -Path $LogFile -Force | Out-Null
} catch {
    # Transcript is nice-to-have; do not abort the task if it fails (rare cases
    # of locked log dir, AV holds, or a previous transcript not cleanly closed).
    Write-Host ("WARN: Start-Transcript failed: {0}" -f $_.Exception.Message)
}

function Write-Result {
    param(
        [string]$Status,
        [string]$Message,
        [string]$CorrelationId,
        [hashtable]$Extra
    )
    $obj = [ordered]@{
        status        = $Status
        message       = $Message
        correlationId = $CorrelationId
        ts            = (Get-Date).ToUniversalTime().ToString('o')
    }
    if ($Extra) {
        foreach ($k in $Extra.Keys) { $obj[$k] = $Extra[$k] }
    }
    ([pscustomobject]$obj) | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ResultPath -Encoding utf8
}

function Write-WipeEventLog {
    param(
        [ValidateSet('Information','Warning','Error')] [string]$EntryType,
        [int]$EventId,
        [string]$Message
    )
    try {
        $src = 'IntuneWipeClient'
        if (-not [System.Diagnostics.EventLog]::SourceExists($src)) {
            [System.Diagnostics.EventLog]::CreateEventSource($src, 'Application')
        }
        Write-EventLog -LogName 'Application' -Source $src -EntryType $EntryType -EventId $EventId -Message $Message -ErrorAction Stop
    } catch {
        Write-Host ("WARN: Write-EventLog failed: {0}" -f $_.Exception.Message)
    }
}

function Send-UserNotification {
    <#
        Stub kept for source compatibility. We deliberately do NOT use
        msg.exe (Terminal-Services WTSSendMessage): it is disabled on
        Server SKUs, can pop opaque dialogs over full-screen apps, and
        is unwanted by the operator. End-user feedback now comes from
        the live progress dialog (Show-WipeProgressDialog) plus the
        Application Event Log entries already written by callers.
    #>
    param([string]$Title, [string]$Body)
    Write-Host ("[notify-suppressed] {0}: {1}" -f $Title, $Body)
}

function Start-StatusPoller {
    <#
        Trigger the SYSTEM-context StatusPoller scheduled task with the
        live correlationId so it can begin polling GET /actions/status/{id}
        for the operator-facing live progress UI. Best-effort: a failure
        here must not bubble up - the wipe itself was already accepted.
    #>
    param([string]$CorrelationId)
    if (-not $CorrelationId) { return }
    try {
        $taskFull = '\IntuneWipeClient\StatusPoller'
        & schtasks.exe /End /TN $taskFull 2>$null | Out-Null
        & schtasks.exe /Run /TN $taskFull 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "Triggered StatusPoller scheduled task."
            return
        }
        Write-Host ("WARN: schtasks /Run StatusPoller failed with exit {0}" -f $LASTEXITCODE)
    } catch {
        Write-Host ("WARN: Start-StatusPoller failed: {0}" -f $_.Exception.Message)
    }
}

function Invoke-LocalMdmNudge {
    <#
        Force a local MDM check-in so Intune Management Extension picks up the
        freshly-issued wipe within seconds instead of waiting up to 8 hours
        for the next scheduled sync. Combines with the server-side syncDevice
        + rebootNow nudges already issued by WipeActionRunner; either path
        alone is enough - running both shortens worst-case latency further.

        Returns the structured result from Invoke-MdmSyncNudge so the caller
        can persist it in last-result.json for telemetry.
    #>
    param(
        [bool] $AllowReboot = $false,
        [int]  $RebootDelaySeconds = 60
    )
    $modPath = Join-Path $InstallDir 'MdmSyncNudge.psm1'
    if (-not (Test-Path $modPath)) {
        Write-Host ("WARN: MdmSyncNudge.psm1 not found at {0} - skipping local nudge." -f $modPath)
        return @{ ok = $false; method = 'module-missing'; taskCount = 0; attempts = @() }
    }
    try {
        Import-Module $modPath -Force -DisableNameChecking -ErrorAction Stop
        $params = @{ RebootDelaySeconds = $RebootDelaySeconds }
        if ($AllowReboot) { $params['AllowRebootFallback'] = $true }
        $result = Invoke-MdmSyncNudge @params
        Write-Host ("Local MDM nudge: method={0} ok={1} taskCount={2}" -f $result.method, $result.ok, $result.taskCount)
        return $result
    } catch {
        Write-Host ("WARN: Invoke-LocalMdmNudge failed: {0}" -f $_.Exception.Message)
        return @{ ok = $false; method = 'error'; taskCount = 0; attempts = @(); error = $_.Exception.Message }
    }
}

function Get-ErrJsonFromOutput {
    param($Lines)
    foreach ($line in $Lines) {
        $s = [string]$line
        $idx = $s.IndexOf('ERRJSON:')
        if ($idx -ge 0) {
            $payload = $s.Substring($idx + 'ERRJSON:'.Length).Trim()
            try { return ($payload | ConvertFrom-Json -ErrorAction Stop) } catch { return $null }
        }
    }
    return $null
}

try {
    if (-not (Test-Path $ConfigPath)) { throw "Config not found: $ConfigPath" }
    if (-not (Test-Path $WipeScript)) { throw "Wipe script not found: $WipeScript" }
    $cfg = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json

    # Allow machine-scope env vars to override the values baked into config.json
    # so the Function App can be repointed / the key rotated without
    # repackaging the .intunewin (see intune-remediation-endpoint).
    $envApiUrl = [Environment]::GetEnvironmentVariable('INTUNE_WIPE_API_URL', 'Machine')
    if ($envApiUrl -and $envApiUrl.Trim()) {
        Write-Host ("ApiUrl override from machine env INTUNE_WIPE_API_URL: {0}" -f $envApiUrl)
        $cfg.ApiUrl = $envApiUrl.Trim()
    }
    $envKey = [Environment]::GetEnvironmentVariable('INTUNE_WIPE_FUNCTION_KEY', 'Machine')
    if ($envKey -and $envKey.Trim()) {
        Write-Host "FunctionKey override from machine env INTUNE_WIPE_FUNCTION_KEY (length=$($envKey.Trim().Length))"
        $cfg.FunctionKey = $envKey.Trim()
    }
    foreach ($pair in @(
        @{ Env = 'INTUNE_WIPE_CERT_THUMBPRINT'  ; Prop = 'CertificateThumbprint'  },
        @{ Env = 'INTUNE_WIPE_CERT_SUBJECT_LIKE'; Prop = 'CertificateSubjectLike' },
        @{ Env = 'INTUNE_WIPE_CERT_ISSUER_LIKE' ; Prop = 'CertificateIssuerLike'  }
    )) {
        $v = [Environment]::GetEnvironmentVariable($pair.Env, 'Machine')
        if ($v -and $v.Trim()) {
            Write-Host ("{0} override from machine env {1}: {2}" -f $pair.Prop, $pair.Env, $v.Trim())
            $cfg.($pair.Prop) = $v.Trim()
        }
    }
    if (-not $cfg.ApiUrl)      { throw "ApiUrl missing: neither config.json nor INTUNE_WIPE_API_URL is set." }
    if (-not $cfg.FunctionKey) { throw "FunctionKey missing: neither config.json nor INTUNE_WIPE_FUNCTION_KEY is set." }

    $params = @{
        ApiUrl      = $cfg.ApiUrl
        FunctionKey = $cfg.FunctionKey
        Silent      = $true
    }
    if ($cfg.CertificateThumbprint)  { $params['CertificateThumbprint']  = $cfg.CertificateThumbprint  }
    if ($cfg.CertificateIssuerLike)  { $params['CertificateIssuerLike']  = $cfg.CertificateIssuerLike  }
    if ($cfg.CertificateSubjectLike) { $params['CertificateSubjectLike'] = $cfg.CertificateSubjectLike }

    # Capture stdout to extract the correlation id (and rich error details
    # on failure) from the wipe script output.
    # NOTE: Write-Host goes to stream 6 (Information) in PS 5.1+; we must
    # redirect it to success stream to capture correlationId output.
    $wipeFailed = $false
    $out = $null
    try {
        $out = & $WipeScript @params 6>&1 2>&1
    } catch {
        $wipeFailed = $true
        $thrown = $_
    }
    if ($out) { $out | ForEach-Object { Write-Host $_ } }

    if ($wipeFailed) {
        $err = Get-ErrJsonFromOutput -Lines $out
        if ($err) {
            $extra = @{
                kind                = [string]$err.kind
                apiUrl              = [string]$err.apiUrl
                httpStatusCode      = $err.httpStatusCode
                httpStatusReason    = [string]$err.httpStatusReason
                serverStatus        = [string]$err.serverStatus
                serverMessage       = [string]$err.serverMessage
                serverCorrelationId = [string]$err.serverCorrelationId
                serverBodyRaw       = [string]$err.serverBodyRaw
                certSubject         = [string]$err.certSubject
                certThumbprint      = [string]$err.certThumbprint
                deviceName          = [string]$err.deviceName
                entraDeviceId       = [string]$err.entraDeviceId
                intuneDeviceId      = [string]$err.intuneDeviceId
                clientMessage       = [string]$err.clientMessage
            }
            $displayMsg = if ($err.serverMessage) { [string]$err.serverMessage } else { [string]$err.clientMessage }
            Write-Result -Status 'error' -Message $displayMsg -CorrelationId ([string]$err.serverCorrelationId) -Extra $extra

            $evtMsg = "Wipe request FAILED.`r`nHTTP {0} {1}`r`nServer: {2}`r`nCorrelationId: {3}`r`nDevice: {4} (Entra={5}, Intune={6})`r`nCert: {7} ({8})" -f `
                $err.httpStatusCode, $err.httpStatusReason, $displayMsg, $err.serverCorrelationId, `
                $err.deviceName, $err.entraDeviceId, $err.intuneDeviceId, $err.certSubject, $err.certThumbprint
            Write-WipeEventLog -EntryType Error -EventId 2001 -Message $evtMsg
            Send-UserNotification -Title 'Reset aziendale: richiesta NON inviata' `
                -Body ("Errore: {0} (correlationId: {1}). Vedi Event Viewer/Application/IntuneWipeClient per dettagli." -f $displayMsg, $err.serverCorrelationId)
        } else {
            $msg = if ($thrown) { $thrown.Exception.Message } else { 'Wipe script terminated with an error.' }
            Write-Result -Status 'error' -Message $msg -CorrelationId $null -Extra @{ kind = 'unknown' }
            Write-WipeEventLog -EntryType Error -EventId 2002 -Message ("Wipe script terminated unexpectedly: {0}" -f $msg)
            Send-UserNotification -Title 'Reset aziendale: errore imprevisto' -Body $msg
        }
        exit 1
    }

    # Success path: pull correlationId out of the captured stdout.
    $corr = $null
    foreach ($line in $out) {
        $lineStr = [string]$line
        if ($lineStr -match 'correlationId\s*:\s*([0-9a-fA-F]{32}|[0-9a-fA-F-]{36})') {
            $corr = $Matches[1]; break
        }
    }

    # Best-effort local MDM nudge: force IME to fetch the freshly-issued wipe
    # NOW (PushLaunch task -> fallback to any EnterpriseMgmt sync task ->
    # optional scheduled reboot). Combined with the server-side syncDevice
    # + rebootNow calls issued by WipeActionRunner, this collapses end-to-end
    # latency from "tens of minutes" to "tens of seconds" on the typical path.
    $allowReboot = $false
    $rebootDelay = 60
    if ($cfg.PSObject.Properties.Name -contains 'AllowRebootFallback') {
        $allowReboot = [bool]$cfg.AllowRebootFallback
    }
    if ($cfg.PSObject.Properties.Name -contains 'MdmNudgeRebootDelaySeconds') {
        try { $rebootDelay = [int]$cfg.MdmNudgeRebootDelaySeconds } catch { }
    }
    $nudge = Invoke-LocalMdmNudge -AllowReboot $allowReboot -RebootDelaySeconds $rebootDelay

    $extraOk = @{
        mdmNudgeMethod    = [string]$nudge.method
        mdmNudgeOk        = [bool]$nudge.ok
        mdmNudgeTaskCount = [int]$nudge.taskCount
    }
    Write-Result -Status 'ok' -Message 'Wipe request accepted by the API.' -CorrelationId $corr -Extra $extraOk
    Write-WipeEventLog -EntryType Information -EventId 1001 -Message ("Wipe request accepted by the API. CorrelationId={0}, Device={1}, LocalMdmNudge={2} (ok={3}, tasks={4})" -f `
        $corr, $env:COMPUTERNAME, $nudge.method, $nudge.ok, $nudge.taskCount)
    Start-StatusPoller -CorrelationId $corr
    exit 0
}
catch {
    $msg = $_.Exception.Message
    Write-Host ("ERROR: {0}" -f $msg) -ForegroundColor Red
    Write-Host $_.ScriptStackTrace
    Write-Result -Status 'error' -Message $msg -CorrelationId $null -Extra @{ kind = 'task-wrapper' }
    Write-WipeEventLog -EntryType Error -EventId 2003 -Message ("Task wrapper failure: {0}`r`n{1}" -f $msg, $_.ScriptStackTrace)
    Send-UserNotification -Title 'Reset aziendale: errore configurazione' -Body $msg
    exit 1
}
finally {
    Stop-Transcript | Out-Null
}
