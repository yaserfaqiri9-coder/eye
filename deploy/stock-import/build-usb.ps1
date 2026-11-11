# Run on the development computer. Builds the folder to copy to a USB stick: the program (self-contained, no .NET needed),
# the launcher, the Excel template and the guide. Contains no passwords, no connection settings and no patient data.
param([string]$Output)

$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $Output) { $Output = Join-Path $repo 'release\AlShifa-StockImport' }
$dotnet = Join-Path $repo '.tools\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
New-Item -ItemType Directory -Force $Output | Out-Null

& $dotnet publish (Join-Path $repo 'backend\src\AlShifa.Api\AlShifa.Api.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $Output 'app') --nologo -v q
if ($LASTEXITCODE) { throw 'publish failed' }
# The program must never pick up a connection from a file; the launcher passes it for one run only.
Get-ChildItem (Join-Path $Output 'app') -Filter 'appsettings.*.json' | Remove-Item -Force

Copy-Item (Join-Path $PSScriptRoot 'Import-Stock.ps1'), (Join-Path $PSScriptRoot 'Import-Stock.bat'), (Join-Path $PSScriptRoot 'README-FA.txt') $Output
& python (Join-Path $PSScriptRoot 'make-template.py') $Output
if ($LASTEXITCODE) { throw 'template failed (needs Python with openpyxl)' }
"USB folder ready: $Output"
