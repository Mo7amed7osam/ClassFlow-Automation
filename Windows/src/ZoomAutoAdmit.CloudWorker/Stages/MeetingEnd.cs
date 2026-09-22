using ZoomAutoAdmit.Attendance;
using ZoomAutoAdmit.Core.Meetings;

namespace ZoomAutoAdmit.CloudWorker.Stages;

/// <summary>How a held class came to an end.</summary>
public enum EndedHow
{
    /// <summary>The Windows rule said the class was over, and this worker ended it for everyone.</summary>
    ByRule,
    /// <summary>The meeting was closed somewhere else - by the instructor, or from a phone.</summary>
    Elsewhere,
    /// <summary>The rule said it was over, but this account is not the host, so it was left open.</summary>
    NotHost,
    /// <summary>The rule said it was over and pressing End did not work.</summary>
    EndFailed,
}

public sealed record MeetingEndOutcome(EndedHow How, string Reason)
{
    /// <summary>Whether the meeting is closed now, by whoever.</summary>
    public bool Closed => How is EndedHow.ByRule or EndedHow.Elsewhere;
}

/// <summary>
/// When a class is ended for everyone: the Windows app's AutoEndMeetingBridge, for the web client,
/// with the same two rules from Core and nothing of its own.
///
/// From three hours after the class's time, the class is ended when nobody but the host is left, or
/// when the instructor (the co-host the bridge made) left and most of the class went with them five
/// minutes ago. Never while anyone's mic is on, never on a list that could not be read in full,
/// never while breakout rooms are open, and never by an account that is not the host. Until then it
/// only watches for the meeting being closed somewhere else.
///
/// "Fewer than five, all muted" is not used, exactly as on Windows: the web list does not say who is
/// muted reliably enough to end a class on it.
///
/// The Windows app gives a minute's warning a person can answer before ending. Nobody answers a
/// server, so the minute is kept and used for what Windows uses it for when nobody answers: one more
/// look, and the class is ended only if it still reads as over.
/// </summary>
public sealed class MeetingEnd(
    IAttendanceParticipantSource participants,
    Func<bool> meetingOpen,
    Func<CancellationToken, Task<bool>> breakoutRoomsOpen,
    Func<CancellationToken, Task<(bool Ended, string Message)>> endForAll,
    DateTimeOffset classStart,
    Guid sessionId,
    Action<string>? log = null,
    Func<DateTimeOffset>? now = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    TimeSpan? interval = null,
    Func<CancellationToken, Task>? beforeEnding = null)
{
    public static readonly TimeSpan WakeBefore = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LastLook = TimeSpan.FromMinutes(1);

    private readonly Action<string> _log = log ?? (_ => { });
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly TimeSpan _interval = interval ?? TimeSpan.FromSeconds(30);

    private sealed record Room(IReadOnlyList<ParticipantRow> Rows, IReadOnlyList<string> Names, bool Complete, RoomState State);

    /// <summary>Watches until the class is over, and ends it. Cancelled, it throws; it never ends a class then.</summary>
    public async Task<MeetingEndOutcome> WatchAsync(CancellationToken cancellationToken)
    {
        // Waiting for the three hours - and watching for the meeting being closed somewhere else
        // meanwhile. Twice in a row, so one unlucky look is not an end.
        var wake = classStart + AutoEndRule.EndAfter - WakeBefore;
        int missing = 0;
        while (_now() < wake)
        {
            var left = wake - _now();
            await _delay(left < _interval ? left : _interval, cancellationToken);
            if (_now() >= wake) break;
            if (meetingOpen()) { missing = 0; continue; }
            if (++missing < 2) continue;
            return Elsewhere();
        }

        var tracker = new AutoEndTracker();
        bool sawCoHost = false;
        DateTimeOffset? coHostLastSeen = null;
        string? lastLogged = null;
        int peopleWithCoHost = 0;
        DateTimeOffset? emptySince = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!meetingOpen()) return Elsewhere();
            var room = await ReadRoomAsync(cancellationToken);

            // The instructor: whoever the bridge made co-host here, or anyone Zoom shows as co-host.
            bool coHostHere = CoHostPresent(room);
            if (coHostHere) { sawCoHost = true; coHostLastSeen = _now(); }
            else if (!sawCoHost && AssignedCoHosts.For(sessionId).Count > 0 && room.Complete) { sawCoHost = true; coHostLastSeen = _now(); }
            TimeSpan? coHostGone = sawCoHost && !coHostHere && room.Complete && coHostLastSeen is { } seen ? _now() - seen : null;

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
            // The web list is not trusted to say who is muted: "small and silent" counts as busy.
            var decision = AutoEndRule.Decide(since, room.State == RoomState.SmallAndSilent ? RoomState.Busy : room.State, held);
            if (decision.Action != AutoEndAction.EndForAll)
            {
                var byCoHost = CoHostAbsenceRule.Decide(since, sawCoHost, coHostGone,
                    room.Rows.Any(r => r.Audio == ParticipantAudio.Unmuted), room.Complete,
                    peopleWithCoHost, people, emptySince is { } emptied ? _now() - emptied : null);
                if (byCoHost.Action == AutoEndAction.EndForAll || coHostGone != null) decision = byCoHost;
            }
            if (decision.Action == AutoEndAction.EndForAll && await breakoutRoomsOpen(cancellationToken))
                decision = new AutoEndDecision(AutoEndAction.Wait, "breakout rooms are open");

            string summary = $"{Describe(room)} - {decision.Reason}";
            if (summary != lastLogged) { _log($"[end] {summary}"); lastLogged = summary; }

            if (decision.Action == AutoEndAction.EndForAll)
            {
                if (!room.Rows.Any(r => r.IsHostMe))
                {
                    _log("[end] this account is not the meeting's host, so the meeting is left open");
                    return new(EndedHow.NotHost, $"{decision.Reason}, but this account is not the meeting's host, so it was left open.");
                }

                // The minute the Windows app gives a person, and then one more look: someone may have
                // unmuted, joined, come back, or opened breakout rooms meanwhile.
                _log($"[end] {decision.Reason}; looking once more in a minute before ending it");
                await _delay(LastLook, cancellationToken);
                var again = await ReadRoomAsync(cancellationToken);
                bool stillOver = again.Complete && !again.Rows.Any(r => r.Audio == ParticipantAudio.Unmuted) &&
                                 (again.State == room.State || !CoHostPresent(again) && coHostGone != null) &&
                                 !await breakoutRoomsOpen(cancellationToken);
                if (!stillOver) { await _delay(_interval, cancellationToken); continue; }

                if (beforeEnding is not null)
                {
                    try { await beforeEnding(cancellationToken); }
                    catch (Exception problem) when (problem is not OperationCanceledException) { _log($"[end] before ending: {problem.Message}"); }
                }
                _log($"[end] ending the meeting for everyone ({decision.Reason})");
                var (ended, message) = await endForAll(cancellationToken);
                _log($"[end] {message}");
                if (ended)
                {
                    LiveMeetings.Finish(sessionId);
                    return new(EndedHow.ByRule, $"{decision.Reason}. {message}");
                }
                return new(EndedHow.EndFailed, $"{decision.Reason}, but ending it did not work: {message}");
            }
            await _delay(_interval, cancellationToken);
        }
    }

    private MeetingEndOutcome Elsewhere()
    {
        LiveMeetings.Finish(sessionId);
        _log("[end] the meeting was closed somewhere else");
        return new(EndedHow.Elsewhere, "The meeting was closed somewhere else (by the instructor, or from a phone).");
    }

    private bool CoHostPresent(Room room)
    {
        var assigned = AssignedCoHosts.For(sessionId);
        return room.Rows.Any(r => r.IsCoHost) ||
               room.Names.Any(n => assigned.Any(a => string.Equals(a.Trim(), n.Trim(), StringComparison.OrdinalIgnoreCase)));
    }

    private async Task<Room> ReadRoomAsync(CancellationToken cancellationToken)
    {
        try
        {
            var read = await participants.ReadAsync(cancellationToken);
            // Without Zoom's row text nobody's role or mic can be known: never an empty, silent room.
            if (read.Participants.Any(p => p.RowLabel == null)) return new([], [], false, RoomState.Unreadable);
            var rows = read.Participants.Select(p => ParticipantRow.Parse(p.RowLabel)).ToArray();
            return new(rows, [.. read.Participants.Select(p => p.Name)], read.IsComplete, AutoEndRule.Classify(rows, read.IsComplete));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new([], [], false, RoomState.Unreadable); }
    }

    private static string Describe(Room room) => room.State switch
    {
        RoomState.HostAlone => "only the host is in the meeting",
        RoomState.SmallAndSilent => $"{room.Rows.Count(r => !r.IsMe)} people, all muted",
        RoomState.Busy => $"{room.Rows.Count(r => !r.IsMe)} people",
        _ => "the participants list could not be read",
    };
}
