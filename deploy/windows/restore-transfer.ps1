# Restore a verified USB snapshot only into a fresh Al-Shifa installation.
#Requires -RunAsAdministrator
param([string]$Root = 'C:\AlShifa', [string]$Snapshot = (Join-Path $PSScriptRoot 'Snapshot-Verified'))
. (Join-Path $PSScriptRoot 'transfer-common.ps1')
$Root = [IO.Path]::GetFullPath($Root).TrimEnd('\')
$Snapshot = (Resolve-Path -LiteralPath $Snapshot).Path
$configFile = Join-Path $Root 'app\appsettings.Production.json'
$config = Get-Content -LiteralPath $configFile -Raw -Encoding UTF8 | ConvertFrom-Json
$manifest = Get-Content -LiteralPath (Join-Path $Snapshot 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.format -ne 1 -or -not $manifest.restoreVerified) { throw 'This snapshot has not passed restoration verification.' }
Assert-TransferFiles $Snapshot $manifest.files
if (-not ($manifest.files | Where-Object path -eq 'db.sql')) { throw 'The manifest does not include the database.' }
$connection = New-Object System.Data.Common.DbConnectionStringBuilder
# Use the CLR setter: PowerShell treats property assignment on this IDictionary
# as adding a key named ConnectionString instead of parsing its value.
$connection.set_ConnectionString([string]$config.ConnectionStrings.Default)
if ($connection['Database'] -ne 'alshifa' -or $connection['Server'] -ne '127.0.0.1') { throw 'Only a local standard Al-Shifa installation is supported.' }
$service = Get-CimInstance Win32_Service -Filter "Name='AlShifaApi'"
$expectedExe = Join-Path $Root 'app\AlShifa.Api.exe'
if (-not $service -or $service.PathName.Trim('"') -ne $expectedExe) { throw 'The installed service does not belong to this Al-Shifa folder.' }
$mysql = Join-Path $Root 'mysql\bin\mysql.exe'
$cnf = Join-Path $Root "secrets\transfer-$([Guid]::NewGuid().ToString('N')).cnf"
$backup = Join-Path $Root "transfer-backups\$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ'))-$([Guid]::NewGuid().ToString('N'))"
$stopped = $false
$changed = $false
$safeToStart = $true
$backupReady = $false
try {
    Write-ClientCnf $cnf 'root' (Get-Content (Join-Path $Root 'secrets\mysql-root.txt') -Raw).Trim() ([int]$connection['Port'])
    Protect-Path $cnf
    Stop-Service AlShifaApi
    $stopped = $true
    # Seeded references and the first administrator are allowed. Any operational row blocks replacement.
    $original = @(Get-TransferFingerprint $mysql $cnf 'alshifa')
    $hasAudit = @($original | Where-Object { $_.table -ieq 'auditlogs' -and $_.rows -gt 0 }).Count -gt 0
    $loginOnlyAudit = $false
    if ($hasAudit) { $loginOnlyAudit = Test-TransferLoginAuditOnly $mysql $cnf 'alshifa' }
    Assert-TransferDestinationFresh $original -LoginOnlyAuditVerified:$loginOnlyAudit
    if ($hasAudit) { Write-Host 'Only sign-in audit records were found. They will be retained in the destination backup before transfer.' }
    $sourceNames = @($manifest.tables | ForEach-Object { $_.table.ToLowerInvariant() })
    $targetNames = @($original | ForEach-Object { $_.table.ToLowerInvariant() })
    if (@(Compare-Object $sourceNames $targetNames).Count) { throw 'Database table sets differ. Install the matching application version first.' }
    $migrations = @(Invoke-Native $mysql @("--defaults-extra-file=$cnf", '-N', '-B', 'alshifa', '-e', 'SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId'))
    if (@(Compare-Object @($manifest.migrations) $migrations).Count) { throw 'Database versions differ. Install the matching application version first.' }
    Write-Host 'This copies ALL data from the development computer, including existing accounts and records.' -ForegroundColor Yellow
    Write-Host "Source: $($manifest.source); snapshot: $($manifest.createdUtc)"
    if ((Read-Host 'Type TRANSFER to copy into this fresh installation') -cne 'TRANSFER') { throw 'Cancelled before making changes.' }
    $admin = Read-Host 'Name for a NEW hospital administrator (example: hospital-admin)'
    if ($admin -notmatch '^[a-zA-Z0-9_-]{3,40}$') { throw 'Use 3-40 letters, numbers, underscore or hyphen for the new administrator.' }
    $password = Read-Host 'Password for the new hospital administrator (at least 12 characters)' -AsSecureString
    $confirm = Read-Host 'Repeat the password' -AsSecureString
    $plain = ConvertTo-Plain $password
    if ($plain.Length -lt 12 -or $plain -cne (ConvertTo-Plain $confirm)) { throw 'Passwords must match and have at least 12 characters.' }
    New-Item -ItemType Directory -Path $backup | Out-Null
    Protect-Path $backup
    $oldDump = Join-Path $backup 'before.sql'
    Invoke-Native (Join-Path $Root 'mysql\bin\mysqldump.exe') @("--defaults-extra-file=$cnf", '--single-transaction', '--no-tablespaces', '--set-gtid-purged=OFF', '--triggers', '--hex-blob', "--result-file=$oldDump", 'alshifa') | Out-Null
    if ((Get-Content -LiteralPath $oldDump -Tail 1) -notmatch '^-- Dump completed') { throw 'The destination backup is incomplete.' }
    Copy-Item -LiteralPath (Join-Path $Root 'storage') -Destination (Join-Path $backup 'storage') -Recurse
    $backupReady = $true
    $changed = $true
    Import-TransferSql $mysql $cnf 'alshifa' (Join-Path $Snapshot 'db.sql')
    Assert-TransferFingerprint $manifest.tables @(Get-TransferFingerprint $mysql $cnf 'alshifa')
    foreach ($file in @($manifest.files | Where-Object { $_.path -like 'storage\*' })) {
        $relative = $file.path.Substring(8)
        $target = Join-Path (Join-Path $Root 'storage') $relative
        New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
        Copy-Item -LiteralPath (Join-Path $Snapshot $file.path) -Destination $target -Force
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $file.sha256) { throw 'Copied storage verification failed.' }
    }
    Push-Location (Join-Path $Root 'app')
    try {
        $env:ASPNETCORE_ENVIRONMENT = 'Production'
        $env:ALSHIFA_ADMIN_USER = $admin
        $env:ALSHIFA_ADMIN_PASSWORD = $plain
        # Inherited development settings must not redirect the operator command away from this installation.
        $env:ConnectionStrings__Default = $config.ConnectionStrings.Default
        Invoke-Native $expectedExe @('--create-admin', '--backup-taken') | Out-Null
    } finally {
        Remove-Item Env:\ALSHIFA_ADMIN_USER, Env:\ALSHIFA_ADMIN_PASSWORD, Env:\ConnectionStrings__Default -ErrorAction SilentlyContinue
        $plain = $null
        Pop-Location
    }
    Invoke-Native 'icacls.exe' @((Join-Path $Root 'storage'), '/grant', 'NT SERVICE\AlShifaApi:(OI)(CI)M', '/T', '/Q') | Out-Null
    Start-Service AlShifaApi
    $stopped = $false
    $url = $config.Kestrel.Endpoints.Loopback.Url.TrimEnd('/') + '/health'
    $healthy = $false
    foreach ($attempt in 1..30) {
        try { $null = Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 5; $healthy = $true; break } catch { Start-Sleep -Seconds 2 }
    }
    if (-not $healthy) { throw 'The restored application did not become healthy.' }
    "RESULT: database and storage transferred and verified; new administrator: $admin"
    $manifest.inventory
    "Rollback copy retained: $backup"
} catch {
    $reason = $_
    if ($changed -and $backupReady) {
        try {
            Stop-Service AlShifaApi -ErrorAction SilentlyContinue
            $stopped = $true
            Import-TransferSql $mysql $cnf 'alshifa' $oldDump
            Assert-TransferFingerprint $original @(Get-TransferFingerprint $mysql $cnf 'alshifa')
            Get-ChildItem -LiteralPath (Join-Path $backup 'storage') -Force | Copy-Item -Destination (Join-Path $Root 'storage') -Recurse -Force
            Write-Host 'Transfer failed; the original fresh database was restored.' -ForegroundColor Yellow
        } catch {
            $safeToStart = $false
            throw "Transfer and rollback failed. Service left stopped. Keep $backup and contact support. $($_.Exception.Message)"
        }
    }
    throw $reason
} finally {
    $plain = $null
    if ($stopped -and $safeToStart) { Start-Service AlShifaApi }
    Remove-Item -LiteralPath $cnf -Force -ErrorAction SilentlyContinue
}
