using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace ZoomAutoAdmit.WindowsUI.Services;

/// <summary>
/// "Uninstall" from Windows' Apps list runs the app with --uninstall. It removes what the setup
/// put on the PC - the app's folder, its shortcuts, its entry in the Apps list and the scheduled
/// classes that would start it - and, only when asked, the data it kept (%LOCALAPPDATA%\ZoomAutoAdmit).
/// It refuses to delete a folder the setup did not install, so a developer's copy is never removed.
/// </summary>
public static class Uninstaller
{
    public const string RegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ZoomAutoAdmit";
    private const string Name = "Zoom Auto Admit";

    public static void Run()
    {
        if (MessageBox.Show($"Remove {Name} from this PC?", $"Uninstall {Name}", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        bool removeData = MessageBox.Show(
            "Also remove your data from this PC (settings, rosters, attendance, logs and browser sign-ins)?\n\n" +
            "Choose No to keep it for a later install. Your data on the server is not touched either way.",
            $"Uninstall {Name}", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

        string folder = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        string? installed;
        using (var key = Registry.CurrentUser.OpenSubKey(RegistryKey))
            installed = key?.GetValue("InstallLocation") as string;
        bool ours = installed != null && string.Equals(Path.TrimEndingDirectorySeparator(installed), folder, StringComparison.OrdinalIgnoreCase);

        StopOtherCopies();                        // the background server too
        BackgroundServer.RemoveStartAtSignIn();
        RemoveScheduledClasses();
        foreach (var link in Shortcuts()) try { if (File.Exists(link)) File.Delete(link); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(RegistryKey, throwOnMissingSubKey: false); } catch { }
        if (removeData)
        {
            try { Directory.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit"), recursive: true); }
            catch { }
        }
        if (ours)
        {
            // The folder is in use until this process ends: a hidden command removes it a moment later.
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 3 /nobreak >nul & rmdir /s /q \"{folder}\"")
            { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        }
        MessageBox.Show(ours ? $"{Name} was removed." : $"{Name}'s shortcuts and Apps entry were removed. Its folder was not installed by the setup, so it was left: {folder}",
            $"Uninstall {Name}", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>The Start menu and desktop shortcuts the setup makes.</summary>
    public static IEnumerable<string> Shortcuts() =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), $"{Name}.lnk"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{Name}.lnk"),
    ];

    private static void StopOtherCopies()
    {
        int me = Environment.ProcessId;
        foreach (var name in new[] { "ZoomAutoAdmit.WindowsUI", "ZoomAutoAdmit.Inspector" })
            foreach (var process in Process.GetProcessesByName(name))
            {
                if (process.Id == me) continue;
                try { process.Kill(entireProcessTree: true); process.WaitForExit(5000); } catch { }
            }
    }

    /// <summary>The Windows tasks that open the scheduled classes, which would point at the removed app.</summary>
    private static void RemoveScheduledClasses()
    {
        try
        {
            var query = Process.Start(new ProcessStartInfo("schtasks.exe", "/Query /FO CSV /NH")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            string output = query.StandardOutput.ReadToEnd();
            query.WaitForExit(15000);
            var tasks = output.Split('\n').Select(line => line.Split(',').FirstOrDefault()?.Trim('"', ' ', '\r') ?? "")
                .Where(t => t.StartsWith(@"\ZoomAutoAdmit\", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var task in tasks)
                Process.Start(new ProcessStartInfo("schtasks.exe", $"/Delete /TN \"{task}\" /F") { UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(10000);
        }
        catch { }
    }
}
