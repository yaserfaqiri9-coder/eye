# First-time setup of the hospital host. Run once from an elevated PowerShell, from the scripts folder of a release.
# Run preflight.ps1 first. If this stops halfway, uninstall.ps1 undoes it without deleting anything.
#Requires -RunAsAdministrator
param(
    # The address staff laptops will use. Reserve it for this laptop on the router first.
    [Parameter(Mandatory)][string]$LanIp,
    # Folder of the extracted official MySQL 8.4 ZIP (the one containing bin\mysqld.exe).
    [Parameter(Mandatory)][string]$MySqlZipFolder,
    # On a different physical disk from the system drive. See backup.ps1.
    [Parameter(Mandatory)][string]$BackupDestination,
    [string]$BackupOptionalDestination,
    [string]$AdminUser = 'admin',
    [string]$Root = 'C:\AlShifa',
    [int]$HttpsPort = 443,
    [int]$LoopbackPort = 5080,
    [int]$MySqlPort = 3307
)

. (Join-Path $PSScriptRoot 'common.ps1')

$step = 'checks before anything changes'
trap {
    ''
    "INSTALL STOPPED during: $step"
    "Reason: $_"
    "Nothing was deleted. To undo what was done: $(Join-Path $PSScriptRoot 'uninstall.ps1') -Root $Root -MoveFolderAside"
    break
}

$release = Split-Path $PSScriptRoot -Parent
foreach ($required in (Join-Path $release 'app\AlShifa.Api.exe'), (Join-Path $MySqlZipFolder 'bin\mysqld.exe')) {
    if (-not (Test-Path $required)) { throw "Missing: $required" }
}
# A space would make the service path ambiguous, and an existing folder would have its permissions replaced below.
if ($Root -match '\s') { throw 'The install folder must not contain spaces.' }
if ((Test-Path $Root) -and (Get-ChildItem $Root -Force | Select-Object -First 1)) { throw "$Root is not empty. Nothing was changed." }
if (Get-Service 'AlShifaApi', 'AlShifaMySQL' -ErrorAction SilentlyContinue) { throw 'The services already exist. Nothing was changed.' }
if (Get-NetTCPConnection -State Listen -LocalPort $HttpsPort, $LoopbackPort, $MySqlPort -ErrorAction SilentlyContinue) { throw "Port $HttpsPort, $LoopbackPort or $MySqlPort is already in use. Nothing was changed." }
$gpg = Find-Gpg

$adminPassword = Read-Host "Password for the '$AdminUser' administrator (10+ characters, with capital, small letter and digit)" -AsSecureString
$adminAgain = Read-Host 'Same password again' -AsSecureString
function ConvertTo-Plain([securestring]$Value) { [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)) }
$adminPlain = ConvertTo-Plain $adminPassword
if ($adminPlain -cne (ConvertTo-Plain $adminAgain)) { throw 'The two passwords are different. Nothing was changed.' }
# The same rules the application enforces, so a weak password stops the install here and not near its end.
if ($adminPlain.Length -lt 10 -or $adminPlain -cnotmatch '[A-Z]' -or $adminPlain -cnotmatch '[a-z]' -or $adminPlain -notmatch '\d') {
    throw 'The password needs 10+ characters with a capital letter, a small letter and a digit. Nothing was changed.'
}

$step = 'folders and secrets'
$step
$folders = 'app', 'mysql', 'data', 'storage', 'keys', 'certs', 'logs', 'secrets', 'scripts', 'backup-staging'
foreach ($folder in $folders) { New-Item -ItemType Directory -Force (Join-Path $Root $folder) | Out-Null }
Protect-Path $Root
foreach ($folder in $folders) { Protect-Path (Join-Path $Root $folder) }

$secrets = @{ Root = New-Secret; App = New-Secret; Backup = New-Secret; Restore = New-Secret; Pfx = New-Secret }
$secretsDir = Join-Path $Root 'secrets'
Set-Content (Join-Path $secretsDir 'mysql-root.txt') $secrets.Root -Encoding Ascii
# Written without a line ending, so the password that opens the backups is exactly what is printed.
[IO.File]::WriteAllText((Join-Path $secretsDir 'backup-password.txt'), (New-PrintablePassword))
Write-ClientCnf (Join-Path $secretsDir 'backup.cnf') 'alshifa_backup' $secrets.Backup $MySqlPort
Write-ClientCnf (Join-Path $secretsDir 'restore.cnf') 'alshifa_restore' $secrets.Restore $MySqlPort

$step = 'copying application files'
$step
Copy-Item (Join-Path $release 'app\*') (Join-Path $Root 'app') -Recurse -Force
Copy-Item (Join-Path $PSScriptRoot '*') (Join-Path $Root 'scripts') -Recurse -Force
Copy-Item (Join-Path $MySqlZipFolder '*') (Join-Path $Root 'mysql') -Recurse -Force

