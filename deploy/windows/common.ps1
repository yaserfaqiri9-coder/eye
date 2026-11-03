# Shared helpers for the hospital host scripts. Dot-source this file. Runs on Windows PowerShell 5.1.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

function New-Secret([int]$Bytes = 32) {
    $buffer = New-Object byte[] $Bytes
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($buffer)
    # Letters and digits only, so the value is safe inside SQL, INI and JSON without quoting rules.
    return ([Convert]::ToBase64String($buffer) -replace '[+/=]', 'x')
}

# A password for printing on paper: no letters or digits that look alike. 30 characters of 32 possibilities is 150 bits.
function New-PrintablePassword {
    $alphabet = 'ABCDEFGHJKMNPQRSTVWXYZ0123456789'
    $buffer = New-Object byte[] 30
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($buffer)
    $characters = $buffer | ForEach-Object { $alphabet[$_ % 32] }
    return (0..5 | ForEach-Object { -join $characters[($_ * 5)..($_ * 5 + 4)] }) -join '-'
}

# Runs a program and throws with its output on failure. Windows PowerShell treats stderr as an error, so it is relaxed here.
function Invoke-Native([string]$Exe, [string[]]$Arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $Exe @Arguments 2>&1 | ForEach-Object { "$_" }
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previous
    }
    if ($code -ne 0) { throw "$(Split-Path $Exe -Leaf) failed with exit code ${code}: $($output -join ' ')" }
    return $output
}

# True if nothing listens on the port and Windows would let a program take it. Binds without listening, so no firewall prompt can appear.
function Test-PortFree([int]$Port) {
    if (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue) { return $false }
    $socket = New-Object Net.Sockets.Socket([Net.Sockets.AddressFamily]::InterNetwork, [Net.Sockets.SocketType]::Stream, [Net.Sockets.ProtocolType]::Tcp)
    try {
        $socket.ExclusiveAddressUse = $true
        $socket.Bind((New-Object Net.IPEndPoint([Net.IPAddress]::Any, $Port)))
        return $true
    } catch {
        # Held by another program, or inside a range Windows reserves, as Hyper-V does.
        return $false
    } finally {
        $socket.Dispose()
    }
}

function Select-FreePort([int[]]$Candidates, [string]$Purpose) {
    foreach ($port in $Candidates) {
        if (Test-PortFree $port) { return $port }
    }
    throw "No free port for $Purpose among: $($Candidates -join ', ')."
}

# A web server that is stopped today can start tomorrow and take 443 back from the hospital system. Whichever came second would fail.
function Test-WebServerPresent {
    if (Get-Service 'W3SVC', 'Apache*', 'wampapache*', 'nginx*', 'VMwareHostd' -ErrorAction SilentlyContinue | Where-Object StartType -ne 'Disabled') { return $true }
    foreach ($folder in 'C:\xampp\apache', 'C:\wamp64', 'C:\wamp', 'C:\laragon') {
        if (Test-Path $folder) { return $true }
    }
    return $false
}

# Removes inherited access and leaves only SYSTEM, Administrators and the named accounts. Well-known SIDs work on any Windows language.
function Protect-Path([string]$Path, [string[]]$ExtraGrants = @()) {
    $isDirectory = Test-Path $Path -PathType Container
    $inherit = if ($isDirectory) { '(OI)(CI)' } else { '' }
    $arguments = @($Path, '/inheritance:r', '/grant:r', "*S-1-5-18:${inherit}F", "*S-1-5-32-544:${inherit}F")
    foreach ($grant in $ExtraGrants) {
        $account, $rights = $grant -split '=', 2
        $arguments += '/grant:r', "${account}:${inherit}${rights}"
    }
    Invoke-Native 'icacls.exe' $arguments | Out-Null
}

