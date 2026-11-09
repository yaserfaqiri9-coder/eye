# Encrypted backup of the database, uploaded files and session keys. Runs from Task Scheduler (see install.ps1).
# Any failure is recorded in logs\backup-status.json (the web app shows a warning from it) and in the Windows Application log.
param(
    # Must be on a different physical disk from the database: external drive or network share.
    [Parameter(Mandatory)][string]$Destination,
    # A second copy when available, for example a USB drive that gets rotated. Its absence is not a failure.
    [string]$OptionalDestination,
    [string]$Root = 'C:\AlShifa',
    [string]$Database = 'alshifa',
    [string]$MySqlBin,
    [string]$Gpg,
    [int]$KeepAllDays = 7,
    [int]$KeepDailyDays = 35,
    [switch]$AllowSameDisk
)

. (Join-Path $PSScriptRoot 'common.ps1')

if (-not $MySqlBin) { $MySqlBin = Join-Path $Root 'mysql\bin' }
$statusFile = Join-Path $Root 'logs\backup-status.json'
$passwordFile = Join-Path $Root 'secrets\backup-password.txt'
$stagingRoot = Join-Path $Root 'backup-staging'
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
$work = Join-Path $stagingRoot $stamp

$started = Get-Date

# Fields not named in $Changes are kept, so a failed run still shows when the last good backup was made.
# Replaced in one step, so the web app never reads a half-written file.
function Update-Status([hashtable]$Changes) {
    $status = [ordered]@{}
    try { (Get-Content $statusFile -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $status[$_.Name] = $_.Value } } catch { }
    foreach ($key in $Changes.Keys) { $status[$key] = $Changes[$key] }
    $temporary = "$statusFile.tmp"
    [IO.File]::WriteAllText($temporary, ($status | ConvertTo-Json -Depth 4))
    Move-Item -Force $temporary $statusFile
}

# What Settings > Backup lists: the files on the main destination after old ones were removed.
function Get-BackupList([string]$Folder) {
    return @(Get-ChildItem $Folder -Filter 'alshifa-*.zip.gpg' | Sort-Object Name -Descending | ForEach-Object {
        [ordered]@{
            name = $_.Name
            sizeBytes = $_.Length
            createdAt = [DateTime]::ParseExact($_.Name.Substring(8, 16), 'yyyyMMddTHHmmssZ', $null, 'AssumeUniversal,AdjustToUniversal').ToString('o')
        }
    })
}

# The copy that counts is the one on the destination, so that is the one opened and compared with the original.
function Copy-Backup([string]$Source, [string]$Folder, [string]$OriginalHash) {
    New-Item -ItemType Directory -Force $Folder | Out-Null
    $name = Split-Path $Source -Leaf
    $partial = Join-Path $Folder "$name.partial"
    Copy-Item $Source $partial -Force
    $check = Join-Path $work 'verify.zip'
    Unprotect-BackupFile $script:gpgExe $partial $check $passwordFile $work
    try {
        if ((Get-FileHash $check -Algorithm SHA256).Hash -ne $OriginalHash) { throw "The copy on $Folder does not match the original." }
    } finally {
        Remove-Item $check -Force -ErrorAction SilentlyContinue
    }
    Move-Item -Force $partial (Join-Path $Folder $name)
    Remove-ExpiredBackups $Folder $KeepAllDays $KeepDailyDays
}

try {
    New-Item -ItemType Directory -Force $work, (Split-Path $statusFile) | Out-Null
    Update-Status @{ runningSince = $started.ToUniversalTime().ToString('o') }

    # A power cut during an earlier run can leave a readable dump behind. This run removes it.
    Get-ChildItem $stagingRoot | Where-Object { $_.Name -ne $stamp -and $_.LastWriteTime -lt (Get-Date).AddHours(-6) } | Remove-Item -Recurse -Force

    if (-not (Test-Path ([IO.Path]::GetPathRoot($Destination)))) { throw "Backup destination $Destination is not connected." }
    if (-not $AllowSameDisk -and (Test-SameDisk $Destination $Root)) { throw "Backup destination $Destination is on the same disk as the database." }

    $script:gpgExe = Find-Gpg $Gpg
    $content = Join-Path $work 'content'
    New-Item -ItemType Directory $content | Out-Null
    $dump = Join-Path $content 'db.sql'

    # A consistent snapshot without blocking users. The dump comes first: a file uploaded after it is harmless extra data,
    # whereas a database row whose file is missing would be a broken record.
    Invoke-Native (Join-Path $MySqlBin 'mysqldump.exe') @(
        "--defaults-extra-file=$(Join-Path $Root 'secrets\backup.cnf')",
        '--single-transaction', '--quick', '--no-tablespaces', '--set-gtid-purged=OFF', '--triggers',
        '--default-character-set=utf8mb4', "--result-file=$dump", $Database) | Out-Null

    # mysqldump writes this line last, so its absence means a truncated dump.
    if ((Get-Content $dump -Tail 1) -notmatch '^-- Dump completed') { throw 'The database dump is incomplete.' }

    foreach ($folder in 'storage', 'keys') {
        $source = Join-Path $Root $folder
        if (Test-Path $source) { Copy-Item $source (Join-Path $content $folder) -Recurse }
    }

    $zip = Join-Path $work 'backup.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($content, $zip)
    Remove-Item $content -Recurse -Force

    $encrypted = Join-Path $work "alshifa-$stamp.zip.gpg"
    Protect-BackupFile $script:gpgExe $zip $encrypted $passwordFile $work
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
    Remove-Item $zip -Force

    Copy-Backup $encrypted $Destination $hash

    $optionalProblem = $null
    if ($OptionalDestination -and (Test-Path ([IO.Path]::GetPathRoot($OptionalDestination)))) {
        try { Copy-Backup $encrypted $OptionalDestination $hash } catch { $optionalProblem = "$_" }
    }

    $now = (Get-Date).ToUniversalTime().ToString('o')
    Update-Status @{
        lastRunAt = $now; lastSuccessAt = $now; lastError = $optionalProblem; runningSince = $null
        destination = $Destination; optionalDestination = $OptionalDestination
        lastSizeBytes = (Get-Item $encrypted).Length; lastDurationSeconds = [int]((Get-Date) - $started).TotalSeconds
        # @() again here: PowerShell turns a returned one-item array into the item, which would not be a JSON list.
        backups = @(Get-BackupList $Destination)
    }
    "Backup complete: $(Join-Path $Destination (Split-Path $encrypted -Leaf))"
} catch {
    $message = "$_"
    try { Update-Status @{ lastRunAt = (Get-Date).ToUniversalTime().ToString('o'); lastError = $message; runningSince = $null } } catch { }
    # eventcreate needs an administrator, which the scheduled task is. Without one, the status file still records the failure.
    try { Invoke-Native 'eventcreate.exe' @('/T', 'ERROR', '/ID', '100', '/L', 'APPLICATION', '/SO', 'AlShifaBackup', '/D', "Al-Shifa backup failed: $message") | Out-Null } catch { }
    Write-Error "Backup failed: $message" -ErrorAction Continue
    exit 1
} finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
