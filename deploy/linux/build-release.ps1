# Run on the development computer. Builds the online copy for the Contabo server (Linux x64, self-contained):
# release\alshifa-linux-<commit>.tar.gz and its SHA-256, for deploy/linux/deploy-release.sh. Contains no secrets and no patient data.
# The same commit must be installed at the hospital (deploy/windows/update.ps1), or sync waits with "schema_mismatch".
param([string]$OutputRoot)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\windows\common.ps1')

$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo 'release' }
$dotnet = Join-Path $repo '.tools\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

$commit = (& git -C $repo rev-parse --short HEAD).Trim()
if (& git -C $repo status --porcelain) { $commit += '-dirty' }
$work = Join-Path $OutputRoot "alshifa-linux-$commit"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }

Push-Location (Join-Path $repo 'frontend')
try {
    Invoke-Native 'cmd.exe' @('/c', 'npm', 'run', 'build') | Out-Null
} finally {
    Pop-Location
}

$project = Join-Path $repo 'backend\src\AlShifa.Api\AlShifa.Api.csproj'
Invoke-Native $dotnet @('publish', $project, '-c', 'Release', '-r', 'linux-x64', '--self-contained', 'true', '-o', $work, '--nologo', '-v', 'q') | Out-Null
Copy-Item (Join-Path $repo 'frontend\dist') (Join-Path $work 'wwwroot') -Recurse
# Settings of the development computer never travel with a release.
Remove-Item (Join-Path $work 'appsettings.Development.json') -ErrorAction SilentlyContinue

$archive = "$work.tar.gz"
Invoke-Native 'tar.exe' @('-czf', $archive, '-C', $work, '.') | Out-Null
Remove-Item $work -Recurse -Force
$hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"Archive: $archive"
"SHA-256: $hash"
"On the server: deploy/linux/deploy-release.sh /tmp/$(Split-Path $archive -Leaf) $hash"
