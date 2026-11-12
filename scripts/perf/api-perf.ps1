# Measures API read paths on synthetic data, so a before/after comparison runs on the same rows and the same machine.
# -Setup builds alshifa_load on the development MySQL (3307): 50k patients, 150k visits spread over the last 900 days,
# then a short concurrent workload adds invoices, payments, sales and stock. Without -Setup the existing rows are reused.
# Only alshifa_load is touched; alshifa_dev, alshifa_test and the installed database on 3308 are not.
param(
    [Parameter(Mandatory)][string]$Label,
    [switch]$Setup,
    [int]$Patients = 50000,
    [int]$Visits = 150000
)

. (Join-Path $PSScriptRoot '..\dev-env.ps1')
$ErrorActionPreference = 'Continue'

$bin = Join-Path $root '.tools\mysql-8.4.11-winx64\bin'
$work = Join-Path $root '.tools\perf'
New-Item -ItemType Directory -Force $work | Out-Null
$port = 5091
$connection = "Server=127.0.0.1;Port=3307;Database=alshifa_load;User=alshifa;Password=$($secrets.app);"
$adminPassword = 'Perf-Admin-Password-1'

function Invoke-Root([string]$Sql, [string]$Database = '') {
    $env:MYSQL_PWD = $secrets.root
    $Sql | & "$bin\mysql.exe" --protocol=TCP -h 127.0.0.1 -P 3307 -u root --default-character-set=utf8mb4 $Database
    Remove-Item Env:\MYSQL_PWD
}

function Stop-Port([int]$Port) {
    $listener = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue
    if ($listener) { Stop-Process -Id $listener.OwningProcess -Force; Start-Sleep -Seconds 2 }
}

Stop-Port $port
$publish = Join-Path $work "publish-$Label"
Set-Location (Join-Path $root 'backend')
dotnet publish src/AlShifa.Api -c Release -o $publish -v q --nologo 2>&1 | Select-Object -Last 2 | ForEach-Object { "$_" }
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

$env:ConnectionStrings__Default = $connection
$env:ASPNETCORE_ENVIRONMENT = 'LoadTest'
$env:ASPNETCORE_URLS = "http://127.0.0.1:$port"
$env:RateLimit__LoginPerMinute = '100000'
$env:Logging__LogLevel__Default = 'Warning'
$env:Storage__Path = Join-Path $work 'storage'
$env:ALSHIFA_ADMIN_USER = 'perfadmin'
$env:ALSHIFA_ADMIN_PASSWORD = $adminPassword

if ($Setup) {
    Invoke-Root @"
DROP DATABASE IF EXISTS alshifa_load;
CREATE DATABASE alshifa_load CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
GRANT ALL PRIVILEGES ON alshifa_load.* TO 'alshifa'@'localhost';
FLUSH PRIVILEGES;
"@
    & (Join-Path $publish 'AlShifa.Api.exe') --create-admin 2>&1 | Select-Object -Last 1 | ForEach-Object { "$_" }
    $seed = Join-Path $work 'seed.sql'
    cmd /c "node `"$root\scripts\load\seed.mjs`" $Patients $Visits > `"$seed`""
    $env:MYSQL_PWD = $secrets.app
    cmd /c "`"$bin\mysql.exe`" --protocol=TCP -h 127.0.0.1 -P 3307 -u alshifa --default-character-set=utf8mb4 alshifa_load < `"$seed`""
    Remove-Item Env:\MYSQL_PWD
    Remove-Item $seed
    # Spread the history up to today, so dashboard and report windows hold rows as in a working clinic.
    Invoke-Root "UPDATE Visits SET VisitDate = DATE_SUB(CURDATE(), INTERVAL (Id % 900) DAY), CreatedAt = TIMESTAMP(DATE_SUB(CURDATE(), INTERVAL (Id % 900) DAY), '04:00:00') WHERE VisitNumber LIKE 'L-%';" 'alshifa_load'
}
else {
    # Schema changes of the build under test. The database is a disposable copy, so no backup is needed.
    & (Join-Path $publish 'AlShifa.Api.exe') --migrate --backup-taken 2>&1 | Select-Object -Last 1 | ForEach-Object { "$_" }
}
Remove-Item Env:\ALSHIFA_ADMIN_PASSWORD

Start-Process -FilePath "$publish\AlShifa.Api.exe" -WorkingDirectory $publish -WindowStyle Hidden -RedirectStandardOutput "$work\api.out.log" -RedirectStandardError "$work\api.err.log"
$up = $false
foreach ($i in 1..60) {
    try { Invoke-WebRequest "http://127.0.0.1:$port/health" -UseBasicParsing -TimeoutSec 3 | Out-Null; $up = $true; break } catch { Start-Sleep -Seconds 1 }
}
if (-not $up) { throw 'API did not start' }

if ($Setup) {
    node (Join-Path $root 'scripts\load\loadtest.mjs') "http://127.0.0.1:$port" 'perfadmin' $adminPassword 60 20 2>&1 | Select-Object -Last 4 | ForEach-Object { "$_" }
}

# Statement counts per request come from performance_schema; start each run from an empty table.
Invoke-Root 'TRUNCATE TABLE performance_schema.events_statements_summary_by_digest;'
$env:MYSQL_PWD = $secrets.root
node (Join-Path $PSScriptRoot 'api-perf.mjs') "http://127.0.0.1:$port" 'perfadmin' $adminPassword (Join-Path $PSScriptRoot "results\api-$Label.json") "$bin\mysql.exe" 2>&1 | ForEach-Object { "$_" }

# Slowest statements of this run, by total time. Digest text holds parameter placeholders, never values.
Invoke-Root @"
SELECT ROUND(SUM_TIMER_WAIT/1e9,1) AS total_ms, COUNT_STAR AS n, ROUND(AVG_TIMER_WAIT/1e9,2) AS avg_ms, SUM_ROWS_EXAMINED AS examined, SUM_NO_INDEX_USED AS no_index, LEFT(DIGEST_TEXT, 220) AS q
FROM performance_schema.events_statements_summary_by_digest WHERE SCHEMA_NAME='alshifa_load' ORDER BY SUM_TIMER_WAIT DESC LIMIT 30;
"@ | Out-File -Encoding utf8 (Join-Path $PSScriptRoot "results\sql-$Label.txt")
Remove-Item Env:\MYSQL_PWD -ErrorAction SilentlyContinue

Stop-Port $port
