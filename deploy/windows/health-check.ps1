# One-page status of the hospital host. Safe to run at any time; it changes nothing. Exit code 1 means something needs attention.
param(
    [string]$Root = 'C:\AlShifa',
    # Without it, the local address this installation was given is read from its settings. The installer may have chosen another port.
    [string]$HealthUrl
)

$ErrorActionPreference = 'Stop'
$problems = 0
if (-not $HealthUrl) {
    try {
        $HealthUrl = "$((Get-Content (Join-Path $Root 'app\appsettings.Production.json') -Raw | ConvertFrom-Json).Kestrel.Endpoints.Loopback.Url)/health"
    } catch {
        $HealthUrl = 'http://127.0.0.1:5080/health'
    }
}
function Report([string]$Name, [bool]$Ok, [string]$Detail) {
    '{0,-6} {1,-22} {2}' -f $(if ($Ok) { 'OK' } else { 'FAIL' }), $Name, $Detail
    if (-not $Ok) { $script:problems++ }
}

foreach ($name in 'AlShifaMySQL', 'AlShifaApi') {
    $service = Get-Service $name -ErrorAction SilentlyContinue
    Report "Service $name" ($service.Status -eq 'Running') $(if ($service) { "$($service.Status), start: $($service.StartType)" } else { 'not installed' })
}

try {
    $response = Invoke-WebRequest $HealthUrl -UseBasicParsing -TimeoutSec 10
    Report 'API and database' ($response.Content -eq 'Healthy') $response.Content
} catch {
    Report 'API and database' $false "$_"
}

$drive = Get-PSDrive ([IO.Path]::GetPathRoot($Root).Substring(0, 1))
$freeGb = [Math]::Round($drive.Free / 1GB, 1)
Report 'Disk space' ($freeGb -ge 10) "$freeGb GB free"

try {
    $status = Get-Content (Join-Path $Root 'logs\backup-status.json') -Raw | ConvertFrom-Json
    $age = if ($status.lastSuccessAt) { ((Get-Date) - [DateTime]$status.lastSuccessAt).TotalHours } else { [double]::MaxValue }
    Report 'Last backup' ($age -le 24 -and -not $status.lastError) "last success: $($status.lastSuccessAt)  error: $($status.lastError)"
} catch {
    Report 'Last backup' $false 'no backup status found'
}

try {
    $password = (Get-Content (Join-Path $Root 'app\appsettings.Production.json') -Raw | ConvertFrom-Json).Kestrel.Endpoints.Lan.Certificate.Password
    # EphemeralKeySet: only the expiry date is needed, so the private key is not written to a key store.
    $certificate = New-Object Security.Cryptography.X509Certificates.X509Certificate2((Join-Path $Root 'certs\server.pfx'), $password, [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    Report 'HTTPS certificate' ($certificate.NotAfter -gt (Get-Date).AddDays(60)) "valid until $($certificate.NotAfter.ToString('yyyy-MM-dd'))"
} catch {
    Report 'HTTPS certificate' $false 'could not read the certificate (run as Administrator)'
}

$tailscale = Get-Service Tailscale -ErrorAction SilentlyContinue
"INFO   Remote access          Tailscale service: $(if ($tailscale) { $tailscale.Status } else { 'not installed' }) (never needed for hospital work)"

if ($problems) { exit 1 }
