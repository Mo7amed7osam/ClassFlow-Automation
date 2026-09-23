using Xunit;
using ZoomAutoAdmit.Attendance;
using ZoomAutoAdmit.CloudWorker.Stages;
using ZoomAutoAdmit.Core.Meetings;

namespace ZoomAutoAdmit.CloudWorker.Tests;

/// <summary>
/// When a class is ended for everyone. The rule itself is the Windows app's, tested in Core; these
/// are about the worker applying it to a live web meeting: the clock, the list, the last look before
/// ending, and the meeting being closed by somebody else.
/// </summary>
public class MeetingEndTests
{
    private static readonly DateTimeOffset ClassStart = new(2026, 9, 23, 10, 0, 0, TimeSpan.FromHours(3));

    /// <summary>A list that is read again and again; each read is the last row set given.</summary>
    private sealed class Room(params string[] rows) : IAttendanceParticipantSource
    {
        public AttendanceSource Source => AttendanceSource.Web;
        public string[] Rows { get; set; } = rows;
        public int Reads { get; private set; }
        public bool Complete { get; set; } = true;

        public Task<ParticipantReadResult> ReadAsync(CancellationToken cancellationToken)
        {
            Reads++;
            var people = Rows.Select(row => new ParticipantPresence(ZoomAutoAdmit.Attendance.ParticipantNames.Clean(row)) { RowLabel = row }).ToArray();
            return Task.FromResult(new ParticipantReadResult(people, Complete));
        }
    }

    /// <summary>A clock that jumps by whatever the code waits for, so three hours pass in no time.</summary>
    private sealed class Clock
    {
        public DateTimeOffset Now { get; set; } = ClassStart;

        /// <summary>Called with every wait the watch asks for, before the clock moves on.</summary>
        public Action<TimeSpan>? OnWait { get; set; }

        /// <summary>
        /// The wait a class spends is over at once, with the clock moved on by it - but the watch has
        /// to give the thread back each time, or a class that is never over would never return here.
        /// </summary>
        public async Task WaitAsync(TimeSpan span, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            OnWait?.Invoke(span);
            Now += span;
            await Task.Yield();
        }
    }

    private static (MeetingEnd End, Clock Clock, List<string> Log, Func<int> Ends) Build(
        Room room, bool open = true, bool breakout = false, bool endWorks = true, Guid? sessionId = null)
    {
        var clock = new Clock();
        var log = new List<string>();
        int ended = 0;
        var end = new MeetingEnd(
            room,
            meetingOpen: () => open,
            breakoutRoomsOpen: _ => Task.FromResult(breakout),
            endForAll: _ => { ended++; return Task.FromResult((endWorks, endWorks ? "Ended for everyone." : "The End button was not there.")); },
            ClassStart,
            sessionId ?? Guid.NewGuid(),
            log: log.Add,
            now: () => clock.Now,
            delay: clock.WaitAsync,
            interval: TimeSpan.FromSeconds(30));
        return (end, clock, log, () => ended);
    }

    [Fact]
    public async Task A_class_is_not_ended_before_three_hours_however_empty_the_room_is()
    {
        // Nobody but the host, from the first minute: the rule still waits for the three hours.
        var room = new Room("eyouth coordinator,(Host, me), Computer audio muted,Video off");
        var (end, clock, _, ends) = Build(room);

        var outcome = await end.WatchAsync(default);

        Assert.Equal(EndedHow.ByRule, outcome.How);
        Assert.Equal(1, ends());
        // Three hours, the host's fifty seconds alone, and the minute's last look - not before.
        Assert.True(clock.Now - ClassStart >= AutoEndRule.EndAfter + AutoEndRule.AloneFor);
    }

