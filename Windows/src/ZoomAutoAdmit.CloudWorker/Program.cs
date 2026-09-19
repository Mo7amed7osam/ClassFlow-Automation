using ZoomAutoAdmit.CentralAgent;
using ZoomAutoAdmit.CloudWorker;
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
// What is not built yet, said plainly rather than started and left to fail.
//
// The worker's shell is here: it reads its settings, proves the machine can drive a browser, holds
// passwords the way a server must, and knows how it is enrolled. What it does not yet have is the
// job types for the class stages - the backend knows one, recording.process, and the stages in the
// Sessions timeline have no server-side path at all (FEATURE_PARITY.md, section 1).
//
// Connecting now would register a worker that answers "I can do nothing", which reads on the
// dashboard as a worker that is fine. It says so instead.
// ---------------------------------------------------------------------------------------------
Log("the machine is ready. The job types for the class stages are not built yet, so there is nothing "
    + "to take: see FEATURE_PARITY.md. Run `preflight` from a deployment to check a machine.");

credentials.Clear();
return 0;

static void Log(string message) =>
    Console.WriteLine($"[worker] {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}Z {message}");
