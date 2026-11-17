param([Parameter(Mandatory=$true)][bool]$Enable)
$ErrorActionPreference = 'Stop'
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Startup')) 'Al-Shifa TV.lnk'
if ($Enable) {
    $launcher = Join-Path $PSScriptRoot 'Launch-TV.ps1'
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'config.json'))) { throw 'Install the TV launcher first.' }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
    $shortcut.Arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $launcher + '" -Startup'
    $shortcut.WorkingDirectory = $PSScriptRoot
    $shortcut.WindowStyle = 7
    $shortcut.Save()
} elseif (Test-Path -LiteralPath $shortcutPath) {
    Remove-Item -LiteralPath $shortcutPath
}
