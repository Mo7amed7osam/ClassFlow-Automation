using ZoomAutoAdmit.CentralAgent;

namespace ZoomAutoAdmit.CloudWorker;

/// <summary>
/// Everything the worker needs to start, read from the environment because that is what a container
/// has. Nothing here is a secret except the enrolment token, which is used once and then replaced by
/// a device token on the state volume.
/// </summary>
public sealed record WorkerSettings
{
    /// <summary>The backend, e.g. https://central.example.com/. ZAA_BACKEND_URL.</summary>
    public required Uri BackendUrl { get; init; }

    /// <summary>
    /// What this worker is called on the dashboard. ZAA_WORKER_NAME, defaulting to the host name,
    /// which is the container id unless the deployment sets one - so it is worth setting.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// A single-use enrolment token from `cli create-enrollment-token`. Only read when the state
    /// volume has no device token yet, and never written anywhere. ZAA_ENROLLMENT_TOKEN.
    /// </summary>
    public string? EnrollmentToken { get; init; }

    /// <summary>Where the device token, browser profiles and journals live. ZAA_STATE_DIR.</summary>
    public required string StateDirectory { get; init; }

    /// <summary>
    /// How many classes this worker runs at once. ZAA_MAX_CONCURRENT_SESSIONS.
    ///
    /// This is not the number of coordinators: each live class is its own Chromium with its own
    /// profile, so the ceiling is the machine's memory and /dev/shm, not how many people are
    /// registered. Two is a deliberately small default - raise it once a real machine has been
    /// measured, not before.
    /// </summary>
    public int MaxConcurrentSessions { get; init; } = 2;

    /// <summary>
    /// Whether Chromium runs with no display. ZAA_HEADLESS, default true.
    ///
    /// Zoom's web client may refuse a headless browser; that has not been tested against live Zoom.
    /// When it does, set this false and the container's Xvfb gives it a display instead.
    /// </summary>
    public bool Headless { get; init; } = true;

    /// <summary>What a stage may take before it is given up on. ZAA_JOB_TIMEOUT_MINUTES.</summary>
    public TimeSpan JobTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>The clock local schedules are read in. Africa/Cairo unless ZAA_TIMEZONE says otherwise.</summary>
    public string TimeZone { get; init; } = "Africa/Cairo";

    public string BrowserProfilesDirectory => Path.Combine(StateDirectory, "profiles");
    public string JournalDirectory => Path.Combine(StateDirectory, "journal");

    public static WorkerSettings FromEnvironment(IReadOnlyDictionary<string, string?>? environment = null)
    {
        string? Read(string key) =>
            (environment is null ? Environment.GetEnvironmentVariable(key)
                                 : environment.TryGetValue(key, out var found) ? found : null)
            ?.Trim() is { Length: > 0 } value ? value : null;

        string? backend = Read("ZAA_BACKEND_URL");
        if (backend is null)
            throw new InvalidOperationException(
                "ZAA_BACKEND_URL is not set. It is the address of the central backend, e.g. https://central.example.com.");

        string state = Read("ZAA_STATE_DIR")
                       ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit");

        return new WorkerSettings
        {
            // Reuses the agent's own checks: absolute, https (plain http only for a loopback
            // backend), and no credentials smuggled into the URL.
            BackendUrl = CentralAgentSettings.ParseBackendUrl(backend),
            Name = Read("ZAA_WORKER_NAME") ?? Environment.MachineName,
            EnrollmentToken = Read("ZAA_ENROLLMENT_TOKEN"),
            StateDirectory = state,
            MaxConcurrentSessions = PositiveNumber(Read("ZAA_MAX_CONCURRENT_SESSIONS"), 2, "ZAA_MAX_CONCURRENT_SESSIONS"),
            Headless = Flag(Read("ZAA_HEADLESS"), true),
            JobTimeout = TimeSpan.FromMinutes(PositiveNumber(Read("ZAA_JOB_TIMEOUT_MINUTES"), 30, "ZAA_JOB_TIMEOUT_MINUTES")),
            TimeZone = Read("ZAA_TIMEZONE") ?? "Africa/Cairo",
        };
    }

    private static int PositiveNumber(string? text, int fallback, string name)
    {
        if (text is null) return fallback;
        if (!int.TryParse(text, out int value) || value <= 0)
            throw new InvalidOperationException($"{name} must be a whole number above zero; it is '{text}'.");
        return value;
    }

    private static bool Flag(string? text, bool fallback) => text?.ToLowerInvariant() switch
    {
        null => fallback,
        "1" or "true" or "yes" or "on" => true,
        "0" or "false" or "no" or "off" => false,
        _ => throw new InvalidOperationException($"Expected true or false, not '{text}'."),
    };

    /// <summary>
    /// The clock, checked at startup rather than when the first class is due. A container without
    /// tzdata answers this with an exception, and it is better heard on the first line of the log
    /// than at 19:00.
    /// </summary>
    public TimeZoneInfo ResolveTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
        }
        catch (Exception problem) when (problem is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidOperationException(
                $"The time zone '{TimeZone}' is not on this machine. A slim container often has no tzdata; " +
                "install it (apt-get install tzdata) or set ZAA_TIMEZONE to one it has.", problem);
        }
    }
}
