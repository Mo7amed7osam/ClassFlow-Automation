<#
.SYNOPSIS
    Zoom Auto Admit Windows Launcher (.NET 8)
#>
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$AppArgs
)

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$rootLauncher = Join-Path $scriptDir "..\run-auto-admit.ps1"

if (Test-Path $rootLauncher) {
    & $rootLauncher @AppArgs
    exit $LASTEXITCODE
}

$projectFile = Join-Path $scriptDir "src\ZoomAutoAdmit.Inspector\ZoomAutoAdmit.Inspector.csproj"
if (-not (Test-Path $projectFile)) {
    Write-Host "[ERROR] Could not locate ZoomAutoAdmit.Inspector.csproj" -ForegroundColor Red
    exit 1
}

$forwardArgs = if ($AppArgs -and $AppArgs.Length -gt 0) { $AppArgs } else { @("waiting-room-auto-admit") }
$dotnet = & (Join-Path $scriptDir "Find-Dotnet.ps1")
& $dotnet run --project $projectFile -- $forwardArgs
exit $LASTEXITCODE
