$ErrorActionPreference = 'Stop'
$root = Join-Path $env:LOCALAPPDATA 'AlShifa/TV'
& (Join-Path $root 'Startup.ps1') -Enable $false
$protocol = 'HKCU:\Software\Classes\alshifa-tv'
$commandKey = $protocol + '\shell\open\command'
if (Test-Path -LiteralPath $commandKey) {
    $registered = (Get-Item -LiteralPath $commandKey).GetValue('')
    if ($registered.Contains((Join-Path $root 'Launch-TV.ps1'))) { Remove-Item -LiteralPath $protocol -Recurse }
}
$desktop = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Al-Shifa TV.lnk'
if (Test-Path -LiteralPath $desktop) {
    $shell = New-Object -ComObject WScript.Shell
    if ($shell.CreateShortcut($desktop).Arguments.Contains((Join-Path $root 'Launch-TV.ps1'))) { Remove-Item -LiteralPath $desktop }
}
# Keep the installed files/profile and any signed-in session for manual review; never close employee browser windows.
Write-Host 'TV protocol and startup disabled. The TV profile has been retained in LocalAppData/AlShifa/TV.'
