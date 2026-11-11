# Undoes install.ps1: services, firewall rule, backup task and power settings. Never deletes data.
# The database and files stay in place, so install.ps1 will refuse to run until the folder is moved. -MoveFolderAside does that by renaming it.
#Requires -RunAsAdministrator
param(
    [string]$Root = 'C:\AlShifa',
    [switch]$MoveFolderAside
)

$ErrorActionPreference = 'Continue'

# Left by the old online reports (before online.ps1): only the tunnel this system set up, never another Cloudflare installation.
$tunnel = Get-CimInstance Win32_Service -Filter "Name='Cloudflared'" -ErrorAction SilentlyContinue
if ($tunnel -and $tunnel.PathName -like "*$(Join-Path $Root 'cloudflared')\*") {
    Stop-Service Cloudflared -Force -ErrorAction SilentlyContinue
    & sc.exe delete Cloudflared | Out-Null
    'Service removed: Cloudflared (online reports tunnel)'
}

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

# What Install-AlShifa.exe added. Its private key no longer exists, so this certificate is harmless, but nothing should be left behind.
Get-ChildItem Cert:\LocalMachine\Root | Where-Object Subject -eq 'CN=Al-Shifa Hospital Local CA' | Remove-Item -ErrorAction SilentlyContinue
$desktop = [Environment]::GetFolderPath('CommonDesktopDirectory')
Get-ChildItem $desktop -Filter '*.url' | Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -match 'Managed=1' } | Remove-Item -ErrorAction SilentlyContinue
$shell = New-Object -ComObject WScript.Shell
# The mark common.ps1 gives the desktop icon, and the folder of AlShifa.exe it opens.
# Read through a copy with an ASCII name: WScript.Shell reads a Persian file name back empty (see Read-Shortcut in common.ps1).
Get-ChildItem $desktop -Filter '*.lnk' | Where-Object {
    $temp = Join-Path $env:TEMP "alshifa-$([guid]::NewGuid().ToString('N')).lnk"
    Copy-Item -LiteralPath $_.FullName $temp
    try { $shell.CreateShortcut($temp).Description -eq 'AlShifa managed shortcut' } finally { Remove-Item -LiteralPath $temp -ErrorAction SilentlyContinue }
} | Remove-Item -ErrorAction SilentlyContinue
Remove-Item (Join-Path $desktop 'AlShifa-Staff-Connect') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:ProgramData 'AlShifa') -Recurse -Force -ErrorAction SilentlyContinue

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
