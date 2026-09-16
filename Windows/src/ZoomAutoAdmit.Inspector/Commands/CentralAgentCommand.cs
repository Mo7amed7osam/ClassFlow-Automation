using System.Reflection;
using ZoomAutoAdmit.CentralAgent;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Models;
using ZoomAutoAdmit.Inspector.Runtime;

namespace ZoomAutoAdmit.Inspector.Commands;

/// <summary>
/// The connection to the central backend (Phase 1). Nothing starts on its own yet.
///
///   agent-register --backend https://central.example.com [--name PC-01]
///       once per PC; the enrollment token is read from ZOOM_AUTO_ADMIT_ENROLLMENT_TOKEN or asked
///       for (hidden) - never taken from the command line, where other programs could read it
///   agent-run [--background]    stay connected and run the jobs the backend sends, until Ctrl+C
///   agent-status                what this PC is registered as
///
/// See Windows/CENTRAL-AGENT.md.
/// </summary>
public static class CentralAgentCommand
{
    public const string EnrollmentTokenVariable = "ZOOM_AUTO_ADMIT_ENROLLMENT_TOKEN";

    public static async Task<int> RegisterAsync(CliOptions options)
    {
        Uri backend;
        try { backend = CentralAgentSettings.ParseBackendUrl(options.CentralBackendUrl); }
        catch (ArgumentException ex)
        {
            ConsoleLogger.Error($"--backend: {ex.Message}");
            return 1;
        }

        string? enrollment = Environment.GetEnvironmentVariable(EnrollmentTokenVariable);
        if (string.IsNullOrWhiteSpace(enrollment))
        {
            if (Console.IsInputRedirected)
            {
                ConsoleLogger.Error($"Set {EnrollmentTokenVariable} to the enrollment token, or run this in a console to type it.");
                return 1;
            }
            enrollment = ReadHidden("Enrollment token (from the backend operator; not shown): ");
        }
        if (string.IsNullOrWhiteSpace(enrollment))
        {
            ConsoleLogger.Error("No enrollment token was given.");
            return 1;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            var identity = await new AgentRegistrar(http, new DeviceIdentityStore(), new CredentialManagerDeviceTokenStore(), RecordingWorkflow.ToConsole)
                .RegisterAsync(backend, enrollment, options.DeviceName ?? Environment.MachineName, AgentVersion(),
                    CentralAgentSettings.DefaultCapabilities, CancellationToken.None);
            ConsoleLogger.Success($"Registered as {identity.Name} (device {identity.DeviceId}) with {backend.Host}. " +
                                  "The device token is in Windows Credential Manager. Start with agent-run.");
            return 0;
        }
        catch (AgentRegistrationException ex)
        {
            ConsoleLogger.Error(ex.Message);
            return 2;
        }
        catch (HttpRequestException ex)
        {
            ConsoleLogger.Error($"Could not reach {backend.Host} ({ex.HttpRequestError}).");
            return 2;
        }
        catch (TaskCanceledException)
        {
            ConsoleLogger.Error($"{backend.Host} did not answer within 30 seconds.");
            return 2;
        }
    }

    public static async Task<int> RunAsync(CliOptions options)
    {
        var identity = new DeviceIdentityStore().Load();
        if (identity is not { IsRegistered: true })
        {
            ConsoleLogger.Error("This PC is not registered with a central backend. Run agent-register first.");
            return 1;
        }

        string logPath = CentralAgentPaths.LogFile;
        void Mirror(LogEntry entry) => RecordingApiCommand.AppendToFile(logPath, entry);
        ConsoleLogger.EntryWritten += Mirror;
        try
        {
            if (options.ApiBackground) RecordingApiCommand.HideConsoleWindow();
            ConsoleLogger.Info($"[AGENT] Logging to {logPath}");

            var settings = new CentralAgentSettings
            {
                BackendUrl = CentralAgentSettings.ParseBackendUrl(identity.BackendUrl),
                Version = AgentVersion(),
            };
            // The same workflow the local recording API runs, lock and all: a job and a local API
            // request never drive the dashboard at the same time.
            var processor = RecordingWorkflow.CreateForApi(RecordingWorkflow.ToConsole);
            var service = new CentralAgentService(
                settings,
                identity,
                new CredentialManagerDeviceTokenStore(),
                new ClientWebSocketFactory(),
                new JobJournal(JobJournal.DefaultPath),
                [new RecordingProcessJobHandler(processor)],
                RecordingWorkflow.ToConsole);

            using var stop = new CancellationTokenSource();
            ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
            Console.CancelKeyPress += onCancel;
            // The attendance this PC takes in meetings goes to the backend too, next to the jobs.
            using var uploads = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            using var attendanceHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var uploading = new AttendanceUploader(settings.BackendUrl, new CredentialManagerDeviceTokenStore(), attendanceHttp,
                log: RecordingWorkflow.ToConsole).RunAsync(uploads.Token);
            // And what this PC did by itself: the classes it opened and the LMS steps it finished.
            var reporting = new ActivityUploader(settings.BackendUrl, new CredentialManagerDeviceTokenStore(), attendanceHttp,
                write: RecordingWorkflow.ToConsole).RunAsync(uploads.Token);
            try
            {
                ConsoleLogger.Info($"[AGENT] Connecting to {settings.BackendUrl.Host} as {identity.Name}. Press Ctrl+C to stop.");
                var reason = await service.RunAsync(stop.Token);
                return reason switch
                {
                    AgentStopReason.Stopped => 0,
                    AgentStopReason.NotRegistered => 1,
                    _ => 3,
                };
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;
                uploads.Cancel();
                // Both end at once when cancelled; anything half-sent is sent again next time.
                await uploading;
                await reporting;
            }
        }
        finally
        {
            ConsoleLogger.EntryWritten -= Mirror;
        }
    }

    public static int Status()
    {
        var store = new DeviceIdentityStore();
        var identity = store.Load();
        bool tokenStored = !string.IsNullOrWhiteSpace(new CredentialManagerDeviceTokenStore().Read());
        ConsoleLogger.Info($"Identity file: {store.Path}");
        if (identity == null)
        {
            ConsoleLogger.Info("Not registered (no installation id yet). Use agent-register --backend <url>.");
            return 0;
        }
        ConsoleLogger.Info($"Installation id: {identity.InstallationId}");
        ConsoleLogger.Info($"Registered: {(identity.IsRegistered ? "yes" : "no")}");
        if (identity.IsRegistered)
        {
            ConsoleLogger.Info($"Device: {identity.Name} ({identity.DeviceId})");
            ConsoleLogger.Info($"Backend: {identity.BackendUrl}");
            ConsoleLogger.Info($"Registered at: {identity.RegisteredAt:yyyy-MM-dd HH:mm} UTC");
        }
        ConsoleLogger.Info($"Device token in Credential Manager: {(tokenStored ? "yes" : "no")}");
        ConsoleLogger.Info($"Job messages waiting for the backend: {new JobJournal(JobJournal.DefaultPath).PendingMessages().Count}");
        return 0;
    }

    public static string AgentVersion()
    {
        string? version = typeof(CentralAgentCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(version) ? "1.0.0" : version.Split('+')[0];
    }

    private static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        var typed = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (typed.Length > 0) typed.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) typed.Append(key.KeyChar);
        }
        Console.WriteLine();
        return typed.ToString().Trim();
    }
}
