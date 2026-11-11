# Run on the laptop before install.ps1. Reads only; it installs and changes nothing. Exit code 1 means fix the FAIL lines first.
param(
    [string]$LanIp,
    [string]$MySqlZipFolder,
    [string]$BackupDestination,
    [string]$Root = 'C:\AlShifa',
    [int]$HttpsPort = 443,
    [int]$LoopbackPort = 5080,
    [int]$MySqlPort = 3307,
    # For the installer's first look, before it has installed GnuPG and set the network profile itself.
    [switch]$BeforePrerequisites,
    # The person installing has no separate disk. Reported as a warning, not a stop.
    [switch]$AllowSameDisk
)

. (Join-Path $PSScriptRoot 'common.ps1')
$ErrorActionPreference = 'Continue'

$failures = 0
function Report([string]$Level, [string]$Name, [string]$Detail) {
    '{0,-5} {1,-24} {2}' -f $Level, $Name, $Detail
    if ($Level -eq 'FAIL') { $script:failures++ }
}
function Check([bool]$Ok, [string]$Name, [string]$Detail, [string]$Otherwise = 'FAIL') {
    Report $(if ($Ok) { 'OK' } else { $Otherwise }) $Name $Detail
}

$os = Get-CimInstance Win32_OperatingSystem
Check ([Environment]::Is64BitOperatingSystem -and [int]$os.BuildNumber -ge 17763) 'Windows' "$($os.Caption) build $($os.BuildNumber)"
$memoryGb = [Math]::Round($os.TotalVisibleMemorySize / 1MB, 1)
Check ($memoryGb -ge 7.5) 'Memory' "$memoryGb GB (8 GB recommended)" 'WARN'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Check $isAdmin 'Administrator' 'install.ps1 needs an elevated PowerShell' 'WARN'

$rootEmpty = -not (Test-Path $Root) -or -not (Get-ChildItem $Root -Force -ErrorAction SilentlyContinue | Select-Object -First 1)
Check ($rootEmpty -and $Root -notmatch '\s') 'Install folder' "$Root must be empty and have no spaces"
$drive = Get-PSDrive ([IO.Path]::GetPathRoot($Root).Substring(0, 1)) -ErrorAction SilentlyContinue
$freeGb = if ($drive) { [Math]::Round($drive.Free / 1GB, 1) } else { 0 }
Check ($freeGb -ge 20) 'Disk space' "$freeGb GB free on the install drive (20 GB needed)"

foreach ($port in $HttpsPort, $LoopbackPort, $MySqlPort) {
    $owner = Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue | Select-Object -First 1
    Check (-not $owner) "Port $port" $(if ($owner) { "in use by process $($owner.OwningProcess)" } else { 'free' })
}

if ($LanIp) {
    $address = Get-NetIPAddress -IPAddress $LanIp -ErrorAction SilentlyContinue
    Check ([bool]$address) 'LAN address' $(if ($address) { "$LanIp on $($address.InterfaceAlias)" } else { "$LanIp is not on this laptop. Check the router reservation." })
    if ($address) {
        $category = (Get-NetConnectionProfile -InterfaceIndex $address.InterfaceIndex -ErrorAction SilentlyContinue).NetworkCategory
        # The firewall rule covers every network type, so another router needs nothing. Public only means Windows trusts the network less.
        Check ($category -ne 'Public') 'Network profile' "$category (Private is better for the hospital network)" 'WARN'
        Check ($address.InterfaceAlias -notmatch 'Wi-?Fi|Wireless') 'Connection type' "$($address.InterfaceAlias) (a cable is more reliable for the host)" 'WARN'
    }
} else {
    Report 'SKIP' 'LAN address' 'pass -LanIp'
}

# A VPN can capture traffic or break the LAN path. Tailscale is left out because other programs on the laptop may use it.
$tunnels = Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object { $_.Status -eq 'Up' -and $_.Name -notmatch 'Tailscale' -and $_.InterfaceDescription -match 'TAP|TUN|Wintun|VPN|WireGuard' }
Check (-not $tunnels) 'Other VPNs' $(if ($tunnels) { "turn off: $(($tunnels.Name) -join ', ')" } else { 'none active' }) 'WARN'

if ($MySqlZipFolder) {
    $mysqld = Join-Path $MySqlZipFolder 'bin\mysqld.exe'
    # Also proves the Visual C++ runtime that MySQL needs is present.
    try { Check $true 'MySQL files' ((Invoke-Native $mysqld @('--version')) -join ' ') }
    catch { Report 'FAIL' 'MySQL files' "$mysqld does not run. Missing folder or Visual C++ Redistributable? $_" }
} else {
    Report 'SKIP' 'MySQL files' 'pass -MySqlZipFolder'
}

if ($BackupDestination) {
    $backupRoot = [IO.Path]::GetPathRoot($BackupDestination)
    if (-not (Test-Path $backupRoot)) {
        Report 'FAIL' 'Backup disk' "$BackupDestination is not connected"
    } else {
        try { Check (-not (Test-SameDisk $BackupDestination $Root)) 'Backup disk' "$BackupDestination must be on a different physical disk from $Root" $(if ($AllowSameDisk) { 'WARN' } else { 'FAIL' }) }
        catch { Report 'WARN' 'Backup disk' "Could not compare disks: $_" }
    }
} else {
    Report 'SKIP' 'Backup disk' 'pass -BackupDestination'
}

# A real round trip with this computer's GnuPG, on a harmless temporary file.
$probe = Join-Path $env:TEMP "alshifa-preflight-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $probe | Out-Null
try {
    if ($BeforePrerequisites) {
        Report 'SKIP' 'GnuPG' 'the installer adds it next'
    } else {
        $gpg = Find-Gpg
        [IO.File]::WriteAllText("$probe\password.txt", (New-PrintablePassword))
        Set-Content "$probe\plain.txt" 'preflight'
        $ErrorActionPreference = 'Stop'
        Protect-BackupFile $gpg "$probe\plain.txt" "$probe\plain.gpg" "$probe\password.txt" $probe
        Unprotect-BackupFile $gpg "$probe\plain.gpg" "$probe\back.txt" "$probe\password.txt" $probe
        Check ((Get-FileHash "$probe\plain.txt").Hash -eq (Get-FileHash "$probe\back.txt").Hash) 'GnuPG' "encrypt and decrypt work: $gpg"
    }
} catch {
    Report 'FAIL' 'GnuPG' "$_"
} finally {
    $ErrorActionPreference = 'Continue'
    Remove-Item $probe -Recurse -Force -ErrorAction SilentlyContinue
}

try {
    $bitLocker = Get-BitLockerVolume -MountPoint ([IO.Path]::GetPathRoot($Root).TrimEnd('\')) -ErrorAction Stop
    Check ($bitLocker.ProtectionStatus -eq 'On') 'Disk encryption' "BitLocker is $($bitLocker.ProtectionStatus). Without it, a stolen laptop exposes patient records." 'WARN'
} catch {
    Report 'WARN' 'Disk encryption' 'Could not read BitLocker status (needs Administrator, or this edition has no BitLocker)'
}

''
if ($failures) { "$failures problem(s) to fix before install.ps1."; exit 1 }
'Nothing blocks install.ps1. Read any WARN lines.'
