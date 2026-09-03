@echo off
setlocal

set SCRIPT_DIR=%~dp0

rem The dotnet on PATH may be a runtime-only install with no SDK, so `where dotnet` succeeding
rem proves nothing. Find-Dotnet.ps1 returns an install that can actually build.
for /f "usebackq delims=" %%D in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%Windows\Find-Dotnet.ps1"`) do set "DOTNET=%%D"
if not defined DOTNET (
    echo ================================================================================
    echo  [ERROR] No .NET 8 SDK was found on this machine.
    echo  Please install .NET 8.0 SDK from: https://dotnet.microsoft.com/download/dotnet/8.0
    echo ================================================================================
    exit /b 1
)

set CSPROJ=%SCRIPT_DIR%Windows\src\ZoomAutoAdmit.Inspector\ZoomAutoAdmit.Inspector.csproj

if not exist "%CSPROJ%" (
    set CSPROJ=%SCRIPT_DIR%src\ZoomAutoAdmit.Inspector\ZoomAutoAdmit.Inspector.csproj
)

if not exist "%CSPROJ%" (
    echo [ERROR] Could not find ZoomAutoAdmit.Inspector.csproj
    exit /b 1
)

if "%~1"=="" (
    "%DOTNET%" run --project "%CSPROJ%" -- waiting-room-auto-admit
) else (
    "%DOTNET%" run --project "%CSPROJ%" -- %*
)
