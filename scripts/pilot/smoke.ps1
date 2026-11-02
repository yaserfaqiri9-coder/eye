# Exercises a real release on this computer, as the hospital host would run it, without touching anything shared:
# its own MySQL on port 3399, its own folders under .tools\pilot, synthetic data only, and everything removed at the end.
# Needs no administrator. Not covered: Windows services, the firewall, other computers, Tailscale.
param(
    [Parameter(Mandatory)][string]$Release,
    [Parameter(Mandatory)][string]$LanIp,
    [int]$HttpsPort = 8443,
    [int]$LoopbackPort = 5081,
    [int]$MySqlPort = 3399
)

$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $repo 'deploy\windows\common.ps1')

# Inherited settings outrank appsettings.Production.json, and scripts\dev-env.ps1 sets one that points at the development database.
Get-ChildItem Env: | Where-Object Name -match '^(ConnectionStrings__|ASPNETCORE_|DOTNET_ENVIRONMENT|Storage__|DataProtection__|Backup__|Kestrel__)' |
    ForEach-Object { Remove-Item "Env:\$($_.Name)" }

$root = Join-Path $repo '.tools\pilot'
$scripts = Join-Path $repo 'deploy\windows'
$mysqlBase = Join-Path $repo '.tools\mysql-8.4.11-winx64'
$mysqlBin = Join-Path $mysqlBase 'bin'
$lanUrl = "https://${LanIp}:$HttpsPort"
$loopUrl = "http://127.0.0.1:$LoopbackPort"
$check = Join-Path $PSScriptRoot 'lan-check.mjs'
$passwordFile = Join-Path $root 'secrets\backup-password.txt'
$gpg = Find-Gpg
$results = New-Object Collections.ArrayList

