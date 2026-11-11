# Starts the local development MySQL 8.4 on 127.0.0.1:3307, initialising it on first run.
# Everything lives under .tools/ and does not touch any other MySQL/MariaDB on this machine.
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$tools = Join-Path $root '.tools'
$bin = Join-Path $tools 'mysql-8.4.11-winx64\bin'
$data = Join-Path $tools 'mysql-data'
$secretsFile = Join-Path $tools 'dev-secrets.json'
$ini = Join-Path $tools 'mysql-dev.ini'
$port = 3307

function New-Password {
    $bytes = [byte[]]::new(24)
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    return ([Convert]::ToBase64String($bytes) -replace '[+/=]', 'x')
}

function Test-Port {
    return [bool](Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue)
}

function Wait-Port {
    foreach ($i in 1..60) {
        if (Test-Port) { return }
        Start-Sleep -Seconds 1
    }
    throw "MySQL did not start on port $port"
}

if (-not (Test-Path $ini)) {
    @"
[mysqld]
basedir=$($tools -replace '\\','/')/mysql-8.4.11-winx64
datadir=$($data -replace '\\','/')
port=$port
bind-address=127.0.0.1
mysqlx=OFF
character-set-server=utf8mb4
collation-server=utf8mb4_0900_ai_ci
default-time-zone=+00:00
innodb_buffer_pool_size=512M
max_connections=200
log_error=$($tools -replace '\\','/')/mysql-dev.err
"@ | Set-Content -Path $ini -Encoding ascii
}

# mysqld writes normal progress to stderr, which PowerShell 5.1 would treat as a terminating error.
$ErrorActionPreference = 'Continue'

if (-not (Test-Path (Join-Path $data 'mysql.ibd'))) {
    & "$bin\mysqld.exe" "--defaults-file=$ini" --initialize-insecure
    if ($LASTEXITCODE) { throw "mysqld initialise failed: $LASTEXITCODE" }
}

if (-not (Test-Port)) {
    Start-Process -FilePath "$bin\mysqld.exe" -ArgumentList "--defaults-file=`"$ini`"" -WindowStyle Hidden
    Wait-Port
}

if (-not (Test-Path $secretsFile)) {
    $secrets = @{ root = New-Password; app = New-Password }
    $sql = @"
ALTER USER 'root'@'localhost' IDENTIFIED BY '$($secrets.root)';
CREATE DATABASE alshifa_dev CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
CREATE DATABASE alshifa_test CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
CREATE USER 'alshifa'@'localhost' IDENTIFIED BY '$($secrets.app)';
GRANT ALL PRIVILEGES ON alshifa_dev.* TO 'alshifa'@'localhost';
GRANT ALL PRIVILEGES ON alshifa_test.* TO 'alshifa'@'localhost';
GRANT ALL PRIVILEGES ON alshifa_synclocal_test.* TO 'alshifa'@'localhost';
GRANT ALL PRIVILEGES ON alshifa_mirror_test.* TO 'alshifa'@'localhost';
FLUSH PRIVILEGES;
"@
    $sql | & "$bin\mysql.exe" --protocol=TCP -h 127.0.0.1 -P $port -u root
    if ($LASTEXITCODE) { throw "bootstrap SQL failed: $LASTEXITCODE" }
    $secrets | ConvertTo-Json | Set-Content -Path $secretsFile -Encoding ascii
}

"MySQL ready on 127.0.0.1:$port"
