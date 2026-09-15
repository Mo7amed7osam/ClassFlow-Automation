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
        IScheduledMeetingRunner runner)
    {
        _store = store;
        _runner = runner;
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
                lock (_opening) _opening[key] = task;
                started.Add(task);
            }
        }
        finally { _triggerLock.Release(); }
        if (!waitForStarts) return started.Count;
        var sessions = await Task.WhenAll(started);
        return sessions.Count(s => s is { State: not MeetingState.Failed });
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
