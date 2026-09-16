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

  Older copies (before changed-files updates) are handed a small update instead of the installer:
  UpdateBootstrap.cs compiled with the app's own files inside - see that file. -FullInstaller hands
  them the installer instead. The full installer is what goes on Drive for new installs.

  With the installer goes its file list (manifest-<version>.json) and each of the app's files, kept
  once by content in releases\files\<sha256>. An app compares the list with its own files and
  fetches only the ones that changed - a few MB instead of the whole 170 MB installer.
#>
param(
    [string]$Installer = "",
    [string]$App = "",
    # Hand older copies the full installer instead of the small update (the old way).
    [switch]$FullInstaller,
    [string]$Releases = (Join-Path $env:LOCALAPPDATA "ZoomAutoAdmit\Central\releases")
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
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
# The file list published before this one: a file that differs from it goes into the small update too.
$before = @{}
try {
    $old = Get-Content (Join-Path $Releases "latest.json") -Raw | ConvertFrom-Json
    if ($old.manifest) { foreach ($f in (Get-Content (Join-Path $Releases $old.manifest) -Raw | ConvertFrom-Json).files) { $before[$f.path] = $f.sha256 } }
} catch { }

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
        # Compressed copy: what an app actually downloads (DLLs shrink to about half).
        $packed = "$stored.gz"
        if (-not (Test-Path $packed)) {
            $in = [IO.File]::OpenRead($stored)
            $out = [IO.File]::Create("$packed.partial")
            $gzip = New-Object IO.Compression.GZipStream($out, [IO.Compression.CompressionLevel]::Optimal)
            $in.CopyTo($gzip); $gzip.Dispose(); $out.Dispose(); $in.Dispose()
            Move-Item "$packed.partial" $packed -Force
        }
        $files += [ordered]@{ path = $file.FullName.Substring($root.Length); sha256 = $hash; size = $file.Length; download = (Get-Item $packed).Length }
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

# ---- what an older copy of the app downloads and runs with --update: the small update, or the installer
$partial = "$target.partial"
if ($manifestName -and -not $FullInstaller) {
    # The app's own files (and anything else that changed since the last release), compressed, with the
    # whole file list; compiled with Windows' own C# compiler so it runs on any Windows 10/11 as it is.
    $work = Join-Path ([IO.Path]::GetTempPath()) ("ZoomAutoAdmit-small-update-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force $work | Out-Null
    try {
        $zipPath = Join-Path $work "payload.zip"
        $zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
        $lines = New-Object Text.StringBuilder
        $carried = 0
        foreach ($f in $files) {
            [void]$lines.Append("$($f.path)`t$($f.sha256)`t$($f.size)`n")
            $own = $f.path -like "ZoomAutoAdmit*" -or $f.path -like "WebSessions\*"
            $changedSinceLast = $before.Count -gt 0 -and (-not $before.ContainsKey($f.path) -or $before[$f.path] -ne $f.sha256)
            if ($own -or $changedSinceLast) {
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $App $f.path), ($f.path -replace '\\', '/'), [IO.Compression.CompressionLevel]::Optimal)
                $carried++
            }
        }
        $listEntry = $zip.CreateEntry("files.tsv", [IO.Compression.CompressionLevel]::Optimal)
        $writer = New-Object IO.StreamWriter($listEntry.Open(), [Text.UTF8Encoding]::new($false))
        $writer.Write($lines.ToString()); $writer.Dispose()
        $zip.Dispose()
        [IO.File]::WriteAllText((Join-Path $work "Version.cs"), "[assembly: System.Reflection.AssemblyVersion(`"$version`")]")
        $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
        & $csc -nologo -target:winexe -optimize "-out:$partial" "-resource:$zipPath,payload.zip" `
            -r:System.IO.Compression.dll -r:System.IO.Compression.FileSystem.dll `
            (Join-Path $PSScriptRoot "UpdateBootstrap.cs") (Join-Path $work "Version.cs") | Out-Host
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $partial)) { throw "The small update could not be compiled." }
        Write-Host "Small update for older copies: $carried files, $([math]::Round((Get-Item $partial).Length / 1MB, 2)) MB." -ForegroundColor Cyan
    }
    finally { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
}
else {
    Copy-Item $source.FullName $partial -Force
    if ((Get-FileHash $partial -Algorithm SHA256).Hash -ne (Get-FileHash $source.FullName -Algorithm SHA256).Hash) { Remove-Item $partial -Force; throw "The copy does not match the installer." }
}
$sha = (Get-FileHash $partial -Algorithm SHA256).Hash.ToLowerInvariant()
Move-Item $partial $target -Force

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
    Get-ChildItem (Join-Path $Releases "files") -File | Where-Object { -not $keep.ContainsKey(($_.Name -replace '\.gz$', '')) } | ForEach-Object { Remove-Quietly $_ }
}
Write-Host "Published $version ($([math]::Round($latest.size / 1MB, 2)) MB for older copies; newer ones fetch only the changed files) - every app will offer it." -ForegroundColor Green
