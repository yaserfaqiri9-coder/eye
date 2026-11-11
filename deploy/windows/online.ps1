# Connects this hospital server to the online read-only copy (https://shafa.novatechsoft.com) and to the offsite backup folder there.
# Hospital work never depends on it: without internet, changes wait and leave when the connection is back.
#
#   Enable:  C:\AlShifa\scripts\online.ps1 -SecretsFolder E:\AlShifa-Online-Secrets
#            The folder holds sync-key.txt and, for the offsite backup, offsite_ed25519 and offsite_known_hosts (prepared on the online server).
#            Delete it from the USB stick afterwards.
#   Or:      online.ps1 -SyncKey <value> [-OffsiteHost 157.173.109.255 -OffsiteHostKey "<line from ssh-keyscan>"]
#   Disable: C:\AlShifa\scripts\online.ps1 -Disable
#
# Also removes what the old remote access left behind: the Cloudflare tunnel this system installed and the Tailscale Serve entry
# pointing at this system. Tailscale itself and anything else it serves are left alone; other programs may use them.
#Requires -RunAsAdministrator
param(
    [string]$Url = 'https://shafa.novatechsoft.com',
    # At least 32 characters. Without one, a new key is made and printed once, to be set online as Mirror__SyncKey.
    [string]$SyncKey,
    # A file holding the key (sync-key.txt from the online server). Delete it from the USB stick afterwards.
    [string]$SyncKeyFile,
    [int]$IntervalMinutes = 5,
    [string]$OffsiteHost,
    [string]$OffsiteUser = 'alshifa-backup',
    [int]$OffsitePort = 22,
    [string]$OffsiteDir = '/upload',
    # The server's host key as ssh-keyscan prints it, checked by the operator against the server console. Pinned: no other server is trusted.
    [string]$OffsiteHostKey,
    # A private key made beforehand whose public half is already on the online server. Without it, a new one is made here.
    [string]$OffsiteKeyFile,
    [string]$SecretsFolder,
    [switch]$Disable,
    [string]$Root = 'C:\AlShifa'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

if ($SecretsFolder) {
    if (-not (Test-Path (Join-Path $SecretsFolder 'sync-key.txt'))) { throw "$SecretsFolder has no sync-key.txt." }
    $SyncKeyFile = Join-Path $SecretsFolder 'sync-key.txt'
    if (Test-Path (Join-Path $SecretsFolder 'offsite_ed25519')) {
        $OffsiteKeyFile = Join-Path $SecretsFolder 'offsite_ed25519'
        $OffsiteHostKey = (Get-Content (Join-Path $SecretsFolder 'offsite_known_hosts') -Raw).Trim()
        if (-not $OffsiteHost) { $OffsiteHost = ($OffsiteHostKey -split '\s+')[0] }
    }
}

$configFile = Join-Path $Root 'app\appsettings.Production.json'
$secretsDir = Join-Path $Root 'secrets'
if (-not (Test-Path $configFile)) { throw "$configFile is missing. Is the system installed in $Root?" }
$config = Get-Content $configFile -Raw | ConvertFrom-Json

'== Old remote access'
# The tunnel remote-access.ps1 installed, recognised by its program folder, never another Cloudflare installation.
$tunnel = Get-CimInstance Win32_Service -Filter "Name='Cloudflared'" -ErrorAction SilentlyContinue
if ($tunnel -and $tunnel.PathName -like "*$(Join-Path $Root 'cloudflared')\*") {
    Stop-Service Cloudflared -Force -ErrorAction SilentlyContinue
    & sc.exe delete Cloudflared | Out-Null
    'Removed: Cloudflare tunnel service'
}

# Only the Serve entry that forwards to this system's loopback port is switched off.
$tailscale = @((Get-Command tailscale.exe -ErrorAction SilentlyContinue).Source, (Join-Path $env:ProgramFiles 'Tailscale\tailscale.exe')) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
$loopbackPort = if ($config.Kestrel.Endpoints.Loopback.Url) { ([Uri]$config.Kestrel.Endpoints.Loopback.Url).Port } else { 5080 }
if ($tailscale) {
    $status = (& $tailscale serve status 2>&1 | Out-String)
    $port = $null
    foreach ($line in $status -split "`r?`n") {
        if ($line -match '^https://[^\s:/]+(?::(\d+))?') { $port = if ($Matches[1]) { $Matches[1] } else { '443' } }
        if ($port -and $line -match "proxy http://(127\.0\.0\.1|localhost):$loopbackPort\b") {
            & $tailscale serve "--https=$port" off 2>&1 | Out-Null
            "Removed: Tailscale Serve on port $port (Tailscale itself stays installed)"
            $port = $null
        }
    }
}

foreach ($old in 'director-access.ps1', 'remote-access.ps1') {
    Remove-Item (Join-Path $Root "scripts\$old") -Force -ErrorAction SilentlyContinue
}

if ($config.PSObject.Properties['RemoteAccess']) { $config.PSObject.Properties.Remove('RemoteAccess') }

if ($Disable) {
    if ($config.PSObject.Properties['Sync']) { $config.PSObject.Properties.Remove('Sync') }
    Remove-Item (Join-Path $secretsDir 'offsite-backup.json') -Force -ErrorAction SilentlyContinue
    $config | ConvertTo-Json -Depth 8 | Set-Content $configFile -Encoding UTF8
    Restart-Service AlShifaApi
    'Online copy and offsite backup are off. Hospital work is not affected.'
    exit 0
}

'== Online copy'
if ($SyncKeyFile) { $SyncKey = (Get-Content $SyncKeyFile -Raw).Trim() }
$printKey = $false
if (-not $SyncKey) {
    $SyncKey = -join ((1..48) | ForEach-Object { 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'[[Security.Cryptography.RandomNumberGenerator]::GetInt32(57)] })
    $printKey = $true
}
if ($SyncKey.Length -lt 32) { throw 'The sync key must have at least 32 characters.' }
if (-not $Url.StartsWith('https://')) { throw 'The online address must start with https://' }

$sync = [pscustomobject]@{ Url = $Url; Key = $SyncKey; IntervalMinutes = $IntervalMinutes }
if ($config.PSObject.Properties['Sync']) { $config.Sync = $sync } else { $config | Add-Member -NotePropertyName Sync -NotePropertyValue $sync }
$config | ConvertTo-Json -Depth 8 | Set-Content $configFile -Encoding UTF8
"Sync to $Url every $IntervalMinutes minutes."

if ($OffsiteHost) {
    '== Offsite backup copy'
    if (-not $OffsiteHostKey) { throw 'Give -OffsiteHostKey: the server key line from ssh-keyscan, checked against the server.' }
    $keyFile = Join-Path $secretsDir 'offsite_ed25519'
    if ($OffsiteKeyFile) {
        Copy-Item $OffsiteKeyFile $keyFile -Force
        Remove-Item "$keyFile.pub" -Force -ErrorAction SilentlyContinue
    } elseif (-not (Test-Path $keyFile)) {
        & (Join-Path $env:SystemRoot 'System32\OpenSSH\ssh-keygen.exe') -q -t ed25519 -N '""' -C 'alshifa-hospital-backup' -f $keyFile
        if ($LASTEXITCODE) { throw 'ssh-keygen failed.' }
    }
    # OpenSSH refuses a private key others can read.
    Protect-Path $secretsDir
    Set-Content (Join-Path $secretsDir 'offsite_known_hosts') $OffsiteHostKey.Trim() -Encoding Ascii
    [ordered]@{ host = $OffsiteHost; user = $OffsiteUser; port = $OffsitePort; remoteDir = $OffsiteDir } | ConvertTo-Json |
        Set-Content (Join-Path $secretsDir 'offsite-backup.json') -Encoding UTF8
    if (Test-Path "$keyFile.pub") {
        ''
        'Add this line to the online server (deploy/linux/offsite-backup-account.sh does it):'
        Get-Content "$keyFile.pub"
    } else {
        # The key was prepared with the online server: check the connection now, as the nightly backup will use it.
        $batch = Join-Path $env:TEMP "alshifa-sftp-$([Guid]::NewGuid().ToString('N')).txt"
        [IO.File]::WriteAllLines($batch, @("ls -1 `"$OffsiteDir`""), (New-Object System.Text.UTF8Encoding $false))
        try {
            Invoke-Native (Join-Path $env:SystemRoot 'System32\OpenSSH\sftp.exe') @('-b', $batch, '-P', "$OffsitePort", '-i', $keyFile,
                '-o', "UserKnownHostsFile=$(Join-Path $secretsDir 'offsite_known_hosts')", '-o', 'StrictHostKeyChecking=yes',
                '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=30', "$OffsiteUser@$OffsiteHost") | Out-Null
            'Offsite backup folder reached: OK'
        } catch {
            "Offsite backup folder not reached now ($_). Nightly backups stay local until it is; Settings > Backup shows the result."
        } finally {
            Remove-Item $batch -Force -ErrorAction SilentlyContinue
        }
    }
}

Restart-Service AlShifaApi
''
if ($printKey) {
    'New sync key. Set it on the online server as Mirror__SyncKey in /etc/alshifa/mirror.env; it is not shown again:'
    $SyncKey
}
'Status: Settings > Backup in the program shows the last sync and any waiting changes.'
