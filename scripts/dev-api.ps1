# Runs the API against the development database on http://localhost:5080.
. (Join-Path $PSScriptRoot 'dev-env.ps1')
Set-Location (Join-Path $root 'backend')
dotnet run --project src/AlShifa.Api --no-launch-profile --urls http://localhost:5080
