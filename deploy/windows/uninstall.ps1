# Undoes install.ps1: services, firewall rule, backup task and power settings. Never deletes data.
# The database and files stay in place, so install.ps1 will refuse to run until the folder is moved. -MoveFolderAside does that by renaming it.
#Requires -RunAsAdministrator
param(
    [string]$Root = 'C:\AlShifa',
    [switch]$MoveFolderAside
)

$ErrorActionPreference = 'Continue'

foreach ($name in 'AlShifaApi', 'AlShifaMySQL') {
    if (Get-Service $name -ErrorAction SilentlyContinue) {
        Stop-Service $name -Force -ErrorAction SilentlyContinue
        & sc.exe delete $name | Out-Null
        "Service removed: $name"
    }
}

Get-NetFirewallRule -DisplayName 'Al-Shifa HTTPS (hospital LAN)' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Unregister-ScheduledTask -TaskName 'AlShifa Backup' -Confirm:$false -ErrorAction SilentlyContinue
'Firewall rule and backup task removed'

$power = Join-Path $Root 'logs\power-before-install.pow'
if (Test-Path $power) {
    $imported = [regex]::Match((& powercfg.exe /import $power | Out-String), '[0-9a-fA-F-]{36}').Value
    if ($imported) { & powercfg.exe /setactive $imported; 'Previous power plan restored' }
}

if ($MoveFolderAside -and (Test-Path $Root)) {
    $aside = "$Root.removed-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    Move-Item $Root $aside
    "Folder kept at: $aside (delete it by hand only when you are sure it holds nothing you need)"
} elseif (Test-Path $Root) {
    "Folder kept at: $Root"
}
