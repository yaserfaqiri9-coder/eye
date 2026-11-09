# Run on the development computer. Produces release\AlShifa-Installer-<date>\ with Install-AlShifa.exe, the one file the hospital host needs.
# Downloads the third-party programs from their publishers once, and refuses any file whose hash, signature or publisher does not match
# prerequisites.json. Contains no secrets and no patient data: certificates and passwords are created on the host during installation.
param(
    [string]$OutputRoot,
    # An existing folder from build-release.ps1. Without it, a new release is built first.
    [string]$Release,
    # Only for someone holding a Visual Studio licence, which is what Microsoft's terms ask of whoever redistributes this runtime.
    # Without it, the installer takes vc_redist.x64.exe from beside itself when a laptop needs it.
    [switch]$IncludeVisualCppRuntime,
    # A folder of voice clips the hospital may use. They install with the system, so calling works with no Import-AlShifa-Voice step.
    # Off by default: whether a voice's licence allows this is the hospital's decision. See DEPLOYMENT-WINDOWS.md.
    [string]$VoicePack
)

. (Join-Path $PSScriptRoot 'common.ps1')

$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo 'release' }
$cache = Join-Path $repo '.tools\installer-cache'
$manifest = Get-Content (Join-Path $PSScriptRoot 'prerequisites.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$gpg = Find-Gpg

# GnuPG from Git for Windows reads "F:/x" as a relative path. See Invoke-Gpg in common.ps1.
function ConvertTo-GpgPath([string]$Path) {
    $path = $Path -replace '\\', '/'
    if ($gpg -notmatch '\\Git\\') { return $path }
    return [regex]::Replace($path, '^([A-Za-z]):/', { param($m) "/$($m.Groups[1].Value.ToLower())/" })
}

# Accepts the signature only if it is good and made by the expected key. The key file alone proves nothing: the fingerprint is the pin.
function Test-OpenPgp([string]$File, [string]$Signature, [string]$KeyFile, [string[]]$Fingerprints) {
    # Not under TEMP: the gpg-agent from Git for Windows cannot start from a path with a space, as a user profile often has.
    $home_ = Join-Path $cache "gpg-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory $home_ | Out-Null
    try {
        $gpgHome = ConvertTo-GpgPath $home_
        Invoke-Native $gpg @('--homedir', $gpgHome, '--batch', '--quiet', '--import', (ConvertTo-GpgPath $KeyFile)) | Out-Null
        $status = Invoke-Native $gpg @('--homedir', $gpgHome, '--batch', '--status-fd', '1', '--verify', (ConvertTo-GpgPath $Signature), (ConvertTo-GpgPath $File))
        # GOODSIG, not only VALIDSIG: gpg reports an expired or revoked key under a different status and still exits with 0.
        $good = @($status | Where-Object { $_ -match '\[GNUPG:\] GOODSIG ' }).Count -gt 0
        $valid = @($status | Where-Object { $_ -match '\[GNUPG:\] VALIDSIG (\S+) .* (\S+)$' } | ForEach-Object { $Matches[1]; $Matches[2] })
        if (-not $good -or -not ($valid | Where-Object { $Fingerprints -contains $_ })) { throw "No good signature from the expected key on $(Split-Path $File -Leaf)." }
        'signature good, key pinned'
    } finally {
        try { Invoke-Native (Join-Path (Split-Path $gpg) 'gpgconf.exe') @('--homedir', (ConvertTo-GpgPath $home_), '--kill', 'gpg-agent') | Out-Null } catch { }
        Remove-Item $home_ -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Test-Publisher([string]$File, [string]$Subject) {
    $signature = Get-AuthenticodeSignature $File
    if ($signature.Status -ne 'Valid') { throw "$(Split-Path $File -Leaf): code signature is $($signature.Status)." }
    if ($signature.SignerCertificate.Subject -notmatch [regex]::Escape($Subject)) { throw "$(Split-Path $File -Leaf): signed by $($signature.SignerCertificate.Subject), expected $Subject." }
    "code signature valid: $Subject"
}

function Get-Download([string]$Url, [string]$Target) {
    if (Test-Path $Target) { return }
    New-Item -ItemType Directory -Force (Split-Path $Target) | Out-Null
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    "Downloading $Url"
    Invoke-WebRequest $Url -OutFile "$Target.part" -UseBasicParsing
    Move-Item "$Target.part" $Target
}

if (-not $Release) {
    $built = & (Join-Path $PSScriptRoot 'build-release.ps1') -OutputRoot $OutputRoot
    $Release = ($built | Select-Object -Last 1) -replace '^Release ready: ', ''
}
if (-not (Test-Path (Join-Path $Release 'app\AlShifa.Api.exe'))) { throw "No application found in $Release." }

$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
$target = Join-Path $OutputRoot "AlShifa-Installer-$stamp"
$package = Join-Path $OutputRoot "payload-$stamp"
New-Item -ItemType Directory -Force $target, $package | Out-Null

try {
    '== Third-party programs'
    $prerequisites = Join-Path $package 'prerequisites'
    New-Item -ItemType Directory $prerequisites | Out-Null
    $notices = New-Object System.Collections.Generic.List[string]
    foreach ($item in $manifest.items) {
        if ($item.requiresVisualStudioLicense -and -not $IncludeVisualCppRuntime) {
            "$($item.file): left out, see -IncludeVisualCppRuntime"
            continue
        }
        $file = Join-Path $cache "$($item.id)\$($item.file)"
        Get-Download $item.url $file
        $hash = (Get-FileHash $file -Algorithm SHA256).Hash
        if ($hash -ne $item.sha256.ToUpper()) { throw "$($item.file): hash $hash does not match the pinned value." }
        "$($item.file): hash matches"
        if ($item.signature) {
            $signature = Join-Path $cache "$($item.id)\$($item.file).$($item.signature.extension)"
            Get-Download $item.signature.url $signature
            $key = Join-Path $cache "keys\$($item.signature.keyFile)"
            Get-Download $item.signature.keyUrl $key
            Test-OpenPgp $file $signature $key @($item.signature.fingerprints)
        }
        if ($item.publisher) { Test-Publisher $file $item.publisher }
        Copy-Item $file $prerequisites
        $notices.Add("$($item.name) $($item.version)`r`n  File: $($item.file)`r`n  SHA-256: $hash`r`n  From: $($item.url)`r`n  License: $($item.license)`r`n  $($item.licenseNote)`r`n")
    }

    '== Programs of the installer'
    $references = 'System.Windows.Forms.dll', 'System.Drawing.dll', 'System.Management.dll', 'System.ServiceProcess.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll', 'System.Core.dll'
    $tools = Join-Path $package 'tools'
    New-Item -ItemType Directory $tools | Out-Null
    # -AsUser: no Administrator prompt. Only AlShifa.exe, the desktop icon, runs that way.
    function New-InstallerProgram([string[]]$Source, [string]$Output, [switch]$AsUser) {
        $manifest = if ($AsUser) { @() } else { @("/win32manifest:$(Join-Path $PSScriptRoot 'installer\app.manifest')") }
        $arguments = @('/nologo', '/target:winexe', '/optimize+', '/codepage:65001') + $manifest + @("/win32icon:$(Join-Path $repo 'frontend\public\favicon.ico')", "/out:$Output") +
            @($references | ForEach-Object { "/r:$_" }) + @($Source | ForEach-Object { Join-Path $PSScriptRoot "installer\$_" })
        Invoke-Native $csc $arguments | Out-Null
    }
    New-InstallerProgram 'ConnectAlShifa.cs', 'Discovery.cs' (Join-Path $tools 'Connect-AlShifa.exe')
    New-InstallerProgram 'Launcher.cs', 'Discovery.cs' (Join-Path $tools 'AlShifa.exe') -AsUser
    New-InstallerProgram 'ImportVoice.cs' (Join-Path $target 'Import-AlShifa-Voice.exe')
    $installer = Join-Path $target 'Install-AlShifa.exe'
    New-InstallerProgram 'InstallAlShifa.cs' $installer
    Copy-Item (Join-Path $tools 'Connect-AlShifa.exe') $target

    '== Package'
    Copy-Item (Join-Path $Release 'app') (Join-Path $package 'app') -Recurse
    Copy-Item (Join-Path $Release 'scripts') (Join-Path $package 'scripts') -Recurse
    if ($VoicePack) {
        if (-not (Get-ChildItem (Join-Path $VoicePack '*.mp3') -ErrorAction SilentlyContinue)) { throw "No MP3 files in $VoicePack." }
        New-Item -ItemType Directory (Join-Path $package 'voice') | Out-Null
        Copy-Item (Join-Path $VoicePack '*.mp3') (Join-Path $package 'voice')
    }
    # build-release.ps1 copied this tree earlier; these are always taken fresh so a rebuild of the installer stays in step.
    Copy-Item (Join-Path $PSScriptRoot '*.ps1') (Join-Path $package 'scripts') -Force
    $zip = "$package.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($package, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)

    # The package goes after the program: zip, its length, its SHA-256 and a mark. See Payload in InstallAlShifa.cs.
    $sha = [Security.Cryptography.SHA256]::Create()
    $input_ = [IO.File]::OpenRead($zip)
    try { $digest = $sha.ComputeHash($input_) } finally { $input_.Dispose() }
    $output = [IO.File]::Open($installer, 'Append')
    $input_ = [IO.File]::OpenRead($zip)
    try {
        $input_.CopyTo($output)
        $length = [BitConverter]::GetBytes([long]$input_.Length)
        $output.Write($length, 0, 8)
        $output.Write($digest, 0, 32)
        $output.Write([Text.Encoding]::ASCII.GetBytes('ALSHIFA1'), 0, 8)
    } finally {
        $input_.Dispose()
        $output.Dispose()
    }

    foreach ($guide in 'INSTALL-SIMPLE.md', 'PILOT-CHECKLIST.md', 'DEPLOYMENT-WINDOWS.md') { Copy-Item (Join-Path $repo $guide) $target }
    $header = "Third-party programs inside Install-AlShifa.exe. Each was checked against its publisher's hash and signature when this package was built.`r`nThe GPL programs are passed on unchanged. Source code for exactly these versions is at the addresses below.`r`n`r`n"
    [IO.File]::WriteAllText((Join-Path $target 'THIRD-PARTY-NOTICES.txt'), $header + ($notices -join "`r`n"))
    Get-ChildItem $target -File | ForEach-Object { "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash)  $($_.Name)" } | Set-Content (Join-Path $target 'SHA256SUMS.txt') -Encoding Ascii

    "Installer ready: $installer ($([Math]::Round((Get-Item $installer).Length / 1MB)) MB)"
} catch {
    Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
    throw
} finally {
    Remove-Item $package -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "$package.zip" -Force -ErrorAction SilentlyContinue
}
