@echo off
setlocal
chcp 65001 >nul 2>&1
net session >nul 2>&1
if %errorlevel% neq 0 (
  set "ALSHIFA_LAUNCHER=%~f0"
  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath $env:ALSHIFA_LAUNCHER -Verb RunAs"
  exit /b
)
set "SHEET=%~dp0AlShifa_149_Inventory_READY_TO_IMPORT.xlsx"
if not exist "%SHEET%" (
  echo The prepared inventory file is missing:
  echo %SHEET%
  pause
  exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Import-Stock.ps1" -Sheet "%SHEET%"
if errorlevel 1 (
  echo.
  echo Import stopped. Nothing was changed if the tool did not show Committed.
  pause
  exit /b 1
)
exit /b 0
