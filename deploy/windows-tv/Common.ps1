Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-TvOrigin([string]$Value) {
    $parsed = $null
    if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$parsed) -or
        $parsed.Scheme -notin @('http', 'https') -or $parsed.UserInfo -or $parsed.Query -or $parsed.Fragment -or $parsed.AbsolutePath -ne '/') {
        throw 'Enter only the hospital origin, such as http://localhost:5080 or https://hospital.example.com. Paths, credentials and queries are not accepted.'
    }
    return $parsed.GetLeftPart([UriPartial]::Authority)
}

function Assert-TvProtocol([string]$Value, [string]$Origin) {
    if (-not $Value) { return }
    $parsed = $null
    if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$parsed) -or $parsed.Scheme -ne 'alshifa-tv' -or
        $parsed.Host -ne 'open' -or $parsed.UserInfo -or -not $parsed.IsDefaultPort -or $parsed.Fragment -or $parsed.AbsolutePath -notin @('', '/')) {
        throw 'Invalid TV launch request.'
    }
    if ($parsed.Query -notmatch '^\?origin=([^&]+)$') { throw 'The launch request must contain exactly one hospital origin.' }
    $requested = Get-TvOrigin ([Uri]::UnescapeDataString($Matches[1]))
    if ($requested -cne $Origin) { throw 'This request is not from the hospital configured during installation. Run Install-TV.cmd to change it.' }
}

function Get-TvDisplay($Displays, [string]$PreferredDevice) {
    $secondary = @($Displays | Where-Object { -not $_.Primary })
    if ($PreferredDevice) {
        $selected = @($secondary | Where-Object { $_.DeviceName -eq $PreferredDevice })
        if ($selected.Count -eq 1) { return $selected[0] }
        throw 'The configured TV is not connected as a secondary display. Reconnect it or run Install-TV.cmd.'
    }
    if ($secondary.Count -eq 1) { return $secondary[0] }
    if ($secondary.Count -eq 0) { throw 'No secondary display is connected. Connect HDMI and select Extend in Windows.' }
    throw 'More than one secondary display is connected. Run Install-TV.cmd and select the TV device.'
}

function Find-TvBrowser {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft/Edge/Application/msedge.exe'),
        (Join-Path $env:ProgramFiles 'Microsoft/Edge/Application/msedge.exe'),
        (Join-Path $env:LOCALAPPDATA 'Microsoft/Edge/Application/msedge.exe'),
        (Join-Path $env:ProgramFiles 'Google/Chrome/Application/chrome.exe'),
        (Join-Path $env:LOCALAPPDATA 'Google/Chrome/Application/chrome.exe')
    )
    foreach ($candidate in $candidates) { if (Test-Path -LiteralPath $candidate) { return $candidate } }
    throw 'Microsoft Edge or Google Chrome is required.'
}

function Get-TvProcessIds([string]$ProfilePath) {
    # Match only browser processes owned by this launcher profile, never the employee browser.
    $pathPattern = [Regex]::Escape($ProfilePath)
    $escaped = '(?:--user-data-dir="' + $pathPattern + '"|"--user-data-dir=' + $pathPattern + '"|--user-data-dir=' + $pathPattern + '(?=\s|$))'
    return @(Get-CimInstance Win32_Process -Filter "Name = 'msedge.exe' OR Name = 'chrome.exe'" |
        Where-Object { $_.CommandLine -and $_.CommandLine -match $escaped } | ForEach-Object { [int]$_.ProcessId })
}
