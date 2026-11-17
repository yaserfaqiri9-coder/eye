param([switch]$Disable)
$ErrorActionPreference = 'Stop'
$installed = Join-Path $env:LOCALAPPDATA 'AlShifa/TV/Startup.ps1'
if (-not (Test-Path -LiteralPath $installed)) { throw 'Run Install-TV.cmd first.' }
& $installed -Enable (-not $Disable)
Write-Host $(if ($Disable) { 'Automatic TV launch disabled.' } else { 'The TV will launch after this Windows user signs in.' })
