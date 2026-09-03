@echo off
setlocal
rem Build the Windows UI after a code change, then start the freshly built app.
rem The solution only defines Debug/Release, so the app project is built directly.
set "CONFIG=%~1"
if "%CONFIG%"=="" set "CONFIG=Release"
set "ROOT=%~dp0"
set "PROJ=%ROOT%src\ZoomAutoAdmit.WindowsUI\ZoomAutoAdmit.WindowsUI.csproj"
set "APP=%ROOT%src\ZoomAutoAdmit.WindowsUI\bin\%CONFIG%\net8.0-windows10.0.19041.0\ZoomAutoAdmit.WindowsUI.exe"

echo Closing any running ZoomAutoAdmit UI so the build can overwrite it...
taskkill /IM ZoomAutoAdmit.WindowsUI.exe /F >nul 2>&1

rem PATH resolves dotnet to the runtime-only install, which has no SDK and cannot build.
rem Find-Dotnet.ps1 picks the install that actually carries the .NET 8 SDK.
for /f "usebackq delims=" %%D in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%Find-Dotnet.ps1"`) do set "DOTNET=%%D"
if not defined DOTNET (
    echo.
    echo No .NET 8 SDK was found on this machine.
    pause
    exit /b 1
)

echo Building %CONFIG%...
"%DOTNET%" build "%PROJ%" -c %CONFIG% -v minimal
if errorlevel 1 (
    echo.
    echo BUILD FAILED - the app was not started. Copy the errors above.
    pause
    exit /b 1
)

if not exist "%APP%" (
    echo.
    echo Build succeeded but this file was not found:
    echo %APP%
    pause
    exit /b 1
)

echo Starting %APP%
start "" "%APP%"
endlocal
