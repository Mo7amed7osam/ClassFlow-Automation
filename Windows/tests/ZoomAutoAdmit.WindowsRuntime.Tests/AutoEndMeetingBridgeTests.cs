using ZoomAutoAdmit.Attendance;
using ZoomAutoAdmit.Core.Meetings;
using ZoomAutoAdmit.Core.Sessions;
using ZoomAutoAdmit.Inspector.Runtime;
using Xunit;

namespace ZoomAutoAdmit.WindowsRuntime.Tests;

public sealed class AutoEndMeetingBridgeTests
{
    private static readonly DateTimeOffset ClassTime = new(2026, 9, 16, 19, 0, 0, TimeSpan.FromHours(3));
    private const string Host = "eyouth coordinator,(Host, me), Computer audio muted,Video off";
    private static string Guest(string name, bool talking = false) => $"{name},(Guest), Computer audio {(talking ? "unmuted" : "muted")},Video off";

    /// <summary>A meeting whose room changes as the minutes pass (a fake clock; no real Zoom anywhere).</summary>
    private sealed class Room(Func<DateTimeOffset, string[]> rowsAt, Func<DateTimeOffset> clock) : IAttendanceParticipantSource
    {
        public AttendanceSource Source => AttendanceSource.Desktop;
        public Task<ParticipantReadResult> ReadAsync(CancellationToken token) =>
            Task.FromResult(new ParticipantReadResult([.. rowsAt(clock()).Select(l => new ParticipantPresence(l) { RowLabel = l })], true));
    }

    private static Task<(List<DateTimeOffset> Ended, bool Open)> Run(Func<DateTimeOffset, string[]> rowsAt, TimeSpan until,
        SessionEngineType engine = SessionEngineType.Desktop, string? assignedCoHost = null) =>
        RunAsync(rowsAt, until, engine, assignedCoHost).ContinueWith(t => (t.Result.Ended, t.Result.Open), TaskScheduler.Default);