    [Fact]
    public async Task A_class_with_people_in_it_is_never_ended()
    {
        var room = new Room(
            "eyouth coordinator,(Host, me), Computer audio muted,Video off",
            "Mostafa Badr,(Guest), Computer audio unmuted,Video on",
            "Sara Ali,(Guest), Computer audio muted,Video on");
        var (end, clock, _, ends) = Build(room);

        // It never returns on its own, so the class is what stops it: five hours of watching here.
        using var stop = new CancellationTokenSource();
        var watching = end.WatchAsync(stop.Token);
        while (clock.Now - ClassStart < TimeSpan.FromHours(5) && !watching.IsCompleted) await Task.Yield();
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watching);
        Assert.Equal(0, ends());
    }

    [Fact]
    public async Task A_list_that_could_not_be_read_in_full_never_ends_a_class()
    {
        // An empty-looking room the reader could not finish is not an empty room.
        var room = new Room("eyouth coordinator,(Host, me), Computer audio muted,Video off") { Complete = false };
        var (end, clock, _, ends) = Build(room);

        using var stop = new CancellationTokenSource();
        var watching = end.WatchAsync(stop.Token);
        while (clock.Now - ClassStart < TimeSpan.FromHours(5) && !watching.IsCompleted) await Task.Yield();
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watching);
        Assert.Equal(0, ends());
    }

    [Fact]
    public async Task Breakout_rooms_hold_a_class_open_however_empty_the_main_room_looks()
    {
        var room = new Room("eyouth coordinator,(Host, me), Computer audio muted,Video off");
        var (end, clock, log, ends) = Build(room, breakout: true);

        using var stop = new CancellationTokenSource();
        var watching = end.WatchAsync(stop.Token);
        while (clock.Now - ClassStart < TimeSpan.FromHours(5) && !watching.IsCompleted) await Task.Yield();
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watching);
        Assert.Equal(0, ends());
        Assert.Contains(log, line => line.Contains("breakout rooms are open"));
    }

    [Fact]
    public async Task Somebody_who_comes_back_during_the_last_minute_keeps_the_class()
    {
        // Empty at the three hours, so the minute's warning starts - and in that minute a student
        // joins and unmutes. The class is kept, and the watch goes back to waiting.
        var room = new Room("eyouth coordinator,(Host, me), Computer audio muted,Video off");
        var (end, clock, _, ends) = Build(room);
        clock.OnWait = span =>
        {
            if (span != MeetingEnd.LastLook) return;
            room.Rows = [
                "eyouth coordinator,(Host, me), Computer audio muted,Video off",
                "Mostafa Badr,(Guest), Computer audio unmuted,Video on"];
        };

        using var stop = new CancellationTokenSource();
        var watching = end.WatchAsync(stop.Token);
        while (clock.Now - ClassStart < TimeSpan.FromHours(5) && !watching.IsCompleted) await Task.Yield();
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watching);
        Assert.Equal(0, ends());
    }

    [Fact]
    public async Task A_meeting_closed_somewhere_else_stops_the_watch_without_ending_anything()
    {
        var room = new Room("eyouth coordinator,(Host, me), Computer audio muted,Video off");
        var (end, _, log, ends) = Build(room, open: false);

        var outcome = await end.WatchAsync(default);

        Assert.Equal(EndedHow.Elsewhere, outcome.How);
        Assert.Equal(0, ends());
        Assert.Contains(log, line => line.Contains("closed somewhere else"));
    }

    [Fact]
    public async Task A_meeting_this_account_does_not_host_is_left_open()
    {
        // The account joined as a co-host (somebody else is the host): ending is not its business.
        var room = new Room("eyouth coordinator,(Co-host, me), Computer audio muted,Video off");
        var (end, _, _, ends) = Build(room);

        var outcome = await end.WatchAsync(default);

        Assert.Equal(EndedHow.NotHost, outcome.How);
        Assert.Equal(0, ends());
        Assert.False(outcome.Closed);
    }

    [Fact]
    public async Task The_instructor_leaving_with_the_class_ends_it_five_minutes_later()
    {
        // The bridge made Nada co-host. After the three hours she and most of the class leave: the
        // lesson is over, which is the user's second rule.
        var session = Guid.NewGuid();
        AssignedCoHosts.Record(session, "Nada Instructor");
        var room = new Room(
            "eyouth coordinator,(Host, me), Computer audio muted,Video off",
            "Nada Instructor,(Co-host, guest), Computer audio muted,Video on",
            "Mostafa Badr,(Guest), Computer audio muted,Video on",
            "Sara Ali,(Guest), Computer audio muted,Video on",
            "Omar Nabil,(Guest), Computer audio muted,Video on");
        var (end, clock, _, ends) = Build(room, sessionId: session);

        var watching = Task.Run(async () =>
        {
            var task = end.WatchAsync(default);
            bool left = false;
            while (!task.IsCompleted)
            {
                if (!left && clock.Now - ClassStart >= AutoEndRule.EndAfter)
                {
                    // She goes, and the class goes with her: one student stays behind.
                    left = true;
                    room.Rows = [
                        "eyouth coordinator,(Host, me), Computer audio muted,Video off",
                        "Mostafa Badr,(Guest), Computer audio muted,Video on"];
                }
                await Task.Yield();
            }
            return await task;
        });
        var outcome = await watching;

        Assert.Equal(EndedHow.ByRule, outcome.How);
        Assert.Contains("co-host", outcome.Reason);
        Assert.Equal(1, ends());
        Assert.True(clock.Now - ClassStart >= AutoEndRule.EndAfter + CoHostAbsenceRule.GoneFor);
    }

    [Fact]
    public async Task The_instructor_dropping_out_alone_does_not_end_a_class_that_still_has_its_people()
    {
        // Her connection dropped; the class is still sitting there. Windows leaves it alone, and so
        // does the worker.
        var session = Guid.NewGuid();
        AssignedCoHosts.Record(session, "Nada Instructor");
        var room = new Room(
            "eyouth coordinator,(Host, me), Computer audio muted,Video off",
            "Nada Instructor,(Co-host, guest), Computer audio muted,Video on",
            "Mostafa Badr,(Guest), Computer audio muted,Video on",
            "Sara Ali,(Guest), Computer audio muted,Video on",
            "Omar Nabil,(Guest), Computer audio muted,Video on",
            "Hana Tarek,(Guest), Computer audio muted,Video on");
        var (end, clock, _, ends) = Build(room, sessionId: session);

        using var stop = new CancellationTokenSource();
        var watching = end.WatchAsync(stop.Token);
        bool dropped = false;
        while (clock.Now - ClassStart < TimeSpan.FromHours(5) && !watching.IsCompleted)
        {
            if (!dropped && clock.Now - ClassStart >= AutoEndRule.EndAfter)
            {
                dropped = true;
                room.Rows = [.. room.Rows.Where(row => !row.StartsWith("Nada", StringComparison.Ordinal))];
            }
            await Task.Yield();
        }
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watching);
        Assert.Equal(0, ends());
    }

    [Fact]
    public async Task The_last_read_is_taken_before_the_meeting_is_ended()
    {
        // Attendance is written down while the class is still there to be read: after ending, the
        // list is gone.
        var room = new Room("eyouth coordinator,(Host, me), Computer audio muted,Video off");
        var clock = new Clock();
        var order = new List<string>();
        var end = new MeetingEnd(
            room,
            meetingOpen: () => true,
            breakoutRoomsOpen: _ => Task.FromResult(false),
            endForAll: _ => { order.Add("ended"); return Task.FromResult((true, "Ended for everyone.")); },
            ClassStart,
            Guid.NewGuid(),
            now: () => clock.Now,
            delay: clock.WaitAsync,
            interval: TimeSpan.FromSeconds(30),
            beforeEnding: _ => { order.Add("read"); return Task.CompletedTask; });

        await end.WatchAsync(default);

        Assert.Equal(["read", "ended"], order);
    }

    [Fact]
    public async Task A_meeting_that_would_not_close_is_reported_rather_than_forgotten()
    {
        var room = new Room("eyouth coordinator,(Host, me), Computer audio muted,Video off");
        var (end, _, _, ends) = Build(room, endWorks: false);

        var outcome = await end.WatchAsync(default);

        Assert.Equal(EndedHow.EndFailed, outcome.How);
        Assert.False(outcome.Closed);
        Assert.Equal(1, ends());
    }
}
