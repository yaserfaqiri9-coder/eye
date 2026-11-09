# Run by Install-AlShifa.exe, from the unpacked package. Puts together the existing scripts so a person never has to run them one by one.
# Lines starting with ## are read by the installer window. Everything else goes to its log, so no password may ever be printed here.
param(
    [Parameter(Mandatory)][ValidateSet('Install', 'Update', 'Rollback')][string]$Mode,
    [string]$Root = 'C:\AlShifa',
    [string]$LanIp,
    [string]$BackupDestination,
    [string]$ShortcutName,
    # The folder Install-AlShifa.exe was started from. vc_redist.x64.exe may sit there when it is not inside the package.
    [string]$InstallerFolder,
    # No separate disk was available. Backups stay on this disk, which the installer window warns about.
    [switch]$AllowSameDisk
)

. (Join-Path $PSScriptRoot 'common.ps1')
[Console]::OutputEncoding = [Text.Encoding]::UTF8

# Persian on the command line can arrive as '?' on some systems. The name is then rebuilt from its letters (Sistem Alshifa).
if ($ShortcutName -match '\?') { $ShortcutName = -join [char[]](0x633, 0x6CC, 0x633, 0x62A, 0x645, 0x20, 0x627, 0x644, 0x634, 0x641, 0x627) }

# Taken out of the environment at once, so the other programs started below never inherit it. Only install.ps1 gets it back.
$adminPassword = $env:ALSHIFA_ADMIN_PASSWORD
Remove-Item Env:\ALSHIFA_ADMIN_PASSWORD -ErrorAction SilentlyContinue

$package = Split-Path $PSScriptRoot -Parent
$setupDir = Split-Path $package -Parent
$marker = Join-Path $setupDir 'install-in-progress.txt'
$prerequisites = Join-Path $package 'prerequisites'
$shell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'

# A child process, so an "exit" inside a script cannot end this one and its exit code is seen here. Output arrives line by line.
function Invoke-Script([string]$Name, [string[]]$Arguments, [switch]$AllowFailure) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $shell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot $Name) @Arguments 2>&1 | ForEach-Object { "$_" }
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previous
    }
    if ($code -and -not $AllowFailure) { throw "$Name stopped with exit code $code." }
    if ($AllowFailure) { $script:lastScriptCode = $code }
}

function Install-Package([string]$File, [string[]]$Arguments, [int[]]$Success) {
    # Not -Wait, which also waits for anything the program leaves running, such as a GnuPG agent. Reading Handle keeps ExitCode available afterwards.
    $process = Start-Process $File -ArgumentList $Arguments -PassThru -WindowStyle Hidden
    $null = $process.Handle
    if (-not $process.WaitForExit(600000)) { throw "$(Split-Path $File -Leaf) did not finish within 10 minutes." }
    if ($Success -notcontains $process.ExitCode) { throw "$(Split-Path $File -Leaf) stopped with exit code $($process.ExitCode)." }
    "$(Split-Path $File -Leaf) finished with exit code $($process.ExitCode)"
}

# Inside the package, or beside the installer. A file from beside it came from outside the checked package, so its signature is checked here.
function Find-VisualCppRuntime {
    $packaged = Join-Path $prerequisites 'vc_redist.x64.exe'
    if (Test-Path $packaged) { return $packaged }
    if (-not $InstallerFolder) { return $null }
    $beside = Join-Path $InstallerFolder 'vc_redist.x64.exe'
    if (-not (Test-Path $beside)) { return $null }
    $signature = Get-AuthenticodeSignature $beside
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'CN=Microsoft Corporation') { throw "$beside is not signed by Microsoft. It was not run." }
    return $beside
}

function Set-PrivateNetwork([string]$Address) {
    $interface = (Get-NetIPAddress -IPAddress $Address -ErrorAction SilentlyContinue).InterfaceIndex
    if (-not $interface) { return }
    # The firewall rule covers every network type (see install.ps1). Private still tells Windows this network is trusted.
    try {
        $profile_ = Get-NetConnectionProfile -InterfaceIndex $interface -ErrorAction Stop
        if ($profile_.NetworkCategory -eq 'Public') {
            Set-NetConnectionProfile -InterfaceIndex $interface -NetworkCategory Private -ErrorAction Stop
            'Network profile set to Private'
        }
    } catch {
        "Could not change the network profile: $_"
    }
}

$appIcon = Join-Path $Root 'app\wwwroot\favicon.ico'

$caCertificate = Join-Path $Root 'certs\alshifa-ca.cer'
$launcher = Join-Path $package 'tools\AlShifa.exe'

function Get-CaThumbprint { (New-Object Security.Cryptography.X509Certificates.X509Certificate2($caCertificate)).Thumbprint }

function Add-Shortcut([int]$Port) {
    if (-not $ShortcutName) { return }
    Install-AlShifaLauncher $ShortcutName $launcher $appIcon $Port (Get-CaThumbprint)
}

