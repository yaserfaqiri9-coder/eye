# Opening stock import from an Excel or CSV sheet. Runs from a USB stick on the hospital server; needs no .NET, Claude or internet.
# Steps: choose sheet -> choose and confirm database -> preview (nothing written) -> backup + restore check -> import -> report.
# Nothing about the connection is saved: the password is asked each time (or read from the installed settings when you say so)
# and lives only in this window's memory until it closes.
param(
    [string]$Sheet,
    # The installed system. Its backup.ps1 and restore-test.ps1 make and check the backup of the installed database.
    [string]$Root = 'C:\AlShifa',
    # For a test database only: folder holding mysql.exe and mysqldump.exe.
    [string]$MySqlBin,
    # Do not open the preview and result in Excel (for scripted checks).
    [switch]$NoOpen
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$exe = Join-Path $here 'app\AlShifa.Api.exe'
$reports = Join-Path $here 'reports'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
New-Item -ItemType Directory -Force $reports | Out-Null
Start-Transcript -Path (Join-Path $reports "$stamp-log.txt") | Out-Null

# Answers can also be piped in (scripted checks); the questions and confirmations stay the same.
function Read-Answer([string]$Prompt, [switch]$Secret) {
    if ([Console]::IsInputRedirected) {
        Write-Host "${Prompt}: " -NoNewline
        $line = [Console]::In.ReadLine()
        if ($null -eq $line) { throw 'No more answers. Nothing was changed.' }
        if (-not $Secret) { Write-Host $line } else { Write-Host '' }
        return $line
    }
    if ($Secret) {
        $secure = Read-Host $Prompt -AsSecureString
        return [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
    }
    return Read-Host $Prompt
}

function Ask([string]$Prompt, [string]$Default) {
    $answer = Read-Answer "$Prompt [$Default]"
    if ([string]::IsNullOrWhiteSpace($answer)) { return $Default }
    return $answer.Trim()
}

function Confirm-Typed([string]$Prompt, [string]$Expected) {
    $answer = Read-Answer "$Prompt (type $Expected to continue)"
    if ($answer -cne $Expected) { throw 'Stopped by the user. Nothing was changed.' }
}

function Read-Settings([string]$Path) {
    try {
        if (-not (Test-Path $Path)) { return $null }
        return Get-Content $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    } catch {
        Write-Host "Cannot read $Path (start with Import-Stock.bat, which runs as administrator). The installed system is not recognised." -ForegroundColor Yellow
        return $null
    }
}

function Split-ConnectionString([string]$Text) {
    $b = New-Object System.Data.Common.DbConnectionStringBuilder
    $b.ConnectionString = $Text
    $values = @{}
    foreach ($key in $b.Keys) { $values[$key.ToLowerInvariant()] = "$($b[$key])" }
    return $values
}

function Invoke-Tool([string[]]$Arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $exe @Arguments 2>&1 | ForEach-Object { "$_" }
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previous
    }
    $output | Where-Object { $_ -notmatch '^\s*(info|warn): |^\s+(Microsoft|Storing keys|No XML encryptor|User profile)' } | ForEach-Object { Write-Host $_ }
    if ($code -ne 0) {
        $reason = $output | Where-Object { $_ -match 'InvalidOperationException|Exception:' } | Select-Object -First 1
        throw "The import tool stopped: $reason"
    }
    return $output
}

function Invoke-MySql([string]$Exe, [string]$Cnf, [string[]]$Arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $Exe "--defaults-extra-file=$Cnf" @Arguments 2>&1 | ForEach-Object { "$_" }
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previous
    }
    if ($code -ne 0) { throw "$(Split-Path $Exe -Leaf) failed: $($output -join ' ')" }
    return $output
}

