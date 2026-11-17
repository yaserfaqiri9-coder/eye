@echo off
setlocal
cd /d "%~dp0"
choice /C YN /M "Open the TV automatically after Windows sign-in"
if errorlevel 2 goto manual
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-TV.ps1" -EnableStartup
goto done
:manual
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-TV.ps1"
:done
if errorlevel 1 echo Installation failed. See the message above.
pause
