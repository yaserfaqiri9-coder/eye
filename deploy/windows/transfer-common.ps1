# Shared verification for a portable database snapshot. Never stores database server accounts.
. (Join-Path $PSScriptRoot 'common.ps1')

function ConvertTo-Plain([securestring]$Value) {
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

function Get-TransferTables([string]$MySql, [string]$Cnf, [string]$Database) {
    if ($Database -notmatch '^alshifa[a-z0-9_]*$') { throw 'Unexpected database name.' }
    return @(Invoke-Native $MySql @("--defaults-extra-file=$Cnf", '-N', '-B', '-e', "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA='$Database' AND TABLE_TYPE='BASE TABLE' ORDER BY TABLE_NAME"))
}

function Get-TransferFingerprint([string]$MySql, [string]$Cnf, [string]$Database) {
    foreach ($table in (Get-TransferTables $MySql $Cnf $Database)) {
        if ($table -notmatch '^[A-Za-z0-9_]+$') { throw 'Unexpected table name.' }
        $count = @(Invoke-Native $MySql @("--defaults-extra-file=$Cnf", '-N', '-B', $Database, '-e', "SELECT COUNT(*) FROM ``$table``"))[0]
        $checksum = @(Invoke-Native $MySql @("--defaults-extra-file=$Cnf", '-N', '-B', $Database, '-e', "CHECKSUM TABLE ``$table`` EXTENDED"))[0] -split "`t"
        if ($checksum[-1] -notmatch '^\d+$') { throw "Checksum unavailable for $table." }
        [ordered]@{ table = $table; rows = [long]$count; checksum = $checksum[-1] }
    }
}

function Assert-TransferFingerprint($Expected, $Actual) {
    $a = @($Expected | ForEach-Object { "$($_.table)|$($_.rows)|$($_.checksum)" })
    $b = @($Actual | ForEach-Object { "$($_.table)|$($_.rows)|$($_.checksum)" })
    if (@(Compare-Object $a $b).Count) { throw 'Database verification failed: table counts or content checksums differ.' }
}

function Test-TransferLoginAuditOnly([string]$MySql, [string]$Cnf, [string]$Database) {
    if ($Database -notmatch '^alshifa[a-z0-9_]*$') { throw 'Unexpected database name.' }
    # Authentication alone is expected after opening a new installation. Never
    # treat audit records for clinical, stock, financial or settings changes as empty.
    $sql = "SELECT COUNT(*) FROM AuditLogs WHERE BINARY COALESCE(Entity,'') <> BINARY 'User' OR BINARY COALESCE(Action,'') NOT IN ('Login','LoginFailed')"
    $result = @(Invoke-Native $MySql @("--defaults-extra-file=$Cnf", '-N', '-B', $Database, '-e', $sql))
    if ($result.Count -ne 1 -or $result[0] -notmatch '^\d+$') { throw 'Could not verify destination audit history.' }
    return ([long]$result[0] -eq 0)
}

function Assert-TransferDestinationFresh($Tables, [bool]$LoginOnlyAuditVerified = $false) {
    $allowed = @('__efmigrationshistory','aspnetroleclaims','aspnetroles','aspnetuserroles','aspnetusers','departments','diagnostictests','ledgeraccounts','services','stocklocations','settings','numbersequences','cashregisters')
    foreach ($table in $Tables) {
        if ($table.table.ToLowerInvariant() -eq 'auditlogs' -and $LoginOnlyAuditVerified) { continue }
        if ($table.rows -gt 0 -and $allowed -notcontains $table.table.ToLowerInvariant()) { throw "Destination has working data in $($table.table). Transfer refused; use an empty, fresh installation." }
    }
}

function Import-TransferSql([string]$MySql, [string]$Cnf, [string]$Database, [string]$File) {
    if ($File -match '[\r\n;]') { throw 'Unsupported SQL file path.' }
    Invoke-Native $MySql @("--defaults-extra-file=$Cnf", '--default-character-set=utf8mb4', $Database, '-e', "source $($File -replace '\\', '/')") | Out-Null
}

function Assert-TransferFiles([string]$Folder, $Files) {
    $base = [IO.Path]::GetFullPath($Folder).TrimEnd('\') + '\'
    foreach ($file in $Files) {
        $path = [IO.Path]::GetFullPath((Join-Path $Folder $file.path))
        if (-not $path.StartsWith($base, [StringComparison]::OrdinalIgnoreCase)) { throw 'Snapshot path is outside its folder.' }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing snapshot file: $($file.path)" }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) { throw "Snapshot file differs: $($file.path)" }
    }
}
