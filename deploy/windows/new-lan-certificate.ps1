# Creates the HTTPS certificate for the hospital network: a private CA that each staff laptop trusts once,
# and a server certificate for the host's address. No internet or public CA is involved, so it keeps working offline.
# The CA private key never leaves the store and is destroyed here: a stolen host cannot mint certificates staff laptops would trust.
# Re-running this means installing the new alshifa-ca.cer on every staff laptop again.
param(
    [Parameter(Mandatory)][string[]]$IpAddress,
    [Parameter(Mandatory)][string]$OutputDir,
    [Parameter(Mandatory)][string]$PfxPassword,
    [string]$HostName = $env:COMPUTERNAME,
    [int]$Years = 5
)

. (Join-Path $PSScriptRoot 'common.ps1')

New-Item -ItemType Directory -Force $OutputDir | Out-Null
$names = @("DNS=$HostName") + ($IpAddress | ForEach-Object { "IPAddress=$_" })
$notAfter = (Get-Date).AddYears($Years)
$store = 'Cert:\CurrentUser\My'

$ca = New-SelfSignedCertificate -Subject 'CN=Al-Shifa Hospital Local CA' -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
    -KeyUsage CertSign, CRLSign, DigitalSignature -KeyExportPolicy NonExportable -NotAfter $notAfter.AddYears(1) -CertStoreLocation $store `
    -TextExtension @('2.5.29.19={critical}{text}CA=1&pathlength=0')
try {
    $server = New-SelfSignedCertificate -Subject "CN=$HostName" -Signer $ca -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
        -KeyUsage DigitalSignature, KeyEncipherment -KeyExportPolicy Exportable -NotAfter $notAfter -CertStoreLocation $store `
        -TextExtension @("2.5.29.17={text}$($names -join '&')", '2.5.29.37={text}1.3.6.1.5.5.7.3.1')
    try {
        $secure = ConvertTo-SecureString $PfxPassword -AsPlainText -Force
        Export-PfxCertificate -Cert $server -FilePath (Join-Path $OutputDir 'server.pfx') -Password $secure -ChainOption EndEntityCertOnly -CryptoAlgorithmOption AES256_SHA256 -Force | Out-Null
        Export-Certificate -Cert $ca -FilePath (Join-Path $OutputDir 'alshifa-ca.cer') -Force | Out-Null
    } finally {
        Remove-Item $server.PSPath -DeleteKey
    }
} finally {
    Remove-Item $ca.PSPath -DeleteKey
}

"Certificate valid until $($notAfter.ToString('yyyy-MM-dd')) for: $($names -join ', ')"
