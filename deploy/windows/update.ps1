# Installs a new release on the hospital host, outside working hours. Run elevated, from the scripts folder of the new release.
# Order: fresh backup, stop, keep the old version, copy, migrate, start, check. Stops at the first problem.
#Requires -RunAsAdministrator
param(
    [Parameter(Mandatory)][string]$BackupDestination,
    [string]$Root = 'C:\AlShifa'
)

. (Join-Path $PSScriptRoot 'common.ps1')

$release = Split-Path $PSScriptRoot -Parent
$appDir = Join-Path $Root 'app'
$previous = Join-Path $Root 'app.previous'
$config = Join-Path $appDir 'appsettings.Production.json'
if (-not (Test-Path $config)) { throw "$config is missing. Is this the right computer?" }

'== Backup before changing anything'
& (Join-Path $PSScriptRoot 'backup.ps1') -Root $Root -Destination $BackupDestination
if ($LASTEXITCODE) { throw 'Backup failed; the update was not started.' }

'== Stop and swap'
Stop-Service AlShifaApi
if (Test-Path $previous) { Remove-Item $previous -Recurse -Force }
Move-Item $appDir $previous
try {
    New-Item -ItemType Directory $appDir | Out-Null
    Copy-Item (Join-Path $release 'app\*') $appDir -Recurse
    Copy-Item (Join-Path $previous 'appsettings.Production.json') $appDir
    Protect-Path $appDir @('NT SERVICE\AlShifaApi=RX')
} catch {
    # The database has not been touched yet, so going back to the old files is complete and safe.
    Remove-Item $appDir -Recurse -Force -ErrorAction SilentlyContinue
    Move-Item $previous $appDir
    Start-Service AlShifaApi
    throw "Copying the new version failed; the old version is running again. Reason: $_"
}
Copy-Item (Join-Path $PSScriptRoot '*') (Join-Path $Root 'scripts') -Recurse -Force

'== Migrate'
Push-Location $appDir
try {
    Invoke-Native (Join-Path $appDir 'AlShifa.Api.exe') @('--migrate') | Out-Null
} catch {
    # A failed migration can leave the schema half changed, so starting either version automatically would be unsafe.
    throw "Migration failed and the application is stopped. See 'Rollback' in DEPLOYMENT-WINDOWS.md. Reason: $_"
} finally {
    Pop-Location
}

Start-Service AlShifaApi
Start-Sleep -Seconds 5
& (Join-Path $Root 'scripts\health-check.ps1') -Root $Root
'Old version kept in app.previous. To go back: see "Rollback" in DEPLOYMENT-WINDOWS.md.'
