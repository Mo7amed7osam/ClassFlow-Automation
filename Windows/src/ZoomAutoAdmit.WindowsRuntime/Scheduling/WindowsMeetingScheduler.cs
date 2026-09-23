using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Meetings;

namespace ZoomAutoAdmit.WindowsRuntime.Scheduling;

public interface IScheduledMeetingRunner
{
    Task<MeetingSession> RunAsync(ScheduledMeeting meeting, CancellationToken cancellationToken = default);
}

public sealed class OrchestratedScheduledMeetingRunner(MeetingOrchestrator orchestrator)
    : IScheduledMeetingRunner
{
    public Task<MeetingSession> RunAsync(
        ScheduledMeeting meeting,
        CancellationToken cancellationToken = default) =>
        orchestrator.RunAsync(meeting, cancellationToken);
}

public sealed class WindowsMeetingScheduler : IAsyncDisposable
{
    private readonly WindowsMeetingScheduleStore _store;
    private readonly IScheduledMeetingRunner _runner;
    private readonly SemaphoreSlim _triggerLock = new(1, 1);
    private CancellationTokenSource? _cancellation;
    private Task? _loop;

    public WindowsMeetingScheduler(
        WindowsMeetingScheduleStore store,
        IScheduledMeetingRunner runner,
        ClassEndings? endings = null)
    {
        _store = store;
        _runner = runner;
        _endings = endings ?? new ClassEndings();
    }

    public event Action<MeetingSession>? SessionStarted;

    public void Start()
    {
        if (_loop is { IsCompleted: false }) return;
        _cancellation = new CancellationTokenSource();
        _loop = MonitorAsync(_cancellation.Token);
    }

    private readonly Dictionary<string, Task> _opening = new();

