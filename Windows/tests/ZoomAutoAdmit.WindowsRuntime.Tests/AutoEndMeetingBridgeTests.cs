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

    private static async Task<(List<DateTimeOffset> Ended, bool Open)> Run(Func<DateTimeOffset, string[]> rowsAt, TimeSpan until)
    {
        var now = ClassTime.AddHours(1);
        var ended = new List<DateTimeOffset>();
        bool open = true;
        var events = new MeetingLifecycleEvents();
        await using var bridge = new AutoEndMeetingBridge(events, _ => new Room(rowsAt, () => now),
            end: (_, _) => { ended.Add(now); open = false; return Task.FromResult((true, "ended")); },
            meetingOpen: () => open && now < ClassTime + until,
            log: _ => { }, interval: TimeSpan.FromSeconds(30), now: () => now,
            delay: (d, _) => { now += d; return Task.CompletedTask; });
        var meeting = new ScheduledMeeting(new Uri("https://zoom.us/j/12345678901"), "S7", DateTimeOffset.UtcNow, GroupId: "CAI5_AIS4_S7", ScheduledStartTime: ClassTime);
        var session = new MeetingSession(Guid.NewGuid(), meeting, DateTimeOffset.UtcNow);
        await events.PublishAsync(new MeetingLaunchContext(session, new("S7", "S7", "ref"), SessionEngineType.Desktop, null), MeetingLifecycleEventKind.Active);
        await bridge.DrainAsync();
        return (ended, open);
    }

    [Fact]
    public async Task OnlyTheHostLeftAfterThreeHoursEndsTheMeeting()
    {
        var (ended, _) = await Run(t => t < ClassTime.AddHours(2.5) ? [Host, Guest("A"), Guest("B")] : [Host], TimeSpan.FromHours(5));
        var at = Assert.Single(ended);
        Assert.InRange(at - ClassTime, TimeSpan.FromHours(3), TimeSpan.FromHours(3) + TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task AFewMutedPeopleForFiveMinutesEndIt()
    {
        var (ended, _) = await Run(_ => [Host, Guest("A"), Guest("B"), Guest("C")], TimeSpan.FromHours(5));
        // Quiet all along: the five minutes run from the three hours, so it ends at 3 h 05.
        Assert.InRange(Assert.Single(ended) - ClassTime, TimeSpan.FromMinutes(185), TimeSpan.FromMinutes(186));
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
        Assert.True(Assert.Single(ended) - ClassTime >= TimeSpan.FromMinutes(190));
    }

    [Fact]
    public async Task ANonHostNeverEndsTheMeeting()
    {
        var (ended, _) = await Run(_ => ["Helper,(Co-host, me), Computer audio muted,Video off"], TimeSpan.FromHours(5));
        Assert.Empty(ended);
    }
}
