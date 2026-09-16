using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ZoomAutoAdmit.WindowsUI.Infrastructure;

namespace ZoomAutoAdmit.WindowsUI.Services;

public enum ServerState { Stopped, Starting, Running, RunningElsewhere, Failed }
public enum AgentState { NotRegistered, Stopped, Running, RunningElsewhere, Exited }

public sealed record ServerStatus(ServerState State, AgentState Agent, string Message)
{
    public static readonly ServerStatus Initial = new(ServerState.Stopped, AgentState.Stopped, "Not started.");
    public bool IsUp => State is ServerState.Running or ServerState.RunningElsewhere;
}

/// <summary>
/// The environment the central backend is started with, in server mode. The API-key hashes (not
/// secret: SHA-256 of the keys) and CENTRAL_ENVIRONMENT come from the "$env:CENTRAL_* = '...'" lines
/// of the repository's api.env - the same lines start-backend.ps1 uses, nothing else in that file is
/// read, and nothing in it is ever executed. The database address comes from the settings, and its
/// password from Windows Credential Manager.
/// </summary>
public static class BackendEnvironment
{
    private static readonly Regex Assignment = new(@"^\s*\$env:(CENTRAL_[A-Z_]+)\s*=\s*(?:""([^""$`]*)""|'([^']*)')\s*$", RegexOptions.CultureInvariant);

    /// <summary>The CENTRAL_* values api.env sets; every API-key hash it has ever set is kept.</summary>
    public static Dictionary<string, string> ReadApiEnv(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var hashes = new List<string>();
        foreach (var line in lines)
        {
            var m = Assignment.Match(line);
            if (!m.Success) continue;
            var name = m.Groups[1].Value;
            var value = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            if (name == "CENTRAL_CLIENT_API_KEY_HASHES")
                hashes.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            else
                values[name] = value;
        }
        if (hashes.Count > 0) values["CENTRAL_CLIENT_API_KEY_HASHES"] = string.Join(",", hashes.Distinct(StringComparer.OrdinalIgnoreCase));
        return values;
    }

    public static string DatabaseUrl(RecordingsDashboardSettings s, string password) =>
        $"postgresql+asyncpg://{Uri.EscapeDataString(s.DatabaseUser)}:{Uri.EscapeDataString(password)}@{s.DatabaseHost}:{s.DatabasePort}/{Uri.EscapeDataString(s.DatabaseName)}";

    /// <summary>
    /// The variables to start uvicorn with. The database URL from api.env is ignored (the settings
    /// name the database), and the retired CENTRAL_ADMIN_USERS is never passed on.
    /// </summary>
    public static Dictionary<string, string> Build(RecordingsDashboardSettings settings, string password, IEnumerable<string> apiEnvLines)
    {
        var env = ReadApiEnv(apiEnvLines);
        env.Remove("CENTRAL_DATABASE_URL");
        env.Remove("CENTRAL_ADMIN_USERS");
        if (!env.ContainsKey("CENTRAL_CLIENT_API_KEY_HASHES"))
            throw new InvalidOperationException("api.env sets no CENTRAL_CLIENT_API_KEY_HASHES, so n8n could not use the backend.");
        env.TryAdd("CENTRAL_ENVIRONMENT", "development");
        env["CENTRAL_DATABASE_URL"] = DatabaseUrl(settings, password);
        return env;
    }
}

public interface ICentralServerHost : IDisposable
{
    ServerStatus Status { get; }
    event Action<ServerStatus>? StatusChanged;
    Task StartAsync(RecordingsDashboardSettings settings, CancellationToken cancellationToken = default);
    void Stop();
}