    /// <param name="answer">What a person watching the countdown presses, as soon as it appears.</param>
    private static async Task<(List<DateTimeOffset> Ended, bool Open, IReadOnlyList<ClassEnding> Endings)> RunAsync(
        Func<DateTimeOffset, string[]> rowsAt, TimeSpan until, SessionEngineType engine = SessionEngineType.Desktop,
        string? assignedCoHost = null, PendingEndAnswer? answer = null, Func<DateTimeOffset, bool>? breakoutRoomsOpen = null)
    {
        var now = ClassTime.AddHours(1);
        var ended = new List<DateTimeOffset>();
        bool open = true;
        var events = new MeetingLifecycleEvents();
        // Its own folders: a test never announces a countdown to the app running on this PC, and
        // never writes into the real record of how classes ended.
        string scratch = Path.Combine(Path.GetTempPath(), $"auto-end-{Guid.NewGuid():N}");
        var ends = new PendingMeetingEnds(Path.Combine(scratch, "ending"));
        var endings = new ClassEndings(Path.Combine(scratch, "endings.json"));
        await using var bridge = new AutoEndMeetingBridge(events, _ => new Room(rowsAt, () => now),
            end: (_, _) => { ended.Add(now); open = false; return Task.FromResult((true, "ended")); },
            meetingOpen: _ => open && now < ClassTime + until,
            breakoutRoomsOpen: _ => Task.FromResult(breakoutRoomsOpen?.Invoke(now) ?? false),
            log: _ => { }, interval: TimeSpan.FromSeconds(30), now: () => now,
            delay: (d, _) =>
            {
                now += d;
                // Whoever is watching presses their answer the moment the countdown shows up.
                if (answer is { } said)
                    foreach (var waiting in ends.Waiting(now)) ends.Answer(waiting.SessionId, said);
                return Task.CompletedTask;
            },
            activity: new ZoomAutoAdmit.Core.Central.ActivityLog(Path.Combine(scratch, "activity")),
            ends: ends,
            endings: endings);
        var meeting = new ScheduledMeeting(new Uri("https://zoom.us/j/12345678901"), "S7", DateTimeOffset.UtcNow, GroupId: "CAI5_AIS4_S7", ScheduledStartTime: ClassTime);
        var session = new MeetingSession(Guid.NewGuid(), meeting, DateTimeOffset.UtcNow);
        if (assignedCoHost != null) AssignedCoHosts.Record(session.SessionId, assignedCoHost);
        await events.PublishAsync(new MeetingLaunchContext(session, new("S7", "S7", "ref"), engine, null), MeetingLifecycleEventKind.Active);
        await bridge.DrainAsync();
        var written = endings.All();
        try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); } catch (IOException) { }
        return (ended, open, written);
    }

    [Fact]
    public async Task OnlyTheHostLeftAfterThreeHoursEndsTheMeeting()
    {
        var (ended, _) = await Run(t => t < ClassTime.AddHours(2.5) ? [Host, Guest("A"), Guest("B")] : [Host], TimeSpan.FromHours(5));
        var at = Assert.Single(ended);
        Assert.InRange(at - ClassTime, TimeSpan.FromHours(3), TimeSpan.FromHours(3) + TimeSpan.FromMinutes(7));
    }

    [Fact]
    public async Task AFewMutedPeopleForFiveMinutesEndIt()
    {
        var (ended, _) = await Run(_ => [Host, Guest("A"), Guest("B"), Guest("C")], TimeSpan.FromHours(5));
        // Quiet all along: the five muted minutes run from the three hours (3 h 05), and the
        // five-minute warning follows, so it ends at about 3 h 10.
        Assert.InRange(Assert.Single(ended) - ClassTime, TimeSpan.FromMinutes(189), TimeSpan.FromMinutes(191));
    }

    [Fact]
    public async Task NobodyIsCutOffWhileSomeoneTalksOrTheRoomIsFull()
    {
        var (talking, _) = await Run(_ => [Host, Guest("A"), Guest("Teacher", talking: true)], TimeSpan.FromHours(4));
        Assert.Empty(talking);
        var (full, _) = await Run(_ => [Host, Guest("A"), Guest("B"), Guest("C"), Guest("D"), Guest("E")], TimeSpan.FromHours(4));
        Assert.Empty(full);
    }

    [Fact]
    public async Task SomeoneUnmutingJustBeforeTheEndKeepsItGoing()
    {
        // Muted from 3 h, but the teacher speaks at 3 h 04: the five minutes start again from there.
        var (ended, _) = await Run(t => t - ClassTime is var age && age > TimeSpan.FromMinutes(184) && age < TimeSpan.FromMinutes(185)
            ? [Host, Guest("A"), Guest("Teacher", talking: true)] : [Host, Guest("A"), Guest("Teacher")], TimeSpan.FromHours(5));
        Assert.True(Assert.Single(ended) - ClassTime >= TimeSpan.FromMinutes(194));
    }

    private static string Instructor(bool talking = false) => $"Mostafa Badr,(Co-host, guest), Computer audio {(talking ? "unmuted" : "muted")},Video on";

    [Fact]
    public async Task TheInstructorLeavingForFiveMinutesAfterThreeHoursEndsIt()
    {
        // Twenty students still there, but the co-host (the instructor) left at 3 h 10.
        string[] students = [.. Enumerable.Range(1, 20).Select(i => Guest("Student " + i))];
        var (ended, _) = await Run(t => t - ClassTime < TimeSpan.FromMinutes(190) ? [Host, Instructor(), .. students] : [Host, .. students], TimeSpan.FromHours(5));
        // Last seen at the 30-second read just before 3 h 10: five minutes after that.
        Assert.InRange(Assert.Single(ended) - ClassTime, TimeSpan.FromMinutes(198), TimeSpan.FromMinutes(201));
    }

    [Fact]
    public async Task TheInstructorComingBackKeepsItGoing()
    {
        string[] students = [Guest("A"), Guest("B"), Guest("C"), Guest("D"), Guest("E")];
        // Out for three minutes only, then back until the meeting closes by itself at 4 h.
        var (ended, _) = await Run(t => t - ClassTime is var age && age > TimeSpan.FromMinutes(185) && age < TimeSpan.FromMinutes(188)
            ? [Host, .. students] : [Host, Instructor(), .. students], TimeSpan.FromHours(4));
        Assert.Empty(ended);
    }

    [Fact]
    public async Task OnTheWebTheHostAloneOrTheInstructorGoneEndsIt()
    {
        // The Web list says who is who, not who is muted.
        const string webHost = "Mohab (Host, me)";
        var (alone, _) = await Run(t => t - ClassTime < TimeSpan.FromMinutes(170) ? [webHost, "Rowida Amr"] : [webHost], TimeSpan.FromHours(5), SessionEngineType.Web);
        Assert.InRange(Assert.Single(alone) - ClassTime, TimeSpan.FromHours(3), TimeSpan.FromMinutes(187));
        var (gone, _) = await Run(t => t - ClassTime < TimeSpan.FromMinutes(200) ? [webHost, "Mostafa Badr", "Rowida Amr", "A", "B", "C", "D"] : [webHost, "Rowida Amr", "A", "B", "C", "D"],
            TimeSpan.FromHours(5), SessionEngineType.Web, assignedCoHost: "Mostafa Badr");
        Assert.InRange(Assert.Single(gone) - ClassTime, TimeSpan.FromMinutes(208), TimeSpan.FromMinutes(211));
        var (small, _) = await Run(_ => [webHost, "Rowida Amr", "A"], TimeSpan.FromHours(4), SessionEngineType.Web);
        Assert.Empty(small);                                     // a small room is not ended on the Web: mics are unknown
    }

    [Fact]
    public async Task ANonHostNeverEndsTheMeeting()
    {
        var (ended, _) = await Run(_ => ["Helper,(Co-host, me), Computer audio muted,Video off"], TimeSpan.FromHours(5));
        Assert.Empty(ended);
    }
    // ---------------------------------------------------------------- the minute before it ends

    [Fact]
    public async Task PressingEndNowDoesNotWaitTheMinuteOut()
    {
        var (ended, _, endings) = await RunAsync(t => t < ClassTime.AddHours(2.5) ? [Host, Guest("A")] : [Host],
            TimeSpan.FromHours(5), answer: PendingEndAnswer.EndNow);
        var at = Assert.Single(ended);
        // The room is judged over at 3 h 01; pressing End now ends it there and then, instead of
        // five minutes later when the countdown would have run out by itself.
        Assert.InRange(at - ClassTime, TimeSpan.FromMinutes(181), TimeSpan.FromMinutes(181) + TimeSpan.FromSeconds(30));
        Assert.Equal(ClassEndedHow.Program, Assert.Single(endings).How);
    }

    [Fact]
    public async Task SayingYouWillEndItYourselfLeavesTheClassOpen()
    {
        var (ended, open, endings) = await RunAsync(t => t < ClassTime.AddHours(2.5) ? [Host, Guest("A")] : [Host],
            TimeSpan.FromHours(5), answer: PendingEndAnswer.EndManually);
        Assert.Empty(ended);
        Assert.True(open);
        Assert.Equal(ClassEndedHow.ByHand, Assert.Single(endings).How);
    }

    [Fact]
    public async Task AMeetingClosedSomewhereElseIsNoticedAndWrittenDown()
    {
        // Closed an hour and a half in - from a phone, say - long before the three hours.
        var (ended, _, endings) = await RunAsync(_ => [Host, Guest("A")], TimeSpan.FromMinutes(90));
        Assert.Empty(ended);
        var noticed = Assert.Single(endings);
        Assert.Equal(ClassEndedHow.Elsewhere, noticed.How);
        Assert.Equal("CAI5_AIS4_S7", noticed.Group);
        Assert.Equal(new TimeOnly(19, 0), noticed.Start);
    }

    [Fact]
    public async Task NothingIsEndedWhileBreakoutRoomsAreOpen()
    {
        // The host looks alone because everyone is inside a room.
        var (ended, open, endings) = await RunAsync(_ => [Host], TimeSpan.FromHours(5), breakoutRoomsOpen: _ => true);
        Assert.Empty(ended);
        Assert.True(open);
        // It was still open when the watch stopped; the program never ended it.
        Assert.DoesNotContain(endings, e => e.How == ClassEndedHow.Program);
    }

    [Fact]
    public async Task OnceTheRoomsAreClosedTheClassIsEndedAsUsual()
    {
        var closed = ClassTime.AddHours(4);
        var (ended, _, _) = await RunAsync(_ => [Host], TimeSpan.FromHours(6), breakoutRoomsOpen: t => t < closed);
        Assert.InRange(Assert.Single(ended) - ClassTime, TimeSpan.FromHours(4), TimeSpan.FromHours(4) + TimeSpan.FromMinutes(7));
    }

}
