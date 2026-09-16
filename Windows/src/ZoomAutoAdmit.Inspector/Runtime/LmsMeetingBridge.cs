using ZoomAutoAdmit.Core.Central;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Meetings;
using ZoomAutoAdmit.WebAutomation.Lms;
using ZoomAutoAdmit.WindowsRuntime.Scheduling;

namespace ZoomAutoAdmit.Inspector.Runtime;

/// <summary>
/// The LMS half of a class, done the moment its meeting goes live: presses Run Session for the
/// meeting's group at its scheduled time, and writes down what the class still owes (attendance at
/// 1.5 h, the late-joiner correction and Complete Session at 3 h) in the durable follow-up queue.
///
/// It lives in the runtime, not in the window, because a scheduled meeting is usually opened by a
/// Windows task running <c>meeting-start</c>, a process with no window at all. Tied to the window,
/// a class opened that way never became running on the dashboard and never got its attendance.
/// The queue itself is worked through by the app (every 30 s while it is open).
/// </summary>
public sealed class LmsMeetingBridge : IAsyncDisposable
{
    public delegate Task<LmsRunResult> RunSession(string group, TimeOnly start, DateOnly day, CancellationToken token);

    /// <summary>Press Run Session when a meeting goes live. The app's "Run on meeting start" box sets it.</summary>
    public static bool RunOnMeetingStart { get; set; } = true;

    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3)];

    private readonly MeetingLifecycleEvents _events;
    private readonly RunSession _runSession;
    private readonly LmsFollowUpQueue _queue;
    private readonly Func<bool> _hasLogin;
    private readonly Action<string> _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _sync = new();
    private readonly HashSet<Guid> _handled = [];
    private readonly List<Task> _pending = [];
    /// <summary>The class each meeting was taken as (its snapped start), for the final attendance.</summary>
    private readonly Dictionary<Guid, (string Group, DateOnly Day, TimeOnly Start)> _classes = [];
    /// <summary>Stops each meeting's live marker.</summary>
    private readonly Dictionary<Guid, CancellationTokenSource> _beats = [];
    /// <summary>How long after a meeting ends its final correction runs (the last reads are saved first).</summary>
    public static readonly TimeSpan FinalAttendanceAfter = TimeSpan.FromMinutes(2);

    /// <summary>The group's scheduled class nearest the moment the meeting went live (null: none that close).</summary>
    public delegate Task<TimeOnly?> ClassStart(string group, DateOnly day, TimeOnly live, CancellationToken token);
    private readonly ClassStart? _classStart;
    /// <summary>What this PC did, for the central server. A test's own queue keeps its notes beside it.</summary>
    private readonly ActivityLog _activity;

    public LmsMeetingBridge(MeetingLifecycleEvents events, RunSession? runSession = null, LmsFollowUpQueue? queue = null,
        Func<bool>? hasLogin = null, Action<string>? log = null, Func<TimeSpan, CancellationToken, Task>? delay = null,
        ClassStart? classStart = null, ActivityLog? activity = null)
    {
        _activity = activity ?? (queue is null
            ? new ActivityLog()
            : new ActivityLog(Path.Combine(Path.GetDirectoryName(queue.FilePath)!, "activity")));
        _events = events;
        _classStart = classStart;
        // Headless: an unattended class must not have a browser window pop over the Zoom window
        // the admission automation is driving.
        _runSession = runSession ?? ((group, start, day, token) =>
            new LmsSessionRunner(new LmsCredentialStore()).RunAsync(group, start, day, headed: false, dryRun: false, cancellationToken: token));
        _queue = queue ?? new LmsFollowUpQueue();
        _hasLogin = hasLogin ?? (() => { try { return new LmsCredentialStore().Read() != null; } catch { return false; } });
        // Also into scheduler.log: a meeting opened by a Windows task has no window to show it in.
        _log = log ?? (message => WindowsSchedulerLog.Write("LMS", message));
        _delay = delay ?? Task.Delay;
        _events.Lifecycle += OnLifecycleAsync;
    }

    /// <summary>Completes when the work for every meeting seen so far is done (for tests and shutdown).</summary>
    public Task DrainAsync()
    {
        lock (_sync) return Task.WhenAll(_pending);
    }

    private Task OnLifecycleAsync(MeetingLifecycleEvent message)
    {
        var session = message.Context.Session;
        if (message.Kind == MeetingLifecycleEventKind.Ending)
        {
            lock (_sync)
            {
                if (_beats.Remove(session.SessionId, out var beat)) { beat.Cancel(); beat.Dispose(); }
                LiveMeetings.Clear(session.SessionId);
                if (_classes.Remove(session.SessionId, out var ended))
                    _pending.Add(Task.Run(() => FinalAttendanceAsync(ended.Group, ended.Day, ended.Start)));
            }
            return Task.CompletedTask;
        }
        lock (_sync)
        {
            if (!_handled.Add(session.SessionId)) return Task.CompletedTask;     // live once per meeting
            // The meeting is running here: kept fresh every minute, so the app (and the steps that
            // wait for the class to end) can see it whichever process runs it.
            var beat = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            _beats[session.SessionId] = beat;
            string engine = message.Context.EngineType.ToString();
            var token = beat.Token;
            // Not in _pending: it runs for the whole meeting, and DrainAsync waits only for the LMS work.
            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    LiveMeetings.Beat(session.SessionId, session.GroupId, engine, session.StartTime);
                    try { await Task.Delay(LiveMeetings.BeatEvery, token); } catch (OperationCanceledException) { break; }
                }
            });
            // Not awaited by the meeting: admission goes on while the dashboard is driven.
            _pending.Add(Task.Run(() => HandleAsync(session.GroupId, session.StartTime, _stopping.Token,
                snapToClass: !session.HasScheduledStart, sessionId: session.SessionId)));
        }
        return Task.CompletedTask;
    }

    /// <summary>The meeting closed: its class's late-joiner correction runs once more, with everyone seen until the end.</summary>
    private async Task FinalAttendanceAsync(string group, DateOnly day, TimeOnly start)
    {
        try
        {
            var due = DateTimeOffset.Now + FinalAttendanceAfter;
            await _queue.ScheduleFinalAttendanceAsync(group, day, start, due);
            _log($"The meeting of {group} ended; the {start.ToString("HH:mm")} class's attendance is corrected once more at {due.LocalDateTime.ToString("HH:mm")}.");
        }
        catch (Exception ex) { _log($"The final attendance for {group} could not be written down: {ex.Message}"); }
    }

    private async Task HandleAsync(string group, DateTimeOffset scheduledStart, CancellationToken token, bool snapToClass = true, Guid? sessionId = null)
    {
        if (string.IsNullOrWhiteSpace(group)) return;
        var local = scheduledStart.ToLocalTime();
        var day = DateOnly.FromDateTime(local.DateTime);
        var start = TimeOnly.FromDateTime(local.DateTime);
        start = new TimeOnly(start.Hour, start.Minute);
        // A meeting opened by hand (or late) has only the moment it went live: it is the class it is
        // nearest to, so its steps are that class's and the Sessions page shows one card, not two.
        // A class whose time was given (scheduled, or taken back after a restart) keeps it.
        if (_classStart != null && snapToClass)
        {
            try
            {
                if (await _classStart(group, day, start, token) is { } scheduled && scheduled != start)
                {
                    _log($"{group}: live at {start:HH\\:mm}, taken as the {scheduled:HH\\:mm} class.");
                    start = scheduled;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }

        if (sessionId is { } id) lock (_sync) _classes[id] = (group, day, start);

        // The central server is told the class started here, whatever Run Session does next.
        _activity.Write("class.opened", "done", $"{group}: the {start:HH\\:mm} class is live.", group, day);

        // Written down first: whatever happens to Run Session, the attendance steps are owed.
        try
        {
            var written = await _queue.ScheduleAsync(group, day, start, token);
            foreach (var item in written.Where(item => item.Group.Equals(group, StringComparison.OrdinalIgnoreCase) && item.SessionDate == day))
                _log($"Due {item.DueAt.LocalDateTime:yyyy-MM-dd HH:mm}: {item.Describe}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"The attendance follow-up for {group} could not be written down: {ex.Message}");
        }

        if (!RunOnMeetingStart) { _log($"Run Session on meeting start is off; {group} was not started on the dashboard."); return; }
        if (!_hasLogin()) { _log($"No LMS sign-in is saved, so {group} was not started on the dashboard."); return; }

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                _log($"Meeting live for {group}; pressing Run Session for {day:yyyy-MM-dd} {start:HH\\:mm}.");
                var result = await _runSession(group, start, day, token);
                bool alreadyRunning = result.Message.Contains("no Run Session button", StringComparison.OrdinalIgnoreCase);
                await RecordAsync(group, day, start, result.IsSuccess || alreadyRunning, result.Message);
                if (result.IsSuccess) { _log($"{result.Message}"); return; }
                // Already running (someone pressed it, or a second meeting of the class): nothing to retry.
                if (alreadyRunning) { _log($"{result.Message}"); return; }
                _log($"Run Session for {group} failed: {result.Message}");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex) { _log($"Run Session for {group} failed: {ex.GetType().Name}."); }

            if (attempt >= RetryDelays.Length) { _log($"Run Session for {group} gave up; press it on the dashboard."); return; }
            try { await _delay(RetryDelays[attempt], token); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Into the follow-up history, so the Sessions page shows Run Session was pressed (or why not).</summary>
    private async Task RecordAsync(string group, DateOnly day, TimeOnly start, bool ok, string message)
    {
        try
        {
            await _queue.RecordAsync(new LmsFollowUp
            {
                Id = $"{group}|{day:yyyy-MM-dd}|{start:HH\\:mm}|{LmsFollowUpStep.RunSession}",
                Group = group, SessionDate = day, SessionStart = start, Step = LmsFollowUpStep.RunSession, DueAt = DateTimeOffset.Now,
            }, ok, message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _events.Lifecycle -= OnLifecycleAsync;
        Task pending;
        lock (_sync) pending = Task.WhenAll(_pending);
        // A Run Session under way is let finish (the meeting process may be ending); a retry wait is not.
        try { await pending.WaitAsync(TimeSpan.FromSeconds(90)); }
        catch (TimeoutException) { _stopping.Cancel(); }
        catch (Exception) { }
        _stopping.Cancel();
        _stopping.Dispose();
    }
}
