using Microsoft.Playwright;
using ZoomAutoAdmit.Attendance;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Meetings;
using ZoomAutoAdmit.Core.Sessions;
using ZoomAutoAdmit.UIAutomation.Meetings;
using ZoomAutoAdmit.WebAutomation;
using ZoomAutoAdmit.WindowsRuntime.Scheduling;

namespace ZoomAutoAdmit.Inspector.Runtime;

/// <summary>
/// Ends a class for everyone once it is over. From three hours after the class's time:
/// <list type="bullet">
/// <item>nobody but the host is left (Zoom app and Web);</item>
/// <item>the co-host the app made (the instructor) left and has not come back for five minutes
/// (Zoom app and Web);</item>
/// <item>fewer than five people have sat there muted for five minutes (Zoom app only: the Web list
/// does not say who is muted).</item>
/// </list>
/// It only reads the participants list until then; right before ending it reads once more, and it
/// never ends while anyone's mic is on, while the list cannot be read in full, or when this account
/// is not the meeting's host. "Leave" is never pressed.
/// </summary>
public sealed class AutoEndMeetingBridge : IAsyncDisposable
{
    public delegate Task<(bool Ended, string Message)> EndMeeting(MeetingLaunchContext context, CancellationToken token);

    /// <summary>The app's "End the class after 3 hours" switch.</summary>
    public static bool Enabled { get; set; } = true;
    private static readonly TimeSpan WakeBefore = TimeSpan.FromMinutes(5);

    private readonly MeetingLifecycleEvents _events;
    private readonly Func<MeetingLaunchContext, IAttendanceParticipantSource> _participants;
    private readonly EndMeeting _end;
    private readonly Func<MeetingLaunchContext, bool> _meetingOpen;
    private readonly LmsMeetingBridge.ClassStart? _classStart;
    private readonly Action<string> _log;
    private readonly TimeSpan _interval;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _watches = [];
    private readonly List<Task> _running = [];

    public AutoEndMeetingBridge(
        MeetingLifecycleEvents events,
        Func<MeetingLaunchContext, IAttendanceParticipantSource> participants,
        EndMeeting? end = null,
        Func<MeetingLaunchContext, bool>? meetingOpen = null,
        LmsMeetingBridge.ClassStart? classStart = null,
        Action<string>? log = null,
        TimeSpan? interval = null,
        Func<DateTimeOffset>? now = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<IPage?>? webPage = null)
    {
        _events = events;
        _participants = participants;
        _end = end ?? ((context, token) => context.EngineType == SessionEngineType.Web
            ? ZoomWebMeetingEnder.EndForAllAsync(webPage?.Invoke(), token)
            : Task.Run(() => new ZoomDesktopMeetingEnder().EndForAll(), token));
        _meetingOpen = meetingOpen ?? (context => context.EngineType == SessionEngineType.Web
            ? webPage?.Invoke() is { IsClosed: false }
            : ZoomDesktopMeetingEnder.MeetingWindow() != IntPtr.Zero);
        _classStart = classStart;
        _log = log ?? (message => { WindowsSchedulerLog.Write("MEETING", message); ConsoleLogger.Info($"[AUTO_END] {message}"); });
        _interval = interval ?? TimeSpan.FromSeconds(30);
        _now = now ?? (() => DateTimeOffset.Now);
        _delay = delay ?? Task.Delay;
        _events.Lifecycle += OnLifecycleAsync;
    }

    private Task OnLifecycleAsync(MeetingLifecycleEvent message)
    {
        var context = message.Context;
        lock (_sync)
        {
            if (message.Kind == MeetingLifecycleEventKind.Ending)
            {
                if (_watches.Remove(context.Session.SessionId, out var stop)) { stop.Cancel(); stop.Dispose(); }
                return Task.CompletedTask;
            }
            if (_watches.ContainsKey(context.Session.SessionId)) return Task.CompletedTask;
            var cancellation = new CancellationTokenSource();
            _watches[context.Session.SessionId] = cancellation;
            _running.Add(Task.Run(() => WatchAsync(context, cancellation.Token)));
        }
        return Task.CompletedTask;
    }

    /// <summary>Completes when every watch seen so far has stopped (tests).</summary>
    public Task DrainAsync() { lock (_sync) return Task.WhenAll(_running); }

    private sealed record Room(IReadOnlyList<ParticipantRow> Rows, IReadOnlyList<string> Names, bool Complete, RoomState State);

