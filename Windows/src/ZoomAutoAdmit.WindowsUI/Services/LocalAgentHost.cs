using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Reflection;
using ZoomAutoAdmit.CentralAgent;
using ZoomAutoAdmit.WindowsUI.Infrastructure;

namespace ZoomAutoAdmit.WindowsUI.Services;

public enum LocalAgentState { Off, NotSignedIn, Registering, Running, RunningElsewhere, Failed }

public sealed record LocalAgentStatus(LocalAgentState State, string Message)
{
    public static readonly LocalAgentStatus Initial = new(LocalAgentState.Off, "Not started.");
}

/// <summary>
/// This PC's own agent, on every PC - not only the one that runs the server.
///
/// The agent is what carries what happens here to the central server: the attendance the classes
/// record, and the results of the jobs the server sends. It only reads files the app has already
/// written, so a class never waits for the network, and whatever this PC saw while the server was
/// away is sent, in order, once it answers again. A PC that is never registered sends nothing at
/// all - which is what used to happen on every PC that connects to someone else's server.
///
/// Registering needs no one: the signed-in account asks the server for a single-use token for its
/// own PC (POST api/v1/me/devices/enroll) and the token is spent at once, here, in this process.
/// On the PC that runs the server itself, <see cref="CentralServerHost"/> already starts the agent
/// and this host stays out of the way.
/// </summary>
public sealed class LocalAgentHost : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _starting = new(1, 1);
    private readonly DeviceIdentityStore _identities;
    private readonly IDeviceTokenStore _tokens;
    private Process? _agent;
    private JobObject? _job;
    private LocalAgentStatus _status = LocalAgentStatus.Initial;

    public LocalAgentHost(DeviceIdentityStore? identities = null, IDeviceTokenStore? tokens = null)
    {
        _identities = identities ?? new DeviceIdentityStore();
        _tokens = tokens ?? new CredentialManagerDeviceTokenStore();
    }

    public event Action<LocalAgentStatus>? StatusChanged;
    public LocalAgentStatus Status { get { lock (_gate) return _status; } }

    /// <summary>Whether this PC still has to register before anything it does can be sent.</summary>
    public bool NeedsRegistration
    {
        get { try { return _identities.Load() is not { IsRegistered: true }; } catch (InvalidDataException) { return true; } }
    }

    /// <summary>
    /// Makes sure this PC is registered and its agent is running. Safe to call as often as wanted:
    /// it does nothing when all is well, and never throws - a PC that cannot reach the server keeps
    /// working and simply tries again later.
    /// </summary>
    public async Task EnsureRunningAsync(RecordingsDashboardSettings settings, CentralApiClient central, CancellationToken token = default)
    {
        // The server's own PC: CentralServerHost starts the agent there, and two agents with one
        // device token keep disconnecting each other.
        if (settings.Mode == DashboardMode.Server) { Report(LocalAgentState.Off, "This PC runs the server; it starts the agent itself."); return; }
        if (settings.ServerUrl is not { Length: > 0 }) { Report(LocalAgentState.Off, "No central server is set up on the Recordings page."); return; }
        if (!await _starting.WaitAsync(0, token)) return;
        try
        {
            if (_agent is { HasExited: false }) { Report(LocalAgentState.Running, "The agent is running."); return; }
            if (AgentAlreadyRunning()) { Report(LocalAgentState.RunningElsewhere, "An agent is already running on this PC."); return; }

            if (NeedsRegistration)
            {
                if (central.Me == null) { Report(LocalAgentState.NotSignedIn, "Sign in once so this PC can join the server."); return; }
                Report(LocalAgentState.Registering, "Registering this PC with the server…");
                if (await RegisterAsync(settings, central, token) is { } problem) { Report(LocalAgentState.Failed, problem); return; }
            }
            Start();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WindowsUiErrorLog.Write("This PC's central agent could not be started.", ex);
            Report(LocalAgentState.Failed, ex.Message);
        }
        finally { _starting.Release(); }
    }

    /// <summary>Null when the PC is now registered, else why it is not.</summary>
    private async Task<string?> RegisterAsync(RecordingsDashboardSettings settings, CentralApiClient central, CancellationToken token)
    {
        Uri backend;
        try { backend = CentralAgentSettings.ParseBackendUrl(settings.ServerUrl); }
        catch (ArgumentException ex) { return ex.Message; }
        try
        {
            // Short-lived and single-use: it is spent on the next line and never written down.
            string enrollment = await central.EnrollThisPcAsync(Environment.MachineName, token);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var identity = await new AgentRegistrar(http, _identities, _tokens,
                    line => WindowsUiRuntimeLog.Write("AGENT", line))
                .RegisterAsync(backend, enrollment, Environment.MachineName, Version(),
                    CentralAgentSettings.DefaultCapabilities, token);
            WindowsUiRuntimeLog.Write("AGENT", $"This PC joined {backend.Host} as {identity.Name}.");
            return null;
        }
        catch (CentralApiException ex) { return $"The server would not let this PC join: {ex.Message}"; }
        catch (AgentRegistrationException ex) { return ex.Message; }
        catch (HttpRequestException ex) { return $"Could not reach {backend.Host} ({ex.HttpRequestError})."; }
        catch (TaskCanceledException) { return $"{backend.Host} did not answer within 30 seconds."; }
    }

    private void Start()
    {
        var inspector = Path.Combine(AppContext.BaseDirectory, "ZoomAutoAdmit.Inspector.exe");
        if (!File.Exists(inspector)) { Report(LocalAgentState.Failed, "ZoomAutoAdmit.Inspector.exe is missing next to the app."); return; }
        var info = new ProcessStartInfo(inspector, "agent-run")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        lock (_gate)
        {
            // The job object ends the agent with the app, even if the app crashes.
            _job ??= new JobObject();
            _agent = Process.Start(info);
            if (_agent != null) _job.Add(_agent);
        }
        Report(_agent == null ? LocalAgentState.Failed : LocalAgentState.Running,
            _agent == null ? "The agent did not start." : "The agent is running: what this PC does reaches the server.");
    }

    private static string Version() =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";

    private static bool AgentAlreadyRunning()
    {
        try
        {
            using var search = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'ZoomAutoAdmit.Inspector.exe'");
            foreach (ManagementObject process in search.Get())
                using (process)
                    if ((process["CommandLine"] as string ?? "").Contains("agent-run", StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch (ManagementException) { }
        return false;
    }

    private void Report(LocalAgentState state, string message)
    {
        var status = new LocalAgentStatus(state, message);
        lock (_gate)
        {
            if (_status == status) return;
            _status = status;
        }
        WindowsUiRuntimeLog.Write("AGENT", $"{state}: {message}");
        StatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { if (_agent is { HasExited: false }) _agent.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            _agent?.Dispose();
            _agent = null;
            _job?.Dispose();
            _job = null;
        }
        _starting.Dispose();
    }
}
