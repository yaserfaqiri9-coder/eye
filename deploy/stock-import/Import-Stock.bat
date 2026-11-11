@echo off
rem Double-click to start. Asks for administrator rights, which the backup of the installed database needs.
net session >nul 2>&1
if %errorlevel% neq 0 (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '%*' -Verb RunAs"
    exit /b
)
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Import-Stock.ps1" %*
echo.
pause
