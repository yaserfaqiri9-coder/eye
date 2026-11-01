# Real backup and restore drill: dumps a source database, restores it into a scratch database,
# and proves the copy matches table by table. Never touches the source.
param([string]$Source = 'alshifa_dev')

. (Join-Path $PSScriptRoot 'dev-env.ps1')
$ErrorActionPreference = 'Continue'

$bin = Join-Path $root '.tools\mysql-8.4.11-winx64\bin'
$dir = Join-Path $root '.tools\backup'
New-Item -ItemType Directory -Force $dir | Out-Null
$file = Join-Path $dir "$Source-drill.sql"
$target = 'alshifa_restore_drill'

$env:MYSQL_PWD = $secrets.root

function Query([string]$Database, [string]$Sql) {
    & "$bin\mysql.exe" --protocol=TCP -h 127.0.0.1 -P 3307 -u root -N --default-character-set=utf8mb4 -e $Sql $Database
}

function Get-Counts([string]$Database) {
    $tables = Query 'information_schema' "SELECT TABLE_NAME FROM TABLES WHERE TABLE_SCHEMA='$Database' AND TABLE_TYPE='BASE TABLE' ORDER BY TABLE_NAME"
    foreach ($t in $tables) { "$t=" + (Query $Database "SELECT COUNT(*) FROM ``$t``") }
}

$dumpArgs = "--protocol=TCP -h 127.0.0.1 -P 3307 -u root --single-transaction --routines --triggers --set-gtid-purged=OFF --default-character-set=utf8mb4 --result-file=`"$file`" $Source"
cmd /c "`"$bin\mysqldump.exe`" $dumpArgs"
"dump exit: $LASTEXITCODE, size: $((Get-Item $file).Length)"
if ($LASTEXITCODE -ne 0) { throw 'dump failed' }

Query 'mysql' "DROP DATABASE IF EXISTS $target; CREATE DATABASE $target CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;"
cmd /c "`"$bin\mysql.exe`" --protocol=TCP -h 127.0.0.1 -P 3307 -u root --default-character-set=utf8mb4 $target < `"$file`""
"restore exit: $LASTEXITCODE"
if ($LASTEXITCODE -ne 0) { throw 'restore failed' }

$before = Get-Counts $Source
$after = Get-Counts $target
$diff = Compare-Object $before $after
"tables compared: $($before.Count)"
"table count differences: $(($diff | Measure-Object).Count)"

$sampleSql = "SELECT COALESCE(SUM(Debit),0), COALESCE(SUM(Credit),0) FROM LedgerEntries"
"ledger source:   $(Query $Source $sampleSql)"
"ledger restored: $(Query $target $sampleSql)"

$dari = "SELECT Name FROM Departments ORDER BY Id LIMIT 1"
"utf8 source:   $(Query $Source $dari)"
"utf8 restored: $(Query $target $dari)"

Query 'mysql' "DROP DATABASE $target;"
Remove-Item $file
Remove-Item Env:\MYSQL_PWD

if (($diff | Measure-Object).Count -eq 0) { 'RESULT: restored copy matches the source' } else { $diff; throw 'RESULT: mismatch' }