    private async Task WatchAsync(MeetingLaunchContext context, CancellationToken token)
    {
        try
        {
            var session = context.Session;
            bool web = context.EngineType == SessionEngineType.Web;
            var local = session.StartTime.ToLocalTime();
            var classStart = new DateTimeOffset(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, local.Offset);
            if (!session.HasScheduledStart && _classStart != null &&
                await _classStart(session.GroupId, DateOnly.FromDateTime(classStart.DateTime), TimeOnly.FromDateTime(classStart.DateTime), token) is { } scheduled)
                classStart = new DateTimeOffset(DateOnly.FromDateTime(classStart.DateTime).ToDateTime(scheduled), classStart.Offset);

            // Asleep until shortly before the three hours.
            var wake = classStart + AutoEndRule.EndAfter - WakeBefore;
            while (_now() < wake)
            {
                var left = wake - _now();
                await _delay(left < TimeSpan.FromMinutes(10) ? left : TimeSpan.FromMinutes(10), token);
            }

            var tracker = new AutoEndTracker();
            bool sawCoHost = false;
            DateTimeOffset? coHostLastSeen = null;
            string? lastLogged = null;
            while (!token.IsCancellationRequested)
            {
                if (!Enabled) { await _delay(_interval, token); continue; }
                if (!_meetingOpen(context)) { _log($"{session.GroupId}: the meeting is over; stopped watching."); return; }
                var room = await ReadRoomAsync(context, token);

                // The instructor: whoever the app made co-host here, or anyone Zoom shows as co-host.
                bool coHostHere = CoHostPresent(room, session.SessionId);
                if (coHostHere) { sawCoHost = true; coHostLastSeen = _now(); }
                else if (!sawCoHost && AssignedCoHosts.For(session.SessionId).Count > 0 && room.Complete) { sawCoHost = true; coHostLastSeen = _now(); }
                TimeSpan? coHostGone = sawCoHost && !coHostHere && room.Complete && coHostLastSeen is { } seen ? _now() - seen : null;

                var held = tracker.Observe(_now(), room.State);
                var since = _now() - classStart;
                var decision = AutoEndRule.Decide(since, web && room.State == RoomState.SmallAndSilent ? RoomState.Busy : room.State, held);
                if (decision.Action != AutoEndAction.EndForAll)
                {
                    var byCoHost = CoHostAbsenceRule.Decide(since, sawCoHost, coHostGone, room.Rows.Any(r => r.Audio == ParticipantAudio.Unmuted), room.Complete);
                    if (byCoHost.Action == AutoEndAction.EndForAll || coHostGone != null) decision = byCoHost;
                }
                string summary = $"{Describe(room)} - {decision.Reason}";
                if (summary != lastLogged) { _log($"{session.GroupId} {classStart:HH:mm}: {summary}."); lastLogged = summary; }

                if (decision.Action == AutoEndAction.EndForAll)
                {
                    if (!room.Rows.Any(r => r.IsHostMe)) { _log($"{session.GroupId}: this account is not the meeting's host, so it is left open."); return; }
                    // One more look right before: someone may have just unmuted, joined or come back.
                    var again = await ReadRoomAsync(context, token);
                    bool stillOver = again.Complete && !again.Rows.Any(r => r.Audio == ParticipantAudio.Unmuted) &&
                                     (again.State == room.State || !CoHostPresent(again, session.SessionId) && coHostGone != null);
                    if (!stillOver) { await _delay(_interval, token); continue; }
                    _log($"{session.GroupId}: ending the meeting for everyone ({decision.Reason}).");
                    var (ended, message) = await _end(context, token);
                    _log($"{session.GroupId}: {message}");
                    if (ended) return;
                }
                await _delay(_interval, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log($"{context.Session.GroupId}: the end-of-class watch stopped ({ex.GetType().Name}: {ex.Message})."); }
    }

    private static bool CoHostPresent(Room room, Guid sessionId)
    {
        var assigned = AssignedCoHosts.For(sessionId);
        return room.Rows.Any(r => r.IsCoHost) ||
               room.Names.Any(n => assigned.Any(a => string.Equals(a.Trim(), n.Trim(), StringComparison.OrdinalIgnoreCase)));
    }

    private async Task<Room> ReadRoomAsync(MeetingLaunchContext context, CancellationToken token)
    {
        try
        {
            var read = await _participants(context).ReadAsync(token);
            // Without Zoom's row text nobody's role or mic can be known: never an empty, silent room.
            if (read.Participants.Any(p => p.RowLabel == null)) return new([], [], false, RoomState.Unreadable);
            var rows = read.Participants.Select(p => ParticipantRow.Parse(p.RowLabel)).ToArray();
            return new(rows, [.. read.Participants.Select(p => p.Name)], read.IsComplete, AutoEndRule.Classify(rows, read.IsComplete));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new([], [], false, RoomState.Unreadable); }
    }

    private static string Describe(Room room) => room.State switch
    {
        RoomState.HostAlone => "only the host is in the meeting",
        RoomState.SmallAndSilent => $"{room.Rows.Count(r => !r.IsMe)} people, all muted",
        RoomState.Busy => $"{room.Rows.Count(r => !r.IsMe)} people",
        _ => "the participants list could not be read",
    };

    public async ValueTask DisposeAsync()
    {
        _events.Lifecycle -= OnLifecycleAsync;
        Task all;
        lock (_sync)
        {
            foreach (var watch in _watches.Values) watch.Cancel();
            all = Task.WhenAll(_running);
        }
        try { await all.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        lock (_sync) { foreach (var watch in _watches.Values) watch.Dispose(); _watches.Clear(); }
    }
}
