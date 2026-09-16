using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;

namespace ZoomAutoAdmit.Setup;

/// <summary>
/// What the setup does: close a running copy, put the app (carried inside this .exe as payload.zip)
/// in its folder, give a new PC the server address, make the Start menu (and desktop) shortcut, and
/// list the app in Windows' Apps list, whose Uninstall runs the app with --uninstall. Everything is
/// for the current Windows user: nothing needs administrator rights.
/// </summary>
public static class Installer
{
    public const string Name = "Zoom Auto Admit";
    private const string Exe = "ZoomAutoAdmit.WindowsUI.exe";
    private const string RegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ZoomAutoAdmit";
    private const string WebView2Client = @"Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static Assembly Me => typeof(Installer).Assembly;

    public static string Version => Me.GetName().Version is { } v
        ? (v.Revision > 0 ? $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}" : $"{v.Major}.{v.Minor}.{v.Build}")
        : "1.0.0";
    public static string DefaultFolder => Path.Combine(LocalAppData, "Programs", Name);
    public static string DefaultServer =>
        Me.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "DefaultServer")?.Value ?? "";
    private static string SettingsPath => Path.Combine(LocalAppData, "ZoomAutoAdmit", "Central", "dashboard.json");

    /// <summary>This PC already ran the app: its settings (server or client) are left as they are.</summary>
    public static bool HasSettings() => File.Exists(SettingsPath);
    public static bool HasPayload() => Me.GetManifestResourceInfo("payload.zip") != null;

    /// <summary>Where an earlier setup installed the app, so an update goes to the same place.</summary>
    public static string? InstalledFolder()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKey);
        return key?.GetValue("InstallLocation") as string;
    }

    /// <summary>The Edge WebView2 runtime the app's pages are drawn with (part of Windows 11, usually of 10).</summary>
    public static bool HasWebView2()
    {
        foreach (var (hive, path) in new[]
                 {
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\" + WebView2Client),
                     (Registry.LocalMachine, @"SOFTWARE\" + WebView2Client),
                     (Registry.CurrentUser, @"Software\" + WebView2Client),
                 })
        {
            using var key = hive.OpenSubKey(path);
            if (key?.GetValue("pv") is string version && version.Length > 0 && version != "0.0.0.0") return true;
        }
        return false;
    }

    public static int RunningCopies() =>
        Process.GetProcessesByName("ZoomAutoAdmit.WindowsUI").Length + Process.GetProcessesByName("ZoomAutoAdmit.Inspector").Length;

    public static void Install(string folder, string server, bool desktopShortcut, IProgress<(int Percent, string Text)> progress)
    {
        folder = Path.GetFullPath(folder);
        progress.Report((2, "Closing Zoom Auto Admit…"));
        foreach (var name in new[] { "ZoomAutoAdmit.WindowsUI", "ZoomAutoAdmit.Inspector" })
            foreach (var process in Process.GetProcessesByName(name))
                try { process.Kill(entireProcessTree: true); process.WaitForExit(10000); } catch { }

        // The previous version goes first, so no old file is left beside the new ones. A folder that
        // holds something else is refused rather than emptied.
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
        {
            if (!File.Exists(Path.Combine(folder, Exe)))
                throw new InvalidOperationException($"{folder} already holds other files. Choose an empty folder or the one the app is installed in.");
            progress.Report((5, "Removing the previous version…"));
            DeleteWithRetry(folder);
        }
        Directory.CreateDirectory(folder);

        Extract(folder, progress);

        // A PC that never ran the app connects to the admin's server; one that did keeps its settings.
        if (!HasSettings() && !string.IsNullOrWhiteSpace(server))
        {
            progress.Report((90, "Setting the server address…"));
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { Mode = "Client", ServerUrl = server.Trim(), StartWithApp = false },
                new JsonSerializerOptions { WriteIndented = true }));
        }

        Register(folder, desktopShortcut, progress);
    }

    /// <summary>Unpacks the app carried in this setup into <paramref name="folder"/>.</summary>
    private static void Extract(string folder, IProgress<(int Percent, string Text)> progress)
    {
        using var payload = Me.GetManifestResourceStream("payload.zip") ?? throw new InvalidOperationException("This setup does not carry the app.");
        using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
        string root = folder.EndsWith(Path.DirectorySeparatorChar) ? folder : folder + Path.DirectorySeparatorChar;
        int done = 0, total = zip.Entries.Count;
        foreach (var entry in zip.Entries)
        {
            string target = Path.GetFullPath(Path.Combine(folder, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;       // never outside the folder
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
            if (++done % 20 == 0 || done == total)
                progress.Report((8 + done * 80 / Math.Max(1, total), $"Copying the app… {done} of {total} files"));
        }
    }

    /// <summary>Shortcuts and the entry in Windows' Apps list.</summary>
    private static void Register(string folder, bool desktopShortcut, IProgress<(int Percent, string Text)> progress)
    {
        progress.Report((93, "Making the shortcuts…"));
        string exe = Path.Combine(folder, Exe);
        MakeShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), $"{Name}.lnk"), exe, folder);
        string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{Name}.lnk");
        if (desktopShortcut) MakeShortcut(desktop, exe, folder);
        else if (File.Exists(desktop)) File.Delete(desktop);

        progress.Report((97, "Adding it to Windows' Apps list…"));
        long kilobytes = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024;
        using (var key = Registry.CurrentUser.CreateSubKey(RegistryKey))
        {
            key.SetValue("DisplayName", Name);
            key.SetValue("DisplayVersion", Version);
            key.SetValue("Publisher", Name);
            key.SetValue("DisplayIcon", exe + ",0");
            key.SetValue("InstallLocation", folder);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
            key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, kilobytes), RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        progress.Report((100, "Done."));
    }

    /// <summary>
    /// Replaces the installed app with the one in this setup, started by the app itself ("Update
    /// now"). The app has already checked that no class is running or about to start, and has
    /// closed itself. The new version is unpacked beside the old one first and only then swapped
    /// in; if anything fails the old folder is put back, so a coordinator is never left with a
    /// broken app and nobody around to fix it. The server address and every setting are kept.
    /// </summary>
    public static void Update(string folder, IProgress<(int Percent, string Text)> progress)
    {
        folder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        if (!File.Exists(Path.Combine(folder, Exe)))
            throw new InvalidOperationException($"{folder} does not hold the app, so there is nothing to update.");
        Log($"update to {Version} in {folder}");

        progress.Report((2, "Waiting for Zoom Auto Admit to close…"));
        var until = DateTime.UtcNow.AddSeconds(60);
        while (RunningFrom(folder).Any(p => p.ProcessName.Equals("ZoomAutoAdmit.WindowsUI", StringComparison.OrdinalIgnoreCase)) && DateTime.UtcNow < until)
            Thread.Sleep(500);
        // Everything still running from this folder is part of the app and is started again with it:
        // the agent, an admission monitor, and the browser driver Playwright keeps in .playwright
        // (node.exe). Windows will not move a folder while any program inside it runs - found when
        // the first update was tried, with node.exe still open. Never another install on the PC.
        for (int round = 0; round < 3; round++)
        {
            var left = RunningFrom(folder);
            if (left.Count == 0) break;
            foreach (var process in left)
            {
                Log($"closing {process.ProcessName} ({process.Id})");
                try { process.Kill(entireProcessTree: true); process.WaitForExit(10000); } catch { }
            }
            Thread.Sleep(1000);
        }

        string fresh = folder + ".update", previous = folder + ".previous";
        if (Directory.Exists(fresh)) DeleteWithRetry(fresh);
        if (Directory.Exists(previous)) DeleteWithRetry(previous);
        Directory.CreateDirectory(fresh);
        Extract(fresh, progress);

        progress.Report((89, "Switching to the new version…"));
        try { MoveWithRetry(folder, previous); }
        catch
        {
            // Nothing was swapped: the installed app is untouched. The unpacked copy is not needed.
            try { DeleteWithRetry(fresh); } catch { }
            throw;
        }
        try { MoveWithRetry(fresh, folder); }
        catch
        {
            Log("the new folder could not take the old one's place; putting the old version back");
            try { MoveWithRetry(previous, folder); } catch (Exception back) { Log($"could not put it back: {back.Message}"); }
            throw;
        }

        string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{Name}.lnk");
        Register(folder, desktopShortcut: File.Exists(desktop), progress);
        try { DeleteWithRetry(previous); } catch (Exception ex) { Log($"the previous version was left in {previous}: {ex.Message}"); }
        Log("update finished");
    }

    /// <summary>Every process whose program is a file inside <paramref name="folder"/>.</summary>
    private static List<Process> RunningFrom(string folder)
    {
        string root = folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var found = new List<Process>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.MainModule?.FileName is { } file && file.StartsWith(root, StringComparison.OrdinalIgnoreCase)) found.Add(process);
            }
            catch { }                                  // gone already, or not ours to look at
        }
        return found;
    }

    public static void Log(string line)
    {
        try
        {
            string logs = Path.Combine(LocalAppData, "ZoomAutoAdmit", "Logs");
            Directory.CreateDirectory(logs);
            File.AppendAllText(Path.Combine(logs, "update.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>A folder whose files were in use a moment ago can refuse to move for a few seconds.</summary>
    private static void MoveWithRetry(string from, string to)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { Directory.Move(from, to); return; }
            catch (Exception) when (attempt < 15) { Thread.Sleep(1000); }
        }
    }

    public static void Launch(string folder)
    {
        string exe = Path.Combine(Path.GetFullPath(folder), Exe);
        if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = true });
    }

    private static void MakeShortcut(string path, string target, string folder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows could not make the shortcut.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(path);
        link.TargetPath = target;
        link.WorkingDirectory = folder;
        link.IconLocation = target + ",0";
        link.Description = "Opens Zoom meetings, lets people in, and keeps the LMS up to date.";
        link.Save();
    }

    /// <summary>A file just released by a closed process can stay locked a moment: a few tries.</summary>
    private static void DeleteWithRetry(string folder)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { Directory.Delete(folder, recursive: true); return; }
            catch (Exception) when (attempt < 6) { Thread.Sleep(1000); }
        }
    }
}
