# Restores a backup into a throwaway database, checks it, and removes it. Production is never touched:
# the name must start with alshifa_restore_, and the account in restore.cnf can reach only databases named that way.
param(
    # A backup file, or a folder whose newest backup is used.
    [Parameter(Mandatory)][string]$Backup,
    [string]$Root = 'C:\AlShifa',
    [string]$MySqlBin,
    [string]$Gpg,
    [string]$TargetDatabase = 'alshifa_restore_test'
)

. (Join-Path $PSScriptRoot 'common.ps1')

if ($TargetDatabase -notmatch '^alshifa_restore_[a-z0-9_]+$') { throw 'The target database must be named alshifa_restore_<name>.' }
if (-not $MySqlBin) { $MySqlBin = Join-Path $Root 'mysql\bin' }

if (Test-Path $Backup -PathType Container) {
    $file = Get-ChildItem $Backup -Filter 'alshifa-*.zip.gpg' | Sort-Object Name -Descending | Select-Object -First 1
    if (-not $file) { throw "No backups found in $Backup" }
    $Backup = $file.FullName
}

$work =Join-Path $Root "backup-staging\restore-$([Guid]::NewGuid().ToString('N'))"
$mysql = Join-Path $MySqlBin 'mysql.exe'
$credentials = "--defaults-extra-file=$(Join-Path $Root 'secrets\restore.cnf')"

function Invoke-Query([string]$Sql) {
    Invoke-Native $mysql @($credentials, '-N', '-B', $TargetDatabase, '-e', $Sql)
}

try {
    "Backup: $Backup"
    Expand-Backup (Find-Gpg $Gpg) $Backup $work (Join-Path $Root 'secrets\backup-password.txt')
    "Integrity check and decryption: OK"

    Invoke-Native $mysql @($credentials, '-e', "DROP DATABASE IF EXISTS $TargetDatabase; CREATE DATABASE $TargetDatabase CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;") | Out-Null
    Invoke-Native $mysql @($credentials, $TargetDatabase, '-e', "source $((Join-Path $work 'db.sql') -replace '\\', '/')") | Out-Null

    $tables = [int](Invoke-Query "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE()")
    $migrations = [int](Invoke-Query 'SELECT COUNT(*) FROM __EFMigrationsHistory')
    if ($tables -lt 50 -or $migrations -lt 1) { throw "Restored database looks incomplete: $tables tables, $migrations migrations." }

    $unbalanced = [int](Invoke-Query 'SELECT COUNT(*) FROM (SELECT TransactionId FROM LedgerEntries GROUP BY TransactionId HAVING SUM(Debit) <> SUM(Credit)) t')
    if ($unbalanced -ne 0) { throw "$unbalanced ledger transactions are unbalanced." }

    # Every recorded attachment must exist and match the hash stored when it was uploaded.
    $attachments = @(Invoke-Query 'SELECT StoredName, Sha256 FROM Attachments')
    $sha = [Security.Cryptography.SHA256]::Create()
    foreach ($row in $attachments) {
        $name, $expected = $row -split "`t"
        $path = Join-Path $work "storage\$name"
        if (-not (Test-Path $path)) { throw "Attachment file is missing from the backup: $name" }
        $bytes = [IO.File]::ReadAllBytes($path)
        if ([BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '') -ne $expected) { throw "Attachment does not match its hash: $name" }
    }

    "Tables: $tables, patients: $(Invoke-Query 'SELECT COUNT(*) FROM Patients'), payments: $(Invoke-Query 'SELECT COUNT(*) FROM Payments'), attachments verified: $($attachments.Count)"
    'RESULT: backup restored and verified in an isolated database'
} finally {
    Invoke-Native $mysql @($credentials, '-e', "DROP DATABASE IF EXISTS $TargetDatabase;") | Out-Null
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
