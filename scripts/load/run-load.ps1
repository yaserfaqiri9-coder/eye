# Seeds a separate load-test database, starts a Release build against it and runs the concurrent workload.
# Everything runs on the local development MySQL; alshifa_dev and alshifa_test are not touched.
param(
    [int]$Patients = 50000,
    [int]$Visits = 150000,
    [int]$Seconds = 120,
    [int]$Users = 30,
    [switch]$CrashTest
)

. (Join-Path $PSScriptRoot '..\dev-env.ps1')
$ErrorActionPreference = 'Continue'

$bin = Join-Path $root '.tools\mysql-8.4.11-winx64\bin'
$work = Join-Path $root '.tools\load'
New-Item -ItemType Directory -Force $work | Out-Null

function Invoke-Root([string]$Sql) {
    $env:MYSQL_PWD = $secrets.root
    $Sql | & "$bin\mysql.exe" --protocol=TCP -h 127.0.0.1 -P 3307 -u root --default-character-set=utf8mb4
    Remove-Item Env:\MYSQL_PWD
}

function Stop-Port([int]$Port) {
    $listener = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue
    if ($listener) { Stop-Process -Id $listener.OwningProcess -Force; Start-Sleep -Seconds 2 }
}

$port = 5090
$connection = "Server=127.0.0.1;Port=3307;Database=alshifa_load;User=alshifa;Password=$($secrets.app);Maximum Pool Size=100;"

Stop-Port $port
Invoke-Root @"
DROP DATABASE IF EXISTS alshifa_load;
CREATE DATABASE alshifa_load CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
GRANT ALL PRIVILEGES ON alshifa_load.* TO 'alshifa'@'localhost';
FLUSH PRIVILEGES;
"@

$publish = Join-Path $work 'publish'
Set-Location (Join-Path $root 'backend')
dotnet publish src/AlShifa.Api -c Release -o $publish -v q --nologo 2>&1 | Select-Object -Last 2 | ForEach-Object { "$_" }
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

$env:ConnectionStrings__Default = $connection
$env:ASPNETCORE_ENVIRONMENT = 'LoadTest'
$env:ASPNETCORE_URLS = "http://127.0.0.1:$port"
$env:RateLimit__LoginPerMinute = '100000'
$env:Logging__LogLevel__Default = 'Warning'
$env:Storage__Path = Join-Path $work 'storage'

$adminPassword = 'Load-Admin-Password-1'
$env:ALSHIFA_ADMIN_USER = 'loadadmin'
$env:ALSHIFA_ADMIN_PASSWORD = $adminPassword
& (Join-Path $publish 'AlShifa.Api.exe') --create-admin 2>&1 | Select-Object -Last 1 | ForEach-Object { "$_" }
"create-admin exit: $LASTEXITCODE"
Remove-Item Env:\ALSHIFA_ADMIN_PASSWORD

