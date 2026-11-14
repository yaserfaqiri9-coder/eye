# Put a fresh installation back to the state saved by restore-transfer.ps1 just before it copied a snapshot in.
# For a transfer that brought in data the hospital does not want. Not for a database the hospital has started to use.
#Requires -RunAsAdministrator
param([string]$Root = 'C:\AlShifa')
. (Join-Path $PSScriptRoot 'transfer-common.ps1')
$Root = [IO.Path]::GetFullPath($Root).TrimEnd('\')
$config = Get-Content -LiteralPath (Join-Path $Root 'app\appsettings.Production.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$connection = New-Object System.Data.Common.DbConnectionStringBuilder
$connection.set_ConnectionString([string]$config.ConnectionStrings.Default)
if ($connection['Database'] -ne 'alshifa' -or $connection['Server'] -ne '127.0.0.1') { throw 'Only a local standard Al-Shifa installation is supported.' }
$service = Get-CimInstance Win32_Service -Filter "Name='AlShifaApi'"
if (-not $service -or $service.PathName.Trim('"') -ne (Join-Path $Root 'app\AlShifa.Api.exe')) { throw 'The installed service does not belong to this Al-Shifa folder.' }
$saved = Get-ChildItem -LiteralPath (Join-Path $Root 'transfer-backups') -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^\d{8}T\d{6}Z-' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'before.sql')) } | Sort-Object Name | Select-Object -Last 1
if (-not $saved) { throw 'No transfer backup found. Nothing was transferred, or it was already undone. Nothing was changed.' }
$before = Join-Path $saved.FullName 'before.sql'
if ((Get-Content -LiteralPath $before -Tail 1) -notmatch '^-- Dump completed') { throw 'The saved backup is incomplete. Nothing was changed.' }
$mysql = Join-Path $Root 'mysql\bin\mysql.exe'
$cnf = Join-Path $Root "secrets\transfer-$([Guid]::NewGuid().ToString('N')).cnf"
$undo = Join-Path $Root "transfer-backups\undo-$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ'))-$([Guid]::NewGuid().ToString('N'))"
$stopped = $false
$changed = $false
$safeToStart = $true
try {
    Write-ClientCnf $cnf 'root' (Get-Content (Join-Path $Root 'secrets\mysql-root.txt') -Raw).Trim() ([int]$connection['Port'])
    Protect-Path $cnf
    $counts = @(Invoke-Native $mysql @("--defaults-extra-file=$cnf", '-N', '-B', 'alshifa', '-e', 'SELECT (SELECT COUNT(*) FROM Patients),(SELECT COUNT(*) FROM Invoices),(SELECT COUNT(*) FROM Payments),(SELECT COUNT(*) FROM StockBatches),(SELECT COUNT(*) FROM AspNetUsers)'))[0] -split "`t"
    Write-Host "Now in the database: patients $($counts[0]), invoices $($counts[1]), payments $($counts[2]), stock batches $($counts[3]), user accounts $($counts[4])"
    Write-Host "It will be replaced by the copy saved before the transfer: $($saved.Name)" -ForegroundColor Yellow
    Write-Host 'Everything entered since the transfer is removed from the database (a copy is kept).' -ForegroundColor Yellow
    if ((Read-Host 'Type UNDO to continue') -cne 'UNDO') { throw 'Cancelled before making changes.' }
    Stop-Service AlShifaApi
    $stopped = $true
    # Keep what is there now, in case it is wanted later.
    New-Item -ItemType Directory -Path $undo | Out-Null
    Protect-Path $undo
    $current = Join-Path $undo 'current.sql'
    Invoke-Native (Join-Path $Root 'mysql\bin\mysqldump.exe') @("--defaults-extra-file=$cnf", '--single-transaction', '--no-tablespaces', '--set-gtid-purged=OFF', '--triggers', '--hex-blob', "--result-file=$current", 'alshifa') | Out-Null
    if ((Get-Content -LiteralPath $current -Tail 1) -notmatch '^-- Dump completed') { throw 'Could not keep a copy of the current database. Nothing was changed.' }
    $storage = Join-Path $Root 'storage'
    Copy-Item -LiteralPath $storage -Destination (Join-Path $undo 'storage') -Recurse
    $changed = $true
    Import-TransferSql $mysql $cnf 'alshifa' $before
    # Uploaded files must match the saved state too: remove what the transfer added, bring back what it replaced.
    Get-ChildItem -LiteralPath $storage -Force | Remove-Item -Recurse -Force
    $savedStorage = Join-Path $saved.FullName 'storage'
    if (Test-Path -LiteralPath $savedStorage) { Get-ChildItem -LiteralPath $savedStorage -Force | Copy-Item -Destination $storage -Recurse -Force }
    Invoke-Native 'icacls.exe' @($storage, '/grant', 'NT SERVICE\AlShifaApi:(OI)(CI)M', '/T', '/Q') | Out-Null
    $after = @(Invoke-Native $mysql @("--defaults-extra-file=$cnf", '-N', '-B', 'alshifa', '-e', 'SELECT (SELECT COUNT(*) FROM Patients),(SELECT COUNT(*) FROM Invoices),(SELECT COUNT(*) FROM StockBatches)'))[0] -split "`t"
    if (@($after | Where-Object { $_ -ne '0' }).Count) { throw 'The restored database is not empty as expected.' }
    # The next transfer needs this backup gone, so that a later undo cannot return to it by mistake.
    Rename-Item -LiteralPath $saved.FullName -NewName "used-$($saved.Name)"
    Start-Service AlShifaApi
    $stopped = $false
    $url = $config.Kestrel.Endpoints.Loopback.Url.TrimEnd('/') + '/health'
    $healthy = $false
    foreach ($attempt in 1..30) {
        try { $null = Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 5; $healthy = $true; break } catch { Start-Sleep -Seconds 2 }
    }
    if (-not $healthy) { throw 'The application did not become healthy after the undo.' }
    "RESULT: transfer undone; the installation is back to its fresh state. Copy of the removed data: $undo"
    'Next: run Restore-Database.bat with the stock-only snapshot.'
} catch {
    $reason = $_
    if ($changed) {
        try {
            Stop-Service AlShifaApi -ErrorAction SilentlyContinue
            $stopped = $true
            Import-TransferSql $mysql $cnf 'alshifa' $current
            Get-ChildItem -LiteralPath $storage -Force | Remove-Item -Recurse -Force
            Get-ChildItem -LiteralPath (Join-Path $undo 'storage') -Force | Copy-Item -Destination $storage -Recurse -Force
            Write-Host 'Undo failed; the database was put back as it was before the undo.' -ForegroundColor Yellow
        } catch {
            $safeToStart = $false
            throw "Undo and its rollback failed. Service left stopped. Keep $undo and contact support. $($_.Exception.Message)"
        }
    }
    throw $reason
} finally {
    if ($stopped -and $safeToStart) { Start-Service AlShifaApi }
    Remove-Item -LiteralPath $cnf -Force -ErrorAction SilentlyContinue
}
