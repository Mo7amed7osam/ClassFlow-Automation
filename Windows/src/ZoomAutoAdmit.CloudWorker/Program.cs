using ZoomAutoAdmit.CentralAgent;
using ZoomAutoAdmit.CloudWorker;
using ZoomAutoAdmit.CloudWorker.Stages;
using ZoomAutoAdmit.WebAutomation.Lms;

// The cloud worker: the half of Zoom Auto Admit that runs where there is no Windows.
//
//   ZoomAutoAdmit.CloudWorker              connect to the backend and take work
//   ZoomAutoAdmit.CloudWorker preflight    check the machine and exit, for a deployment to run first
//
// It only ever connects out. Nothing here opens a port, and the container needs no inbound rule.

string command = args.FirstOrDefault()?.ToLowerInvariant() ?? "run";

// Not disposed on the way out: ProcessExit runs after a `using` would have disposed it, and a stop
// signal must not be the thing that ends the process on an unhandled exception.
var stopping = new CancellationTokenSource();

// SIGTERM is how Docker and Coolify ask for a stop. Draining is not instant - a class being opened
// finishes its step first - so the runtime's own delay is what decides whether it is allowed to.
void RequestStop()
{
    try { stopping.Cancel(); }
    catch (ObjectDisposedException) { }
}
AppDomain.CurrentDomain.ProcessExit += (_, _) => RequestStop();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; RequestStop(); };

WorkerSettings settings;
try
{
    settings = WorkerSettings.FromEnvironment();
}
catch (Exception problem)
{
    Console.Error.WriteLine($"[worker] cannot start: {problem.Message}");
    return 2;
}

Log($"name={settings.Name} backend={settings.BackendUrl} state={settings.StateDirectory} "
    + $"concurrency={settings.MaxConcurrentSessions} headless={settings.Headless} tz={settings.TimeZone}");

var checks = await Preflight.RunAsync(settings, stopping.Token);
Console.WriteLine(Preflight.Describe(checks));

var failed = checks.Where(c => !c.Passed).ToList();

if (command == "preflight")
{
    Log(failed.Count == 0 ? "preflight passed" : $"preflight failed: {failed.Count} of {checks.Count}");
    return failed.Count == 0 ? 0 : 1;
}

if (command != "run")
{
    Console.Error.WriteLine($"[worker] unknown command '{command}'. Use 'run' or 'preflight'.");
    return 2;
}

// A worker that cannot drive a browser or does not know what time it is cannot open a class. It
// stops here rather than connecting, taking work and failing every class it is given.
if (failed.Any(c => c.Name is "Browser" or "Time zone" or "State directory" or "Browser profiles"))
{
    Log("refusing to start: " + string.Join("; ", failed.Select(c => c.Name)));
    return 1;
}
foreach (var check in failed) Log($"warning: {check.Name}: {check.Detail}");

// On a server there is no Credential Manager. Passwords come from the backend, for the one run that
// needs them, and are held in memory only - a restart asks again rather than keeping them anywhere.
var credentials = new InMemoryLmsCredentialBackend();
LmsCredentialBackend.Current = credentials;

var tokens = new FileDeviceTokenStore(Path.Combine(settings.StateDirectory, "device-token"));
if (tokens.Read() is null && string.IsNullOrWhiteSpace(settings.EnrollmentToken))
{
    Log("this worker is not enrolled yet and no ZAA_ENROLLMENT_TOKEN was given. Make one on the server with "
        + "`python -m central_backend.cli create-enrollment-token --label <name>`, put it in the environment, and "
        + "deploy again. It is used once; the device token that replaces it lives on the state volume.");
    return 1;
}

Log("enrolled" + (tokens.Read() is null ? " (will register with the enrolment token)" : ""));

