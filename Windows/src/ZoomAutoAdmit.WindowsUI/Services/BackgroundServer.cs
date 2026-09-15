using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using ZoomAutoAdmit.WindowsUI.Infrastructure;

namespace ZoomAutoAdmit.WindowsUI.Services;

/// <summary>
/// The server PC's central server in a process of its own: "ZoomAutoAdmit.WindowsUI.exe --server",
/// no window. It starts when Windows signs in and when the app opens, keeps running when the app is
/// closed (so coordinators are never left with "the server answered 502"), and starts the backend or
/// the agent again whenever one of them stops. The app only hands it the start and shows its status.
/// </summary>
public static class BackgroundServer
{
    public const string Argument = "--server";
    private const string MutexName = @"Local\ZoomAutoAdmit.CentralServer";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValue = "ZoomAutoAdmit Server";
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(20);

    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "Central");
    private static string PidFile => Path.Combine(Folder, "server.pid");
    public static string StatusFile => Path.Combine(Folder, "server-status.json");

    /// <summary>The app's own program, when it is the app that is running (never a test host).</summary>
    private static string? AppExe =>
        Environment.ProcessPath is { } path && Path.GetFileName(path).Equals("ZoomAutoAdmit.WindowsUI.exe", StringComparison.OrdinalIgnoreCase) ? path : null;

    public static bool IsRunning()
    {
        try { using var held = Mutex.OpenExisting(MutexName); return true; }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>Starts it, apart from the app, unless it runs already. False when it cannot be started here.</summary>
    public static bool EnsureRunning()
    {
        if (IsRunning()) return true;
        if (AppExe is not { } exe) return false;
        Process.Start(new ProcessStartInfo(exe, Argument)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe)!,
        })?.Dispose();
        return true;
    }

    /// <summary>So it comes back by itself after a restart: started when this Windows user signs in.</summary>
    public static void StartAtSignIn()
    {
        if (AppExe is not { } exe) return;
        string command = $"\"{exe}\" {Argument}";
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (!string.Equals(key.GetValue(RunValue) as string, command, StringComparison.OrdinalIgnoreCase)) key.SetValue(RunValue, command);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            WindowsUiRuntimeLog.Write("SERVER", $"Could not start the server at sign-in: {ex.Message}");
        }
    }

    public static void RemoveStartAtSignIn()
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true); key?.DeleteValue(RunValue, throwOnMissingValue: false); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
    }

    /// <summary>Ends it and what it runs (its backend and agent end with it).</summary>
    public static void Stop()
    {
        try
        {
            if (File.Exists(PidFile) && int.TryParse(File.ReadAllText(PidFile).Trim(), out int pid))
            {
                using var process = Process.GetProcessById(pid);
                if (process.ProcessName.Equals("ZoomAutoAdmit.WindowsUI", StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(10000);
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException) { }
    }

    /// <summary>What it last said about the backend and agent, for the app to show.</summary>
    public static ServerStatus? LastStatus()
    {
        try
        {
            if (!File.Exists(StatusFile)) return null;
            using var json = JsonDocument.Parse(File.ReadAllText(StatusFile));
            var root = json.RootElement;
            return new ServerStatus(Enum.Parse<ServerState>(root.GetProperty("state").GetString()!),
                Enum.Parse<AgentState>(root.GetProperty("agent").GetString()!), root.GetProperty("message").GetString() ?? "");
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    /// <summary>The "--server" process: one per Windows user, as long as this PC is set to run the server.</summary>
    public static async Task RunAsync()
    {
        using var mutex = new Mutex(true, MutexName, out bool first);
        if (!first) return;
        Directory.CreateDirectory(Folder);
        File.WriteAllText(PidFile, Environment.ProcessId.ToString());
        WindowsUiRuntimeLog.Write("SERVER", "The background server started.");
        var store = new RecordingsDashboardSettingsStore();
        using var host = new CentralServerHost(new DatabasePasswordStore());
        host.StatusChanged += status =>
        {
            try { File.WriteAllText(StatusFile, JsonSerializer.Serialize(new { state = status.State.ToString(), agent = status.Agent.ToString(), message = status.Message, at = DateTimeOffset.Now })); }
            catch (IOException) { }
        };
        try
        {
            while (true)
            {
                var settings = store.Load();
                // A PC that connects to someone else's server (or no longer starts one) has nothing to run.
                if (settings.Mode != DashboardMode.Server || !settings.StartWithApp) break;
                try { await host.KeepRunningAsync(settings); }
                catch (Exception ex) { WindowsUiErrorLog.Write("The background server could not start the backend.", ex); }
                await Task.Delay(CheckEvery);
            }
        }
        finally
        {
            WindowsUiRuntimeLog.Write("SERVER", "The background server stopped.");
            try { File.Delete(PidFile); } catch (IOException) { }
        }
    }
}