# What each staff computer needs. None of it is secret: the private key of this certificate was destroyed when it was made.
# The address is only a first guess: AlShifa.exe finds the server again whenever the network changes.
function Add-StaffFolder([int]$Port, [string]$Address) {
    $desktop = [Environment]::GetFolderPath('CommonDesktopDirectory')
    $folder = Join-Path $desktop 'AlShifa-Staff-Connect'
    New-Item -ItemType Directory -Force $folder | Out-Null
    Copy-Item $caCertificate $folder -Force
    Copy-Item (Join-Path $package 'tools\Connect-AlShifa.exe') $folder -Force
    Copy-Item $launcher $folder -Force
    if (Test-Path $appIcon) { Copy-Item $appIcon (Join-Path $folder 'alshifa.ico') -Force }
    [IO.File]::WriteAllText((Join-Path $folder 'connect.ini'), "name=$AlShifaServerName`r`nport=$Port`r`nthumbprint=$(Get-CaThumbprint)`r`nserver=$env:COMPUTERNAME`r`naddress=$Address`r`n")
    "##STAFF $folder"
}

function Test-Everything([string]$Url, [string]$HealthUrl) {
    '##STEP health'
    Invoke-Script 'health-check.ps1' @('-Root', $Root, '-HealthUrl', $HealthUrl) -AllowFailure
    if ($script:lastScriptCode) { '##HEALTH attention' }

    # The address staff use, through the real certificate. If this fails, staff would see a warning or nothing.
    if ($Url) {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        try {
            $answer = Invoke-WebRequest "$($Url.TrimEnd('/'))/health" -UseBasicParsing -TimeoutSec 20
            "OK     HTTPS for staff       $Url answered $($answer.Content)"
        } catch {
            "FAIL   HTTPS for staff       $Url : $_"
            '##HEALTH attention'
        }
    }

    $voiceDir = Join-Path $Root 'storage\voice'
    $bundledVoice = Join-Path $package 'voice'
    if (Test-Path $bundledVoice) {
        Invoke-Script 'build-voice-pack.ps1' @('-Mode', 'Import', '-SourceDir', $bundledVoice, '-VoiceDir', $voiceDir) -AllowFailure
    } else {
        Invoke-Script 'build-voice-pack.ps1' @('-Mode', 'Check', '-VoiceDir', $voiceDir) -AllowFailure
    }
    if ($script:lastScriptCode) { '##VOICE pending' }
}

