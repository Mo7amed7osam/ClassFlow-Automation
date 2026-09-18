using Microsoft.Playwright;
using ZoomAutoAdmit.Attendance;
using ZoomAutoAdmit.Core.Central;
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
    /// <summary>Whether breakout rooms are open: a class is never ended while anyone may be inside one.</summary>
    private readonly Func<MeetingLaunchContext, Task<bool>> _breakoutRoomsOpen;
    private readonly LmsMeetingBridge.ClassStart? _classStart;
    private readonly Action<string> _log;
    /// <summary>What this PC did, for the central server.</summary>
    private readonly ActivityLog _activity;
    /// <summary>The countdown a person can answer, wherever the app happens to be running.</summary>
    private readonly PendingMeetingEnds _ends;
    /// <summary>When each class ended and how, for the class card.</summary>
    private readonly ClassEndings _endings;
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
        Func<IPage?>? webPage = null,
        Func<MeetingLaunchContext, Task<bool>>? breakoutRoomsOpen = null,
        ActivityLog? activity = null,
        PendingMeetingEnds? ends = null,
        ClassEndings? endings = null)
    {
        // A test passes its own log; nothing is written to the folder the agent sends from.
        _activity = activity ?? new ActivityLog();
        _ends = ends ?? new PendingMeetingEnds();
        _endings = endings ?? new ClassEndings();
        _events = events;
        _participants = participants;
        _end = end ?? ((context, token) => context.EngineType == SessionEngineType.Web
            ? ZoomWebMeetingEnder.EndForAllAsync(webPage?.Invoke(), token)
            : Task.Run(() => new ZoomDesktopMeetingEnder().EndForAll(), token));
        _meetingOpen = meetingOpen ?? (context => context.EngineType == SessionEngineType.Web
            ? webPage?.Invoke() is { IsClosed: false }
            : ZoomDesktopMeetingEnder.MeetingWindow() != IntPtr.Zero);
        _breakoutRoomsOpen = breakoutRoomsOpen ?? (context => context.EngineType == SessionEngineType.Web
            ? ZoomWebBreakoutRooms.AreOpenAsync(webPage?.Invoke())
            : Task.Run(ZoomBreakoutRooms.AreOpen));
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

            // Waiting for the three hours - and watching for the meeting being closed somewhere
            // else meanwhile (from a phone, say). Twice in a row, so one unlucky look is not an end.
            var wake = classStart + AutoEndRule.EndAfter - WakeBefore;
            int missing = 0;
            while (_now() < wake)
            {
                var left = wake - _now();
                await _delay(left < _interval ? left : _interval, token);
                if (_now() >= wake) break;
                if (_meetingOpen(context)) { missing = 0; continue; }
                if (++missing < 2) continue;
                EndedElsewhere(session, classStart);
                return;
            }

            var tracker = new AutoEndTracker();
            bool sawCoHost = false;
            DateTimeOffset? coHostLastSeen = null;
            string? lastLogged = null;
            // How full the class was while the instructor was still in it, and since when it has been
            // this empty: the class walking out with them is what says the lesson is over.
            int peopleWithCoHost = 0;
            DateTimeOffset? emptySince = null;
            while (!token.IsCancellationRequested)
            {
                if (!Enabled) { await _delay(_interval, token); continue; }
                if (!_meetingOpen(context)) { EndedElsewhere(session, classStart); return; }
                var room = await ReadRoomAsync(context, token);

                // The instructor: whoever the app made co-host here, or anyone Zoom shows as co-host.
                bool coHostHere = CoHostPresent(room, session.SessionId);
                if (coHostHere) { sawCoHost = true; coHostLastSeen = _now(); }
                else if (!sawCoHost && AssignedCoHosts.For(session.SessionId).Count > 0 && room.Complete) { sawCoHost = true; coHostLastSeen = _now(); }
                TimeSpan? coHostGone = sawCoHost && !coHostHere && room.Complete && coHostLastSeen is { } seen ? _now() - seen : null;

                // The class as it was with the instructor in it, against the class now.
                int people = room.Rows.Count(r => !r.IsMe && !r.IsJoining);
                if (room.Complete && coHostHere) peopleWithCoHost = Math.Max(peopleWithCoHost, people);
                if (room.Complete)
                {
                    bool empty = CoHostAbsenceRule.MostLeft(peopleWithCoHost, people);
                    if (!empty) emptySince = null;
                    else emptySince ??= _now();
                }

                var held = tracker.Observe(_now(), room.State);
                var since = _now() - classStart;
                var decision = AutoEndRule.Decide(since, web && room.State == RoomState.SmallAndSilent ? RoomState.Busy : room.State, held);
                if (decision.Action != AutoEndAction.EndForAll)
                {
                    var byCoHost = CoHostAbsenceRule.Decide(since, sawCoHost, coHostGone,
                        room.Rows.Any(r => r.Audio == ParticipantAudio.Unmuted), room.Complete,
                        peopleWithCoHost, people, emptySince is { } emptied ? _now() - emptied : null);
                    if (byCoHost.Action == AutoEndAction.EndForAll || coHostGone != null) decision = byCoHost;
                }
                // Everyone may simply be inside a breakout room: the main list leaves them out, so a
                // room that looks empty is not one. Only asked once the room otherwise looks over.
                if (decision.Action == AutoEndAction.EndForAll && await _breakoutRoomsOpen(context))
                    decision = new AutoEndDecision(AutoEndAction.Wait, "breakout rooms are open");

                string summary = $"{Describe(room)} - {decision.Reason}";
                if (summary != lastLogged) { _log($"{session.GroupId} {classStart:HH:mm}: {summary}."); lastLogged = summary; }

                if (decision.Action == AutoEndAction.EndForAll)
                {
                    if (!room.Rows.Any(r => r.IsHostMe)) { _log($"{session.GroupId}: this account is not the meeting's host, so it is left open."); return; }
                    // A minute's warning first, wherever anyone is watching: they can end it at once,
                    // or keep the class and end it themselves. Nobody answering ends it as before.
                    var answer = await WaitForAnswerAsync(session, decision.Reason, classStart, token);
                    if (answer == PendingEndAnswer.EndManually)
                    {
                        _ends.Withdraw(session.SessionId);
                        _log($"{session.GroupId}: someone is ending this class themselves; stopped watching.");
                        _activity.Write("class.end.cancelled", "skipped",
                            $"{decision.Reason}: someone chose to end this class themselves.", session.GroupId,
                            DateOnly.FromDateTime(classStart.LocalDateTime));
                        Record(session, classStart, ClassEndedHow.ByHand, "Left open: someone said they would end it themselves.");
                        return;
                    }
                    if (answer != PendingEndAnswer.EndNow)
                    {
                        // One more look right before: someone may have just unmuted, joined, come back,
                        // or opened breakout rooms during the countdown.
                        var again = await ReadRoomAsync(context, token);
                        bool stillOver = again.Complete && !again.Rows.Any(r => r.Audio == ParticipantAudio.Unmuted) &&
                                         (again.State == room.State || !CoHostPresent(again, session.SessionId) && coHostGone != null) &&
                                         !await _breakoutRoomsOpen(context);
                        if (!stillOver) { _ends.Withdraw(session.SessionId); await _delay(_interval, token); continue; }
                    }
                    _log($"{session.GroupId}: ending the meeting for everyone ({decision.Reason}).");
                    var (ended, message) = await _end(context, token);
                    _ends.Withdraw(session.SessionId);
                    _log($"{session.GroupId}: {message}");
                    if (ended) Record(session, classStart, ClassEndedHow.Program, $"{decision.Reason}. {message}");
                    // The central server is told the class was ended here, and why.
                    _activity.Write("class.ended", ended ? "done" : "failed",
                        $"{decision.Reason}: {message}", session.GroupId,
                        DateOnly.FromDateTime(classStart.LocalDateTime));
                    if (ended) return;
                }
                await _delay(_interval, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log($"{context.Session.GroupId}: the end-of-class watch stopped ({ex.GetType().Name}: {ex.Message})."); }
    }

    /// <summary>The meeting is gone and this program did not end it: someone closed it elsewhere.</summary>
    private void EndedElsewhere(MeetingSession session, DateTimeOffset classStart)
    {
        _ends.Withdraw(session.SessionId);
        _log($"{session.GroupId}: the meeting is over (closed somewhere else); stopped watching.");
        _activity.Write("class.ended", "done", "The meeting was closed somewhere else.", session.GroupId,
            DateOnly.FromDateTime(classStart.LocalDateTime), at: _now());
        Record(session, classStart, ClassEndedHow.Elsewhere, "Closed somewhere else (not by this program).");
    }

    private void Record(MeetingSession session, DateTimeOffset classStart, ClassEndedHow how, string message) =>
        _endings.Record(new ClassEnding
        {
            Group = session.GroupId,
            Date = DateOnly.FromDateTime(classStart.LocalDateTime),
            Start = new TimeOnly(classStart.LocalDateTime.Hour, classStart.LocalDateTime.Minute),
            At = _now(),
            How = how,
            Message = message,
        });

    /// <summary>
    /// Counts the minute down where anyone can see it and waits for an answer. Nobody watching
    /// (a class a Windows task opened, with the app closed) simply means the minute passes.
    /// </summary>
    private async Task<PendingEndAnswer> WaitForAnswerAsync(MeetingSession session, string reason, DateTimeOffset classStart, CancellationToken token)
    {
        var endsAt = _now() + PendingMeetingEnds.Warning;
        _ends.Announce(new PendingMeetingEndNotice
        {
            SessionId = session.SessionId,
            Group = session.GroupId,
            ClassStart = classStart,
            Reason = reason,
            EndsAt = endsAt,
        });
        _log($"{session.GroupId}: {reason}; ending it in {PendingMeetingEnds.Warning.TotalSeconds:0} seconds unless someone says otherwise.");
        while (_now() < endsAt)
        {
            if (_ends.Read(session.SessionId) is var said && said != PendingEndAnswer.NoAnswer) return said;
            await _delay(TimeSpan.FromSeconds(1), token);
        }
        return _ends.Read(session.SessionId);
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
