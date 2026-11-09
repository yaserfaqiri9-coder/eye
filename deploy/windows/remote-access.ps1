# Online access to reports through Cloudflare Tunnel and Cloudflare Access. See DEPLOYMENT-WINDOWS.md section 7.
# Run elevated on the hospital host, and only after the hospital approved online access and the Access application exists.
#
#   Enable:  remote-access.ps1 -ApprovedBy "<name, date>" -Hostname reports.example.org -TunnelId <uuid> -CredentialsFile <uuid>.json `
#              -TeamDomain <team>.cloudflareaccess.com -Audience <AUD tag> -Cloudflared C:\path\cloudflared.exe
#   Disable: remote-access.ps1 -Disable      (stops the tunnel at once; the hospital network keeps working)
#
# Nothing here opens a port: cloudflared only makes outgoing connections, and the application listens for it on 127.0.0.1 only.
#Requires -RunAsAdministrator
[CmdletBinding(DefaultParameterSetName = 'Enable')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Enable')][string]$ApprovedBy,
    [Parameter(Mandatory, ParameterSetName = 'Enable')][string]$Hostname,
    [Parameter(Mandatory, ParameterSetName = 'Enable')][guid]$TunnelId,
    [Parameter(Mandatory, ParameterSetName = 'Enable')][string]$CredentialsFile,
    [Parameter(Mandatory, ParameterSetName = 'Enable')][string]$TeamDomain,
    [Parameter(Mandatory, ParameterSetName = 'Enable')][string]$Audience,
    [Parameter(Mandatory, ParameterSetName = 'Enable')][string]$Cloudflared,
    [Parameter(Mandatory, ParameterSetName = 'Disable')][switch]$Disable,
    [int]$RemotePort = 5081,
    [string]$Root = 'C:\AlShifa'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$serviceName = 'Cloudflared'
$logFile = Join-Path $Root 'logs\remote-access.log'
function Write-Log([string]$Message) {
    $line = "$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss')) $env:USERNAME $Message"
    Add-Content -Path $logFile -Value $line -Encoding UTF8
    $line
}

if ($Disable) {
    if (Get-Service $serviceName -ErrorAction SilentlyContinue) {
        Stop-Service $serviceName -Force
        Set-Service $serviceName -StartupType Disabled
    }
    Write-Log 'Online access DISABLED: tunnel service stopped and set to Disabled. Re-run with the Enable parameters to turn it back on.'
    return
}

$appDir = Join-Path $Root 'app'
$config = Join-Path $appDir 'appsettings.Production.json'
if (-not (Test-Path $config)) { throw "$config is missing. Run install.ps1 first." }
if ($TeamDomain -notmatch '^[a-z0-9-]+\.cloudflareaccess\.com$') { throw "TeamDomain must look like <team>.cloudflareaccess.com, not '$TeamDomain'." }
if ($Hostname -notmatch '^[a-z0-9-]+(\.[a-z0-9-]+)+$') { throw "Hostname '$Hostname' is not a plain host name." }
if ($Audience -notmatch '^[0-9a-f]{64}$') { throw 'Audience must be the 64-character AUD tag of the Access application.' }
if (-not (Test-Path $CredentialsFile)) { throw "Tunnel credentials file $CredentialsFile not found." }
"cloudflared: $((Invoke-Native $Cloudflared @('--version')) -join ' ')"

$settings = Get-Content $config -Raw | ConvertFrom-Json
$alreadyOurs = $settings.RemoteAccess -and $settings.RemoteAccess.Port -eq $RemotePort
if (-not $alreadyOurs -and (Get-NetTCPConnection -State Listen -LocalPort $RemotePort -ErrorAction SilentlyContinue)) { throw "Port $RemotePort is already in use. Choose another with -RemotePort." }

'== Files'
$tunnelDir = Join-Path $Root 'cloudflared'
New-Item -ItemType Directory -Force $tunnelDir | Out-Null
# Administrators and SYSTEM only: the credentials let anyone run this tunnel.
Protect-Path $tunnelDir
if (Get-Service $serviceName -ErrorAction SilentlyContinue) { Stop-Service $serviceName -Force }
Copy-Item $Cloudflared (Join-Path $tunnelDir 'cloudflared.exe') -Force
$credentials = Join-Path $tunnelDir "$TunnelId.json"
Copy-Item $CredentialsFile $credentials -Force
$tunnelConfig = Join-Path $tunnelDir 'config.yml'
$forward = { param($path) $path -replace '\\', '/' }
# One host name, sent only to the application's tunnel port. Anything else Cloudflare might send is answered with 404 here.
@"
tunnel: $TunnelId
credentials-file: $(& $forward $credentials)
logfile: $(& $forward (Join-Path $Root 'logs\cloudflared.log'))
loglevel: info
no-autoupdate: true
ingress:
  - hostname: $Hostname
    service: http://127.0.0.1:$RemotePort
  - service: http_status:404
"@ | Set-Content $tunnelConfig -Encoding Ascii
Invoke-Native (Join-Path $tunnelDir 'cloudflared.exe') @('tunnel', '--config', $tunnelConfig, 'ingress', 'validate') | Out-Null

'== Application settings'
Copy-Item $config "$config.before-remote-access" -Force
$settings.Kestrel.Endpoints | Add-Member -Force -NotePropertyName Remote -NotePropertyValue ([pscustomobject]@{ Url = "http://127.0.0.1:$RemotePort" })
$settings | Add-Member -Force -NotePropertyName RemoteAccess -NotePropertyValue ([pscustomobject]@{ Port = $RemotePort; TeamDomain = $TeamDomain; Audience = $Audience })
$settings | ConvertTo-Json -Depth 10 | Set-Content $config -Encoding UTF8
Restart-Service AlShifaApi
Start-Sleep -Seconds 5

# The application itself must refuse a request without an Access token. If it does not, online access is not turned on.
$status = try { (Invoke-WebRequest "http://127.0.0.1:$RemotePort/api/auth/csrf" -UseBasicParsing -TimeoutSec 10).StatusCode } catch { [int]$_.Exception.Response.StatusCode }
if ($status -ne 403) {
    Copy-Item "$config.before-remote-access" $config -Force
    Restart-Service AlShifaApi
    throw "Self-check failed: the tunnel port answered $status instead of 403 without an Access token. Settings were restored; nothing was exposed."
}
'Self-check OK: the tunnel port refuses requests without a Cloudflare Access token.'

'== Tunnel service'
if (-not (Get-Service $serviceName -ErrorAction SilentlyContinue)) {
    try {
        Invoke-Native (Join-Path $tunnelDir 'cloudflared.exe') @('service', 'install') | Out-Null
    } catch {
        # Some versions refuse without a configuration in their default folder. The command line is set just below in either case.
        New-Service -Name $serviceName -BinaryPathName "`"$(Join-Path $tunnelDir 'cloudflared.exe')`"" -DisplayName 'Cloudflared agent' -StartupType Automatic | Out-Null
    }
}
# As Cloudflare documents for a locally managed tunnel on Windows: the service runs this exact configuration.
Set-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" -Name ImagePath `
    -Value "`"$(Join-Path $tunnelDir 'cloudflared.exe')`" --config `"$tunnelConfig`" tunnel run"
Set-Service $serviceName -StartupType Automatic
Invoke-Native 'sc.exe' @('failure', $serviceName, 'reset=', '86400', 'actions=', 'restart/10000/restart/30000/restart/60000') | Out-Null
Start-Service $serviceName

# Cloudflare Access must stand in front: an unauthenticated visit has to be sent to the Access sign-in page.
# Windows PowerShell returns a redirect either as a response or as an error, depending on the case, so both are read.
$verdict = 'not reachable yet'
foreach ($attempt in 1..12) {
    Start-Sleep -Seconds 5
    $response = try { Invoke-WebRequest "https://$Hostname/" -UseBasicParsing -MaximumRedirection 0 -TimeoutSec 10 -ErrorAction Stop } catch { $_.Exception.Response }
    if (-not $response) { continue }
    $code = [int]$response.StatusCode
    if ($code -in 301, 302, 303, 307, 308) {
        $location = "$($response.Headers['Location'])"
        $verdict = if ($location -match '^https://[a-z0-9-]+\.cloudflareaccess\.com/') { 'protected' } else { "redirects to $location" }
    } else {
        $verdict = "answered $code without sign-in"
    }
    break
}

switch -Wildcard ($verdict) {
    'protected' {
        Write-Log "Online access ENABLED for https://$Hostname/ (tunnel $TunnelId, Access team $TeamDomain). Approved by: $ApprovedBy"
    }
    'not reachable yet' {
        Write-Log "Tunnel service started for $Hostname, but the name does not answer yet (DNS route missing or not propagated). Approved by: $ApprovedBy"
        'Finish the DNS route (section 7), then run this script again. The application refuses everything without an Access token meanwhile.'
    }
    default {
        Stop-Service $serviceName -Force
        Set-Service $serviceName -StartupType Disabled
        Write-Log "Tunnel STOPPED: https://$Hostname/ $verdict, so Cloudflare Access is not in front of it."
        throw "https://$Hostname/ $verdict. Cloudflare Access is not protecting this name, so the tunnel was stopped. Create the Access application (section 7), then run again."
    }
}
