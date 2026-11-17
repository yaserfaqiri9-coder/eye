# Export only the pharmacy stock of this checkout's development database: products, batches,
# their opening-stock movements and the matching opening-stock ledger postings, on top of the seeded
# reference data. Patients, visits, invoices, payments, expenses, cash sessions, accounts, audit and
# sync history are left out. The result is restored with restore-transfer.ps1 like a full snapshot.
param(
    [Parameter(Mandatory)][string]$Destination,
    # Only products whose code matches this LIKE pattern travel (e.g. 'ASH2611-%'); test items and their stock stay behind.
    [ValidatePattern('^[A-Za-z0-9_%-]+$')][string]$ProductCodeLike
)
. (Join-Path $PSScriptRoot 'transfer-common.ps1')
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$bin = Join-Path $repo '.tools\mysql-8.4.11-winx64\bin'
$mysql = Join-Path $bin 'mysql.exe'
$mysqldump = Join-Path $bin 'mysqldump.exe'
$secrets = Get-Content (Join-Path $repo '.tools\dev-secrets.json') -Raw | ConvertFrom-Json
if (Test-Path -LiteralPath $Destination) { throw 'Destination already exists; choose a new snapshot folder.' }
New-Item -ItemType Directory -Path $Destination | Out-Null
$Destination = (Resolve-Path -LiteralPath $Destination).Path
$id = [Guid]::NewGuid().ToString('N')
$cnf = Join-Path $repo ".tools\transfer-$id.cnf"
$full = Join-Path $repo ".tools\transfer-$id-full.sql"
$work = "alshifa_stock_work_$id"
$check = "alshifa_stock_check_$id"
$created = @()

# Every table not named here is emptied. Kept tables are trimmed to stock and seeded reference rows.
$keep = @('__efmigrationshistory', 'aspnetroleclaims', 'aspnetroles', 'aspnetusers', 'cashregisters', 'departments',
    'diagnostictests', 'ledgeraccounts', 'ledgerentries', 'ledgertransactions', 'numbersequences', 'products',
    'services', 'stockbatches', 'stocklocations', 'stockmovements')
