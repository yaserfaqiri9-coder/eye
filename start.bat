@echo off
rem Starts the Al-Shifa system on this computer: MySQL (3307), API (5080) and the web app (5173).
rem Parts that are already running are left alone, so it is safe to run this twice.
rem Use --no-browser to skip opening the browser.
setlocal
title Al-Shifa
cd /d "%~dp0"

if not exist ".tools\dotnet\dotnet.exe" (
    echo The local tools folder .tools is missing. See README.md.
    goto :failed
)

echo [1/4] Database...
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\dev-db.ps1"
if errorlevel 1 (
    echo The database did not start. See .tools\mysql-dev.err
    goto :failed
)

echo [2/4] API...
curl.exe -s -f -o nul http://localhost:5080/health
if errorlevel 1 start "Al-Shifa API" powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\dev-api.ps1"

set /a tries=0
:wait_api
curl.exe -s -f -o nul http://localhost:5080/health
if not errorlevel 1 goto :api_ready
set /a tries+=1
if %tries% geq 90 (
    echo The API did not start. Check the "Al-Shifa API" window.
    goto :failed
)
ping -n 3 127.0.0.1 >nul
goto :wait_api
:api_ready

echo [3/4] Building the web app...
pushd frontend
if not exist "node_modules" call npm install --no-audit --no-fund
call npx vite build --logLevel warn
if errorlevel 1 (
    popd
    echo The web app could not be built.
    goto :failed
)
popd

echo [4/4] Web app...
curl.exe -s -o nul http://localhost:5173
if errorlevel 1 start "Al-Shifa Web" /D "%~dp0frontend" cmd /c npx vite preview --port 5173 --strictPort

set /a tries=0
:wait_web
curl.exe -s -o nul http://localhost:5173
if not errorlevel 1 goto :web_ready
set /a tries+=1
if %tries% geq 30 (
    echo The web app did not start. Check the "Al-Shifa Web" window.
    goto :failed
)
ping -n 3 127.0.0.1 >nul
goto :wait_web
:web_ready

echo.
echo Ready: http://localhost:5173
echo To stop the API and web app, close their two windows.
if /i not "%~1"=="--no-browser" start "" http://localhost:5173
exit /b 0

:failed
echo.
pause
exit /b 1