$cnf = $null
try {
    if (-not (Test-Path $exe)) { throw "The program is missing: $exe" }

    # 1. The sheet.
    if (-not $Sheet) {
        Add-Type -AssemblyName System.Windows.Forms
        $dialog = New-Object System.Windows.Forms.OpenFileDialog
        $dialog.Filter = 'Stock sheet (*.xlsx;*.csv)|*.xlsx;*.csv'
        $dialog.InitialDirectory = $here
        $dialog.Title = 'Choose the stock sheet'
        if ($dialog.ShowDialog() -ne 'OK') { throw 'No sheet chosen. Nothing was changed.' }
        $Sheet = $dialog.FileName
    }
    $Sheet = (Resolve-Path $Sheet).Path
    if ([IO.Path]::GetExtension($Sheet) -notin '.xlsx', '.csv') { throw 'The sheet must be .xlsx or .csv.' }
    Write-Host "Sheet: $Sheet"

    # 2. The database: chosen and confirmed here, never stored.
    $installed = Read-Settings (Join-Path $Root 'app\appsettings.Production.json')
    $installedConnection = if ($installed) { Split-ConnectionString $installed.ConnectionStrings.Default } else { @{} }
    $server = Ask 'Database server' '127.0.0.1'
    $port = Ask 'Database port' $(if ($installedConnection.port) { $installedConnection.port } else { '3308' })
    $database = Ask 'Database name' 'alshifa'
    $isInstalled = $installed -and $server -in '127.0.0.1', 'localhost' -and $port -eq $installedConnection.port -and $database -eq $installedConnection.database

    $password = $null
    if ($isInstalled -and (Ask 'Use the account of the installed system (reads its password from the installed settings, saves nothing)? Y/N' 'Y') -match '^[Yy]') {
        $user = $installedConnection.user
        $password = $installedConnection.password
    } else {
        $user = Ask 'Database user' 'alshifa'
        $password = Read-Answer 'Database password' -Secret
    }

    $b = New-Object System.Data.Common.DbConnectionStringBuilder
    $b['Server'] = $server; $b['Port'] = $port; $b['Database'] = $database; $b['User'] = $user; $b['Password'] = $password
    # Only this window's environment, inherited by the tool; gone when the window closes.
    $env:ConnectionStrings__Default = $b.ConnectionString
    $env:ASPNETCORE_ENVIRONMENT = 'Production'
    $env:Logging__LogLevel__Default = 'Warning'
    # On the hospital server, changes go to the online copy the same way the application sends them.
    if ($isInstalled -and $installed.Sync -and $installed.Sync.Url) { $env:Sync__Url = $installed.Sync.Url } else { Remove-Item Env:Sync__Url -ErrorAction SilentlyContinue }

    Write-Host ''
    Write-Host "TARGET DATABASE: $database on ${server}:$port (user $user)" -ForegroundColor Yellow
    if ($isInstalled) { Write-Host 'This is the INSTALLED hospital database.' -ForegroundColor Yellow }
    Confirm-Typed 'Is this the right database?' $database

    $env:ALSHIFA_IMPORT_USER = Ask 'Al-Shifa user name that records this stock (shown in the history)' 'admin'

    # 3. Preview: reads only.
    $preview = Join-Path $reports "$stamp-preview.csv"
    Write-Host ''
    Write-Host 'Preview (nothing is written)...'
    $output = Invoke-Tool @('--import-opening-stock', $Sheet, '--report', $preview)
    $summary = $output | Where-Object { $_ -match '^Rows \d+: import (\d+)' } | Select-Object -Last 1
    if (-not $summary) { throw 'The preview did not finish.' }
    $toImport = [int]([regex]::Match($summary, 'import (\d+)').Groups[1].Value)
    Write-Host ''
    Write-Host $summary -ForegroundColor Cyan
    Write-Host "Full preview with every row and reason: $preview"
    if (-not $NoOpen) { Start-Process $preview -ErrorAction SilentlyContinue }

    if ($toImport -eq 0) {
        Write-Host 'Nothing to import. Fix the rows marked in the preview and run again.'
        return
    }

    $choice = Ask 'Dry run only (D), or import these rows (I)?' 'D'
    if ($choice -notmatch '^[Ii]') {
        Write-Host 'Dry run finished. Nothing was changed.'
        return
    }
    Confirm-Typed "Import $toImport rows into ${database}?" 'IMPORT'

    # Priced stock is booked against account 3000 Opening capital. The finance officer approves the amount before anything is written.
    $value = [decimal]::Parse([regex]::Match($summary, 'Value to post: ([0-9]+(?:\.[0-9]+)?)').Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture)
    if ($value -gt 0) {
        Write-Host ''
        Write-Host "Accounting entry: Debit 1200 Inventory $value AFN / Credit 3000 Opening capital $value AFN" -ForegroundColor Yellow
        Write-Host 'Rows without a purchase price are counted at cost 0 and post nothing.'
        $officer = (Read-Answer 'Name of the finance officer who approved account 3000 and this amount').Trim()
        if (-not $officer) { throw 'Account 3000 needs the finance officer''s approval. Nothing was changed.' }
        Confirm-Typed "$officer approves $value AFN on account 3000?" '3000'
        Write-Host "Finance approval recorded in this log: $officer, $value AFN on account 3000, $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
    }

    # 4. Backup, then prove it restores.
    Write-Host ''
    if ($isInstalled) {
        $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        if (-not $admin) { throw 'Run as administrator: the backup reads the protected settings of the installed system. Nothing was changed.' }
        $nightly = $null
        try {
            $arguments = (Get-ScheduledTask -TaskName 'AlShifa Backup' -ErrorAction Stop).Actions[0].Arguments
            if ($arguments -match '-Destination "([^"]+)"') { $nightly = $Matches[1] }
        } catch { }
        $destination = Ask 'Backup folder (on another disk)' $(if ($nightly) { $nightly } else { Join-Path $here 'backups' })
        Write-Host 'Backup of the installed database (encrypted)...'
        $backupOutput = & (Join-Path $Root 'scripts\backup.ps1') -Root $Root -Destination $destination -Database $database 2>&1 | ForEach-Object { "$_" }
        $line = $backupOutput | Where-Object { $_ -match '^Backup complete: (.+)$' } | Select-Object -Last 1
        if (-not $line) { throw "Backup failed: $($backupOutput -join ' '). Nothing was changed." }
        $backupFile = [regex]::Match($line, '^Backup complete: (.+)$').Groups[1].Value
        Write-Host "Backup: $backupFile"
        Write-Host 'Restoring the backup into a separate test database to check it...'
        $check = & (Join-Path $Root 'scripts\restore-test.ps1') -Root $Root -Backup $backupFile 2>&1 | ForEach-Object { "$_" }
        $check | ForEach-Object { Write-Host "  $_" }
        if (-not ($check -match 'RESULT: backup restored and verified')) { throw 'The backup could not be restored. Nothing was changed.' }
    } else {
        if ($database -eq 'alshifa') { throw 'The database alshifa is backed up only through the installed system. Check the port and run again. Nothing was changed.' }
        if (-not $MySqlBin) { $MySqlBin = Join-Path $Root 'mysql\bin' }
        $dump = Join-Path $MySqlBin 'mysqldump.exe'
        $mysql = Join-Path $MySqlBin 'mysql.exe'
        if (-not (Test-Path $dump)) { throw "mysqldump.exe not found in $MySqlBin. Pass -MySqlBin. Nothing was changed." }

        # The password goes to MySQL in a file only this user can read, removed in the finally block below.
        $cnf = Join-Path $env:TEMP "alshifa-import-$([Guid]::NewGuid().ToString('N')).cnf"
        Set-Content $cnf "[client]`nuser=$user`npassword=`"$($password -replace '\\', '\\' -replace '"', '\"')`"`nhost=$server`nport=$port`nprotocol=TCP`ndefault-character-set=utf8mb4" -Encoding Ascii
        & icacls.exe $cnf /inheritance:r /grant:r "$($env:USERNAME):F" | Out-Null

        $backupDir = Join-Path $here 'backups'
        New-Item -ItemType Directory -Force $backupDir | Out-Null
        $backupFile = Join-Path $backupDir "$database-$stamp.sql"
        Write-Host "Backup of $database (test database, not encrypted)..."
        Invoke-MySql $dump $cnf @('--single-transaction', '--quick', '--no-tablespaces', '--set-gtid-purged=OFF', '--triggers', '--default-character-set=utf8mb4', "--result-file=$backupFile", $database) | Out-Null
        if ((Get-Content $backupFile -Tail 1) -notmatch '^-- Dump completed') { throw 'The backup is incomplete. Nothing was changed.' }
        Write-Host "Backup: $backupFile"

        $restore = 'alshifa_restore_import_check'
        Write-Host "Restoring the backup into $restore to check it..."
        try {
            Invoke-MySql $mysql $cnf @('-e', "DROP DATABASE IF EXISTS $restore; CREATE DATABASE $restore CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;") | Out-Null
            Invoke-MySql $mysql $cnf @($restore, '-e', "source $($backupFile -replace '\\', '/')") | Out-Null
            $count = "SELECT (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE()), (SELECT COUNT(*) FROM __EFMigrationsHistory), (SELECT COUNT(*) FROM Patients), (SELECT COUNT(*) FROM Products), (SELECT COUNT(*) FROM StockBatches), (SELECT COUNT(*) FROM StockMovements), (SELECT COUNT(*) FROM LedgerEntries)"
            $original = (Invoke-MySql $mysql $cnf @('-N', '-B', $database, '-e', $count)) -join ''
            $restored = (Invoke-MySql $mysql $cnf @('-N', '-B', $restore, '-e', $count)) -join ''
            Write-Host "  tables, migrations, patients, products, batches, movements, ledger rows: $restored"
            if ($original -ne $restored) { throw "The restored copy differs from the database ($original / $restored). Nothing was changed." }
        } finally {
            Invoke-MySql $mysql $cnf @('-e', "DROP DATABASE IF EXISTS $restore;") | Out-Null
        }
        Write-Host '  RESULT: backup restored and verified' -ForegroundColor Green
    }

    # 5. Import: the tool checks everything again inside one transaction and stops if anything differs from the preview.
    $result = Join-Path $reports "$stamp-result.csv"
    Write-Host ''
    Write-Host 'Importing...'
    $output = Invoke-Tool @('--import-opening-stock', $Sheet, '--apply', '--backup-taken', '--expect-import', "$toImport", '--report', $result)
    $done = $output | Where-Object { $_ -match '^Committed ' } | Select-Object -Last 1
    if (-not $done) { throw 'The import did not confirm. Check the log.' }
    Write-Host ''
    Write-Host $done -ForegroundColor Green
    Write-Host "Backup taken before the import: $backupFile"
    Write-Host "Result per row: $result"
    if (-not $NoOpen) { Start-Process $result -ErrorAction SilentlyContinue }
} catch {
    Write-Host ''
    Write-Host "STOPPED: $_" -ForegroundColor Red
    exit 1
} finally {
    if ($cnf) { Remove-Item $cnf -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:ConnectionStrings__Default, Env:Sync__Url, Env:ALSHIFA_IMPORT_USER -ErrorAction SilentlyContinue
    $password = $null
    Stop-Transcript | Out-Null
}