$only = if ($ProductCodeLike) { @"
-- Chosen products only: the rest of the catalogue and its stock is not copied.
DELETE m FROM StockMovements m JOIN StockBatches b ON b.Id = m.BatchId JOIN Products p ON p.Id = b.ProductId WHERE p.Code NOT LIKE '$ProductCodeLike';
DELETE b FROM StockBatches b JOIN Products p ON p.Id = b.ProductId WHERE p.Code NOT LIKE '$ProductCodeLike';
DELETE FROM Products WHERE Code NOT LIKE '$ProductCodeLike';
"@ } else { '' }
$trim = $only + @"
-- Opening stock only: no sale, purchase or transfer may have moved a batch.
SELECT IF(COUNT(*) = 0, 'ok', 'other-movements') INTO @movements FROM StockMovements WHERE SourceType <> 'OpeningStock';
DELETE e FROM LedgerEntries e JOIN LedgerTransactions t ON t.Id = e.TransactionId WHERE t.Kind <> 'OpeningStock';
DELETE FROM LedgerTransactions WHERE Kind <> 'OpeningStock';
DELETE FROM NumberSequences WHERE Scope <> 'code:product';
-- Seeded rows only: the first register and the three system services. Test rows and tariffs go.
DELETE FROM CashRegisters WHERE Id <> (SELECT Id FROM (SELECT MIN(Id) Id FROM CashRegisters) r);
DELETE FROM DiagnosticTests WHERE ServiceId NOT IN (SELECT Id FROM Services WHERE Code IN ('VISIT-FEE','REFRACTION','IMAGING'));
-- The stock rows name user 1 as their author. It stays as a record only: no role, no password, locked.
DELETE FROM AspNetUsers WHERE Id <> 1;
UPDATE AspNetUsers SET IsActive = 0, PasswordHash = NULL, SecurityStamp = UUID(), ConcurrencyStamp = UUID(),
    LockoutEnabled = 1, LockoutEnd = '9999-12-31 00:00:00', AccessFailedCount = 0, FullName = 'انتقال موجودی اولیه' WHERE Id = 1;
SELECT @movements;
"@
try {
    Write-ClientCnf $cnf 'root' $secrets.root 3307
    Protect-Path $cnf @("$([Security.Principal.WindowsIdentity]::GetCurrent().Name)=F")
    $serviceCodes = @(Invoke-Native $mysql @("--defaults-extra-file=$cnf", '-N', '-B', 'alshifa_dev', '-e', 'SELECT Code FROM Services ORDER BY Id'))
    Invoke-Native $mysqldump @("--defaults-extra-file=$cnf", '--single-transaction', '--quick', '--no-tablespaces', '--set-gtid-purged=OFF', '--triggers', '--hex-blob', '--default-character-set=utf8mb4', "--result-file=$full", 'alshifa_dev') | Out-Null
    if ((Get-Content -LiteralPath $full -Tail 1) -notmatch '^-- Dump completed') { throw 'Incomplete dump.' }
    Invoke-Native $mysql @("--defaults-extra-file=$cnf", '-e', "CREATE DATABASE $work CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci") | Out-Null
    $created += $work
    Import-TransferSql $mysql $cnf $work $full

    $empty = @(Get-TransferTables $mysql $cnf $work | Where-Object { $keep -notcontains $_.ToLowerInvariant() })
    $sql = 'SET FOREIGN_KEY_CHECKS=0; ' + (($empty | ForEach-Object { "DELETE FROM ``$_``;" }) -join ' ') + ' SET FOREIGN_KEY_CHECKS=1;'
    Invoke-Native $mysql @("--defaults-extra-file=$cnf", $work, '-e', $sql) | Out-Null
    $services = "DELETE FROM Services WHERE Code NOT IN ('VISIT-FEE','REFRACTION','IMAGING');"
    $result = @(Invoke-Native $mysql @("--defaults-extra-file=$cnf", '--default-character-set=utf8mb4', '-N', '-B', $work, '-e', ($trim + $services)))
    if ($result[-1] -ne 'ok') { throw 'The development stock has movements other than opening stock; a stock-only copy would not match the batches.' }

    # Nothing outside stock and reference data may remain.
    $left = @(Get-TransferFingerprint $mysql $cnf $work | Where-Object { $_.rows -gt 0 -and $keep -notcontains $_.table.ToLowerInvariant() })
    if ($left.Count) { throw "Not emptied: $(($left | ForEach-Object table) -join ', ')" }
    $counts = @(Invoke-Native $mysql @("--defaults-extra-file=$cnf", '-N', '-B', $work, '-e', "SELECT (SELECT COUNT(*) FROM Services), (SELECT COUNT(*) FROM AspNetUsers), (SELECT COUNT(*) FROM StockBatches), (SELECT COALESCE(SUM(Quantity),0) FROM StockBatches) - (SELECT COALESCE(SUM(Quantity),0) FROM StockMovements)"))[0] -split "`t"
    if ([int]$counts[0] -ne 3) { throw "Expected the three system services, found $($counts[0]). Seeded codes differ: $($serviceCodes -join ', ')" }
    if ([int]$counts[1] -ne 1) { throw 'Expected only the locked author account.' }
    if ([decimal]$counts[3] -ne 0) { throw 'Batch quantities do not match their movements.' }

    $expected = @(Get-TransferFingerprint $mysql $cnf $work)
    $dump = Join-Path $Destination 'db.sql'
    Invoke-Native $mysqldump @("--defaults-extra-file=$cnf", '--single-transaction', '--quick', '--no-tablespaces', '--set-gtid-purged=OFF', '--triggers', '--hex-blob', '--default-character-set=utf8mb4', "--result-file=$dump", $work) | Out-Null
    if ((Get-Content -LiteralPath $dump -Tail 1) -notmatch '^-- Dump completed') { throw 'Incomplete dump.' }

    # Prove the copy restores to exactly the trimmed content.
    Invoke-Native $mysql @("--defaults-extra-file=$cnf", '-e', "CREATE DATABASE $check CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci") | Out-Null
    $created += $check
    Import-TransferSql $mysql $cnf $check $dump
    Assert-TransferFingerprint $expected @(Get-TransferFingerprint $mysql $cnf $check)
    $inventory = @(Invoke-Native $mysql @("--defaults-extra-file=$cnf", '-N', '-B', $check, '-e', 'SELECT l.Code,COUNT(DISTINCT b.ProductId),COUNT(*),SUM(b.Quantity),SUM(b.Quantity*b.UnitCost) FROM StockBatches b JOIN StockLocations l ON l.Id=b.LocationId GROUP BY l.Code ORDER BY l.Code'))
    $migrations = @(Invoke-Native $mysql @("--defaults-extra-file=$cnf", '-N', '-B', $check, '-e', 'SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId'))
    $files = @([ordered]@{ path = 'db.sql'; sha256 = (Get-FileHash -LiteralPath $dump -Algorithm SHA256).Hash })
    [ordered]@{ format = 1; createdUtc = [DateTime]::UtcNow.ToString('o'); source = '127.0.0.1:3307/alshifa_dev (stock only)'; restoreVerified = $true; tables = $expected; migrations = $migrations; inventory = $inventory; files = $files } |
        ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $Destination 'manifest.json') -Encoding UTF8
    "RESULT: stock-only snapshot restored and verified."
    $expected | Where-Object { $_.rows -gt 0 } | ForEach-Object { "  $($_.table): $($_.rows)" }
    $inventory
} finally {
    foreach ($db in $created) { Invoke-Native $mysql @("--defaults-extra-file=$cnf", '-e', "DROP DATABASE IF EXISTS $db") | Out-Null }
    Remove-Item -LiteralPath $cnf, $full -Force -ErrorAction SilentlyContinue
}
