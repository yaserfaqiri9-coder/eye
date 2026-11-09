# Full access for the hospital director from outside the hospital, through Tailscale. See DEPLOYMENT-WINDOWS.md section 6.
# Run elevated on the hospital host, after Tailscale for Windows is installed (tailscale.com/download).
#
#   Enable:  C:\AlShifa\scripts\director-access.ps1
#   Disable: C:\AlShifa\scripts\director-access.ps1 -Disable   (outside access stops at once; the hospital network keeps working)
#
# Nothing here opens a port in the router or the firewall. Only devices signed in to the same Tailscale account reach the system,
# and they still need a user name and password in the program. Funnel (public access) is never used and is checked for.
#Requires -RunAsAdministrator
param(
    [switch]$Disable,
    [string]$Root = 'C:\AlShifa'
)
# The computer keeps the name Tailscale gave it. Renaming it here left Serve's certificate on the old name (seen 2026-11-08),
# so the printed address did not open.

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$logFile = Join-Path $Root 'logs\director-access.log'
function Write-Log([string]$Message) {
    $line = "$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss')) $env:USERNAME $Message"
    Add-Content -Path $logFile -Value $line -Encoding UTF8
    $line
}

$tailscale = @((Join-Path $env:ProgramFiles 'Tailscale\tailscale.exe'), (Get-Command tailscale.exe -ErrorAction SilentlyContinue).Source) |
    Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if (-not $tailscale) { throw 'Tailscale is not installed on this computer. Install Tailscale for Windows from https://tailscale.com/download, then run this again.' }

function Invoke-Tailscale([string[]]$Arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $tailscale @Arguments 2>&1 | ForEach-Object { "$_" }
        return [pscustomobject]@{ Code = $LASTEXITCODE; Output = ($output -join "`n") }
    } finally {
        $ErrorActionPreference = $previous
    }
}

# For steps that may print a link and wait for the person to open it: every line shows at once.
function Invoke-TailscaleLive([string[]]$Arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $tailscale @Arguments 2>&1 | ForEach-Object { Write-Host "$_" }
        return $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previous
    }
}

function Get-TailscaleJson([string[]]$Arguments) {
    try { return (Invoke-Tailscale $Arguments).Output | ConvertFrom-Json } catch { return $null }
}

if ($Disable) {
    $result = Invoke-Tailscale @('serve', 'reset')
    if ($result.Code) { throw "Could not stop outside access: $($result.Output)" }
    Write-Log 'Director access DISABLED: Tailscale Serve removed. Run director-access.ps1 again to turn it back on.'
    exit 0
}

# The address the program listens on for this computer only. The installer may have chosen another port than 5080.
$config = Get-Content (Join-Path $Root 'app\appsettings.Production.json') -Raw | ConvertFrom-Json
$loopback = $config.Kestrel.Endpoints.Loopback.Url
$lanPort = ([Uri]($config.Kestrel.Endpoints.Lan.Url -replace '0\.0\.0\.0', 'localhost')).Port
if (-not $loopback) { throw 'The local address of the program was not found in its settings. Is the system installed in this folder?' }

# The program itself listens on every address on the LAN port, the Tailscale one included, so Serve takes a different port.
# Tailscale Serve offers HTTPS on 443, 8443 and 10000 only.
$servePort = @(8443, 10000) | Where-Object { $_ -ne $lanPort } | Select-Object -First 1

'== Signing in to Tailscale'
$state = Get-TailscaleJson @('status', '--json')
if (-not $state -or $state.BackendState -ne 'Running') {
    'A sign-in page or link will appear. Sign in with the hospital Tailscale account (one with two-step verification).'
    if (Invoke-TailscaleLive @('up', '--unattended')) { throw 'Tailscale sign-in did not finish. See the lines above.' }
} else {
    # Unattended: keeps working after the signed-in Windows user logs off, and after a restart.
    $result = Invoke-Tailscale @('set', '--unattended=true')
    if ($result.Code) { throw "Tailscale settings were not applied: $($result.Output)" }
}

'== Making the system reachable over Tailscale'
'If a link to enable HTTPS appears, open it, approve, and this continues by itself.'
if (Invoke-TailscaleLive @('serve', '--bg', "--https=$servePort", $loopback)) { throw 'Tailscale Serve was not set up. See the lines above.' }

# Funnel would put the system on the public internet. It must never be on.
$serve = Get-TailscaleJson @('serve', 'status', '--json')
if ($serve -and $serve.AllowFunnel -and @($serve.AllowFunnel.PSObject.Properties | Where-Object { $_.Value }).Count) {
    Invoke-Tailscale @('serve', 'reset') | Out-Null
    throw 'Tailscale Funnel (public internet access) was on. Outside access was removed. Turn Funnel off in the Tailscale admin console and run this again.'
}

$state = Get-TailscaleJson @('status', '--json')
$name = "$($state.Self.DNSName)".TrimEnd('.')
if (-not $name) { throw 'Tailscale gave this computer no name. Turn on MagicDNS in the Tailscale admin console (DNS page) and run this again.' }
$address = "https://${name}:$servePort/"

'== Checking'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
try {
    $answer = Invoke-WebRequest "$($address)health" -UseBasicParsing -TimeoutSec 60
    "OK     $address answered $($answer.Content)"
} catch {
    "WARN   $address did not answer from this computer yet ($($_.Exception.Message)). The first certificate can take a minute; try the address from the director's device."
}

Write-Log "Director access ENABLED at $address (program $loopback)"
''
"Address for the director: $address"
'On the director''s phone or laptop: install Tailscale, sign in with the same Tailscale account, open the address above.'
'In the program, give the director a user with the Manager role.'
