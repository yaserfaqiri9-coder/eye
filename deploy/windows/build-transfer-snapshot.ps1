# Export this checkout's development database; prove restoration in a unique, isolated database.
param([Parameter(Mandatory)][string]$Destination)
. (Join-Path $PSScriptRoot 'transfer-common.ps1')
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$bin = Join-Path $repo '.tools\mysql-8.4.11-winx64\bin'
$mysql = Join-Path $bin 'mysql.exe'
$secrets = Get-Content (Join-Path $repo '.tools\dev-secrets.json') -Raw | ConvertFrom-Json
if (Test-Path -LiteralPath $Destination) { throw 'Destination already exists; choose a new snapshot folder.' }
New-Item -ItemType Directory -Path $Destination | Out-Null
$Destination = (Resolve-Path -LiteralPath $Destination).Path
$cnf = Join-Path $repo ".tools\transfer-$([Guid]::NewGuid().ToString('N')).cnf"
$scratch = 'alshifa_restore_transfer_' + [Guid]::NewGuid().ToString('N')
$createdScratch = $false
try {
    Write-ClientCnf $cnf 'root' $secrets.root 3307
    Protect-Path $cnf @("$([Security.Principal.WindowsIdentity]::GetCurrent().Name)=F")
    $before = @(Get-TransferFingerprint $mysql $cnf 'alshifa_dev')
    $dump = Join-Path $Destination 'db.sql'
    Invoke-Native (Join-Path $bin 'mysqldump.exe') @("--defaults-extra-file=$cnf", '--single-transaction', '--quick', '--no-tablespaces', '--set-gtid-purged=OFF', '--triggers', '--hex-blob', '--default-character-set=utf8mb4', "--result-file=$dump", 'alshifa_dev') | Out-Null
    if ((Get-Content -LiteralPath $dump -Tail 1) -notmatch '^-- Dump completed') { throw 'Incomplete dump.' }
    $storage = Join-Path $repo 'backend\src\AlShifa.Api\storage'
    Copy-Item -LiteralPath $storage -Destination (Join-Path $Destination 'storage') -Recurse
    # Every uploaded file referenced by the database must be present in the copied storage.
    $attachments = @(Invoke-Native $mysql @("--defaults-extra-file=$cnf", '-N', '-B', 'alshifa_dev', '-e', 'SELECT StoredName FROM Attachments'))
    foreach ($name in $attachments) {
        if (-not (Test-Path -LiteralPath (Join-Path $Destination "storage\$name") -PathType Leaf)) { throw "Missing attachment: $name" }
    }
    Assert-TransferFingerprint $before @(Get-TransferFingerprint $mysql $cnf 'alshifa_dev')
    Invoke-Native $mysql @("--defaults-extra-file=$cnf", '-e', "CREATE DATABASE $scratch CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci") | Out-Null
    $createdScratch = $true
    Import-TransferSql $mysql $cnf $scratch $dump
    Assert-TransferFingerprint $before @(Get-TransferFingerprint $mysql $cnf $scratch)
    $inventory = @(Invoke-Native $mysql @("--defaults-extra-file=$cnf", '-N', '-B', $scratch, '-e', 'SELECT l.Code,COUNT(DISTINCT b.ProductId),COUNT(*),SUM(b.Quantity),SUM(b.Quantity*b.UnitCost) FROM StockBatches b JOIN StockLocations l ON l.Id=b.LocationId GROUP BY l.Code ORDER BY l.Code'))
    $migrations = @(Invoke-Native $mysql @("--defaults-extra-file=$cnf", '-N', '-B', $scratch, '-e', 'SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId'))
    $files = @(Get-ChildItem -LiteralPath $Destination -Recurse -File | ForEach-Object {
        [ordered]@{ path = $_.FullName.Substring($Destination.Length + 1); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    [ordered]@{ format = 1; createdUtc = [DateTime]::UtcNow.ToString('o'); source = '127.0.0.1:3307/alshifa_dev'; restoreVerified = $true; tables = $before; migrations = $migrations; inventory = $inventory; files = $files } |
        ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $Destination 'manifest.json') -Encoding UTF8
    "RESULT: snapshot restored and verified; $($before.Count) tables; $($files.Count) files."
    $inventory
} finally {
    if ($createdScratch) { Invoke-Native $mysql @("--defaults-extra-file=$cnf", '-e', "DROP DATABASE $scratch") | Out-Null }
    Remove-Item -LiteralPath $cnf -Force -ErrorAction SilentlyContinue
}
