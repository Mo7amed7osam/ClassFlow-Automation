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
// The three LMS stages are built: they drive the same LmsSessionRunner the Windows app drives,
// under the account the class names. The Zoom stages - opening a meeting, admitting, taking a
// snapshot, ending, and reading Zoom's own report afterwards - are not, so "zoom_web" is not in
// the list below and the backend will not hand this worker one. Those jobs stay queued, which is
// visible, rather than being taken and failed, which looks like the class went wrong.
//
// None of the LMS stages has run against the real DEPI LMS from Linux. They are written and unit
// tested; FEATURE_PARITY.md keeps them as IMPLEMENTED_NOT_LIVE_VERIFIED until somebody watches one
// work on a class that is safe to run against.
// ---------------------------------------------------------------------------------------------
var accountSource = new ServerLmsAccounts();
var attendanceNames = new NoAttendanceCollected();

var handlers = new IJobHandler[]
{
    new RunSessionStage(accountSource, Log),
    new CompleteSessionStage(accountSource, Log),
    new AttendanceStage(accountSource, attendanceNames, Log),
};

var capabilities = new[] { "lms" };
Log($"can run: {string.Join(", ", handlers.Select(h => h.JobType))} (capabilities: {string.Join(", ", capabilities)})");
Log("the Zoom stages are not built, so this worker does not claim zoom_web and is never given one.");

// Connecting is the next piece: the agent's own socket, journal and outbox already exist in
// ZoomAutoAdmit.CentralAgent and are what this will be handed to. Until the account source below
// is wired to the server, a connected worker would take an LMS stage and fail it for want of a
// sign-in, which is worse than not connecting.
Log("not connecting yet: the account source has no route to the server. See FEATURE_PARITY.md.");

credentials.Clear();
return 0;

static void Log(string message) =>
    Console.WriteLine($"[worker] {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}Z {message}");
