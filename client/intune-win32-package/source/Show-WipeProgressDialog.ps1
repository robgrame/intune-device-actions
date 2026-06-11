#requires -Version 5.1
<#
.SYNOPSIS
    User-context live progress dialog for an in-flight wipe.
.DESCRIPTION
    Tails %ProgramData%\IntuneWipeClient\status\<corrId>.json (written by
    the SYSTEM-context Watch-WipeStatus.ps1 scheduled task) every 2 seconds
    and renders the current state in a friendly WinForms window.

    The dialog shows:
      - Current state (translated to Italian).
      - Last server-known DeviceLastSync / ComplianceState / OsVersion.
      - Number of polling attempts and last update timestamp.
      - A timeline of state transitions seen so far.
      - "Chiudi" button (the SYSTEM poller keeps running in background - the
        user just dismisses the UI). No msg.exe / Terminal-Services popups.

    Why polling a file (not the API directly): the device client cert lives
    in Cert:\LocalMachine\My and its private key is ACL'd to SYSTEM /
    BUILTIN\Administrators only. A standard user process cannot perform mTLS.
    The SYSTEM scheduled task is the single point that talks to the API.
#>
function Show-WipeProgressDialog {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $CorrelationId,
        [string] $DeviceName = $env:COMPUTERNAME,
        [int]    $MaxMinutes = 30
    )

    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    [System.Windows.Forms.Application]::EnableVisualStyles()

    $a_grave = [char]0x00E0
    $e_grave = [char]0x00E8

    $StatusFile = Join-Path $env:ProgramData ("IntuneWipeClient\status\{0}.json" -f $CorrelationId)

    function _Translate-State {
        param([string]$State)
        switch -Regex ($State) {
            '^pending$'           { return 'In attesa di presa in carico da Intune' }
            '^active$'            { return 'Comando di wipe in corso sul dispositivo' }
            '^done$'              { return 'Comando completato da Intune' }
            '^failed$'            { return 'Intune ha segnalato un errore' }
            '^notSupported$'      { return 'Operazione non supportata su questo dispositivo' }
            '^removedFromIntune$' { return 'Dispositivo rimosso da Intune (wipe completato)' }
            '^awaiting-graph$'    { return 'In attesa del primo controllo Intune' }
            default               { if ($State) { return $State } else { return 'In attesa...' } }
        }
    }

    $form              = New-Object System.Windows.Forms.Form
    $form.Text         = 'Stato reset dispositivo'
    $form.Size         = New-Object System.Drawing.Size(620, 520)
    $form.MinimumSize  = New-Object System.Drawing.Size(620, 520)
    $form.StartPosition = 'CenterScreen'
    $form.FormBorderStyle = 'Sizable'
    $form.MaximizeBox  = $true
    $form.MinimizeBox  = $true
    $form.TopMost      = $false
    $form.BackColor    = [System.Drawing.Color]::White

    $icon = New-Object System.Windows.Forms.PictureBox
    $icon.Image    = [System.Drawing.SystemIcons]::Information.ToBitmap()
    $icon.SizeMode = 'CenterImage'
    $icon.Location = New-Object System.Drawing.Point(20, 20)
    $icon.Size     = New-Object System.Drawing.Size(48, 48)
    $icon.Anchor   = 'Top, Left'
    $form.Controls.Add($icon)

    $title = New-Object System.Windows.Forms.Label
    $title.Text     = "Reset in corso su $DeviceName"
    $title.Location = New-Object System.Drawing.Point(85, 22)
    $title.Size     = New-Object System.Drawing.Size(490, 28)
    $title.Font     = New-Object System.Drawing.Font('Segoe UI', 14, [System.Drawing.FontStyle]::Bold)
    $title.ForeColor = [System.Drawing.Color]::FromArgb(0, 90, 158)
    $title.Anchor   = 'Top, Left, Right'
    $form.Controls.Add($title)

    $intro = New-Object System.Windows.Forms.Label
    $intro.Text     = "Questa finestra viene aggiornata automaticamente con lo stato comunicato da Intune.`r`nPuoi chiuderla in qualsiasi momento: il monitoraggio continua comunque in background."
    $intro.Location = New-Object System.Drawing.Point(85, 52)
    $intro.Size     = New-Object System.Drawing.Size(500, 40)
    $intro.Font     = New-Object System.Drawing.Font('Segoe UI', 9)
    $intro.Anchor   = 'Top, Left, Right'
    $form.Controls.Add($intro)

    $stateLbl = New-Object System.Windows.Forms.Label
    $stateLbl.Text = 'Stato corrente:'
    $stateLbl.Location = New-Object System.Drawing.Point(20, 105)
    $stateLbl.Size = New-Object System.Drawing.Size(120, 20)
    $stateLbl.Font = New-Object System.Drawing.Font('Segoe UI', 9, [System.Drawing.FontStyle]::Bold)
    $form.Controls.Add($stateLbl)

    $state = New-Object System.Windows.Forms.Label
    $state.Text = 'in attesa del primo aggiornamento...'
    $state.Location = New-Object System.Drawing.Point(140, 105)
    $state.Size = New-Object System.Drawing.Size(450, 22)
    $state.Font = New-Object System.Drawing.Font('Segoe UI', 11, [System.Drawing.FontStyle]::Bold)
    $state.ForeColor = [System.Drawing.Color]::FromArgb(70, 70, 70)
    $state.Anchor = 'Top, Left, Right'
    $form.Controls.Add($state)

    $progress = New-Object System.Windows.Forms.ProgressBar
    $progress.Location = New-Object System.Drawing.Point(20, 132)
    $progress.Size     = New-Object System.Drawing.Size(565, 14)
    $progress.Style    = 'Marquee'
    $progress.MarqueeAnimationSpeed = 40
    $progress.Anchor   = 'Top, Left, Right'
    $form.Controls.Add($progress)

    $infoBox = New-Object System.Windows.Forms.TextBox
    $infoBox.Multiline   = $true
    $infoBox.ReadOnly    = $true
    $infoBox.ScrollBars  = 'Vertical'
    $infoBox.WordWrap    = $false
    $infoBox.Location    = New-Object System.Drawing.Point(20, 160)
    $infoBox.Size        = New-Object System.Drawing.Size(565, 110)
    $infoBox.Anchor      = 'Top, Left, Right'
    $infoBox.Font        = New-Object System.Drawing.Font('Consolas', 9)
    $infoBox.BackColor   = [System.Drawing.Color]::FromArgb(248, 248, 248)
    $infoBox.Text        = "CorrelationId : $CorrelationId`r`nDevice         : $DeviceName`r`n(in attesa dei dati dal server)"
    $form.Controls.Add($infoBox)

    $timelineLbl = New-Object System.Windows.Forms.Label
    $timelineLbl.Text = 'Cronologia stati:'
    $timelineLbl.Location = New-Object System.Drawing.Point(20, 280)
    $timelineLbl.Size = New-Object System.Drawing.Size(200, 20)
    $timelineLbl.Font = New-Object System.Drawing.Font('Segoe UI', 9, [System.Drawing.FontStyle]::Bold)
    $form.Controls.Add($timelineLbl)

    $timeline = New-Object System.Windows.Forms.TextBox
    $timeline.Multiline   = $true
    $timeline.ReadOnly    = $true
    $timeline.ScrollBars  = 'Vertical'
    $timeline.WordWrap    = $false
    $timeline.Location    = New-Object System.Drawing.Point(20, 302)
    $timeline.Size        = New-Object System.Drawing.Size(565, 120)
    $timeline.Anchor      = 'Top, Left, Right, Bottom'
    $timeline.Font        = New-Object System.Drawing.Font('Consolas', 9)
    $timeline.BackColor   = [System.Drawing.Color]::FromArgb(250, 250, 245)
    $form.Controls.Add($timeline)

    $footer = New-Object System.Windows.Forms.Label
    $footer.Text     = "Ultimo aggiornamento: --"
    $footer.Location = New-Object System.Drawing.Point(20, 435)
    $footer.Size     = New-Object System.Drawing.Size(400, 20)
    $footer.Font     = New-Object System.Drawing.Font('Segoe UI', 8)
    $footer.ForeColor = [System.Drawing.Color]::Gray
    $footer.Anchor   = 'Bottom, Left'
    $form.Controls.Add($footer)

    $close = New-Object System.Windows.Forms.Button
    $close.Text         = 'Chiudi'
    $close.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $close.Size         = New-Object System.Drawing.Size(120, 30)
    $close.Location     = New-Object System.Drawing.Point(465, 435)
    $close.Anchor       = 'Bottom, Right'
    $close.Font         = New-Object System.Drawing.Font('Segoe UI', 9, [System.Drawing.FontStyle]::Bold)
    $form.Controls.Add($close)
    $form.AcceptButton  = $close
    $form.CancelButton  = $close

    # --- Tail loop (UI timer, runs on the form's thread) -------------------
    $script:lastSeenState  = ''
    $script:transitions    = New-Object System.Collections.Generic.List[string]
    $script:deadline       = (Get-Date).AddMinutes($MaxMinutes)

    $timer = New-Object System.Windows.Forms.Timer
    $timer.Interval = 2000

    $tick = {
        try {
            if (-not (Test-Path -LiteralPath $StatusFile)) {
                $footer.Text = "In attesa del primo aggiornamento... (poller SYSTEM non ancora partito)  |  scadenza UI: $(($script:deadline).ToString('HH:mm:ss'))"
                return
            }
            $raw = Get-Content -LiteralPath $StatusFile -Raw -ErrorAction Stop
            if (-not $raw) { return }
            $obj = $raw | ConvertFrom-Json -ErrorAction Stop

            $local = [string]$obj.localState
            $srv   = $obj.server
            $srvState = $null
            if ($srv) { $srvState = [string]$srv.state }
            $display = if ($srvState) { _Translate-State $srvState } elseif ($local) { "[$local] in attesa di risposta da Intune" } else { 'in attesa...' }
            $state.Text = $display

            switch ($local) {
                'terminal' {
                    $state.ForeColor = if ($srvState -match 'failed|notSupported') { [System.Drawing.Color]::FromArgb(168, 0, 0) } else { [System.Drawing.Color]::FromArgb(0, 120, 50) }
                    $progress.Style = 'Continuous'
                    $progress.Value = 100
                    $timer.Stop()
                }
                'timeout' {
                    $state.ForeColor = [System.Drawing.Color]::FromArgb(176, 122, 0)
                    $progress.Style = 'Continuous'
                    $progress.Value = 100
                    $timer.Stop()
                }
                'error'  { $state.ForeColor = [System.Drawing.Color]::FromArgb(176, 122, 0) }
                default  { $state.ForeColor = [System.Drawing.Color]::FromArgb(0, 90, 158) }
            }

            # Build details pane.
            $sb = New-Object System.Text.StringBuilder
            [void]$sb.AppendLine("CorrelationId : $CorrelationId")
            [void]$sb.AppendLine("Device         : $DeviceName")
            if ($srv) {
                if ($srv.state)           { [void]$sb.AppendLine(("ServerState    : {0}" -f $srv.state)) }
                if ($srv.previousState)   { [void]$sb.AppendLine(("PreviousState  : {0}" -f $srv.previousState)) }
                if ($srv.issuedAt)        { [void]$sb.AppendLine(("IssuedAt       : {0}" -f $srv.issuedAt)) }
                if ($srv.lastChangedAt)   { [void]$sb.AppendLine(("LastChangedAt  : {0}" -f $srv.lastChangedAt)) }
                if ($srv.lastPolledAt)    { [void]$sb.AppendLine(("LastPolledAt   : {0}" -f $srv.lastPolledAt)) }
                if ($srv.pollAttempts)    { [void]$sb.AppendLine(("PollAttempts   : {0}" -f $srv.pollAttempts)) }
            }
            if ($obj.note) { [void]$sb.AppendLine(("Note           : {0}" -f $obj.note)) }
            $infoBox.Text = $sb.ToString()

            # Append state-transition line.
            $effective = if ($srvState) { $srvState } else { "[$local]" }
            if ($effective -and $effective -ne $script:lastSeenState) {
                $line = "{0}  {1}" -f ((Get-Date).ToString('HH:mm:ss')), $effective
                $script:transitions.Add($line) | Out-Null
                $timeline.Text = ($script:transitions -join "`r`n")
                $timeline.SelectionStart = $timeline.Text.Length
                $timeline.ScrollToCaret()
                $script:lastSeenState = $effective
            }

            $footer.Text = ("Ultimo aggiornamento: {0}  |  scadenza UI: {1}" -f `
                ([string]$obj.clientUpdatedAt), $script:deadline.ToString('HH:mm:ss'))

            if ((Get-Date) -ge $script:deadline -and $timer.Enabled) {
                $timer.Stop()
                $progress.Style = 'Continuous'; $progress.Value = 100
                $state.ForeColor = [System.Drawing.Color]::FromArgb(176, 122, 0)
                $state.Text = "Tempo massimo UI scaduto. Il poller SYSTEM continua in background."
            }
        } catch {
            $footer.Text = "Errore lettura stato: $($_.Exception.Message)"
        }
    }
    $timer.Add_Tick($tick.GetNewClosure())
    $timer.Start()

    [void]$form.ShowDialog()
    $timer.Stop()
    $timer.Dispose()
    $form.Dispose()
}
