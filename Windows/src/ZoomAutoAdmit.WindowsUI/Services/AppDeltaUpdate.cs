using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace ZoomAutoAdmit.WindowsUI.Services;

/// <summary>One file of a published version, as the server's file list names it.</summary>
/// <param name="Download">The compressed copy's size, when the server keeps one (<c>&lt;sha256&gt;.gz</c>).</param>
public sealed record AppFile(string Path, string Sha256, long Size, long? Download = null)
{
    /// <summary>What fetching this file costs: its compressed size when there is one.</summary>
    public long Transfer => Download ?? Size;
}

/// <summary>What updating to a version takes on this PC: its files, and the ones that differ here.</summary>
public sealed record AppDeltaPlan(string Version, IReadOnlyList<AppFile> Files, IReadOnlyList<AppFile> Changed)
{
    /// <summary>What the update downloads: the changed files, compressed where the server has them so.</summary>
    public long ChangedSize => Changed.Sum(file => file.Transfer);
}

/// <summary>
/// An update made of only the files that changed. The server publishes each version's file list
/// (GET api/v1/app/manifest) and hands out single files by their SHA-256; this compares the list with
/// the installed app and downloads what differs - usually the app's own few MB, never the 170 MB of
/// runtime and browser that stay the same.
///
/// The new version is built beside the installed one (&lt;folder&gt;.update: unchanged files copied from
/// this install, changed ones downloaded and checked), then a small script waits for the app to close,
/// swaps the folders and opens the app again - putting the old folder back if the swap fails.
/// </summary>
public static class AppDeltaUpdate
{
    /// <summary>The plan for the published version, or null when the server has no file list for it.</summary>
    public static async Task<AppDeltaPlan?> PlanAsync(CentralApiClient api, string folder, Version version, CancellationToken token)
    {
        JsonElement manifest;
        try { manifest = await api.GetAsync<JsonElement>("api/v1/app/manifest", token); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }      // an older server, or no list
        return await PlanAsync(manifest, folder, version, token);
    }

    /// <summary>The plan from a file list already in hand.</summary>
    public static async Task<AppDeltaPlan?> PlanAsync(JsonElement manifest, string folder, Version version, CancellationToken token)
    {
        if (!manifest.TryGetProperty("version", out var v) || v.GetString() != version.ToString() ||
            !manifest.TryGetProperty("files", out var list) || list.ValueKind != JsonValueKind.Array) return null;

        var files = new List<AppFile>();
        foreach (var entry in list.EnumerateArray())
        {
            string path = entry.GetProperty("path").GetString() ?? "";
            if (!IsInside(folder, path)) return null;
            long? download = entry.TryGetProperty("download", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt64() : null;
            files.Add(new(path, entry.GetProperty("sha256").GetString()!.ToLowerInvariant(), entry.GetProperty("size").GetInt64(), download));
        }
        if (files.Count == 0) return null;

        var changed = await Task.Run(() => files.Where(file => !SameHere(folder, file)).ToList(), token);
        return new(version.ToString(), files, changed);
    }

    /// <summary>Builds the new version in &lt;folder&gt;.update and returns that folder.</summary>
    public static Task<string> StageAsync(CentralApiClient api, string folder, AppDeltaPlan plan, IProgress<long> downloaded, CancellationToken token) =>
        StageAsync((file, stream, progress, t) =>
                api.DownloadAsync($"api/v1/app/files/{file.Sha256}{(file.Download != null ? ".gz" : "")}", stream, progress, t),
            folder, plan, downloaded, token);

    /// <summary>Builds the new version with any way of fetching a changed file (the server, or a test's folder).</summary>
    public static async Task<string> StageAsync(Func<AppFile, Stream, IProgress<long>, CancellationToken, Task> fetch,
        string folder, AppDeltaPlan plan, IProgress<long> downloaded, CancellationToken token)
    {
        string staged = folder + ".update";
        if (Directory.Exists(staged)) Directory.Delete(staged, recursive: true);
        Directory.CreateDirectory(staged);
        var changed = plan.Changed.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        long received = 0;
        foreach (var file in plan.Files)
        {
            token.ThrowIfCancellationRequested();
            string target = Path.Combine(staged, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!changed.Contains(file.Path))
            {
                File.Copy(Path.Combine(folder, file.Path), target, overwrite: true);
                continue;
            }
            long before = received;
            var fetched = new Progress<long>(bytes => downloaded.Report(before + bytes));
            if (file.Download != null)
            {
                // Fetched compressed, unpacked into place.
                string packed = target + ".gz";
                await using (var stream = File.Create(packed))
                    await fetch(file, stream, fetched, token);
                await using (var input = File.OpenRead(packed))
                await using (var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress))
                await using (var output = File.Create(target))
                    await gzip.CopyToAsync(output, token);
                File.Delete(packed);
            }
            else
            {
                await using var stream = File.Create(target);
                await fetch(file, stream, fetched, token);
            }
            received += file.Transfer;
            if (new FileInfo(target).Length != file.Size || await HashAsync(target, token) != file.Sha256)
                throw new InvalidDataException($"{file.Path} arrived damaged.");
        }
        return staged;
    }

