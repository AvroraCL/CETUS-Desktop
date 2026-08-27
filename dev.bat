@echo off
setlocal
rem -----------------------------------------------------------------
rem CETUS DEV launcher: isolated state under .dev-check, dev skin.
rem Double-click from the repo root or run from any console.
rem -----------------------------------------------------------------
set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"
set "DEV_STATE=%ROOT%\.dev-check"

set "CETUS_DEV=1"
set "CETUS_PORT=3084"
set "CETUS_INSTANCE_ID=dev-bat"
set "CETUS_DSH_HOME=%DEV_STATE%\dsh-home"
set "CETUS_SETTINGS_PATH=%DEV_STATE%\settings.json"
set "CETUS_WEBVIEW2_USER_DATA=%DEV_STATE%\webview2"
set "CETUS_LOG_DIR=%DEV_STATE%\logs"
set "DSH_HOME=%CETUS_DSH_HOME%"
set "CETUS_NODE_EXE=%ROOT%\dist\runtime\node.exe"
set "CETUS_DSH_ENTRY=%ROOT%\dist\runtime\dsh\node_modules\@deepseek-ai\dsh\lib\bin.js"

echo [dev] port  : %CETUS_PORT%
echo [dev] state : %DEV_STATE%

if not exist "%CETUS_NODE_EXE%" (
    echo [dev] ERROR: missing pinned runtime node.exe under dist\runtime.
    echo [dev] Run scripts\publish.ps1 once to populate dist\runtime, then retry.
    pause
    exit /b 1
)
if not exist "%CETUS_DSH_ENTRY%" (
    echo [dev] ERROR: missing pinned dsh entry bin.js under dist\runtime.
    pause
    exit /b 1
)

rem Restart-friendly: end only THIS repo's dev instances (never the installed
rem release) BEFORE the port check, then give the stack a moment to unwind.
echo [dev] stopping any previous repo dev instance...
powershell -NoProfile -Command "Get-Process Cetus -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '%ROOT%\*' } | Stop-Process -Force" >nul 2>&1
ping -n 2 127.0.0.1 >nul

powershell -NoProfile -Command "$own = (Get-NetTCPConnection -State Listen -LocalPort %CETUS_PORT% -ErrorAction SilentlyContinue | Select-Object -First 1).OwningProcess; if ($own) { $pn = (Get-Process -Id $own -ErrorAction SilentlyContinue).ProcessName; Write-Host ('[dev] port holder: PID ' + $own + ' [' + $pn + ']'); exit 1 }"
if errorlevel 1 (
    echo [dev] ERROR: port %CETUS_PORT% is held by another program - see holder above.
    echo [dev] Free it or edit CETUS_PORT inside dev.bat.
    pause
    exit /b 1
)

for %%D in ("%CETUS_DSH_HOME%" "%CETUS_WEBVIEW2_USER_DATA%" "%CETUS_LOG_DIR%") do (
    if not exist "%%~D" mkdir "%%~D"
)

set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"
rem The Debug build is framework-dependent; point its host at this SDK
rem installation so double-clicking works without a system-wide runtime.
set "DOTNET_ROOT=%USERPROFILE%\.dotnet"
if not exist "%DOTNET_ROOT%\host\fxr" set "DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet"
set "PATH=%DOTNET_ROOT%;%PATH%"

echo [dev] building Debug...
"%DOTNET%" build "%ROOT%\src\Cetus.Desktop\Cetus.Desktop.csproj" -c Debug -v q
if errorlevel 1 (
    echo [dev] ERROR: build failed. See output above.
    pause
    exit /b 1
)

start "" "%ROOT%\src\Cetus.Desktop\bin\Debug\net10.0-windows\Cetus.exe"
echo [dev] CETUS DEV launched at http://127.0.0.1:%CETUS_PORT%/ .
ping -n 3 127.0.0.1 >nul
endlocal
exit /b 0
