$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot '../deploy/windows-tv'
$target = Join-Path $PSScriptRoot '../frontend/public/tools'
New-Item -ItemType Directory -Force -Path $target | Out-Null
$names = @('Install-TV.cmd','Install-TV.ps1','Common.ps1','Native.cs','Launch-TV.ps1','Startup.ps1','Set-Autostart.ps1','Enable-Autostart.cmd','Disable-Autostart.cmd','Run-TV.cmd','Uninstall-TV.cmd','Uninstall-TV.ps1','README.fa.md')
$files = @($names | ForEach-Object { Join-Path $source $_ })
Compress-Archive -LiteralPath $files -DestinationPath (Join-Path $target 'AlShifa-TV.zip') -Force
Write-Host 'Packaged frontend/public/tools/AlShifa-TV.zip'
