# Installs a new release on the hospital host, outside working hours. Run elevated, from the scripts folder of the new release.
# Order: stop, fresh backup, prove the backup restores, keep the old version, copy, migrate, start, check. Stops at the first problem.
#Requires -RunAsAdministrator
param(
    [Parameter(Mandatory)][string]$BackupDestination,
    [string]$Root = 'C:\AlShifa',
    [switch]$AllowSameDisk
)

. (Join-Path $PSScriptRoot 'common.ps1')

$release = Split-Path $PSScriptRoot -Parent
$appDir = Join-Path $Root 'app'
$previous = Join-Path $Root 'app.previous'
$config = Join-Path $appDir 'appsettings.Production.json'
if (-not (Test-Path $config)) { throw "$config is missing. Is this the right computer?" }

# Stopped first, so nothing entered after the backup is lost if the backup has to be restored.
'== Stop the application'
Stop-Service AlShifaApi

'== Backup before changing anything'
try {
    & (Join-Path $PSScriptRoot 'backup.ps1') -Root $Root -Destination $BackupDestination -AllowSameDisk:$AllowSameDisk
    if ($LASTEXITCODE) { throw 'The backup failed.' }
    # MySQL cannot undo a schema change, so this backup is the only way back. It must be known to open and restore.
    & (Join-Path $PSScriptRoot 'restore-test.ps1') -Root $Root -Backup $BackupDestination
} catch {
    Start-Service AlShifaApi
    throw "The update was not started and the current version is running again. Reason: $_"
}

'== Swap the application files'
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
    Invoke-Native (Join-Path $appDir 'AlShifa.Api.exe') @('--migrate', '--backup-taken') | Out-Null
} catch {
    # A failed migration can leave the schema half changed, so starting either version automatically would be unsafe.
    throw "Migration failed and the application is stopped. See 'Rollback' in DEPLOYMENT-WINDOWS.md. Reason: $_"
} finally {
    Pop-Location
}

Start-Service AlShifaApi
Start-Sleep -Seconds 5
& (Join-Path $Root 'scripts\health-check.ps1') -Root $Root
if ($LASTEXITCODE) { 'The update finished, but the check above found a problem. Fix it before staff start work.' }
'Old version kept in app.previous. To go back: see "Rollback" in DEPLOYMENT-WINDOWS.md.'