    /// <summary>
    /// Starts opening every class that is due and not opened yet. Each class is opened (and retried)
    /// by <see cref="ScheduledClassStarter"/> on its own, so one class that takes long never holds up
    /// another; a class the Windows task is already opening is left to it. Returns how many opened
    /// (when <paramref name="waitForStarts"/>) or how many were started.
    /// </summary>
    public async Task<int> RunDueAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default,
        bool waitForStarts = true)
    {
        await _triggerLock.WaitAsync(cancellationToken);
        var started = new List<Task<MeetingSession?>>();
        try
        {
            DateOnly today = DateOnly.FromDateTime(now.LocalDateTime);
            // A schedule opens ScheduleTiming.StartLead before its own time, so a meeting set for
            // just after midnight is launched on the day before. Both days are considered.
            DateOnly tomorrow = today.AddDays(1);
            foreach (var schedule in await _store.ListAsync(cancellationToken))
            {
                if (!schedule.Enabled) continue;
                DateOnly? due = null;
                foreach (var date in new[] { today, tomorrow })
                {
                    bool runsThatDay = schedule.OccurrenceDate.HasValue
                        ? schedule.OccurrenceDate.Value == date
                        : schedule.Days.Includes(date.ToDateTime(TimeOnly.MinValue).DayOfWeek);
                    if (!runsThatDay || schedule.LastTriggeredDate == date) continue;
                    // Too late to be worth opening (a class whose tries all failed is marked opened).
                    if (now.LocalDateTime > date.ToDateTime(schedule.Time) + ScheduledClassStarter.GiveUpAfterStart) continue;
                    if (now.LocalDateTime >= schedule.LaunchMoment(date)) { due = date; break; }
                }
                if (due == null) continue;
                string key = $"{schedule.Id:N}|{due.Value:yyyyMMdd}";
                lock (_opening) if (_opening.TryGetValue(key, out var running) && !running.IsCompleted) continue;

                ConsoleLogger.Info(
                    $"[SCHEDULER] Triggering: {schedule.Name} ({ScheduleTiming.StartLead.TotalMinutes:0} min before {schedule.Time:HH\\:mm})");
                var day = due.Value;
                var task = Task.Run(async () =>
                {
                    try
                    {
                        var session = await new ScheduledClassStarter(_runner, _store).StartAsync(schedule, day, now, cancellationToken);
                        if (session is { State: not MeetingState.Failed }) SessionStarted?.Invoke(session);
                        else if (session != null) ConsoleLogger.Error($"[SCHEDULER] Failed: {session.FailureReason}");
                        return session;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return null; }
                    catch (Exception ex) { ConsoleLogger.Error($"[SCHEDULER] Failed: {ex.Message}"); return null; }
                }, CancellationToken.None);
                lock (_opening) { _opening[key] = task; _lastOpened[key] = now; }
                started.Add(task);
            }
        }
        finally { _triggerLock.Release(); }
        started.AddRange(await ReopenClosedClassesAsync(now, cancellationToken));
        if (!waitForStarts) return started.Count;
        var sessions = await Task.WhenAll(started);
        return sessions.Count(s => s is { State: not MeetingState.Failed });
    }

    /// <summary>How long after its time a class is still worth putting back on its feet.</summary>
    public static TimeSpan ReopenWithin { get; set; } = TimeSpan.FromHours(2) + TimeSpan.FromMinutes(30);

    /// <summary>How many times one class may be opened again in a day before a person is needed.</summary>
    public const int MostReopens = 3;

    /// <summary>
    /// How long a class that has just been opened is left alone. A meeting takes a while to appear,
    /// and asking for it again in the meantime would open the same class twice.
    /// </summary>
    public static TimeSpan ReopenSettleTime { get; set; } = TimeSpan.FromMinutes(3);

    private readonly Dictionary<string, int> _reopened = [];
    private readonly Dictionary<string, DateTimeOffset> _lastOpened = [];
    private readonly ClassEndings _endings;

    /// <summary>
    /// A class that opened and whose meeting is no longer there, while the class is still going on:
    /// the browser crashed, the profile was closed, Zoom dropped the connection. It is opened again,
    /// with its own account and link, up to <see cref="MostReopens"/> times.
    ///
    /// A meeting somebody ended - the program at the end of the class, a person, or from a phone -
    /// is never reopened: that is written down as the class's ending, and a class that ended is over.
    /// </summary>
    private async Task<List<Task<MeetingSession?>>> ReopenClosedClassesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var started = new List<Task<MeetingSession?>>();
        DateOnly today = DateOnly.FromDateTime(now.LocalDateTime);
        foreach (var schedule in await _store.ListAsync(cancellationToken))
        {
            if (!schedule.Enabled || schedule.LastTriggeredDate != today) continue;
            var since = now.LocalDateTime - today.ToDateTime(schedule.Time);
            if (since < TimeSpan.Zero || since > ReopenWithin) continue;

            string group = string.IsNullOrWhiteSpace(schedule.GroupName) ? schedule.AccountId : schedule.GroupName;
            if (LiveMeetings.IsLive(group, now)) continue;
            if (_endings.For(group, today, schedule.Time) != null) continue;      // it ended; it is over

            string key = $"{schedule.Id:N}|{today:yyyyMMdd}";
            lock (_opening)
            {
                if (_opening.TryGetValue(key, out var running) && !running.IsCompleted) continue;
                // Just opened: its meeting has not had time to show itself yet.
                if (_lastOpened.TryGetValue(key, out var when) && now - when < ReopenSettleTime) continue;
                if (!_reopened.TryGetValue(key, out int times)) times = 0;
                if (times >= MostReopens)
                {
                    if (times == MostReopens)
                    {
                        _reopened[key] = times + 1;
                        ConsoleLogger.Warn($"[SCHEDULER] {schedule.Name}: its meeting keeps closing; it was opened again {MostReopens} times and is now left alone.");
                    }
                    continue;
                }
                _reopened[key] = times + 1;
            }
            ConsoleLogger.Info($"[SCHEDULER] {schedule.Name}: its meeting is no longer running {since.TotalMinutes:0} min into the class; opening it again.");

            var task = Task.Run(async () =>
            {
                try
                {
                    var session = await new ScheduledClassStarter(_runner, _store).StartAsync(schedule, today, now, cancellationToken, reopen: true);
                    if (session is { State: not MeetingState.Failed }) SessionStarted?.Invoke(session);
                    return session;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return null; }
                catch (Exception ex) { ConsoleLogger.Error($"[SCHEDULER] {schedule.Name} could not be opened again: {ex.Message}"); return null; }
            }, CancellationToken.None);
            lock (_opening) { _opening[key] = task; _lastOpened[key] = now; }
            started.Add(task);
        }
        return started;
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            // Not waiting for a class to open: the next tick must still see the next class.
            await RunDueAsync(DateTimeOffset.Now, cancellationToken, waitForStarts: false);
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await RunDueAsync(DateTimeOffset.Now, cancellationToken, waitForStarts: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public async Task StopAsync()
    {
        _cancellation?.Cancel();
        if (_loop != null)
        {
            try { await _loop; }
            catch (OperationCanceledException) { }
        }
        _cancellation?.Dispose();
        _cancellation = null;
        _loop = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _triggerLock.Dispose();
    }
}
