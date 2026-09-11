using System.Net;
using System.Runtime.InteropServices;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Models;
using ZoomAutoAdmit.Inspector.Runtime;
using ZoomAutoAdmit.WebAutomation.Api;

namespace ZoomAutoAdmit.Inspector.Commands;

/// <summary>
/// The recording API, for n8n: it puts the recording link it is given - the Google Drive link from
/// the recordings sheet - on the matching DEPI dashboard session. It never opens Zoom.
///
///   serve-api [--background]            run it until Ctrl+C (--background hides the console window)
///   api-autostart [--enable|--disable]  start it at every sign-in, or stop doing so; no flag reports
///
/// See RECORDING-API.md for the key, the port and the n8n settings.
/// </summary>
public static class RecordingApiCommand
{
    public const string StartupFileName = "ZoomAutoAdmit Recording API.cmd";
    private static readonly object FileLogGate = new();
    private const long FileLogLimit = 5 * 1024 * 1024;

    public static async Task<int> ServeAsync(CliOptions options)
    {
        RecordingApiOptions apiOptions;
        try { apiOptions = RecordingApiOptions.FromEnvironment(); }
        catch (InvalidOperationException ex)
        {
            ConsoleLogger.Error(ex.Message);
            return 1;
        }

        string logPath = LogPath();
        // Everything the API and the dashboard step say goes to the file as well, so
        // a run started at sign-in with no window can still be read afterwards.
        void Mirror(LogEntry entry) => AppendToFile(logPath, entry);
        ConsoleLogger.EntryWritten += Mirror;
        try
        {
            if (options.ApiBackground) HideConsoleWindow();
            ConsoleLogger.Info($"[API] Logging to {logPath}");

            var processor = RecordingWorkflow.CreateForApi(RecordingWorkflow.ToConsole, apiOptions.LockWait);
            await using var server = new RecordingApiServer(apiOptions, processor, RecordingWorkflow.ToConsole);
            try { server.Start(); }
            catch (HttpListenerException ex)
            {
                ConsoleLogger.Error($"[API] Could not listen on port {apiOptions.Port} ({ex.ErrorCode}). " +
                                    $"Is the API already running? Another port can be set with {RecordingApiOptions.PortVariable}.");
                return 2;
            }

            using var stop = new CancellationTokenSource();
            ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
            Console.CancelKeyPress += onCancel;
            try
            {
                ConsoleLogger.Info("[API] Press Ctrl+C to stop.");
                try { await Task.Delay(Timeout.Infinite, stop.Token); }
                catch (OperationCanceledException) { }
            }
            finally { Console.CancelKeyPress -= onCancel; }
            return 0;
        }
        finally
        {
            ConsoleLogger.EntryWritten -= Mirror;
        }
    }

    public static int Autostart(CliOptions options)
    {
        string startupFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupFileName);

        if (options.AutostartDisable)
        {
            if (File.Exists(startupFile)) File.Delete(startupFile);
            ConsoleLogger.Success("The recording API will no longer start at sign-in. A running one keeps running until stopped.");
            return 0;
        }

        if (options.AutostartEnable)
        {
            string? exe = Environment.ProcessPath;
            if (exe == null || !exe.EndsWith("ZoomAutoAdmit.Inspector.exe", StringComparison.OrdinalIgnoreCase))
            {
                ConsoleLogger.Error("Run api-autostart from ZoomAutoAdmit.Inspector.exe itself, so the start-up entry can point at it.");
                return 1;
            }
            File.WriteAllText(startupFile, BuildStartupScript(exe));
            ConsoleLogger.Success($"The recording API will start at every sign-in: {startupFile}");
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RecordingApiOptions.KeyVariable, EnvironmentVariableTarget.User)))
                ConsoleLogger.Warn($"{RecordingApiOptions.KeyVariable} is not set for your user, so the API will refuse to start. See RECORDING-API.md.");
            return 0;
        }

        ConsoleLogger.Info(File.Exists(startupFile)
            ? $"Starts at sign-in: yes ({startupFile})"
            : "Starts at sign-in: no. Use api-autostart --enable.");
        ConsoleLogger.Info($"{RecordingApiOptions.KeyVariable} set for your user: " +
                           $"{(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RecordingApiOptions.KeyVariable, EnvironmentVariableTarget.User)) ? "no" : "yes")}");
        return 0;
    }

    /// <summary>
    /// The sign-in script. "start" gives the API its own window, which it then hides; the script
    /// itself ends at once. Deleting the file is all it takes to stop starting the API.
    /// </summary>
    public static string BuildStartupScript(string exePath) =>
        "@echo off\r\n" +
        "rem Starts the Zoom Auto Admit recording API at sign-in. Delete this file to stop that.\r\n" +
        $"start \"\" /min \"{exePath}\" serve-api --background\r\n";

    public static string LogPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "Logs", "recording-api.log");

    private static void AppendToFile(string path, LogEntry entry)
    {
        try
        {
            lock (FileLogGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var info = new FileInfo(path);
                if (info.Exists && info.Length > FileLogLimit)
                    File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, $"[{entry.Timestamp.LocalDateTime:yyyy-MM-dd HH:mm:ss}] [{entry.Level}] {entry.Message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);

    private static void HideConsoleWindow()
    {
        IntPtr window = GetConsoleWindow();
        if (window != IntPtr.Zero) ShowWindow(window, 0);
    }
}
