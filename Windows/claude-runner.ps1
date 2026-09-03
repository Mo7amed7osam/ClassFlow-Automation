# Claude runner — start it once and leave the window open.
# It watches for a request file that Claude writes, runs it on this PC, and saves the full output
# to claude-runner.log so Claude can read the result. Close the window to revoke this at any time.
$ErrorActionPreference = 'Continue'
$root    = $PSScriptRoot
$request = Join-Path $root '.claude-run-request'
$log     = Join-Path $root 'claude-runner.log'
$project = Join-Path $root 'src\ZoomAutoAdmit.WindowsUI\ZoomAutoAdmit.WindowsUI.csproj'
$tests   = Join-Path $root 'tests\ZoomAutoAdmit.WindowsUI.Tests\ZoomAutoAdmit.WindowsUI.Tests.csproj'
$app     = Join-Path $root 'src\ZoomAutoAdmit.WindowsUI\bin\Release\net8.0-windows10.0.19041.0\ZoomAutoAdmit.WindowsUI.exe'
$dotnet  = & (Join-Path $root 'Find-Dotnet.ps1')

Write-Host "Claude runner is watching. Requests appear here; press Ctrl+C or close this window to stop." -ForegroundColor Cyan
Write-Host "Folder: $root`n"

while ($true) {
    if (Test-Path $request) {
        $command = ''
        try { $command = (Get-Content $request -Raw).Trim() } catch { Start-Sleep -Milliseconds 300; continue }
        Remove-Item $request -Force -ErrorAction SilentlyContinue
        if ([string]::IsNullOrWhiteSpace($command)) { continue }

        Write-Host "`n>>> $command" -ForegroundColor Yellow
        Set-Content -Path $log -Value "=== $command === $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===" -Encoding UTF8
        $exit = 0
        switch -Regex ($command) {
            '^(build|run)$' {
                taskkill /IM ZoomAutoAdmit.WindowsUI.exe /F 2>&1 | Out-Null
                & $dotnet build $project -c Release -v minimal 2>&1 | Tee-Object -FilePath $log -Append | Out-Host
                $exit = $LASTEXITCODE
                if ($command -eq 'run' -and $exit -eq 0) { Start-Process $app; Add-Content $log "Started: $app" }
            }
            '^test$' {
                & $dotnet test $tests -c Release --nologo 2>&1 | Tee-Object -FilePath $log -Append | Out-Host
                $exit = $LASTEXITCODE
            }
            '^cmd .+' {
                $line = $command.Substring(4)
                & cmd /c $line 2>&1 | Tee-Object -FilePath $log -Append | Out-Host
                $exit = $LASTEXITCODE
            }
            default { Add-Content $log "Unknown request. Use: build | run | test | cmd <command line>"; $exit = 2 }
        }
        Add-Content $log "EXIT CODE: $exit"
        Add-Content $log "DONE"
        Write-Host "<<< finished, exit $exit" -ForegroundColor Green
    }
    Start-Sleep -Milliseconds 800
}