/// <summary>
/// Runs this PC's central backend (uvicorn, from Backend\.venv) and its agent (Inspector.exe
/// agent-run) as hidden child processes, in a Windows job object that ends them when the app
/// closes - even if it crashes. A backend already answering on the port (started by hand, e.g.
/// start-backend.ps1) is used as it is, and so is an agent that is already running: two agents
/// with one device token would keep disconnecting each other.
/// </summary>
public sealed class CentralServerHost(IDatabasePasswordStore passwords) : ICentralServerHost
{
    private static readonly string LogFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "Logs");
    private static readonly string DeviceFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "Central", "device.json");
    private readonly object _gate = new();
    private readonly SemaphoreSlim _starting = new(1, 1);
    private Process? _backend, _agent;
    private JobObject? _job;
    private string? _secretToMask;
    private ServerStatus _status = ServerStatus.Initial;

    public event Action<ServerStatus>? StatusChanged;
    public ServerStatus Status { get { lock (_gate) return _status; } }

    /// <summary>
    /// The app's host: the backend and agent run in the background server (BackgroundServer), which
    /// outlives the app, rather than as children of the window. Off for the background server itself.
    /// </summary>
    public bool HandToBackground { get; init; }

    /// <summary>For the background server: starts whatever is not running, and says nothing when all is.</summary>
    public async Task KeepRunningAsync(RecordingsDashboardSettings settings, CancellationToken cancellationToken = default)
    {
        bool agentUp = _agent is { HasExited: false } || !File.Exists(DeviceFile) || AgentAlreadyRunning();
        if (agentUp && settings.BaseUri is { } uri && await IsHealthyAsync(uri, cancellationToken)) return;
        await StartAsync(settings, cancellationToken);
    }

    /// <summary>Starts the background server (or finds it running) and waits for its backend to answer.</summary>
    private async Task StartInBackgroundAsync(RecordingsDashboardSettings settings, CancellationToken cancellationToken)
    {
        var baseUri = settings.BaseUri!;
        if (!await IsHealthyAsync(baseUri, cancellationToken))
        {
            if (settings.Problem() is { } problem) { Report(ServerState.Failed, Status.Agent, problem); return; }
            if (string.IsNullOrEmpty(passwords.Read())) { Report(ServerState.Failed, Status.Agent, "Save the database password first (below)."); return; }
            if (!BackgroundServer.EnsureRunning()) { Report(ServerState.Failed, Status.Agent, "The background server could not be started from here."); return; }
            Report(ServerState.Starting, Status.Agent, "Starting the server in the background…");
            // A database update (backed up first) can take a while before the backend answers.
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (DateTime.UtcNow < deadline && !await IsHealthyAsync(baseUri, cancellationToken))
            {
                if (BackgroundServer.LastStatus() is { State: ServerState.Failed } failed && !BackgroundServer.IsRunning())
                {
                    Report(ServerState.Failed, failed.Agent, failed.Message);
                    return;
                }
                await Task.Delay(1000, cancellationToken);
            }
            if (!await IsHealthyAsync(baseUri, cancellationToken))
            {
                var last = BackgroundServer.LastStatus();
                Report(ServerState.Failed, Status.Agent, last is { State: ServerState.Failed } ? last.Message
                    : "The server did not answer within 2 minutes. See Logs\\central-backend.log.");
                return;
            }
        }
        else BackgroundServer.EnsureRunning();       // started by hand: the background server still watches it
        // The agent follows the backend by a moment.
        for (int i = 0; i < 10 && File.Exists(DeviceFile) && !AgentAlreadyRunning(); i++) await Task.Delay(1000, cancellationToken);
        var agent = !File.Exists(DeviceFile) ? AgentState.NotRegistered : AgentAlreadyRunning() ? AgentState.RunningElsewhere : AgentState.Stopped;
        Report(ServerState.RunningElsewhere, agent, "The server runs in the background: it keeps going when the app is closed.");
    }

    private void Report(ServerState state, AgentState agent, string message)
    {
        var status = new ServerStatus(state, agent, message);
        lock (_gate) _status = status;
        WindowsUiRuntimeLog.Write("CENTRAL", $"{state} / agent {agent}: {message}");
        StatusChanged?.Invoke(status);
    }

    public async Task StartAsync(RecordingsDashboardSettings settings, CancellationToken cancellationToken = default)
    {
        if (settings.Mode != DashboardMode.Server) return;
        if (!await _starting.WaitAsync(0, cancellationToken)) return;       // a start is already under way
        try
        {
            if (HandToBackground) { await StartInBackgroundAsync(settings, cancellationToken); return; }
            var baseUri = settings.BaseUri!;
            if (await IsHealthyAsync(baseUri, cancellationToken))
            {
                var external = _backend is null || _backend.HasExited;
                Report(external ? ServerState.RunningElsewhere : ServerState.Running, StartAgent(), external
                    ? $"A backend is already running on port {settings.Port}; using it."
                    : "The backend is running.");
                return;
            }
            if (settings.Problem() is { } problem) { Report(ServerState.Failed, Status.Agent, problem); return; }
            var password = passwords.Read();
            if (string.IsNullOrEmpty(password)) { Report(ServerState.Failed, Status.Agent, "Save the database password first (below)."); return; }

            var apiEnv = Path.Combine(Path.GetDirectoryName(settings.BackendFolder.TrimEnd('\\', '/'))!, "api.env");
            Dictionary<string, string> env;
            try { env = BackendEnvironment.Build(settings, password, File.Exists(apiEnv) ? File.ReadLines(apiEnv) : []); }
            catch (InvalidOperationException ex) { Report(ServerState.Failed, Status.Agent, ex.Message); return; }

            // The key that encrypts the LMS passwords users keep on the server. It is the server's
            // own secret: made once on this PC, kept by Windows, never in the database.
            env["CENTRAL_SECRETS_KEY"] = SecretsKey();
            if (await PrepareDatabaseAsync(settings, env, password, cancellationToken) is { } databaseProblem)
            {
                Report(ServerState.Failed, Status.Agent, databaseProblem);
                return;
            }

            Report(ServerState.Starting, Status.Agent, "Starting the backend…");
            StopProcesses();
            lock (_gate)
            {
                _secretToMask = password;
                _job = new JobObject();
                _backend = Launch(Path.Combine(settings.BackendFolder, ".venv", "Scripts", "python.exe"),
                    $"-m uvicorn central_backend.main:create_app --factory --host 127.0.0.1 --port {settings.Port} --ws-max-size 65536",
                    settings.BackendFolder, env, "central-backend.log");
            }

            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (DateTime.UtcNow < deadline)
            {
                if (_backend is { HasExited: true } exited)
                {
                    Report(ServerState.Failed, Status.Agent, $"The backend stopped at once (exit code {exited.ExitCode}). See Logs\\central-backend.log.");
                    return;
                }
                if (await IsHealthyAsync(baseUri, cancellationToken)) { Report(ServerState.Running, StartAgent(), "The backend is running."); return; }
                await Task.Delay(500, cancellationToken);
            }
            Report(ServerState.Failed, Status.Agent, "The backend did not answer within 45 seconds. See Logs\\central-backend.log.");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            WindowsUiErrorLog.Write("The central backend could not be started.", ex);
            Report(ServerState.Failed, Status.Agent, ex.Message);
        }
        finally { _starting.Release(); }
    }

    private static string SecretsKey()
    {
        var store = new DatabasePasswordStore("ZoomAutoAdmit/Central/SecretsKey");
        if (store.Read() is { Length: > 0 } key) return key;
        key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        store.Save(key);
        WindowsUiRuntimeLog.Write("CENTRAL", "Made the server's secrets key (kept by Windows).");
        return key;
    }

    /// <summary>
    /// A new version of the backend can bring a new database migration. When the database is behind,
    /// it is backed up first (pg_dump, into E:\zooommmm\db-backups or Logs\db-backups) and only then
    /// migrated - migrations only add. Null when all is well, else why the backend was not started.
    /// </summary>
    private async Task<string?> PrepareDatabaseAsync(RecordingsDashboardSettings settings, Dictionary<string, string> env, string password, CancellationToken token)
    {
        string python = Path.Combine(settings.BackendFolder, ".venv", "Scripts", "python.exe");
        string versions = Path.Combine(settings.BackendFolder, "migrations", "versions");
        string? head = Directory.Exists(versions)
            ? Directory.GetFiles(versions, "*.py").Select(Path.GetFileNameWithoutExtension).Where(n => n != null && Regex.IsMatch(n, @"^\d{4}_")).Max()
            : null;
        if (head == null) return null;
        const string probe = "import asyncio,os,asyncpg\n" +
            "async def m():\n" +
            " c=await asyncpg.connect(os.environ['CENTRAL_DATABASE_URL'].replace('postgresql+asyncpg','postgresql'))\n" +
            " try: print(await c.fetchval('select version_num from alembic_version'))\n" +
            " finally: await c.close()\n" +
            "asyncio.run(m())";
        var (probeExit, current) = await RunAsync(python, ["-c", probe], settings.BackendFolder, env, password, token);
        current = current.Trim();
        if (probeExit != 0) return "The database could not be read before starting (see Logs\\central-migrate.log).";
        if (current == head) return null;

        Report(ServerState.Starting, Status.Agent, $"Backing up the database before updating it ({current} → {head})…");
        string folder = Directory.Exists(@"E:\zooommmm\db-backups") ? @"E:\zooommmm\db-backups" : Path.Combine(LogFolder, "db-backups");
        Directory.CreateDirectory(folder);
        string dump = Path.Combine(folder, $"{settings.DatabaseName}-{settings.DatabasePort}_before-{head}_{DateTime.Now:yyyyMMdd-HHmmss}.dump");
        string? pgDump = Directory.GetDirectories(@"C:\Program Files\PostgreSQL").OrderByDescending(d => int.TryParse(Path.GetFileName(d), out var v) ? v : 0)
            .Select(d => Path.Combine(d, "bin", "pg_dump.exe")).FirstOrDefault(File.Exists);
        if (pgDump == null) return "pg_dump was not found, so the database was not backed up and not updated.";
        var pgEnv = new Dictionary<string, string> { ["PGPASSWORD"] = password };
        var (dumpExit, _) = await RunAsync(pgDump, ["-h", settings.DatabaseHost, "-p", settings.DatabasePort.ToString(), "-U", settings.DatabaseUser, "-Fc", "-f", dump, settings.DatabaseName],
            settings.BackendFolder, pgEnv, password, token);
        if (dumpExit != 0 || !File.Exists(dump)) return "The database backup failed, so it was not updated (see Logs\\central-migrate.log).";

        Report(ServerState.Starting, Status.Agent, $"Updating the database to {head}…");
        var (migrateExit, _) = await RunAsync(python, ["-m", "central_backend.cli", "migrate"], settings.BackendFolder, env, password, token);
        if (migrateExit != 0) return $"The database update failed; the backup is {Path.GetFileName(dump)} (see Logs\\central-migrate.log).";
        WindowsUiRuntimeLog.Write("CENTRAL", $"Database updated {current} → {head}; backup {dump}.");
        return null;
    }

    /// <summary>Runs a helper to the end; its output goes to Logs\central-migrate.log with the password masked.</summary>
    private static async Task<(int Exit, string Output)> RunAsync(string file, IEnumerable<string> arguments, string folder,
        IDictionary<string, string> env, string password, CancellationToken token)
    {
        var info = new ProcessStartInfo(file) { WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("CENTRAL_", StringComparison.OrdinalIgnoreCase)).ToList()) info.Environment.Remove(key);
        foreach (var (key, value) in env) info.Environment[key] = value;
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{Path.GetFileName(file)} did not start.");
        var output = process.StandardOutput.ReadToEndAsync(token);
        var errors = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        string text = (await output), problems = (await errors);
        Directory.CreateDirectory(LogFolder);
        string Mask(string s) => password.Length > 0 ? s.Replace(password, "***").Replace(Uri.EscapeDataString(password), "***") : s;
        await File.AppendAllTextAsync(Path.Combine(LogFolder, "central-migrate.log"),
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {Path.GetFileName(file)} exited {process.ExitCode}\n{Mask(text)}{Mask(problems)}\n", token);
        return (process.ExitCode, text);
    }

    /// <summary>Starts the agent unless this PC is not registered or one is already running.</summary>
    private AgentState StartAgent()
    {
        if (_agent is { HasExited: false }) return AgentState.Running;
        if (!File.Exists(DeviceFile)) return AgentState.NotRegistered;
        if (AgentAlreadyRunning()) return AgentState.RunningElsewhere;
        var inspector = Path.Combine(AppContext.BaseDirectory, "ZoomAutoAdmit.Inspector.exe");
        if (!File.Exists(inspector)) return AgentState.Stopped;
        lock (_gate)
        {
            _job ??= new JobObject();
            _agent = Launch(inspector, "agent-run", AppContext.BaseDirectory, null, "central-agent-host.log");
        }
        return AgentState.Running;
    }

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

    private Process Launch(string file, string arguments, string folder, IDictionary<string, string>? env, string logName)
    {
        var info = new ProcessStartInfo(file, arguments)
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("CENTRAL_", StringComparison.OrdinalIgnoreCase)).ToList())
            info.Environment.Remove(key);
        if (env != null) foreach (var (key, value) in env) info.Environment[key] = value;

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        Directory.CreateDirectory(LogFolder);
        var log = new StreamWriter(new FileStream(Path.Combine(LogFolder, logName), FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        void Write(string? line)
        {
            if (line is null) return;
            var secret = _secretToMask;
            lock (log) log.WriteLine(secret is { Length: > 0 } ? line.Replace(secret, "***") : line);
        }
        process.OutputDataReceived += (_, e) => Write(e.Data);
        process.ErrorDataReceived += (_, e) => Write(e.Data);
        process.Exited += (_, _) =>
        {
            Write($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] process exited ({SafeExitCode(process)})");
            try { log.Dispose(); } catch (ObjectDisposedException) { }
            OnChildExited(process);
        };
        Write($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] starting {Path.GetFileName(file)} {arguments}");
        process.Start();
        _job!.Add(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static string SafeExitCode(Process p) { try { return p.ExitCode.ToString(); } catch (InvalidOperationException) { return "?"; } }

    private void OnChildExited(Process process)
    {
        var current = Status;
        if (ReferenceEquals(process, _agent) && current.Agent == AgentState.Running)
            Report(current.State, AgentState.Exited, "The agent stopped. See Logs\\central-agent.log.");
        else if (ReferenceEquals(process, _backend) && current.State == ServerState.Running)
            Report(ServerState.Failed, current.Agent, "The backend stopped. See Logs\\central-backend.log.");
    }

    private static async Task<bool> IsHealthyAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await http.GetAsync(new Uri(baseUri, "health"), cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return false; }
    }

    public void Stop()
    {
        if (HandToBackground) BackgroundServer.Stop();
        StopProcesses();
        Report(ServerState.Stopped, File.Exists(DeviceFile) ? AgentState.Stopped : AgentState.NotRegistered, "Stopped.");
    }

    private void StopProcesses()
    {
        Process? backend, agent; JobObject? job;
        lock (_gate) { backend = _backend; agent = _agent; job = _job; _backend = _agent = null; _job = null; }
        foreach (var p in new[] { agent, backend })
        {
            if (p is null) continue;
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
            p.Dispose();
        }
        job?.Dispose();                           // anything left in the job ends here
    }

    public void Dispose()
    {
        StopProcesses();
        _starting.Dispose();
    }
}