function Pass([string]$Name, [string]$Detail = '') { [void]$results.Add("PASS  $Name  $Detail"); "PASS  $Name  $Detail" }
# mysqld on Windows runs as a parent and a child, so processes are found by command line. This also leaves other MySQL servers alone.
function Stop-Database {
    Get-CimInstance Win32_Process -Filter "Name='mysqld.exe'" | Where-Object { $_.CommandLine -like '*\.tools\pilot\mysql.ini*' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Wait-Until 'database to stop' { -not (Get-NetTCPConnection -State Listen -LocalPort $MySqlPort -ErrorAction SilentlyContinue) }
}
function Stop-Pilot {
    if ($script:apiProcess) { Stop-Process -Id $script:apiProcess.Id -Force -ErrorAction SilentlyContinue }
    Stop-Database
}
function Wait-Until([string]$What, [scriptblock]$Test, [int]$Seconds = 90) {
    foreach ($i in 1..$Seconds) {
        try { if (& $Test) { return } } catch { }
        Start-Sleep -Seconds 1
    }
    throw "Timed out waiting for $What"
}
function Start-Database {
    Start-Process (Join-Path $mysqlBin 'mysqld.exe') -ArgumentList "--defaults-file=`"$(Join-Path $root 'mysql.ini')`"" -WindowStyle Hidden
    Wait-Until 'database' { Get-NetTCPConnection -State Listen -LocalPort $MySqlPort -ErrorAction SilentlyContinue }
}
function Test-Healthy { (Invoke-WebRequest "$loopUrl/health" -UseBasicParsing -TimeoutSec 5).Content -eq 'Healthy' }
function Get-HealthStatus {
    try { return [int](Invoke-WebRequest "$loopUrl/health" -UseBasicParsing -TimeoutSec 30).StatusCode }
    catch [Net.WebException] { return [int]$_.Exception.Response.StatusCode }
}
function Start-ApiProcess {
    $script:apiProcess = Start-Process (Join-Path $root 'app\AlShifa.Api.exe') -WorkingDirectory (Join-Path $root 'app') -WindowStyle Hidden -PassThru
}
function Start-Api {
    Start-ApiProcess
    Wait-Until 'API' { Test-Healthy }
}
function Invoke-Check([string]$Mode) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & node $check $Mode 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = $previous
    if ($code) { throw "lan-check $Mode failed" }
}
function Query([string]$Sql) {
    Invoke-Native (Join-Path $mysqlBin 'mysql.exe') @('--protocol=TCP', '-h', '127.0.0.1', '-P', "$MySqlPort", '-u', 'root', '-N', '-B', 'alshifa', '-e', $Sql)
}
function Get-TlsVersion([Security.Authentication.SslProtocols]$Protocol) {
    $client = New-Object Net.Sockets.TcpClient($LanIp, $HttpsPort)
    try {
        $stream = New-Object Net.Security.SslStream($client.GetStream(), $false, { $true })
        $stream.AuthenticateAsClient($LanIp, $null, $Protocol, $false)
        return "$($stream.SslProtocol)"
    } catch {
        return $null
    } finally {
        $client.Close()
    }
}
# Every live connection of the API and database, except to this computer or the hospital network address under test.
function Get-OutsideConnections {
    $ids = @($script:apiProcess.Id) + @(Get-CimInstance Win32_Process -Filter "Name='mysqld.exe'" | Where-Object { $_.CommandLine -like '*\.tools\pilot\mysql.ini*' } | ForEach-Object { $_.ProcessId })
    Get-NetTCPConnection -OwningProcess $ids -ErrorAction SilentlyContinue |
        Where-Object { $_.State -ne 'Listen' -and $_.RemoteAddress -notin @('127.0.0.1', '::1', '0.0.0.0', '::', $LanIp) } |
        ForEach-Object { "$($_.RemoteAddress):$($_.RemotePort)" }
}

if (Test-Path $root) { Remove-Item $root -Recurse -Force }
foreach ($folder in 'storage', 'keys', 'certs', 'logs', 'secrets', 'backups', 'backup-staging') { New-Item -ItemType Directory -Force (Join-Path $root $folder) | Out-Null }

try {
    # The built web app must not call anything outside the hospital. XML namespaces and licence comments are the only URLs allowed.
    $external = Get-ChildItem (Join-Path $Release 'app\wwwroot') -Recurse -File -Include *.html, *.js, *.css |
        Select-String -Pattern 'https?://[a-zA-Z0-9][^"''\s)\\`]*' -AllMatches |
        ForEach-Object { $_.Matches.Value } | Where-Object { $_ -notmatch 'w3\.org|bootstrap|getbootstrap|github\.com|vuejs\.org|fb\.me|reactjs|mozilla\.org|whatwg\.org|microsoft|aka\.ms|asp\.net|tools\.ietf|rolldown\.rs' } | Sort-Object -Unique
    if ($external) { throw "Unexpected external addresses in the web app: $($external -join ', ')" }
    Pass 'Web app has no external addresses'

    Copy-Item (Join-Path $Release 'app') (Join-Path $root 'app') -Recurse

    $secrets = @{ Root = New-Secret; App = New-Secret; Backup = New-Secret; Restore = New-Secret; Pfx = New-Secret; Admin = "Aa1$(New-Secret)" }
    & (Join-Path $scripts 'new-lan-certificate.ps1') -IpAddress $LanIp -OutputDir (Join-Path $root 'certs') -PfxPassword $secrets.Pfx
    if (Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -match 'Al-Shifa' }) { throw 'The CA stayed in the certificate store.' }
    $ca = New-Object Security.Cryptography.X509Certificates.X509Certificate2((Join-Path $root 'certs\alshifa-ca.cer'))
    if ($ca.HasPrivateKey) { throw 'The exported CA contains a private key.' }
    $caPem = Join-Path $root 'certs\ca.pem'
    "-----BEGIN CERTIFICATE-----`n$([Convert]::ToBase64String($ca.RawData, 'InsertLineBreaks'))`n-----END CERTIFICATE-----" | Set-Content $caPem -Encoding Ascii
    Pass 'Certificate created and the CA private key destroyed'

    $ini = Join-Path $root 'mysql.ini'
    Get-MySqlIni $mysqlBase (Join-Path $root 'data') (Join-Path $root 'logs\mysql.err') $MySqlPort | Set-Content $ini -Encoding Ascii
    $bootstrap = Join-Path $root 'secrets\bootstrap.sql'
    Get-MySqlBootstrapSql 'alshifa' $secrets.Root $secrets.App $secrets.Backup $secrets.Restore | Set-Content $bootstrap -Encoding Ascii
    Invoke-Native (Join-Path $mysqlBin 'mysqld.exe') @("--defaults-file=$ini", '--initialize-insecure', "--init-file=$bootstrap") | Out-Null
    Remove-Item $bootstrap
    Start-Database
    Write-ClientCnf (Join-Path $root 'secrets\backup.cnf') 'alshifa_backup' $secrets.Backup $MySqlPort
    Write-ClientCnf (Join-Path $root 'secrets\restore.cnf') 'alshifa_restore' $secrets.Restore $MySqlPort
    $printed = New-PrintablePassword
    if ($printed -notmatch '^([A-HJKMNP-TV-Z0-9]{5}-){5}[A-HJKMNP-TV-Z0-9]{5}$') { throw "Unexpected password format: $printed" }
    [IO.File]::WriteAllText($passwordFile, $printed)

    $emptyRootRefused = $false
    try { Invoke-Native (Join-Path $mysqlBin 'mysql.exe') @('--protocol=TCP', '-h', '127.0.0.1', '-P', "$MySqlPort", '-u', 'root', '--skip-password', '-e', 'SELECT 1') | Out-Null } catch { $emptyRootRefused = $true }
    if (-not $emptyRootRefused) { throw 'Root accepts an empty password.' }
    $env:MYSQL_PWD = $secrets.Root
    Pass 'MySQL initialised with accounts in place, no empty root password, loopback only' (Get-NetTCPConnection -State Listen -LocalPort $MySqlPort).LocalAddress

    [ordered]@{
        ConnectionStrings = @{ Default = "Server=127.0.0.1;Port=$MySqlPort;Database=alshifa;User=alshifa;Password=$($secrets.App);" }
        Kestrel = @{ Endpoints = [ordered]@{
            Lan = @{ Url = "https://0.0.0.0:$HttpsPort"; SslProtocols = @('Tls12', 'Tls13'); Certificate = @{ Path = (Join-Path $root 'certs\server.pfx'); Password = $secrets.Pfx } }
            Loopback = @{ Url = $loopUrl }
        } }
        Storage = @{ Path = (Join-Path $root 'storage') }
        DataProtection = @{ KeysPath = (Join-Path $root 'keys') }
        Backup = @{ StatusFile = (Join-Path $root 'logs\backup-status.json') }
    } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $root 'app\appsettings.Production.json') -Encoding UTF8

    Push-Location (Join-Path $root 'app')
    try {
        $env:ALSHIFA_ADMIN_USER = 'pilotadmin'
        $env:ALSHIFA_ADMIN_PASSWORD = $secrets.Admin
        Invoke-Native (Join-Path $root 'app\AlShifa.Api.exe') @('--create-admin') | Out-Null
    } finally {
        Remove-Item Env:\ALSHIFA_ADMIN_USER, Env:\ALSHIFA_ADMIN_PASSWORD
        Pop-Location
    }
    if ([int](Query "SELECT COUNT(*) FROM AspNetUsers WHERE UserName = 'pilotadmin'") -ne 1) { throw 'The app is not using the scratch database.' }
    Start-Api
    Pass 'Published app started in Production mode with HTTPS'

    $tls12 = Get-TlsVersion 'Tls12'
    if ($tls12 -ne 'Tls12') { throw "TLS 1.2 did not connect: $tls12" }
    if (Get-TlsVersion 'Tls11') { throw 'TLS 1.1 was accepted.' }
    Pass 'TLS 1.2 works and older TLS is refused'

    $env:NODE_EXTRA_CA_CERTS = $caPem
    $env:PILOT_LAN_URL = $lanUrl
    $env:PILOT_LOOP_URL = $loopUrl
    $env:PILOT_ADMIN_PASSWORD = $secrets.Admin
    $env:PILOT_STATE = Join-Path $root 'check-state.json'
    Invoke-Check 'workflow'
    Pass 'LAN address over trusted HTTPS: 4 sessions, full journey, attachment, proxied login'

    $env:ALSHIFA_PILOT_BASE = $lanUrl
    $env:ALSHIFA_PILOT_PASSWORD = $secrets.Admin
    Push-Location (Join-Path $repo 'frontend')
    try {
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        & cmd /c npx playwright test e2e/pilot-lan.spec.ts --reporter=line 2>&1 | ForEach-Object { "$_" } | Select-Object -Last 25
        $code = $LASTEXITCODE
        $ErrorActionPreference = $previous
        if ($code) { throw 'Browser check failed' }
    } finally {
        Pop-Location
    }
    Pass 'Real browser: no outside requests, lost response, double click, disconnect, edit conflict'

    # What a power cut during a backup would leave behind.
    $stale = New-Item -ItemType Directory (Join-Path $root 'backup-staging\20200101T000000Z')
    Set-Content (Join-Path $stale.FullName 'db.sql') 'readable leftovers'
    $stale.LastWriteTime = (Get-Date).AddDays(-1)

    $backups = Join-Path $root 'backups'
    & (Join-Path $scripts 'backup.ps1') -Root $root -MySqlBin $mysqlBin -Destination $backups -AllowSameDisk
    if ($LASTEXITCODE) { throw 'Backup failed' }
    if (Test-Path $stale.FullName) { throw 'Readable leftovers from an interrupted backup were not removed.' }
    $file = (Get-ChildItem $backups -Filter '*.zip.gpg').FullName
    $bytes = [IO.File]::ReadAllBytes($file)
    $asText = [Text.Encoding]::GetEncoding('ISO-8859-1').GetString($bytes)
    if ($asText -match 'CREATE TABLE|db\.sql|PK\x03\x04') { throw 'Backup contains readable data' }
    if (Get-ChildItem (Join-Path $root 'backup-staging')) { throw 'The backup left files in the staging folder.' }
    Pass 'OpenPGP backup created, opened and compared on its destination' "$([Math]::Round($bytes.Length / 1KB)) KB, no readable content, staging empty"
    Invoke-Check 'backup-ok'

    & (Join-Path $scripts 'restore-test.ps1') -Root $root -MySqlBin $mysqlBin -Backup $backups
    Pass 'Restore into an isolated database verified'

    if ((Query 'SELECT COUNT(*) FROM Patients') -lt 1) { throw 'Production copy lost its data' }
    $denied = $false
    try { Invoke-Native (Join-Path $mysqlBin 'mysql.exe') @("--defaults-extra-file=$(Join-Path $root 'secrets\restore.cnf')", '-e', 'DROP DATABASE alshifa') | Out-Null } catch { $denied = $true }
    if (-not $denied) { throw 'The restore account was able to drop production' }
    Pass 'Restore account cannot touch the production database'

    $bytes[$bytes.Length - 100] = $bytes[$bytes.Length - 100] -bxor 1
    $tampered = Join-Path $root 'tampered.zip.gpg'
    [IO.File]::WriteAllBytes($tampered, $bytes)
    $tamperedOutput = Join-Path $root 'tampered.zip'
    $caught = $false
    try { Unprotect-BackupFile $gpg $tampered $tamperedOutput $passwordFile $root } catch { $caught = $true }
    if (-not $caught -or (Test-Path $tamperedOutput)) { throw 'A damaged backup passed the integrity check or left output' }
    $wrongPassword = Join-Path $root 'wrong.txt'
    [IO.File]::WriteAllText($wrongPassword, (New-PrintablePassword))
    $caught = $false
    try { Unprotect-BackupFile $gpg $file $tamperedOutput $wrongPassword $root } catch { $caught = $true }
    if (-not $caught) { throw 'A wrong password opened the backup' }
    Pass 'Damaged backup and wrong password are both refused'

    # Recovery on another computer starts from the password on paper, not from the file on this one.
    $fromPaper = Join-Path $root 'from-paper.txt'
    [IO.File]::WriteAllText($fromPaper, $printed)
    $expanded = Join-Path $root 'expanded'
    & (Join-Path $scripts 'expand-backup.ps1') -BackupFile $file -OutputDir $expanded -PasswordFile $fromPaper | Out-Null
    if (-not (Test-Path (Join-Path $expanded 'db.sql')) -or -not (Get-ChildItem (Join-Path $expanded 'storage'))) { throw 'Expanded backup is incomplete' }
    Remove-Item $expanded -Recurse -Force
    Pass 'Backup opens with the printed password for a real recovery'

    $retention = Join-Path $root 'retention'
    New-Item -ItemType Directory $retention | Out-Null
    $now = (Get-Date).ToUniversalTime()
    foreach ($hoursAgo in 1, 5, 9, 72, 76, (10 * 24), (10 * 24 + 4), (20 * 24), (40 * 24), (41 * 24)) {
        New-Item -ItemType File (Join-Path $retention "alshifa-$($now.AddHours(-$hoursAgo).ToString('yyyyMMddTHHmmssZ')).zip.gpg") | Out-Null
    }
    Remove-ExpiredBackups $retention 7 35
    $kept = (Get-ChildItem $retention).Count
    # Five recent ones, at least one of the two from day 10 (two if they straddle midnight), the one from day 20, none older.
    if ($kept -lt 7 -or $kept -gt 8) { throw "Retention kept $kept backups" }
    $oldest = Get-ChildItem $retention | Sort-Object Name | Select-Object -First 1
    if ($oldest.Name -notlike "alshifa-$($now.AddDays(-20).ToString('yyyyMMdd'))*") { throw "Retention kept too much: $($oldest.Name)" }
    Pass 'Retention keeps the promised recovery points' "$kept of 10"

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & (Join-Path $scripts 'backup.ps1') -Root $root -MySqlBin $mysqlBin -Destination (Join-Path $root 'backups-same-disk') 2>&1 | Out-Null
    $sameDiskCode = $LASTEXITCODE
    $ErrorActionPreference = $previous
    if (-not $sameDiskCode) { throw 'A destination on the database disk was accepted' }
    Invoke-Check 'backup-failed'
    Pass 'Same-disk destination refused, and the failure reaches the web app'

    # Unexpected failure: both processes killed while writes are in flight, then every acknowledged write must still be there.
    $writer = Start-Process node -ArgumentList "`"$check`" writer" -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 4
    $outside = @(); foreach ($sample in 1..4) { $outside += Get-OutsideConnections; Start-Sleep -Seconds 1 }
    if ($outside) { throw "API or database talked to the outside: $(($outside | Sort-Object -Unique) -join ', ')" }
    Pass 'During real work, API and database hold no connection outside this host'
    Stop-Process -Id $apiProcess.Id -Force
    Stop-Database
    Stop-Process -Id $writer.Id -Force -ErrorAction SilentlyContinue
    Start-Database
    Start-Api
    Invoke-Check 'after-crash'
    $problems = Query @'
SELECT (SELECT COUNT(*) FROM (SELECT FileNumber FROM Patients GROUP BY FileNumber HAVING COUNT(*) > 1) d)
     + (SELECT COUNT(*) FROM (SELECT TransactionId FROM LedgerEntries GROUP BY TransactionId HAVING SUM(Debit) <> SUM(Credit)) u)
     + (SELECT COUNT(*) FROM StockBatches WHERE Quantity < 0)
'@
    if ([int]$problems -ne 0) { throw "Integrity problems after the crash: $problems" }
    Pass 'Hard kill of API and database: acknowledged writes kept, sessions kept, integrity clean'

    # The database failing alone must not take the application down or need a manual restart.
    Stop-Database
    $status = Get-HealthStatus
    if ($status -ne 503) { throw "Health during a database outage: $status" }
    Invoke-Check 'db-down'
    if ($apiProcess.HasExited) { throw 'The API stopped during a database outage.' }
    Start-Database
    Wait-Until 'API to reconnect by itself' { Test-Healthy }
    Pass 'Database outage: API stays up, saves fail with a clear error, recovery needs no restart'

    # After a reboot nothing guarantees the order in which things become ready.
    Stop-Process -Id $apiProcess.Id -Force
    Stop-Database
    Start-ApiProcess
    Wait-Until 'API to answer without a database' { (Get-HealthStatus) -eq 503 }
    Start-Database
    Wait-Until 'API to find the database' { Test-Healthy }
    Invoke-Check 'after-crash'
    Pass 'API started before the database recovers by itself'
} finally {
    Stop-Pilot
    Remove-Item Env:\MYSQL_PWD, Env:\NODE_EXTRA_CA_CERTS, Env:\PILOT_LAN_URL, Env:\PILOT_LOOP_URL, Env:\PILOT_ADMIN_PASSWORD, Env:\PILOT_STATE, Env:\ALSHIFA_PILOT_BASE, Env:\ALSHIFA_PILOT_PASSWORD -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
}

''
$results
