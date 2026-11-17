# Read-only validation of the recorded WAV assets installed with this application.
param(
    [Parameter(Mandatory)][string]$Root,
    [string]$Version = 'recorded-c98dd13ed170'
)
$ErrorActionPreference = 'Stop'
try {
    if ($Version -notmatch '^recorded-[a-f0-9]{12}$') { throw 'Invalid recorded audio version.' }
    $folder = [IO.Path]::GetFullPath((Join-Path $Root "wwwroot\audio\$Version")).TrimEnd('\') + '\'
    $manifestPath = Join-Path $folder 'manifest.json'
    $hash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ('recorded-' + $hash.Substring(0,12) -cne $Version) { throw 'Recorded audio manifest does not match this version.' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if (@($manifest.clips).Count -ne 68) { throw 'The recorded audio manifest must contain 68 WAVs.' }
    $seen = @{}
    foreach ($clip in $manifest.clips) {
        $relative = [string]$clip.wav
        $file = [IO.Path]::GetFullPath((Join-Path $folder $relative))
        if (-not $file.StartsWith($folder, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetExtension($file) -ine '.wav') { throw 'Invalid audio file path.' }
        if ($seen.ContainsKey($relative)) { throw 'Duplicate audio file path.' }
        $seen[$relative] = $true
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ine [string]$clip.sha256_wav) { throw "Recorded audio differs: $relative" }
    }
    $required = @(1..50 | ForEach-Object { 'WAV/numbers/number_{0:D3}.wav' -f $_ })
    $required += @('doctor','refraction','imaging','pharmacy','optical','initial_exam','reception','cashier','surgery' | ForEach-Object { "WAV/destinations/destination_$_.wav" })
    foreach ($relative in $required) { if (-not $seen.ContainsKey($relative)) { throw "Missing announcement asset: $relative" } }
    Write-Output "OK     Recorded audio        $Version; 68 WAV hashes verified; 59 announcement clips ready."
    exit 0
} catch {
    Write-Output "FAIL   Recorded audio        $($_.Exception.Message)"
    exit 1
}