$seed = Join-Path $work 'seed.sql'
"generating $Patients patients and $Visits visits"
cmd /c "node `"$PSScriptRoot\seed.mjs`" $Patients $Visits > `"$seed`""
$env:MYSQL_PWD = $secrets.app
$seedTime = Measure-Command { cmd /c "`"$bin\mysql.exe`" --protocol=TCP -h 127.0.0.1 -P 3307 -u alshifa --default-character-set=utf8mb4 alshifa_load < `"$seed`"" }
Remove-Item Env:\MYSQL_PWD
"seeded in $([int]$seedTime.TotalSeconds)s"
Remove-Item $seed

function Start-Api {
    Start-Process -FilePath "$publish\AlShifa.Api.exe" -WorkingDirectory $publish -WindowStyle Hidden -RedirectStandardOutput "$work\api.out.log" -RedirectStandardError "$work\api.err.log"
    foreach ($i in 1..60) {
        try { Invoke-WebRequest "http://127.0.0.1:$port/health" -UseBasicParsing -TimeoutSec 3 | Out-Null; return } catch { Start-Sleep -Seconds 1 }
    }
    throw 'API did not start'
}

Start-Api

$env:MYSQL_PWD = $secrets.app
"rows: " + (& "$bin\mysql.exe" --protocol=TCP -h 127.0.0.1 -P 3307 -u alshifa -N -e "SELECT (SELECT COUNT(*) FROM Patients), (SELECT COUNT(*) FROM Visits)" alshifa_load)
Remove-Item Env:\MYSQL_PWD

$summary = Join-Path $work 'summary.json'
if ($CrashTest) {
    # The API is killed mid-workload and restarted while clients keep sending; the books must still agree.
    $job = Start-Job -ScriptBlock {
        param($p, $delay)
        Start-Sleep -Seconds $delay
        (Get-NetTCPConnection -State Listen -LocalPort $p).OwningProcess | ForEach-Object { Stop-Process -Id $_ -Force }
    } -ArgumentList $port, ([int]($Seconds / 2))
    $restart = Start-Job -ScriptBlock {
        param($publish, $work, $delay, $connection, $port, $storage)
        Start-Sleep -Seconds $delay
        $env:ConnectionStrings__Default = $connection
        $env:ASPNETCORE_ENVIRONMENT = 'LoadTest'
        $env:ASPNETCORE_URLS = "http://127.0.0.1:$port"
        $env:RateLimit__LoginPerMinute = '100000'
        $env:Storage__Path = $storage
        Start-Process -FilePath "$publish\AlShifa.Api.exe" -WorkingDirectory $publish -WindowStyle Hidden -RedirectStandardOutput "$work\api2.out.log" -RedirectStandardError "$work\api2.err.log"
    } -ArgumentList $publish, $work, ([int]($Seconds / 2) + 8), $connection, $port, $env:Storage__Path
}

node (Join-Path $PSScriptRoot 'loadtest.mjs') "http://127.0.0.1:$port" 'loadadmin' $adminPassword $Seconds $Users $summary 2>&1 | ForEach-Object { "$_" }
"loadtest exit: $LASTEXITCODE"

if ($CrashTest) {
    Wait-Job $job, $restart | Out-Null
    Remove-Job $job, $restart
}

$sql = @"
SELECT 'duplicate_queue', COUNT(*) FROM (SELECT 1 FROM Visits GROUP BY VisitDate, DepartmentId, QueueNumber HAVING COUNT(*) > 1) d
UNION ALL SELECT 'duplicate_visit_number', COUNT(*) FROM (SELECT 1 FROM Visits GROUP BY VisitNumber HAVING COUNT(*) > 1) d
UNION ALL SELECT 'duplicate_receipt', COUNT(*) FROM (SELECT 1 FROM Payments GROUP BY ReceiptNumber HAVING COUNT(*) > 1) d
UNION ALL SELECT 'negative_stock', COUNT(*) FROM StockBatches WHERE Quantity < 0
UNION ALL SELECT 'stock_vs_movements', COUNT(*) FROM (SELECT b.Id FROM StockBatches b LEFT JOIN StockMovements m ON m.BatchId = b.Id GROUP BY b.Id, b.Quantity HAVING b.Quantity <> COALESCE(SUM(m.Quantity), 0)) d
UNION ALL SELECT 'unbalanced_ledger_tx', COUNT(*) FROM (SELECT TransactionId FROM LedgerEntries GROUP BY TransactionId HAVING SUM(Debit) <> SUM(Credit)) d
UNION ALL SELECT 'invoice_total_vs_items', COUNT(*) FROM (SELECT i.Id FROM Invoices i LEFT JOIN InvoiceItems x ON x.InvoiceId = i.Id AND x.IsVoid = 0 WHERE i.Status <> 'Void' GROUP BY i.Id, i.Total HAVING i.Total <> COALESCE(SUM(x.LineTotal), 0)) d
UNION ALL SELECT 'invoice_paid_vs_payments', COUNT(*) FROM (SELECT i.Id FROM Invoices i LEFT JOIN Payments p ON p.InvoiceId = i.Id GROUP BY i.Id, i.Paid HAVING i.Paid <> COALESCE(SUM(p.Amount), 0)) d
UNION ALL SELECT 'overpaid_invoice', COUNT(*) FROM Invoices WHERE Paid + Discount > Total
UNION ALL SELECT 'unprocessed_outbox', COUNT(*) FROM OutboxEvents WHERE ProcessedAt IS NULL;
"@
"--- integrity after load ---"
$env:MYSQL_PWD = $secrets.app
& "$bin\mysql.exe" --protocol=TCP -h 127.0.0.1 -P 3307 -u alshifa -N -e $sql alshifa_load
Remove-Item Env:\MYSQL_PWD

Stop-Port $port
