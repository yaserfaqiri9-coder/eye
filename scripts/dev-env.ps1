# Dot-source to point the local SDK and connection string at the development database.
$root = Split-Path $PSScriptRoot -Parent
$secrets = Get-Content (Join-Path $root '.tools\dev-secrets.json') -Raw | ConvertFrom-Json

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_ROOT = Join-Path $root '.tools\dotnet'
$env:PATH = "$env:DOTNET_ROOT;$(Join-Path $root '.tools\ef');$env:PATH"
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ConnectionStrings__Default = "Server=127.0.0.1;Port=3307;Database=alshifa_dev;User=alshifa;Password=$($secrets.app);"