$step = 'HTTPS certificate'
$step
& (Join-Path $PSScriptRoot 'new-lan-certificate.ps1') -IpAddress $LanIp -OutputDir (Join-Path $Root 'certs') -PfxPassword $secrets.Pfx

$step = 'database service'
$step
$mysqld = Join-Path $Root 'mysql\bin\mysqld.exe'
$ini = Join-Path $Root 'mysql\alshifa.ini'
Get-MySqlIni (Join-Path $Root 'mysql') (Join-Path $Root 'data') (Join-Path $Root 'logs\mysql.err') $MySqlPort | Set-Content $ini -Encoding Ascii
$bootstrap = Join-Path $secretsDir 'bootstrap.sql'
Get-MySqlBootstrapSql 'alshifa' $secrets.Root $secrets.App $secrets.Backup $secrets.Restore | Set-Content $bootstrap -Encoding Ascii
try {
    # The accounts exist before the server first opens a port, so there is never a moment with an empty root password.
    Invoke-Native $mysqld @("--defaults-file=$ini", '--initialize-insecure', "--init-file=$bootstrap") | Out-Null
} finally {
    Remove-Item $bootstrap -Force
}
Invoke-Native $mysqld @('--install', 'AlShifaMySQL', "--defaults-file=$ini") | Out-Null
Invoke-Native 'sc.exe' @('config', 'AlShifaMySQL', 'obj=', 'NT SERVICE\AlShifaMySQL', 'start=', 'auto') | Out-Null
Invoke-Native 'sc.exe' @('failure', 'AlShifaMySQL', 'reset=', '86400', 'actions=', 'restart/10000/restart/30000/restart/60000') | Out-Null
Invoke-Native 'sc.exe' @('failureflag', 'AlShifaMySQL', '1') | Out-Null
Protect-Path (Join-Path $Root 'mysql') @('NT SERVICE\AlShifaMySQL=RX')
Protect-Path (Join-Path $Root 'data') @('NT SERVICE\AlShifaMySQL=F')
Protect-Path (Join-Path $Root 'logs') @('NT SERVICE\AlShifaMySQL=M')
# The folder above these must be traversable, or the narrower grants have no effect.
Invoke-Native 'icacls.exe' @($Root, '/grant', 'NT SERVICE\AlShifaMySQL:(X)') | Out-Null
Start-Service AlShifaMySQL

$mysql = Join-Path $Root 'mysql\bin\mysql.exe'
$backupCnf = "--defaults-extra-file=$(Join-Path $secretsDir 'backup.cnf')"
foreach ($attempt in 1..60) {
    try { Invoke-Native $mysql @($backupCnf, '-e', 'SELECT 1') | Out-Null; break }
    catch { if ($attempt -eq 60) { throw }; Start-Sleep -Seconds 2 }
}

$step = 'application configuration'
$step
$appDir = Join-Path $Root 'app'
[ordered]@{
    ConnectionStrings = @{ Default = "Server=127.0.0.1;Port=$MySqlPort;Database=alshifa;User=alshifa;Password=$($secrets.App);" }
    Kestrel = @{ Endpoints = [ordered]@{
        Lan = @{ Url = "https://0.0.0.0:$HttpsPort"; SslProtocols = @('Tls12', 'Tls13'); Certificate = @{ Path = (Join-Path $Root 'certs\server.pfx'); Password = $secrets.Pfx } }
        # Only for this computer: health checks and Tailscale Serve, which adds its own HTTPS in front.
        Loopback = @{ Url = "http://127.0.0.1:$LoopbackPort" }
    } }
    Storage = @{ Path = (Join-Path $Root 'storage') }
    DataProtection = @{ KeysPath = (Join-Path $Root 'keys') }
    Backup = @{ StatusFile = (Join-Path $Root 'logs\backup-status.json') }
} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $appDir 'appsettings.Production.json') -Encoding UTF8

$step = 'database schema and first administrator'
$step
$apiExe = Join-Path $appDir 'AlShifa.Api.exe'
Push-Location $appDir
try {
    $env:ALSHIFA_ADMIN_USER = $AdminUser
    $env:ALSHIFA_ADMIN_PASSWORD = $adminPlain
    Invoke-Native $apiExe @('--create-admin') | Out-Null
} finally {
    Remove-Item Env:\ALSHIFA_ADMIN_USER, Env:\ALSHIFA_ADMIN_PASSWORD -ErrorAction SilentlyContinue
    $adminPlain = $null
    Pop-Location
}

