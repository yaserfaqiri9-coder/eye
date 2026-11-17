# For the TV connected directly to the hospital server. Reads only the public
# connection file, never appsettings/SQL credentials. Does not install in ValidateOnly mode.
param(
    [string]$AppOrigin,
    [string]$ConnectionFile = (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'AlShifa-Staff-Connect\connect.ini'),
    [switch]$AskStartup,
    [switch]$EnableStartup,
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
if (-not $AppOrigin -and (Test-Path -LiteralPath $ConnectionFile)) {
    $ports = @(Get-Content -LiteralPath $ConnectionFile | Where-Object { $_ -match '^port=(\d+)$' } | ForEach-Object { [int]$_.Substring(5) })
    if ($ports.Count -ne 1 -or $ports[0] -lt 1 -or $ports[0] -gt 65535) { throw 'The public hospital connection file has no valid unique port.' }
    # This helper is for the TV on the server itself: loopback remains valid after a router/IP change.
    $AppOrigin = 'https://127.0.0.1' + $(if ($ports[0] -ne 443) { ':' + $ports[0] })
    Write-Host "Installed hospital address: $AppOrigin"
}
if (-not $AppOrigin) {
    if ($ValidateOnly) { throw 'No public connection file found. Supply the hospital AppOrigin.' }
    $AppOrigin = Read-Host 'Hospital address shown at the end of installation (https://...)'
}
if ($AskStartup -and -not $ValidateOnly) {
    $EnableStartup = (Read-Host 'Open the TV automatically after this Windows user signs in? [Y/N]') -match '^[Yy]$'
}
& (Join-Path $PSScriptRoot 'Install-TV.ps1') -AppOrigin $AppOrigin -EnableStartup:$EnableStartup -ValidateOnly:$ValidateOnly
