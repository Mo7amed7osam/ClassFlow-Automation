<#
  Offers one built installer to every copy of the app, through the central server.

    .\installer\publish-update.ps1                         # the newest dist\installer\ZoomAutoAdmit-Setup-*.exe
    .\installer\publish-update.ps1 -Installer <path>       # a particular one

  Building never publishes: a build that is still being tried out reaches nobody until this is run.
  The installer is copied into the server's releases folder, checked, and only then named in
  latest.json - a copy of the app never sees a half-copied file. Older installers in the folder are
  removed afterwards. Each app asks the server now and then and offers "Update now" when the
  published version is newer than its own.
#>
param(
    [string]$Installer = "",
    [string]$Releases = (Join-Path $env:LOCALAPPDATA "ZoomAutoAdmit\Central\releases")
)
$ErrorActionPreference = "Stop"
$windows = Split-Path $PSScriptRoot -Parent

if (-not $Installer) {
    $newest = Get-ChildItem (Join-Path $windows "dist\installer") -Filter "ZoomAutoAdmit-Setup-*.exe" |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $newest) { throw "No installer in dist\installer. Run build-setup.ps1 first." }
    $Installer = $newest.FullName
}
$source = Get-Item $Installer
if ($source.Name -notmatch '^ZoomAutoAdmit-Setup-(\d+(\.\d+){1,3})\.exe$') { throw "$($source.Name) is not an installer this server hands out." }
$version = $Matches[1]

New-Item -ItemType Directory -Force $Releases | Out-Null
$target = Join-Path $Releases $source.Name
$partial = "$target.partial"
Copy-Item $source.FullName $partial -Force
$sha = (Get-FileHash $partial -Algorithm SHA256).Hash.ToLowerInvariant()
if ($sha -ne (Get-FileHash $source.FullName -Algorithm SHA256).Hash.ToLowerInvariant()) { Remove-Item $partial -Force; throw "The copy does not match the installer." }
Move-Item $partial $target -Force

$latest = [ordered]@{
    version     = $version
    fileName    = $source.Name
    sha256      = $sha
    size        = (Get-Item $target).Length
    publishedAt = (Get-Date).ToString("o")
}
$json = Join-Path $Releases "latest.json"
[IO.File]::WriteAllText("$json.tmp", ($latest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
Move-Item "$json.tmp" $json -Force

Get-ChildItem $Releases -Filter "ZoomAutoAdmit-Setup-*.exe" | Where-Object { $_.Name -ne $source.Name } | Remove-Item -Force
Write-Host "Published $version ($([math]::Round($latest.size / 1MB, 1)) MB) - every app will offer it." -ForegroundColor Green
