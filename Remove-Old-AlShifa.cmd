@echo off
setlocal EnableExtensions
chcp 65001 >nul 2>&1
title Remove old Al-Shifa installation

rem Ask Windows for Administrator access when the file is double-clicked.
fltmc >nul 2>&1
if errorlevel 1 (
    set "ALSHIFA_SELF=%~f0"
    powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath $env:ALSHIFA_SELF -Verb RunAs"
    exit /b
)

set "ALSHIFA_SELF=%~f0"
set "ALSHIFA_TEMP=%TEMP%\remove-old-alshifa-%RANDOM%-%RANDOM%.ps1"

rem Extract the PowerShell payload stored at the end of this same file.
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$raw=[IO.File]::ReadAllText($env:ALSHIFA_SELF); $marker='# AL-SHIFA-'+'POWERSHELL-PAYLOAD'; $at=$raw.IndexOf($marker); if($at -lt 0){throw 'Cleanup payload was not found.'}; $code=$raw.Substring($at+$marker.Length); [IO.File]::WriteAllText($env:ALSHIFA_TEMP,$code,(New-Object Text.UTF8Encoding($false)))"
if errorlevel 1 goto :failed

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%ALSHIFA_TEMP%"
set "ALSHIFA_EXIT=%ERRORLEVEL%"
del /q "%ALSHIFA_TEMP%" >nul 2>&1

if not "%ALSHIFA_EXIT%"=="0" goto :failed
echo.
echo The old Al-Shifa installation and its database were removed successfully.
echo You can now close this window.
pause >nul
exit /b 0

:failed
del /q "%ALSHIFA_TEMP%" >nul 2>&1
echo.
echo Removal did not finish. Please take a photo of this window.
pause
exit /b 1

# AL-SHIFA-POWERSHELL-PAYLOAD
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath('C:\AlShifa').TrimEnd('\')
if ($root -ne 'C:\AlShifa') {
    throw "Unexpected deletion target: $root"
}

# Remove only the tunnel installed inside this Al-Shifa installation.
$tunnel = Get-CimInstance Win32_Service -Filter "Name='Cloudflared'" -ErrorAction SilentlyContinue
if ($tunnel -and $tunnel.PathName -like '*C:\AlShifa\cloudflared*') {
    Stop-Service Cloudflared -Force -ErrorAction SilentlyContinue
    & sc.exe delete Cloudflared | Out-Null
}

foreach ($name in 'AlShifaApi', 'AlShifaMySQL') {
    $service = Get-Service -Name $name -ErrorAction SilentlyContinue
    if ($service) {
        if ($service.Status -ne 'Stopped') {
            Stop-Service -Name $name -Force -ErrorAction Stop
            $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(45))
        }
        & sc.exe delete $name | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not remove service: $name" }
    }
}

Get-NetFirewallRule -DisplayName 'Al-Shifa HTTPS (hospital LAN)' -ErrorAction SilentlyContinue |
    Remove-NetFirewallRule -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName 'AlShifa Backup' -Confirm:$false -ErrorAction SilentlyContinue

Get-ChildItem Cert:\LocalMachine\Root -ErrorAction SilentlyContinue |
    Where-Object Subject -eq 'CN=Al-Shifa Hospital Local CA' |
    Remove-Item -ErrorAction SilentlyContinue

$desktop = [Environment]::GetFolderPath('CommonDesktopDirectory')
Get-ChildItem -LiteralPath $desktop -Filter '*.url' -ErrorAction SilentlyContinue |
    Where-Object { (Get-Content -LiteralPath $_.FullName -Raw -ErrorAction SilentlyContinue) -match 'Managed=1' } |
    Remove-Item -Force -ErrorAction SilentlyContinue

$shell = New-Object -ComObject WScript.Shell
Get-ChildItem -LiteralPath $desktop -Filter '*.lnk' -ErrorAction SilentlyContinue |
    Where-Object {
        $copy = Join-Path $env:TEMP "alshifa-shortcut-$([guid]::NewGuid().ToString('N')).lnk"
        Copy-Item -LiteralPath $_.FullName $copy
        try { $shell.CreateShortcut($copy).Description -eq 'AlShifa managed shortcut' }
        finally { Remove-Item -LiteralPath $copy -Force -ErrorAction SilentlyContinue }
    } |
    Remove-Item -Force -ErrorAction SilentlyContinue

Remove-Item -LiteralPath (Join-Path $desktop 'AlShifa-Staff-Connect') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $env:ProgramData 'AlShifa') -Recurse -Force -ErrorAction SilentlyContinue

$savedPowerPlan = Join-Path $root 'logs\power-before-install.pow'
if (Test-Path -LiteralPath $savedPowerPlan) {
    $powerOutput = & powercfg.exe /import $savedPowerPlan | Out-String
    $powerGuid = [regex]::Match($powerOutput, '[0-9a-fA-F-]{36}').Value
    if ($powerGuid) { & powercfg.exe /setactive $powerGuid | Out-Null }
}

if (Test-Path -LiteralPath $root) {
    Remove-Item -LiteralPath $root -Recurse -Force
}

if (Test-Path -LiteralPath $root) { throw 'C:\AlShifa still exists.' }
if (Get-Service -Name 'AlShifaApi','AlShifaMySQL' -ErrorAction SilentlyContinue) {
    throw 'An Al-Shifa service still exists.'
}
