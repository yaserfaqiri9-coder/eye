param(
    [string]$ProtocolUri,
    [switch]$Startup,
    [switch]$DryRun
)
. (Join-Path $PSScriptRoot 'Common.ps1')
$mutex = $null
$owned = $false
try {
    $config = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'config.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $origin = Get-TvOrigin ([string]$config.Origin)
    Assert-TvProtocol $ProtocolUri $origin
    $profile = Join-Path $PSScriptRoot 'BrowserProfile'
    $browser = Find-TvBrowser
    $url = $origin + '/call-screen?tv=windows'
    if ($DryRun) {
        [pscustomobject]@{ Origin = $origin; Url = $url; Browser = $browser; Profile = $profile; ChangesMade = $false }
        return
    }
    $mutex = New-Object Threading.Mutex($false, ('Local\AlShifaTV-' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value))
    try { $owned = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $owned = $true }
    if (-not $owned) { return }
    if (-not ('AlShifaTvNative' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'Native.cs') }
    [AlShifaTvNative]::InitDpi()
    $previousFocus = [AlShifaTvNative]::GetForegroundWindow()
    # Extend is requested only on an explicit launch or the user's configured sign-in startup.
    $extendResult = [AlShifaTvNative]::Extend()
    if ($extendResult -ne 0) { throw "Windows could not enable Extend (code $extendResult). Select Win+P > Extend and try again." }
    $deadline = [DateTime]::UtcNow.AddSeconds($(if ($Startup) { 90 } else { 10 }))
    $display = $null
    do {
        try { $display = Get-TvDisplay ([AlShifaTvNative]::Displays()) ([string]$config.MonitorDevice) } catch {
            if ([DateTime]::UtcNow -ge $deadline) { throw }
            Start-Sleep -Milliseconds 500
        }
    } until ($display)
    # Do not open the TV over the employee's only screen, even if HDMI disappears while starting.
    if ($display.Primary) { throw 'The primary display cannot be used as the TV.' }
    if ($Startup) {
        $deadline = [DateTime]::UtcNow.AddSeconds(120)
        $ready = $false
        do {
            try {
                $response = Invoke-WebRequest -Uri ($origin + '/api/auth/me') -UseBasicParsing -TimeoutSec 3
                $ready = $response.StatusCode -eq 200
            } catch {
                if ($_.Exception.Response) { $ready = [int]$_.Exception.Response.StatusCode -in @(401, 403) }
            }
            if (-not $ready) { Start-Sleep -Seconds 2 }
        } until ($ready -or [DateTime]::UtcNow -ge $deadline)
        if (-not $ready) { throw 'Hospital server is not available yet. Use the TV button after it starts.' }
    }
    $tvIds = @(Get-TvProcessIds $profile)
    $windows = @([AlShifaTvNative]::Windows([int[]]$tvIds))
    if ($windows.Count -eq 0) {
        # Dedicated persistent browser profile: no employee tabs/cookies/passwords are copied.
        # App mode plus native borderless positioning avoids InPrivate kiosk sessions losing login on every launch.
        $arguments = @(
            ('--user-data-dir="' + $profile + '"'), ('--app="' + $url + '"'),
            '--new-window', '--no-first-run', '--no-default-browser-check', '--disable-session-crashed-bubble',
            '--autoplay-policy=no-user-gesture-required', '--start-fullscreen',
            ('--window-position=' + $display.X + ',' + $display.Y), ('--window-size=' + $display.Width + ',' + $display.Height)
        )
        Start-Process -FilePath $browser -ArgumentList $arguments -WindowStyle Hidden | Out-Null
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    $placed = $false
    do {
        $tvIds = @(Get-TvProcessIds $profile)
        $windows = @([AlShifaTvNative]::Windows([int[]]$tvIds))
        if ($windows.Count -gt 0) {
            # Re-read monitor bounds in case Windows changed resolution during HDMI negotiation.
            $display = Get-TvDisplay ([AlShifaTvNative]::Displays()) ([string]$config.MonitorDevice)
            foreach ($handle in $windows) {
                if (-not [AlShifaTvNative]::Place($handle, $display)) { throw 'Windows could not move the TV window.' }
            }
            $placed = $true
            [AlShifaTvNative]::RestoreFocus($previousFocus, [int[]]$tvIds)
            # Stabilise the initial Chromium fullscreen transition without waiting on the employee page.
            Start-Sleep -Milliseconds 600
            foreach ($handle in $windows) { if ([AlShifaTvNative]::IsWindow($handle)) { [void][AlShifaTvNative]::Place($handle, $display) } }
            [AlShifaTvNative]::RestoreFocus($previousFocus, [int[]]$tvIds)
            break
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $placed) { throw 'The dedicated TV browser window did not become available.' }
    Add-Content -LiteralPath (Join-Path $PSScriptRoot 'launcher.log') -Value ((Get-Date -Format s) + ' TV opened on ' + $display.DeviceName) -Encoding UTF8
    $lastBounds = '{0}:{1}:{2}:{3}:{4}' -f $display.DeviceName,$display.X,$display.Y,$display.Width,$display.Height
    # Keep a single hidden process with this window. HDMI removal must not leave the TV covering reception.
    while ($true) {
        Start-Sleep -Seconds 2
        $windows = @($windows | Where-Object { [AlShifaTvNative]::IsWindow($_) })
        if ($windows.Count -eq 0) { break }
        try { $display = Get-TvDisplay ([AlShifaTvNative]::Displays()) ([string]$config.MonitorDevice) } catch {
            foreach ($handle in $windows) { [AlShifaTvNative]::Hide($handle) }
            $lastBounds = ''
            continue
        }
        $bounds = '{0}:{1}:{2}:{3}:{4}' -f $display.DeviceName,$display.X,$display.Y,$display.Width,$display.Height
        if ($bounds -ne $lastBounds) {
            foreach ($handle in $windows) { [void][AlShifaTvNative]::Place($handle, $display) }
            $lastBounds = $bounds
        }
    }
} catch {
    $message = $_.Exception.Message
    Add-Content -LiteralPath (Join-Path $PSScriptRoot 'launcher.log') -Value ((Get-Date -Format s) + ' ' + $message) -Encoding UTF8
    if (-not $Startup -and -not $DryRun) {
        $shell = New-Object -ComObject WScript.Shell
        [void]$shell.Popup($message, 15, 'Al-Shifa TV', 48)
    }
    if ($DryRun) { throw }
    exit 1
} finally {
    if ($owned) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
}