function Get-MySqlIni([string]$BaseDir, [string]$DataDir, [string]$LogFile, [int]$Port) {
    return @"
[mysqld]
basedir=$($BaseDir -replace '\\', '/')
datadir=$($DataDir -replace '\\', '/')
port=$Port
# Reachable only from this computer. Client laptops talk to the application, never to the database.
bind-address=127.0.0.1
mysqlx=OFF
character-set-server=utf8mb4
collation-server=utf8mb4_0900_ai_ci
default-time-zone=+00:00
innodb_buffer_pool_size=512M
innodb_flush_log_at_trx_commit=1
max_connections=100
# Scheduled backups are the recovery plan. Binary logs would only add disk writes on a laptop.
disable-log-bin
log_error=$($LogFile -replace '\\', '/')
"@
}

function Get-MySqlBootstrapSql([string]$Database, [string]$RootPassword, [string]$AppPassword, [string]$BackupPassword, [string]$RestorePassword) {
    # Runs through --init-file, which needs one statement per line and no comments, so the passwords never appear on a command line.
    # The restore account can only reach databases named alshifa_restore_*, so a restore test cannot overwrite production.
    return @"
ALTER USER 'root'@'localhost' IDENTIFIED BY '$RootPassword';
CREATE DATABASE $Database CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
CREATE USER 'alshifa'@'localhost' IDENTIFIED BY '$AppPassword';
GRANT ALL PRIVILEGES ON $Database.* TO 'alshifa'@'localhost';
CREATE USER 'alshifa_backup'@'localhost' IDENTIFIED BY '$BackupPassword';
GRANT SELECT, LOCK TABLES, SHOW VIEW, TRIGGER ON $Database.* TO 'alshifa_backup'@'localhost';
CREATE USER 'alshifa_restore'@'localhost' IDENTIFIED BY '$RestorePassword';
GRANT ALL PRIVILEGES ON ``alshifa\_restore\_%``.* TO 'alshifa_restore'@'localhost';
FLUSH PRIVILEGES;
"@
}

function Write-ClientCnf([string]$Path, [string]$User, [string]$Password, [int]$Port) {
    @"
[client]
user=$User
password=$Password
host=127.0.0.1
port=$Port
protocol=TCP
default-character-set=utf8mb4
"@ | Set-Content -Path $Path -Encoding Ascii
}

# Finds GnuPG: Gpg4win on the hospital host, or the copy inside Git for Windows on a development computer.
function Find-Gpg([string]$Path) {
    $candidates = @($Path, 'C:\Program Files (x86)\GnuPG\bin\gpg.exe', 'C:\Program Files\GnuPG\bin\gpg.exe', 'C:\Program Files\Gpg4win\bin\gpg.exe', 'C:\Program Files (x86)\Gpg4win\bin\gpg.exe', 'C:\Program Files\Git\usr\bin\gpg.exe')
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }
    throw 'GnuPG was not found. Install Gpg4win.'
}