try {
    if ($Mode -eq 'Rollback') {
        if (-not (Test-Path $marker)) { throw 'Nothing to undo: no unfinished install is recorded on this computer.' }
        Invoke-Script 'uninstall.ps1' @('-Root', $Root, '-MoveFolderAside')
        Remove-Item $marker -Force
        '##RESULT rolled-back'
        exit 0
    }

    if ($Mode -eq 'Update') {
        # The destination chosen at install time. If it is not connected, update.ps1 stops before changing anything.
        $task = Get-ScheduledTask -TaskName 'AlShifa Backup' -ErrorAction SilentlyContinue
        $match = if ($task) { [regex]::Match($task.Actions[0].Arguments, '-Destination "([^"]+)"') }
        if (-not $match -or -not $match.Success) { throw 'The backup destination of this installation was not found, so nothing was changed.' }
        $updateArguments = @('-BackupDestination', $match.Groups[1].Value, '-Root', $Root)
        if ($task.Actions[0].Arguments -match '-AllowSameDisk') { $updateArguments += '-AllowSameDisk' }
        Invoke-Script 'update.ps1' $updateArguments

        $config = Get-Content (Join-Path $Root 'app\appsettings.Production.json') -Raw | ConvertFrom-Json
        $loopback = $config.Kestrel.Endpoints.Loopback.Url
        Test-Everything '' "$loopback/health"
        # As install.ps1 now makes it: another router must not need anyone to mark the network Private.
        Get-NetFirewallRule -DisplayName 'Al-Shifa HTTPS (hospital LAN)' -ErrorAction SilentlyContinue | Set-NetFirewallRule -Profile Any
        $shortcut = Get-AlShifaShortcut
        $launcherSettings = Join-Path $AlShifaProgramFolder 'alshifa.ini'
        if (Test-Path $launcherSettings) {
            # Installed with AlShifa.exe: the new version of it for this computer and in the staff folder.
            $port = [int]((Get-Content $launcherSettings | Where-Object { $_ -like 'port=*' }) -replace '^port=', '')
            $name = if ($shortcut) { $shortcut.Name } else { $ShortcutName }
            if ($name) { Install-AlShifaLauncher $name $launcher $appIcon $port (Get-CaThumbprint) }
            Add-StaffFolder $port ''
            "##URL https://127.0.0.1$(if ($port -ne 443) { ":$port" })/"
        } elseif ($shortcut -and $shortcut.Url) {
            # Installed before AlShifa.exe: its certificate names only the address, so its shortcut keeps that address.
            Set-AlShifaShortcut $shortcut.Name $shortcut.Url $appIcon
            "##URL $($shortcut.Url)"
        }
        '##RESULT updated'
        exit 0
    }

    # A fresh install from here on. Nothing below may run over an existing one.
    if (Get-Service 'AlShifaApi', 'AlShifaMySQL' -ErrorAction SilentlyContinue) { throw 'The Al-Shifa services already exist. Nothing was changed.' }

    $sameDisk = @(if ($AllowSameDisk) { '-AllowSameDisk' })

    '##STEP check'
    # Chosen once, here, and only ever on a fresh install. They end up in the settings, the MySQL configuration, the firewall rule and connect.ini, and
    # nothing reads them from anywhere else or chooses again, so a restart or an update cannot move them. Programs already on these ports are left alone.
    # 8443 and up, because browsers refuse some ports outright. MySQL is bound to 127.0.0.1 by Get-MySqlIni whichever port it gets.
    $httpsCandidates = @(8443, 9443, 10443, 11443, 12443, 18443)
    if (-not (Test-WebServerPresent)) { $httpsCandidates = @(443) + $httpsCandidates }
    $HttpsPort = Select-FreePort $httpsCandidates 'HTTPS'
    $LoopbackPort = Select-FreePort (5080..5099) 'the local health check'
    $MySqlPort = Select-FreePort (3307..3330) 'MySQL'
    "##PORTS https=$HttpsPort loopback=$LoopbackPort mysql=$MySqlPort"
    $ports = @('-HttpsPort', $HttpsPort, '-LoopbackPort', $LoopbackPort, '-MySqlPort', $MySqlPort)

    Invoke-Script 'preflight.ps1' (@('-LanIp', $LanIp, '-BackupDestination', $BackupDestination, '-Root', $Root, '-BeforePrerequisites') + $ports + $sameDisk)

    $mysqlWork = Join-Path $setupDir 'mysql'
    if (Test-Path $mysqlWork) { Remove-Item $mysqlWork -Recurse -Force }
    $zip = Get-ChildItem $prerequisites -Filter 'mysql-*-winx64.zip' | Select-Object -First 1
    [IO.Compression.ZipFile]::ExtractToDirectory($zip.FullName, $mysqlWork)
    $mysqlFolder = (Get-ChildItem $mysqlWork -Directory | Select-Object -First 1).FullName

    # MySQL running is the real test of whether this laptop has the Visual C++ runtime, whatever the registry says.
    $mysqlRuns = $true
    try { Invoke-Native (Join-Path $mysqlFolder 'bin\mysqld.exe') @('--version') | Out-Null } catch { $mysqlRuns = $false }
    $runtime = if (-not $mysqlRuns) { Find-VisualCppRuntime }
    if (-not $mysqlRuns -and -not $runtime) {
        '##ERROR vc-runtime-missing'
        exit 1
    }

    # Recorded before the first change. Only this marker lets the installer offer to undo things.
    New-Item -ItemType File -Force $marker | Out-Null

    '##STEP deps'
    Set-PrivateNetwork $LanIp
    if ($runtime) {
        # 1638: a newer version is already installed. 3010: installed, a restart is wanted later; MySQL still runs.
        Install-Package $runtime @('/install', '/quiet', '/norestart') @(0, 1638, 3010)
    }
    $gpgFound = $false
    try { Find-Gpg | Out-Null; $gpgFound = $true } catch { }
    if (-not $gpgFound) { Install-Package (Get-ChildItem $prerequisites -Filter 'gpg4win-*.exe' | Select-Object -First 1).FullName @('/S') @(0) }

    Invoke-Script 'preflight.ps1' (@('-LanIp', $LanIp, '-MySqlZipFolder', $mysqlFolder, '-BackupDestination', $BackupDestination, '-Root', $Root) + $ports + $sameDisk)

    if ($AllowSameDisk) {
        # A new folder at the root of C: is open to ordinary users, who could delete the backups even though they cannot read them.
        New-Item -ItemType Directory -Force $BackupDestination | Out-Null
        Protect-Path $BackupDestination
    }

    $env:ALSHIFA_ADMIN_PASSWORD = $adminPassword
    try {
        Invoke-Script 'install.ps1' (@('-LanIp', $LanIp, '-MySqlZipFolder', $mysqlFolder, '-BackupDestination', $BackupDestination, '-Root', $Root) + $ports + $sameDisk)
    } finally {
        Remove-Item Env:\ALSHIFA_ADMIN_PASSWORD -ErrorAction SilentlyContinue
    }
    Remove-Item $mysqlWork -Recurse -Force -ErrorAction SilentlyContinue

    '##STEP shortcut'
    $url = "https://$LanIp$(if ($HttpsPort -ne 443) { ":$HttpsPort" })/"
    # So the browser on this laptop opens the system without a warning too.
    Import-Certificate -FilePath (Join-Path $Root 'certs\alshifa-ca.cer') -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
    Add-Shortcut $HttpsPort
    Add-StaffFolder $HttpsPort $LanIp
    "##URL $url"

    Test-Everything $url "http://127.0.0.1:$LoopbackPort/health"

    if (Test-Path $marker) { Remove-Item $marker -Force }
    '##RESULT installed'
    exit 0
} catch {
    "##ERROR $_"
    exit 1
}
