# Runs the API against the development database on http://localhost:5080.
. (Join-Path $PSScriptRoot 'dev-env.ps1')
Set-Location (Join-Path $root 'backend')
# Builds into .tools\api-run, so a running API never locks backend\...\bin\Debug that the tests build into.
dotnet run --project src/AlShifa.Api --artifacts-path (Join-Path $root '.tools\api-run') --no-launch-profile --urls http://localhost:5080
