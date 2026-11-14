@echo off
setlocal
net session >nul 2>&1
if %errorlevel% neq 0 (
  set "ALSHIFA_TRANSFER_LAUNCHER=%~f0"
  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath $env:ALSHIFA_TRANSFER_LAUNCHER -Verb RunAs"
  exit /b
)
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Undo-Transfer-Interactive.ps1"