$step = 'application service'
$step
if (-not [Diagnostics.EventLog]::SourceExists('AlShifa.Api')) { [Diagnostics.EventLog]::CreateEventSource('AlShifa.Api', 'Application') }
Invoke-Native 'sc.exe' @('create', 'AlShifaApi', 'binPath=', $apiExe, 'start=', 'auto', 'depend=', 'AlShifaMySQL', 'obj=', 'NT SERVICE\AlShifaApi', 'DisplayName=', 'Al-Shifa Hospital Application') | Out-Null
Invoke-Native 'sc.exe' @('failure', 'AlShifaApi', 'reset=', '86400', 'actions=', 'restart/5000/restart/15000/restart/60000') | Out-Null
# Without this, Windows restarts a service only after a crash, not after it stops by itself with an error.
Invoke-Native 'sc.exe' @('failureflag', 'AlShifaApi', '1') | Out-Null
Protect-Path $appDir @('NT SERVICE\AlShifaApi=RX')
Protect-Path (Join-Path $Root 'certs') @('NT SERVICE\AlShifaApi=R')
Protect-Path (Join-Path $Root 'storage') @('NT SERVICE\AlShifaApi=M')
Protect-Path (Join-Path $Root 'keys') @('NT SERVICE\AlShifaApi=M')
Invoke-Native 'icacls.exe' @((Join-Path $Root 'logs'), '/grant', 'NT SERVICE\AlShifaApi:(OI)(CI)R') | Out-Null
Invoke-Native 'icacls.exe' @($Root, '/grant', 'NT SERVICE\AlShifaApi:(X)') | Out-Null
Start-Service AlShifaApi

$step = 'firewall'
$step
New-NetFirewallRule -DisplayName 'Al-Shifa HTTPS (hospital LAN)' -Direction Inbound -Protocol TCP -LocalPort $HttpsPort `
    -RemoteAddress LocalSubnet -Profile Private, Domain -Action Allow | Out-Null
if (Get-NetConnectionProfile | Where-Object NetworkCategory -eq 'Public') {
    'WARNING: a network is marked Public, so the rule above does not apply on it. Run: Set-NetConnectionProfile -InterfaceAlias <name> -NetworkCategory Private'
}

$step = 'power settings'
$step
# The previous plan is saved so uninstall.ps1 can put it back.
$activeScheme = [regex]::Match((Invoke-Native 'powercfg.exe' @('/getactivescheme') | Out-String), '[0-9a-fA-F-]{36}').Value
Invoke-Native 'powercfg.exe' @('/export', (Join-Path $Root 'logs\power-before-install.pow'), $activeScheme) | Out-Null
foreach ($setting in 'standby-timeout-ac', 'standby-timeout-dc', 'hibernate-timeout-ac', 'hibernate-timeout-dc') {
    Invoke-Native 'powercfg.exe' @('/change', $setting, '0') | Out-Null
}
foreach ($target in '/setacvalueindex', '/setdcvalueindex') {
    Invoke-Native 'powercfg.exe' @($target, 'SCHEME_CURRENT', 'SUB_BUTTONS', 'LIDACTION', '0') | Out-Null
}
Invoke-Native 'powercfg.exe' @('/setactive', 'SCHEME_CURRENT') | Out-Null

$step = 'backup task'
$step
$arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $Root 'scripts\backup.ps1')`" -Root `"$Root`" -Destination `"$BackupDestination`""
if ($BackupOptionalDestination) { $arguments += " -OptionalDestination `"$BackupOptionalDestination`"" }
# Every 4 hours, and soon after start-up if a run was missed while the laptop was off.
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).Date.AddHours(6) -RepetitionInterval (New-TimeSpan -Hours 4)
$taskSettings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName 'AlShifa Backup' -Action (New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments) `
    -Trigger $trigger -Settings $taskSettings -User 'SYSTEM' -RunLevel Highest | Out-Null

$step = 'first backup and restore test'
$step
& (Join-Path $Root 'scripts\backup.ps1') -Root $Root -Destination $BackupDestination
if ($LASTEXITCODE) {
    'WARNING: the first backup failed. Fix the destination, then run: Start-ScheduledTask "AlShifa Backup"'
} else {
    & (Join-Path $Root 'scripts\restore-test.ps1') -Root $Root -Backup $BackupDestination
}

''
& (Join-Path $Root 'scripts\health-check.ps1') -Root $Root -HealthUrl "http://127.0.0.1:$LoopbackPort/health"
''
"Open on staff laptops: https://$LanIp$(if ($HttpsPort -ne 443) { ":$HttpsPort" })/"
"Install on each staff laptop: $(Join-Path $Root 'certs\alshifa-ca.cer')"
"Print and store away from this laptop: $(Join-Path $secretsDir 'backup-password.txt') - backups cannot be opened without it."
