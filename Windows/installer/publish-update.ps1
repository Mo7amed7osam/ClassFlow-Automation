<#
  Offers one built installer to every copy of the app, through the central server.

    .\installer\publish-update.ps1                         # the newest dist\installer\ZoomAutoAdmit-Setup-*.exe
    .\installer\publish-update.ps1 -Installer <path>       # a particular one
    .\installer\publish-update.ps1 -App <folder>           # the app folder that installer was built from

  Building never publishes: a build that is still being tried out reaches nobody until this is run.
  The installer is copied into the server's releases folder, checked, and only then named in
  latest.json - a copy of the app never sees a half-copied file. Older installers in the folder are
  removed afterwards. Each app asks the server now and then and offers "Update now" when the
  published version is newer than its own.

  With the installer goes its file list (manifest-<version>.json) and each of the app's files, kept
  once by content in releases\files\<sha256>. An app compares the list with its own files and
  fetches only the ones that changed - a few MB instead of the whole 170 MB installer.
#>
param(
    [string]$Installer = "",
    [string]$App = "",
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

# ---- the file list, only when the app folder is this installer's own build
if (-not $App) { $App = Join-Path $windows "dist\setup-work\app" }
$manifestName = $null
$appExe = Join-Path $App "ZoomAutoAdmit.WindowsUI.exe"
if ((Test-Path $appExe) -and ((Get-Item $appExe).VersionInfo.FileVersion -eq $version)) {
    $store = Join-Path $Releases "files"
    New-Item -ItemType Directory -Force $store | Out-Null
    $root = (Resolve-Path $App).Path.TrimEnd('\') + '\'
    $files = @()
    foreach ($file in Get-ChildItem $App -Recurse -File) {
        $hash = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $stored = Join-Path $store $hash
        if (-not (Test-Path $stored) -or (Get-Item $stored).Length -ne $file.Length) {
            Copy-Item $file.FullName "$stored.partial" -Force
            Move-Item "$stored.partial" $stored -Force
        }
        $files += [ordered]@{ path = $file.FullName.Substring($root.Length); sha256 = $hash; size = $file.Length }
    }
    $manifestName = "manifest-$version.json"
    $manifestPath = Join-Path $Releases $manifestName
    [IO.File]::WriteAllText("$manifestPath.tmp", ([ordered]@{ version = $version; files = $files } | ConvertTo-Json -Depth 4 -Compress), [Text.UTF8Encoding]::new($false))
    Move-Item "$manifestPath.tmp" $manifestPath -Force
    $allBytes = 0; foreach ($f in $files) { $allBytes += $f.size }
    Write-Host "File list: $($files.Count) files, $([math]::Round($allBytes / 1MB, 1)) MB in all." -ForegroundColor Cyan
}
else {
    Write-Host "No app folder of version $version at $App - published as a full installer only." -ForegroundColor Yellow
}

$latest = [ordered]@{
    version     = $version
    fileName    = $source.Name
    sha256      = $sha
    size        = (Get-Item $target).Length
    publishedAt = (Get-Date).ToString("o")
}
if ($manifestName) { $latest.manifest = $manifestName }
$json = Join-Path $Releases "latest.json"
[IO.File]::WriteAllText("$json.tmp", ($latest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
Move-Item "$json.tmp" $json -Force

# ---- what the published version no longer needs. A file being downloaded right now stays until next time.
function Remove-Quietly($item) { try { Remove-Item $item.FullName -Force -ErrorAction Stop } catch { } }
Get-ChildItem $Releases -Filter "ZoomAutoAdmit-Setup-*.exe" | Where-Object { $_.Name -ne $source.Name } | ForEach-Object { Remove-Quietly $_ }
Get-ChildItem $Releases -Filter "manifest-*.json" | Where-Object { $_.Name -ne $manifestName } | ForEach-Object { Remove-Quietly $_ }
if ($manifestName) {
    $keep = @{}; foreach ($f in $files) { $keep[$f.sha256] = $true }
    Get-ChildItem (Join-Path $Releases "files") -File | Where-Object { -not $keep.ContainsKey($_.Name) } | ForEach-Object { Remove-Quietly $_ }
}
Write-Host "Published $version ($([math]::Round($latest.size / 1MB, 1)) MB installer) - every app will offer it." -ForegroundColor Green
