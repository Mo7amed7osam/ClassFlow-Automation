using ZoomAutoAdmit.Core.Meetings;
using ZoomAutoAdmit.WindowsRuntime.Scheduling;
using Xunit;

namespace ZoomAutoAdmit.WindowsRuntime.Tests;

public sealed class WindowsMeetingSchedulerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ZoomAutoAdmitSchedulerTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CreateSchedulePersistsAllFields()
    {
        var store = Store();
        var schedule = Schedule(enabled: true);

        await store.UpsertAsync(schedule);

        Assert.Equal(schedule, Assert.Single(await store.ListAsync()));
    }

    [Fact]
    public async Task DeleteScheduleRemovesIt()
    {
        var store = Store();
        var schedule = Schedule(enabled: true);
        await store.UpsertAsync(schedule);

        Assert.True(await store.DeleteAsync(schedule.Id));
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task DueScheduleTriggersExistingMeetingRunnerOnce()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        var store = Store();
        await store.UpsertAsync(Schedule(
            enabled: true,
            days: Day(now.DayOfWeek),
            time: new TimeOnly(now.Hour, now.Minute)));
        var runner = new FakeScheduledMeetingRunner();
        await using var scheduler = new WindowsMeetingScheduler(store, runner);

        int first = await scheduler.RunDueAsync(now);
        int second = await scheduler.RunDueAsync(now.AddSeconds(10));

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Single(runner.Meetings);
        Assert.Equal("teacher-1", runner.Meetings[0].AccountId);
        Assert.Equal("teacher-1", runner.Meetings[0].GroupId);
    }

    [Fact]
    public async Task DisabledScheduleIsIgnored()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        var store = Store();
        await store.UpsertAsync(Schedule(
            enabled: false,
            days: Day(now.DayOfWeek),
            time: new TimeOnly(now.Hour, now.Minute)));
        var runner = new FakeScheduledMeetingRunner();
        await using var scheduler = new WindowsMeetingScheduler(store, runner);

        int triggered = await scheduler.RunDueAsync(now);

        Assert.Equal(0, triggered);
        Assert.Empty(runner.Meetings);
    }

    private WindowsMeetingScheduleStore Store() =>
        new(Path.Combine(_root, "Schedules", "schedules.json"));

    [Fact]
    public async Task ImportedExactDateRunsOnlyOnThatDateAndNeverRepeatsWeekly()
    {
        var now = DateTimeOffset.Now;
        var date = DateOnly.FromDateTime(now.LocalDateTime);
        var store = Store();
        var schedule = Schedule(true, ScheduleDays.None, TimeOnly.MinValue) with { OccurrenceDate = date };
        await store.UpsertAsync(schedule);
        Assert.Equal(date, Assert.Single(await store.ListAsync()).OccurrenceDate);
        var runner = new FakeScheduledMeetingRunner();
        await using var scheduler = new WindowsMeetingScheduler(store, runner);
        Assert.Equal(0, await scheduler.RunDueAsync(now.AddDays(-1)));
        Assert.Equal(1, await scheduler.RunDueAsync(now));
        Assert.Equal(0, await scheduler.RunDueAsync(now.AddSeconds(1)));
        Assert.Equal(0, await scheduler.RunDueAsync(now.AddDays(7)));
        Assert.Single(runner.Meetings);
    }

    [Fact]
    public void OneTimeWindowsTaskUsesExactIsoDateAndNoWeeklyTrigger()
    {
        var schedule = Schedule(true, ScheduleDays.None, new TimeOnly(19, 0)) with { OccurrenceDate = new DateOnly(2026, 9, 14) };
        var xml = WindowsTaskSchedulerService.BuildOneTimeTaskXml(schedule, @"C:\Some Folder\ZoomAutoAdmit.Inspector.exe");
        Assert.Contains("2026-09-14T18:45:00", xml);
        Assert.Contains("TimeTrigger", xml); Assert.DoesNotContain("CalendarTrigger", xml);
        Assert.Contains("InteractiveToken", xml); Assert.Contains(schedule.Id.ToString(), xml);
        Assert.DoesNotContain("WEEKLY", xml);
    }

    [Fact]
    public async Task EarlyLaunchKeepsOfficialSessionTimeAndSeparateGroup()
    {
        var date = new DateOnly(2026, 9, 14);
        var official = new TimeOnly(19, 0);
        var schedule = Schedule(true, ScheduleDays.None, official) with
        {
            OccurrenceDate = date,
            GroupName = "CAI5_AIS4_S7"
        };
        var store = Store();
        await store.UpsertAsync(schedule);
        var runner = new FakeScheduledMeetingRunner();
        await using var scheduler = new WindowsMeetingScheduler(store, runner);

        var launch = new DateTimeOffset(date.ToDateTime(new TimeOnly(18, 45)), DateTimeOffset.Now.Offset);
        Assert.Equal(1, await scheduler.RunDueAsync(launch));

        var meeting = Assert.Single(runner.Meetings);
        Assert.Equal("teacher-1", meeting.AccountId);
        Assert.Equal("CAI5_AIS4_S7", meeting.GroupId);
        Assert.Equal(new DateTimeOffset(date.ToDateTime(official), launch.Offset), meeting.ScheduledStartTime);
        var session = new MeetingSession(Guid.NewGuid(), meeting, launch);
        Assert.Equal("CAI5_AIS4_S7", session.GroupId);
        Assert.Equal(meeting.ScheduledStartTime, session.StartTime);
    }

    private static MeetingSchedule Schedule(
        bool enabled,
        ScheduleDays days = ScheduleDays.Monday,
        TimeOnly? time = null) =>
        new(
            Guid.NewGuid(),
            "Morning class",
            "https://zoom.us/j/123456789",
            "teacher-1",
            time ?? new TimeOnly(9, 0),
            days,
            enabled);

    private static ScheduleDays Day(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => ScheduleDays.Monday,
        DayOfWeek.Tuesday => ScheduleDays.Tuesday,
        DayOfWeek.Wednesday => ScheduleDays.Wednesday,
        DayOfWeek.Thursday => ScheduleDays.Thursday,
        DayOfWeek.Friday => ScheduleDays.Friday,
        DayOfWeek.Saturday => ScheduleDays.Saturday,
        _ => ScheduleDays.Sunday
    };

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeScheduledMeetingRunner : IScheduledMeetingRunner
    {
        public List<ScheduledMeeting> Meetings { get; } = [];

        public Task<MeetingSession> RunAsync(
            ScheduledMeeting meeting,
            CancellationToken cancellationToken = default)
        {
            Meetings.Add(meeting);
            return Task.FromResult(new MeetingSession(
                Guid.NewGuid(),
                meeting,
                DateTimeOffset.UtcNow));
        }
    }
}
