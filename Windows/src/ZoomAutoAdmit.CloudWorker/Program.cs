using System.Runtime.InteropServices;
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

// Not disposed on the way out: a stop signal must not be the thing that ends the process on an
// unhandled exception.
var stopping = new CancellationTokenSource();

// SIGTERM is how Docker and Coolify ask for a stop, and what happens next decides whether a
// redeploy during class hours abandons a live class.
//
// AppDomain.ProcessExit is the wrong hook for it: it runs while the runtime is already tearing the
// process down, on a timer of about two seconds, so a class being held is killed mid-close - the
// meeting left open, the profile left locked, the job recorded as failed for a reason that was
// never about the class.
//
// PosixSignalRegistration sees the signal before any of that, and Cancel = true refuses the
// default termination. The process then stops on its own terms: the stage is asked to finish, the
// meeting is ended, the browser closes, and the job is reported. The container's stop grace period
// is what limits how long that may take.
void RequestStop(string signal)
{
    Log($"{signal}: draining. The class being held is closed properly before this exits.");
    try { stopping.Cancel(); }
    catch (ObjectDisposedException) { }
}

using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    RequestStop("SIGTERM");
});
using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
{
    context.Cancel = true;
    RequestStop("SIGINT");
});

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
    + $"one class at a time, headless={settings.Headless} tz={settings.TimeZone}");

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

using var http = new HttpClient { BaseAddress = settings.BackendUrl, Timeout = TimeSpan.FromSeconds(30) };
string version = typeof(WorkerSettings).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

// ---- The meeting lane: the device the operator enrolled. Its files stay where they always were,
// so a worker enrolled before the lanes existed keeps its identity.
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
    Log("registering with the enrolment token");
    try
    {
        identity = await new AgentRegistrar(http, identities, tokens, Log).RegisterAsync(
            settings.BackendUrl, settings.EnrollmentToken!, settings.Name, version, ["zoom_web"], stopping.Token);
    }
    catch (AgentRegistrationException problem)
    {
        Log($"the backend would not register this worker: {problem.Message}");
        return 1;
    }
    Log($"registered as {identity.DeviceId}. Clear ZAA_ENROLLMENT_TOKEN: it is spent.");
}

// ---- The LMS lane: a companion device in its own folder, enrolled by the worker itself.
string lmsState = Path.Combine(settings.StateDirectory, "lms-lane");
Directory.CreateDirectory(lmsState);
var lmsTokens = new FileDeviceTokenStore(Path.Combine(lmsState, "device-token"));
var lmsIdentities = new DeviceIdentityStore(Path.Combine(lmsState, "device.json"));
var lmsIdentity = await CompanionLane.EnsureAsync(
    http, tokens.Read, lmsIdentities, lmsTokens, settings.BackendUrl, version, Log, stopping.Token);
bool twoLanes = lmsIdentity is not null;

CentralAgentService? meetingAgent = null;
CentralAgentService? lmsAgent = null;
static Guid? Running(CentralAgentService? agent) => Guid.TryParse(agent?.RunningJobId, out var id) ? id : null;

var zoomAccounts = new ServerZoomAccounts(http, tokens.Read, () => Running(meetingAgent), Log);
var snapshots = new ServerAttendanceSnapshots(http, tokens.Read, Log);
var meetingHandlers = new List<IJobHandler>
{
    new ClassRunStage(zoomAccounts, settings.Headless, Log, attendance: snapshots),
    new ZoomReportStage(zoomAccounts, Log),
    new ZoomRecordingStage(zoomAccounts, Log),
};

// The LMS stages ask the server as whichever device runs them: the job they hold is that device's.
Func<string?> lmsToken = twoLanes ? lmsTokens.Read : tokens.Read;
Func<Guid?> lmsJob = twoLanes ? () => Running(lmsAgent) : () => Running(meetingAgent);
var lmsAccounts = new ServerLmsAccounts(http, lmsToken, lmsJob, Log);
var presentNames = new ServerAttendanceNames(http, lmsToken, lmsJob, Log);
var lmsHandlers = new IJobHandler[]
{
    new RunSessionStage(lmsAccounts, Log),
    new AttendanceStage(lmsAccounts, presentNames, Log),
    new LateJoinersStage(lmsAccounts, presentNames, Log),
    new CompleteSessionStage(lmsAccounts, Log),
};

if (!twoLanes)
{
    // Still a working worker, only a slower one: every LMS stage waits for the meeting before it.
    Log("warning: running on one lane. The LMS stages of a class will wait until its meeting is over.");
    meetingHandlers.AddRange(lmsHandlers);
}

string[] meetingCapabilities = twoLanes ? ["zoom_web"] : ["zoom_web", "lms"];
Log($"meeting lane: {string.Join(", ", meetingHandlers.Select(h => h.JobType))}");
if (twoLanes) Log($"LMS lane: {string.Join(", ", lmsHandlers.Select(h => h.JobType))}");

meetingAgent = new CentralAgentService(
    new CentralAgentSettings { BackendUrl = settings.BackendUrl, Version = version, Capabilities = meetingCapabilities },
    identity,
    tokens,
    new ClientWebSocketFactory(),
    new JobJournal(Path.Combine(settings.JournalDirectory, "jobs.jsonl")),
    meetingHandlers,
    Log);

if (twoLanes)
    lmsAgent = new CentralAgentService(
        new CentralAgentSettings { BackendUrl = settings.BackendUrl, Version = version, Capabilities = ["lms"] },
        lmsIdentity!,
        lmsTokens,
        new ClientWebSocketFactory(),
        new JobJournal(Path.Combine(settings.JournalDirectory, "lms-jobs.jsonl")),
        lmsHandlers,
        message => Log($"[lms lane] {message}"));

Log($"connecting to {settings.BackendUrl}");
var lanes = new List<Task<AgentStopReason>> { meetingAgent.RunAsync(stopping.Token) };
if (lmsAgent is not null) lanes.Add(lmsAgent.RunAsync(stopping.Token));

// A lane that stops on its own - its token revoked - takes the other with it: half a worker that
// holds meetings nobody writes up is worse than a stopped one that says why.
await Task.WhenAny(lanes);
if (!stopping.IsCancellationRequested) RequestStop("a lane stopped");
var reasons = await Task.WhenAll(lanes);

// Whatever passwords this run was given go now, whether it stopped cleanly or not.
credentials.Clear();

if (reasons.Contains(AgentStopReason.Unauthorized))
{
    // Reconnecting would be refused the same way, so it stops and says why rather than retrying
    // against a token somebody revoked on purpose.
    Log("the backend refused a device token of this worker. It may have been revoked; enrol again.");
    return 1;
}
if (reasons.Contains(AgentStopReason.NotRegistered))
{
    Log("this worker has no device identity. Give it ZAA_ENROLLMENT_TOKEN and start it again.");
    return 1;
}
Log("stopped");
return 0;

static void Log(string message) =>
    Console.WriteLine($"[worker] {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}Z {message}");
