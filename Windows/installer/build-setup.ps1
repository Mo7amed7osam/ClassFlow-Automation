<#
  Builds the installer people run: dist\installer\ZoomAutoAdmit-Setup-<version>.exe

    .\installer\build-setup.ps1                                  # the admin's server, today's version
    .\installer\build-setup.ps1 -Server https://other.ts.net/ -Version 1.2.0

  Building publishes nothing. To offer a build to everyone's app, run publish-update.ps1.

  1. the React pages (web\sessions -> WindowsUI\WebSessions)
  2. the app, self-contained for 64-bit Windows (the PC needs no .NET)
  3. zipped, and carried inside a single-file setup .exe (ZoomAutoAdmit.Setup)

  The setup installs for the current Windows user (no administrator rights), adds Start menu and
  desktop shortcuts and an entry in Windows' Apps list, and gives a PC that never ran the app the
  server address below. The LMS browser is downloaded by the app the first time it is needed.
#>
param(
    [string]$Server = "https://mohab-pc.tail5d9f33.ts.net/",
    # 1.<year>.<month and day>.<hour and minute>, e.g. 1.26.916.1830 at 18:30 on 16 Sep 2026 (each
    # part must stay under 65535). Every build is newer than the one before, so a copy of the app can
    # tell a published update from the version it already has.
    [string]$Version = ("1.{0}.{1}.{2}" -f (Get-Date -Format "yy"), [int](Get-Date -Format "MMdd"), [int](Get-Date -Format "HHmm"))
)
$ErrorActionPreference = "Stop"
$windows = Split-Path $PSScriptRoot -Parent
$work = Join-Path $windows "dist\setup-work"
$app = Join-Path $work "app"
$zip = Join-Path $work "payload.zip"
$out = Join-Path $windows "dist\installer"
if (-not $env:DOTNET_ROOT -and (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe")) {
    $env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"; $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
}

Write-Host "1/3  React pages" -ForegroundColor Cyan
Push-Location (Join-Path $windows "web\sessions")
# Through cmd: npm writes its notices to stderr, which PowerShell would take for a failure.
try { cmd /c "npm run build 2>&1" | Out-Host; if ($LASTEXITCODE) { throw "The pages did not build." } } finally { Pop-Location }

Write-Host "2/3  The app (self-contained, win-x64), version $Version" -ForegroundColor Cyan
if (Test-Path $app) { [IO.Directory]::Delete($app, $true) }
dotnet publish (Join-Path $windows "src\ZoomAutoAdmit.WindowsUI\ZoomAutoAdmit.WindowsUI.csproj") -c Release -r win-x64 --self-contained true `
    -o $app -p:DebugType=None -p:DebugSymbols=false -p:SatelliteResourceLanguages=en -p:AppVersion=$Version -nologo | Out-Host
if ($LASTEXITCODE) { throw "The app did not publish." }
if (Test-Path $zip) { [IO.File]::Delete($zip) }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($app, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)

Write-Host "3/3  The setup" -ForegroundColor Cyan
$build = Join-Path $work "setup"
dotnet publish (Join-Path $windows "src\ZoomAutoAdmit.Setup\ZoomAutoAdmit.Setup.csproj") -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false -p:SatelliteResourceLanguages=en `
    -p:PayloadZip=$zip -p:DefaultServer=$Server -p:Version=$Version -o $build -nologo | Out-Host
if ($LASTEXITCODE) { throw "The setup did not build." }
New-Item -ItemType Directory -Force $out | Out-Null
$final = Join-Path $out "ZoomAutoAdmit-Setup-$Version.exe"
Copy-Item (Join-Path $build "ZoomAutoAdmit-Setup.exe") $final -Force
Write-Host ("Done: {0} ({1:N0} MB)" -f $final, ((Get-Item $final).Length / 1MB)) -ForegroundColor Green
