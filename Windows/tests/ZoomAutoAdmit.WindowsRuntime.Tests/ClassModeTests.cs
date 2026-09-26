using ZoomAutoAdmit.WebAutomation.Lms;
using ZoomAutoAdmit.WindowsRuntime.Scheduling;
using Xunit;

namespace ZoomAutoAdmit.WindowsRuntime.Tests;

[Collection("ScheduledClassStarter")]
public sealed class ClassModeTests : IDisposable
{
    private static readonly DateOnly Friday = new(2026, 9, 25);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ZoomAutoAdmitClassModeTests", Guid.NewGuid().ToString("N"));

    public ClassModeTests()
    {
        ScheduledClassStarter.ClaimFolder = Path.Combine(_root, "runs");
        ScheduledClassStarter.RetryGap = TimeSpan.FromMilliseconds(10);
    }

    public void Dispose()
    {
        ScheduledClassStarter.RunInRoom = null;
        ClassMode.LmsSessions = () => [];
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static MeetingSchedule Class(DateOnly day, TimeOnly time, string? mode = null) =>
        new(Guid.NewGuid(), "S8 class", "https://zoom.us/j/92844609413", "CAI5_AIS4_S8", time, ScheduleDays.None, true,
            OccurrenceDate: day, GroupName: "CAI5_AIS4_S8", Mode: mode);

    private static LmsSessionCache.Entry Listed(string mode, TimeOnly time) =>
        new(new LmsSessionRunner.LmsSessionInfo("CAI5_AIS4_S8", Friday, time, "Week 10 - Session 1", "finished", null, "", "", "unknown", null, [])
            { Mode = mode, Focus = "Technical" }, DateTimeOffset.Now, "mohab");

    [Fact]
    public void TheLmsSaysPhysicalWhenTheScheduleDoesNotSay()
    {
        var six = new TimeOnly(18, 0);
        Assert.True(ClassMode.IsPhysical([Class(Friday, six)], [Listed("Physical", six)], "CAI5_AIS4_S8", Friday, six));
        Assert.False(ClassMode.IsPhysical([Class(Friday, six)], [Listed("Online", six)], "CAI5_AIS4_S8", Friday, six));
        Assert.Null(ClassMode.Resolve([Class(Friday, six)], [], "CAI5_AIS4_S8", Friday, six));
    }

    [Fact]
    public void TheScheduleComesBeforeTheLms()
    {
        var six = new TimeOnly(18, 0);
        Assert.False(ClassMode.IsPhysical([Class(Friday, six, "Online")], [Listed("Physical", six)], "CAI5_AIS4_S8", Friday, six));
        Assert.True(ClassMode.IsPhysical([Class(Friday, six, "Physical")], [Listed("Online", six)], "CAI5_AIS4_S8", Friday, six));
    }

    [Fact]
    public void AnotherDaysSessionOfTheGroupSaysNothingAboutThisOne()
    {
        var six = new TimeOnly(18, 0);
        Assert.Null(ClassMode.Resolve([], [Listed("Physical", six)], "CAI5_AIS4_S8", Friday.AddDays(1), six));
        Assert.Null(ClassMode.Resolve([], [Listed("Physical", six)], "CAI5_AIS4_S7", Friday, six));
    }

    [Theory]
    [InlineData("Physical", "Physical")]
    [InlineData("offline", "Physical")]
    [InlineData("Physical Session", "Physical")]
    [InlineData("Online", "Online")]
    [InlineData("N/A", null)]
    [InlineData("", null)]
    public void TheTypeIsReadHoweverItIsWritten(string written, string? mode) =>
        Assert.Equal(mode, ClassMode.Normalize(written));

    private sealed class CountingRunner : IScheduledMeetingRunner
    {
        public int Runs;
        public Task<ZoomAutoAdmit.Core.Meetings.MeetingSession> RunAsync(ZoomAutoAdmit.Core.Meetings.ScheduledMeeting meeting, CancellationToken cancellationToken = default)
        {
            Runs++;
            return Task.FromResult(new ZoomAutoAdmit.Core.Meetings.MeetingSession(Guid.NewGuid(), meeting, DateTimeOffset.UtcNow));
        }
    }

    [Fact]
    public async Task APhysicalClassOpensNoMeetingAndIsRunOnTheLmsInstead()
    {
        var store = new WindowsMeetingScheduleStore(Path.Combine(_root, "schedules.json"));
        var soon = DateTime.Now.AddMinutes(10);
        var day = DateOnly.FromDateTime(soon);
        var schedule = Class(day, new TimeOnly(soon.Hour, soon.Minute), "Physical");
        await store.UpsertAsync(schedule);
        var runner = new CountingRunner();
        var ranInRoom = new List<(string Group, DateOnly Day, TimeOnly Start)>();
        ScheduledClassStarter.RunInRoom = (group, d, start, _) => { ranInRoom.Add((group, d, start)); return Task.CompletedTask; };

        var session = await new ScheduledClassStarter(runner, store, _ => { }).StartAsync(schedule, day, DateTimeOffset.Now);

        Assert.Null(session);
        Assert.Equal(0, runner.Runs);                                  // no Zoom meeting at all
        Assert.Equal(("CAI5_AIS4_S8", day, schedule.Time), Assert.Single(ranInRoom));
        Assert.Equal(day, Assert.Single(await store.ListAsync()).LastTriggeredDate);

        // Taken for a class whose meeting closed, it is not run a second time.
        Assert.Null(await new ScheduledClassStarter(runner, store, _ => { }).StartAsync(schedule, day, DateTimeOffset.Now, reopen: true));
        Assert.Single(ranInRoom);
    }

    [Fact]
    public async Task AClassTheLmsListsAsPhysicalOpensNoMeetingEither()
    {
        var store = new WindowsMeetingScheduleStore(Path.Combine(_root, "schedules.json"));
        var soon = DateTime.Now.AddMinutes(10);
        var day = DateOnly.FromDateTime(soon);
        var time = new TimeOnly(soon.Hour, soon.Minute);
        var schedule = Class(day, time);
        await store.UpsertAsync(schedule);
        ClassMode.LmsSessions = () => [new(new LmsSessionRunner.LmsSessionInfo("CAI5_AIS4_S8", day, time, "Week 10 - Session 1", "pending", null, "", "", "unknown", null, [])
            { Mode = "Physical" }, DateTimeOffset.Now, "mohab")];
        var runner = new CountingRunner();

        Assert.Null(await new ScheduledClassStarter(runner, store, _ => { }).StartAsync(schedule, day, DateTimeOffset.Now));
        Assert.Equal(0, runner.Runs);
    }
}
