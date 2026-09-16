using ZoomAutoAdmit.Core.Meetings;
using ZoomAutoAdmit.Core.Sessions;
using ZoomAutoAdmit.WindowsRuntime.Scheduling;
using Xunit;

namespace ZoomAutoAdmit.WindowsRuntime.Tests;

public sealed class ScheduledClassStarterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ZoomAutoAdmitStarterTests", Guid.NewGuid().ToString("N"));

    public ScheduledClassStarterTests()
    {
        ScheduledClassStarter.ClaimFolder = Path.Combine(_root, "runs");
        ScheduledClassStarter.RetryGap = TimeSpan.FromMilliseconds(10);
    }

    /// <summary>Fails the first <see cref="FailFirst"/> tries (as a crashed or blank start), then opens.</summary>
    private sealed class FlakyRunner : IScheduledMeetingRunner
    {
        public int FailFirst;
        public List<ScheduledMeeting> Meetings { get; } = [];
        public Task<MeetingSession> RunAsync(ScheduledMeeting meeting, CancellationToken cancellationToken = default)
        {
            Meetings.Add(meeting);
            if (Meetings.Count <= FailFirst) throw new InvalidOperationException("Opening in existing browser session.");
            return Task.FromResult(new MeetingSession(Guid.NewGuid(), meeting, DateTimeOffset.UtcNow));
        }
    }

    private WindowsMeetingScheduleStore Store() => new(Path.Combine(_root, "schedules.json"));

    private static MeetingSchedule Class(DateTime start, SessionEngineType? engine = null) =>
        new(Guid.NewGuid(), "S7 class", "https://zoom.us/j/12345678901", "CAI5_AIS4_S7", new TimeOnly(start.Hour, start.Minute),
            ScheduleDays.None, true, OccurrenceDate: DateOnly.FromDateTime(start), GroupName: "CAI5_AIS4_S7", PreferredEngine: engine);

    [Fact]
    public void EachFailedTryUsesTheOtherEngine()
    {
        Assert.Null(ScheduledClassStarter.EngineFor(null, 0));                                      // Auto: the account's way first
        Assert.Equal(SessionEngineType.Web, ScheduledClassStarter.EngineFor(null, 1));
        Assert.Equal(SessionEngineType.Desktop, ScheduledClassStarter.EngineFor(null, 2));
        Assert.Equal(SessionEngineType.Web, ScheduledClassStarter.EngineFor(SessionEngineType.Web, 0));
        Assert.Equal(SessionEngineType.Desktop, ScheduledClassStarter.EngineFor(SessionEngineType.Web, 1));
    }

    [Fact]
    public async Task AClassThatFailsIsTriedAgainAndMarkedOpenedOnlyOnceItOpens()
    {
        var store = Store();
        var schedule = Class(DateTime.Now.AddMinutes(10));
        await store.UpsertAsync(schedule);
        var runner = new FlakyRunner { FailFirst = 2 };
        var day = schedule.OccurrenceDate!.Value;

        var session = await new ScheduledClassStarter(runner, store, _ => { }).StartAsync(schedule, day, DateTimeOffset.Now);

        Assert.NotNull(session);
        Assert.NotEqual(MeetingState.Failed, session!.State);
        Assert.Equal(new SessionEngineType?[] { null, SessionEngineType.Web, SessionEngineType.Desktop }, runner.Meetings.Select(m => m.PreferredEngine));
        Assert.All(runner.Meetings, m => Assert.Equal(day.ToDateTime(schedule.Time), m.ScheduledStartTime!.Value.DateTime));
        Assert.Equal(day, Assert.Single(await store.ListAsync()).LastTriggeredDate);
    }

    [Fact]
    public async Task AClassAlreadyBeingOpenedElsewhereIsNotOpenedTwice()
    {
        var store = Store();
        var schedule = Class(DateTime.Now.AddMinutes(10));
        await store.UpsertAsync(schedule);
        var runner = new FlakyRunner();
        var day = schedule.OccurrenceDate!.Value;

        using (ScheduledClassStarter.TryClaim(schedule.Id, day))       // the Windows task holds it
            Assert.Null(await new ScheduledClassStarter(runner, store, _ => { }).StartAsync(schedule, day, DateTimeOffset.Now));
        Assert.Empty(runner.Meetings);
        Assert.Null(Assert.Single(await store.ListAsync()).LastTriggeredDate);   // still owed: the holder may fail

        Assert.NotNull(await new ScheduledClassStarter(runner, store, _ => { }).StartAsync(schedule, day, DateTimeOffset.Now));
        Assert.Single(runner.Meetings);
    }

    [Fact]
    public async Task TooLateToOpenGivesUpAndSaysSo()
    {
        var store = Store();
        var schedule = Class(DateTime.Now.AddMinutes(-50));
        await store.UpsertAsync(schedule);
        var runner = new FlakyRunner { FailFirst = 99 };
        string? reported = null;
        void OnGaveUp(MeetingSchedule s, string why) { if (s.Id == schedule.Id) reported = why; }
        ScheduledClassStarter.GaveUp += OnGaveUp;
        try
        {
            await new ScheduledClassStarter(runner, store, _ => { }).StartAsync(schedule, schedule.OccurrenceDate!.Value, DateTimeOffset.Now);
        }
        finally { ScheduledClassStarter.GaveUp -= OnGaveUp; }

        Assert.Single(runner.Meetings);
        Assert.NotNull(reported);
        Assert.Equal(schedule.OccurrenceDate, Assert.Single(await store.ListAsync()).LastTriggeredDate);
    }

    [Fact]
    public void AMeetingOpenedByHandIsTheClassItIsNearest()
    {
        var day = new DateOnly(2026, 9, 15);
        MeetingSchedule At(int h, int m, string group = "CAI5_AIS4_S7") =>
            Class(day.ToDateTime(new TimeOnly(h, m))) with { GroupName = group, AccountId = group };
        var schedules = new[] { At(19, 0), At(14, 0), At(18, 0, "CAI5_AIS4_S8") };

        Assert.Equal(new TimeOnly(19, 0), ScheduleTiming.ClassStartNear(schedules, "CAI5_AIS4_S7", day, new TimeOnly(18, 51)));
        Assert.Equal(new TimeOnly(19, 0), ScheduleTiming.ClassStartNear(schedules, "cai5_ais4_s7", day, new TimeOnly(19, 40)));
        // Opened again while the class is still going on (the meeting dropped, Start pressed at 20:32).
        Assert.Equal(new TimeOnly(19, 0), ScheduleTiming.ClassStartNear(schedules, "CAI5_AIS4_S7", day, new TimeOnly(20, 32)));
        Assert.Equal(new TimeOnly(14, 0), ScheduleTiming.ClassStartNear(schedules, "CAI5_AIS4_S7", day, new TimeOnly(16, 45)));
        Assert.Null(ScheduleTiming.ClassStartNear(schedules, "CAI5_AIS4_S7", day, new TimeOnly(11, 0)));      // no class that close
        Assert.Null(ScheduleTiming.ClassStartNear(schedules, "CAI5_AIS4_S7", day, new TimeOnly(23, 0)));      // that class is over
        Assert.Null(ScheduleTiming.ClassStartNear(schedules, "CAI5_AIS4_S7", day.AddDays(1), new TimeOnly(18, 51)));
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }
}
