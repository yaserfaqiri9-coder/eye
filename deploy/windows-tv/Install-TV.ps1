param(
    [string]$AppOrigin,
    [string]$MonitorDevice = '',
    [switch]$EnableStartup,
    [switch]$ValidateOnly
)
. (Join-Path $PSScriptRoot 'Common.ps1')
if (-not $AppOrigin) { $AppOrigin = Read-Host 'Hospital address (for example http://localhost:5080)' }
$origin = Get-TvOrigin $AppOrigin
$browser = Find-TvBrowser
$installRoot = Join-Path $env:LOCALAPPDATA 'AlShifa/TV'
$powerShell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
$launcher = Join-Path $installRoot 'Launch-TV.ps1'
$command = '"' + $powerShell + '" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $launcher + '" -ProtocolUri "%1"'
if ($ValidateOnly) {
    [pscustomobject]@{Origin=$origin; Browser=$browser; InstallRoot=$installRoot; ProtocolCommand=$command; Startup=[bool]$EnableStartup; ChangesMade=$false}
    return
}
if (-not ('AlShifaTvNative' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'Native.cs') }
[AlShifaTvNative]::InitDpi()
$displays = @([AlShifaTvNative]::Displays() | Where-Object { -not $_.Primary })
if (-not $MonitorDevice -and $displays.Count -eq 1) { $MonitorDevice = $displays[0].DeviceName }
if (-not $MonitorDevice -and $displays.Count -gt 1) {
    for ($index = 0; $index -lt $displays.Count; $index++) { Write-Host ("{0}: {1} ({2}x{3})" -f ($index + 1), $displays[$index].DeviceName, $displays[$index].Width, $displays[$index].Height) }
    $selection = 0
    if (-not [int]::TryParse((Read-Host 'TV display number'), [ref]$selection) -or $selection -lt 1 -or $selection -gt $displays.Count) { throw 'Invalid display number.' }
    $MonitorDevice = $displays[$selection - 1].DeviceName
}
if ($MonitorDevice -and @($displays | Where-Object DeviceName -eq $MonitorDevice).Count -ne 1) { throw 'The selected device must be a connected secondary display.' }
# Current-user installation: no elevation, services, firewall changes, scheduled passwords or HTTP listeners.
New-Item -ItemType Directory -Force -Path $installRoot | Out-Null
foreach ($file in @('Common.ps1','Native.cs','Launch-TV.ps1','Startup.ps1','Uninstall-TV.ps1')) {
    $source = Join-Path $PSScriptRoot $file
    $target = Join-Path $installRoot $file
    if ([IO.Path]::GetFullPath($source) -ne [IO.Path]::GetFullPath($target)) { Copy-Item -LiteralPath $source -Destination $target -Force }
}
@{Origin=$origin; MonitorDevice=$MonitorDevice} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $installRoot 'config.json') -Encoding UTF8
$protocol = 'HKCU:\Software\Classes\alshifa-tv'
New-Item -Path ($protocol + '\shell\open\command') -Force | Out-Null
Set-Item -LiteralPath $protocol -Value 'URL:Al-Shifa TV'
New-ItemProperty -LiteralPath $protocol -Name 'URL Protocol' -Value '' -PropertyType String -Force | Out-Null
Set-Item -LiteralPath ($protocol + '\shell\open\command') -Value $command
& (Join-Path $installRoot 'Startup.ps1') -Enable ([bool]$EnableStartup)
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Al-Shifa TV.lnk'))
$shortcut.TargetPath = $powerShell
$shortcut.Arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $launcher + '"'
$shortcut.WorkingDirectory = $installRoot
$shortcut.WindowStyle = 7
$shortcut.Save()
Write-Host 'Installed. Use the TV button in reception. Sign in with an authorised hospital account in the separate TV window.'