// ---------------------------------------------------------------------------------------------
// What this worker can do, and what it therefore says it can do.
//
// class.run owns a meeting for the length of a class: it opens it as the host, admits the waiting
// room as people arrive, and closes it. The three LMS stages drive the same LmsSessionRunner the
// Windows app drives, under the account the class names.
//
// Not built: zoom.report and zoom.recording, which read Zoom's own pages after a meeting has
// ended. A worker claiming zoom_web can still be handed one, so the handler dictionary is what
// actually answers - an unknown type is rejected rather than taken and failed.
//
// Nothing here has opened a real Zoom meeting yet. lms.run_session has run against the real DEPI
// LMS from Linux; FEATURE_PARITY.md is where each one's evidence is.
// ---------------------------------------------------------------------------------------------
using var http = new HttpClient { BaseAddress = settings.BackendUrl, Timeout = TimeSpan.FromSeconds(30) };

// The server ties a class's sign-in to the job being run, so the account source has to know which
// one that is. The agent already tracks it; this closure reads it back once the agent exists,
// which is why it is a closure and not a constructor argument - the two need each other.
CentralAgentService? agent = null;
Guid? RunningJob() => Guid.TryParse(agent?.RunningJobId, out var id) ? id : null;

var accountSource = new ServerLmsAccounts(http, tokens.Read, RunningJob, Log);
var zoomAccounts = new ServerZoomAccounts(http, tokens.Read, RunningJob, Log);
var attendanceNames = new NoAttendanceCollected();

var handlers = new IJobHandler[]
{
    new ClassRunStage(zoomAccounts, settings.Headless, Log),
    new RunSessionStage(accountSource, Log),
    new CompleteSessionStage(accountSource, Log),
    new AttendanceStage(accountSource, attendanceNames, Log),
};

// One job at a time: a worker holding a meeting is busy for the class's length. Several classes
// at once means several workers, which is what the compose file scales.
var capabilities = new[] { "zoom_web", "lms" };
Log($"can run: {string.Join(", ", handlers.Select(h => h.JobType))} (capabilities: {string.Join(", ", capabilities)})");

string version = typeof(WorkerSettings).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
var identities = new DeviceIdentityStore(Path.Combine(settings.StateDirectory, "device.json"));

DeviceIdentity? identity;
try
{
    identity = identities.Load();
}
catch (InvalidDataException problem)
{
    Log($"cannot start: {problem.Message}");
    return 1;
}

if (identity?.IsRegistered != true)
{
    // The enrolment token is spent here and never written down. What replaces it is the device
    // token, on the state volume, which is what every later connection uses.
    Log("registering with the enrolment token");
    try
    {
        identity = await new AgentRegistrar(http, identities, tokens, Log).RegisterAsync(
            settings.BackendUrl, settings.EnrollmentToken!, settings.Name, version, capabilities, stopping.Token);
    }
    catch (AgentRegistrationException problem)
    {
        Log($"the backend would not register this worker: {problem.Message}");
        return 1;
    }
    Log($"registered as {identity.DeviceId}. Clear ZAA_ENROLLMENT_TOKEN: it is spent.");
}

agent = new CentralAgentService(
    new CentralAgentSettings
    {
        BackendUrl = settings.BackendUrl,
        Version = version,
        Capabilities = capabilities,
    },
    identity,
    tokens,
    new ClientWebSocketFactory(),
    new JobJournal(Path.Combine(settings.JournalDirectory, "jobs.jsonl")),
    handlers,
    Log);

Log($"connecting to {settings.BackendUrl}");
var reason = await agent.RunAsync(stopping.Token);

// Whatever passwords this run was given go now, whether it stopped cleanly or not.
credentials.Clear();

switch (reason)
{
    case AgentStopReason.Unauthorized:
        // Reconnecting would be refused the same way, so it stops and says why rather than
        // retrying against a token somebody revoked on purpose.
        Log("the backend refused this worker's device token. It may have been revoked; enrol again.");
        return 1;
    case AgentStopReason.NotRegistered:
        Log("this worker has no device identity. Give it ZAA_ENROLLMENT_TOKEN and start it again.");
        return 1;
    default:
        Log("stopped");
        return 0;
}

static void Log(string message) =>
    Console.WriteLine($"[worker] {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}Z {message}");
