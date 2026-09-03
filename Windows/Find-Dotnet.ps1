<#
.SYNOPSIS
    Resolve a dotnet.exe that actually has the .NET 8 SDK installed.

.DESCRIPTION
    This machine has two dotnet installs: C:\Program Files\dotnet (runtime only, and first on
    PATH) and %USERPROFILE%\.dotnet (the real SDK). A bare `dotnet build` therefore fails with
    "No .NET SDKs were found" even though everything needed is present. Dot-source or call this
    script to get a host that can build.

.OUTPUTS
    The full path to a dotnet.exe whose `--list-sdks` reports an 8.x SDK.
#>
[CmdletBinding()]
param()

function Test-DotnetHasSdk {
    param([string]$Path)
    if (-not $Path -or -not (Test-Path $Path)) { return $false }
    try { $sdks = & $Path --list-sdks 2>$null } catch { return $false }
    return [bool]($sdks | Where-Object { $_ -match '^8\.' })
}

$candidates = @(
    $env:DOTNET_HOST_PATH
    if ($env:DOTNET_ROOT) { Join-Path $env:DOTNET_ROOT 'dotnet.exe' }
    Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    'C:\Program Files\dotnet\dotnet.exe'
) | Where-Object { $_ } | Select-Object -Unique

foreach ($candidate in $candidates) {
    if (Test-DotnetHasSdk $candidate) { return $candidate }
}

throw "No dotnet.exe with a .NET 8 SDK was found. Checked: $($candidates -join '; '). Install the .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0"
