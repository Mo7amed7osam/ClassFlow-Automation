using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Meetings;
using ZoomAutoAdmit.Core.Sessions;

namespace ZoomAutoAdmit.WindowsRuntime.Scheduling;

/// <summary>
/// Opens one scheduled class, and keeps at it until it is open. Both the Windows task
/// (meeting-start) and the app's own scheduler come here, and only one of them at a time does it:
/// a class is claimed per day by a lock file every process respects, so the two never drive Zoom
/// together (on 2026-09-15 they did, and the class did not open). Each failed try is followed by
/// the other engine - the Zoom app, then Web, then the app again - every <see cref="RetryGap"/>
/// until <see cref="GiveUpAfterStart"/> past the class's time. Only a class that opened is marked
/// opened; one that gave up says so in the log (and to the app, which shows it).
/// </summary>
public sealed class ScheduledClassStarter(IScheduledMeetingRunner runner, WindowsMeetingScheduleStore store, Action<string>? log = null)
{
    public static TimeSpan RetryGap { get; set; } = TimeSpan.FromSeconds(20);
    public static TimeSpan GiveUpAfterStart { get; set; } = TimeSpan.FromMinutes(45);
    public static string ClaimFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "Schedules", "runs");

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Reported = new();
    private readonly Action<string> _log =log ?? (message => ConsoleLogger.Info($"[SCHEDULER] {message}"));

    /// <summary>A class that could not be opened at all (after every try).</summary>
    public static event Action<MeetingSchedule, string>? GaveUp;

    /// <summary>The engine for try number <paramref name="attempt"/>: the preferred one first, then the other, in turn.</summary>
    public static SessionEngineType? EngineFor(SessionEngineType? preferred, int attempt)
    {
        if (attempt == 0) return preferred;
        // Auto's first try is the Zoom app (with its own switch to Web inside); then Web, then the app.
        var first = preferred ?? SessionEngineType.Desktop;
        var other = first == SessionEngineType.Desktop ? SessionEngineType.Web : SessionEngineType.Desktop;
        return attempt % 2 == 1 ? other : first;
    }

    /// <summary>Null when another process is opening it, or it is already open, disabled or gone.</summary>
    public async Task<MeetingSession?> StartAsync(MeetingSchedule schedule, DateOnly day, DateTimeOffset now, CancellationToken token = default)
    {
        using var claim = TryClaim(schedule.Id, day);
        if (claim == null)
        {
            if (Reported.TryAdd($"{schedule.Id:N}{day}", 0)) _log($"{schedule.Name}: already being opened elsewhere; not opened twice.");
            return null;
        }
        var current = (await store.ListAsync(token)).FirstOrDefault(s => s.Id == schedule.Id);
        if (current == null || !current.Enabled || current.LastTriggeredDate == day) return null;

        DateTime classStart = day.ToDateTime(current.Time);
        DateTime deadline = classStart + GiveUpAfterStart;
        MeetingSession? last = null;
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var engine = EngineFor(current.PreferredEngine, attempt);
            _log($"{current.Name}: opening (try {attempt + 1}, {engine?.ToString() ?? "Auto"}) for {classStart:HH:mm}.");
            try
            {
                last = await runner.RunAsync(new ScheduledMeeting(
                    new Uri(current.MeetingUrl), current.AccountId, now,
                    PreferredEngine: engine,
                    GroupId: string.IsNullOrWhiteSpace(current.GroupName) ? current.AccountId : current.GroupName,
                    ScheduledStartTime: new DateTimeOffset(classStart, now.Offset)), token);
                if (last.State != MeetingState.Failed)
                {
                    await store.MarkOpenedAsync(current.Id, day, token);
                    _log($"{current.Name}: open ({last.Allocation?.EngineType}).");
                    return last;
                }
                _log($"{current.Name}: try {attempt + 1} failed: {last.FailureReason}");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log($"{current.Name}: try {attempt + 1} failed: {ex.Message}"); }

            if (DateTime.Now + RetryGap > deadline)
            {
                await store.MarkOpenedAsync(current.Id, day, CancellationToken.None);
                string why = last?.FailureReason ?? "every try failed";
                _log($"{current.Name}: gave up at {DateTime.Now:HH:mm} ({why}). Open it by hand.");
                GaveUp?.Invoke(current, why);
                return last;
            }
            await Task.Delay(RetryGap, token);
            now = DateTimeOffset.Now;
        }
    }

    /// <summary>Held while a class is being opened; any other process (or thread) gets null.</summary>
    public static IDisposable? TryClaim(Guid scheduleId, DateOnly day)
    {
        try
        {
            Directory.CreateDirectory(ClaimFolder);
            string path = Path.Combine(ClaimFolder, $"{scheduleId:N}-{day:yyyyMMdd}.lock");
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