    /// <summary>Starts the script that swaps the staged version in once this app has closed.</summary>
    public static void StartSwap(string folder, string staged, string version) =>
        StartSwap(folder, staged, version, Environment.ProcessId);

    public static Process StartSwap(string folder, string staged, string version, int appProcessId)
    {
        string scripts = Path.Combine(Path.GetTempPath(), "ZoomAutoAdmit-Update");
        Directory.CreateDirectory(scripts);
        string script = Path.Combine(scripts, "apply-update.ps1");
        File.WriteAllText(script, SwapScript);
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
                                         "-Install", folder, "-Staged", staged, "-AppPid", appProcessId.ToString(), "-Version", version })
            start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }

    private static bool SameHere(string folder, AppFile file)
    {
        try
        {
            string local = Path.Combine(folder, file.Path);
            if (!File.Exists(local) || new FileInfo(local).Length != file.Size) return false;
            using var stream = File.OpenRead(local);
            return Convert.ToHexString(SHA256.HashData(stream)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
    }

    /// <summary>A path from the server's list that stays inside the app folder.</summary>
    public static bool IsInside(string folder, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return false;
        string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(Path.Combine(root, relative)).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    // Windows PowerShell 5.1, which every Windows has. Kills only programs running from the app folder.
    private const string SwapScript = """
        param([string]$Install, [string]$Staged, [int]$AppPid, [string]$Version)
        $log = Join-Path $env:LOCALAPPDATA 'ZoomAutoAdmit\Logs\update.log'
        function Log($message) { try { Add-Content -Path $log -Value ("[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $message) } catch { } }
        function Move-Retry($from, $to) {
            for ($i = 0; $i -lt 30; $i++) { try { Move-Item -LiteralPath $from -Destination $to -ErrorAction Stop; return } catch { Start-Sleep -Milliseconds 500 } }
            throw "could not move $from"
        }
        $exe = 'ZoomAutoAdmit.WindowsUI.exe'
        Log "update to $Version (changed files only) in $Install"
        try { Wait-Process -Id $AppPid -Timeout 60 -ErrorAction SilentlyContinue } catch { }
        $root = $Install.TrimEnd('\') + '\'
        for ($round = 0; $round -lt 3; $round++) {
            $left = @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) })
            if ($left.Count -eq 0) { break }
            foreach ($process in $left) { Log "closing $($process.Name) ($($process.ProcessId))"; & taskkill.exe /F /T /PID $process.ProcessId | Out-Null }
            Start-Sleep -Seconds 1
        }
        $previous = "$Install.previous"
        if (Test-Path -LiteralPath $previous) { Remove-Item -LiteralPath $previous -Recurse -Force -ErrorAction SilentlyContinue }
        try { Move-Retry $Install $previous }
        catch { Log "the app folder could not be moved, nothing was changed: $_"; Start-Process (Join-Path $Install $exe); exit 1 }
        try { Move-Retry $Staged $Install }
        catch {
            Log "the new version could not take its place; putting the old one back: $_"
            try { Move-Retry $previous $Install } catch { Log "could not put it back: $_" }
            Start-Process (Join-Path $Install $exe); exit 1
        }
        try { Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ZoomAutoAdmit' -Name DisplayVersion -Value $Version } catch { }
        Start-Process (Join-Path $Install $exe)
        try { Remove-Item -LiteralPath $previous -Recurse -Force -ErrorAction Stop } catch { Log "the previous version was left in $previous" }
        Log "update finished"
        """;
}