# GnuPG runs with a private, temporary home so it neither reads nor leaves anything in a user profile, and never caches the password.
function Invoke-Gpg([string]$Gpg, [string]$PasswordFile, [string]$WorkDir, [string[]]$Arguments) {
    $homeDir = Join-Path $WorkDir "gnupg-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Force $homeDir | Out-Null
    # The copy inside Git for Windows reads "F:/x" as a relative path and needs "/f/x". Gpg4win takes the path as it is.
    $forGpg = {
        param([string]$Value)
        if ($Gpg -notmatch '\\Git\\') { return $Value }
        return [regex]::Replace($Value, '^([A-Za-z]):/', { param($m) "/$($m.Groups[1].Value.ToLower())/" })
    }
    $gpgHome = & $forGpg ($homeDir -replace '\\', '/')
    $paths = foreach ($argument in @($PasswordFile -replace '\\', '/') + $Arguments) { & $forGpg $argument }
    try {
        Invoke-Native $Gpg (@('--homedir', $gpgHome, '--batch', '--yes', '--no-symkey-cache', '--pinentry-mode', 'loopback', '--passphrase-file') + $paths) | Out-Null
    } finally {
        try { Invoke-Native (Join-Path (Split-Path $Gpg) 'gpgconf.exe') @('--homedir', $gpgHome, '--kill', 'gpg-agent') | Out-Null } catch { }
        Remove-Item $homeDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Standard OpenPGP (RFC 4880) password encryption: AES-256, SHA-512 key stretching and an integrity check.
# Opens with Kleopatra or plain gpg on any computer, given only the password.
function Protect-BackupFile([string]$Gpg, [string]$InputFile, [string]$OutputFile, [string]$PasswordFile, [string]$WorkDir) {
    Invoke-Gpg $Gpg $PasswordFile $WorkDir @('--symmetric', '--cipher-algo', 'AES256', '--s2k-mode', '3', '--s2k-count', '65011712',
        '--s2k-digest-algo', 'SHA512', '--compress-algo', 'none', '--output', ($OutputFile -replace '\\', '/'), ($InputFile -replace '\\', '/'))
}

# Throws if the file is damaged, altered or the password is wrong, and then leaves no partial output behind.
function Unprotect-BackupFile([string]$Gpg, [string]$File, [string]$OutputFile, [string]$PasswordFile, [string]$WorkDir) {
    try {
        Invoke-Gpg $Gpg $PasswordFile $WorkDir @('--decrypt', '--output', ($OutputFile -replace '\\', '/'), ($File -replace '\\', '/'))
    } catch {
        Remove-Item $OutputFile -Force -ErrorAction SilentlyContinue
        throw
    }
}

# Decrypts a backup into a folder holding db.sql, storage\ and keys\.
function Expand-Backup([string]$Gpg, [string]$File, [string]$OutputDir, [string]$PasswordFile) {
    New-Item -ItemType Directory -Force $OutputDir | Out-Null
    $zip = Join-Path $OutputDir 'backup.zip'
    Unprotect-BackupFile $Gpg $File $zip $PasswordFile $OutputDir
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $OutputDir)
    Remove-Item $zip
}

# C: and D: are often two partitions of one physical disk, so comparing letters alone would not protect anything.
function Test-SameDisk([string]$First, [string]$Second) {
    $firstRoot = [IO.Path]::GetPathRoot($First)
    $secondRoot = [IO.Path]::GetPathRoot($Second)
    if ($firstRoot -eq $secondRoot) { return $true }
    if ($firstRoot -notmatch '^[A-Za-z]:' -or $secondRoot -notmatch '^[A-Za-z]:') { return $false }
    return (Get-Partition -DriveLetter $firstRoot[0]).DiskNumber -eq (Get-Partition -DriveLetter $secondRoot[0]).DiskNumber
}

# Keeps the newest backup always, every backup for KeepAllDays, then one per day until KeepDailyDays.
function Remove-ExpiredBackups([string]$Folder, [int]$KeepAllDays, [int]$KeepDailyDays) {
    $now = (Get-Date).ToUniversalTime()
    $backups = Get-ChildItem $Folder -Filter 'alshifa-*.zip.gpg' | ForEach-Object {
        $when = [DateTime]::ParseExact($_.Name.Substring(8, 16), 'yyyyMMddTHHmmssZ', $null, 'AssumeUniversal,AdjustToUniversal')
        [pscustomobject]@{ File = $_; When = $when; Age = ($now - $when).TotalDays }
    } | Sort-Object When -Descending

    $seenDays = @{}
    foreach ($backup in ($backups | Select-Object -Skip 1)) {
        if ($backup.Age -le $KeepAllDays) { continue }
        $day = $backup.When.ToString('yyyyMMdd')
        if ($backup.Age -le $KeepDailyDays -and -not $seenDays.ContainsKey($day)) { $seenDays[$day] = $true; continue }
        Remove-Item $backup.File.FullName
    }
}
