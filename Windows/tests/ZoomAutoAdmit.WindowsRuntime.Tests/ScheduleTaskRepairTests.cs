using ZoomAutoAdmit.WindowsRuntime.Scheduling;
using Xunit;

namespace ZoomAutoAdmit.WindowsRuntime.Tests;

/// <summary>
/// Nothing here touches the real Task Scheduler: the tasks are a dictionary, and "the program
/// exists" is a set of paths. The repair only ever sees what it is handed.
/// </summary>
public sealed class ScheduleTaskRepairTests
{
    private const string OldExe = @"D:\Zoom Admite\zoom-auto-admit\Windows\src\ZoomAutoAdmit.WindowsUI\bin\Release\net8.0-windows10.0.19041.0\ZoomAutoAdmit.Inspector.exe";
    private const string NewExe = @"E:\zooommmm\zoom-auto-admit claude\Windows\src\ZoomAutoAdmit.WindowsUI\bin\Release\net8.0-windows10.0.19041.0\ZoomAutoAdmit.Inspector.exe";
    private static readonly DateOnly Today = new(2026, 9, 11);

    private static MeetingSchedule Dated(string name, DateOnly day, bool enabled = true) =>
        new(Guid.NewGuid(), name, "https://zoom.us/j/1", "CAI5_AIS4_S7", new TimeOnly(18, 0), ScheduleDays.None, enabled, OccurrenceDate: day);

    /// <summary>A task list and a program set standing in for Windows.</summary>
    private sealed class FakeWindows
    {
        public readonly Dictionary<Guid, string?> Tasks = [];
        public readonly HashSet<string> Programs = new(StringComparer.OrdinalIgnoreCase) { NewExe };
        public readonly List<Guid> Registered = [];
        public readonly Dictionary<Guid, DateTime?> Launches = [];
        public string RegisterWith = NewExe;

        public ScheduleTaskRepair Repair(IReadOnlyList<MeetingSchedule> schedules) => new(
            _ => Task.FromResult(schedules),
            (id, _) => Task.FromResult(Tasks.TryGetValue(id, out var target) ? target : null),
            (schedule, _) => { Registered.Add(schedule.Id); Tasks[schedule.Id] = RegisterWith; return Task.CompletedTask; },
            Programs.Contains,
            (id, _) => Task.FromResult(Launches.TryGetValue(id, out var launch) ? launch : null));
    }

    [Fact]
    public async Task AWorkingTaskSavedAtClassTimeIsMovedFifteenMinutesEarlier()
    {
        var windows = new FakeWindows();
        var meeting = Dated("early opening", Today.AddDays(1));
        windows.Tasks[meeting.Id] = NewExe;
        windows.Launches[meeting.Id] = meeting.OccurrenceDate!.Value.ToDateTime(meeting.Time);

        var result = await windows.Repair([meeting]).RepairAsync(Today, dryRun: true);

        Assert.Equal(1, result.Repaired);
        Assert.Empty(windows.Registered);
        Assert.Contains("instead of", Assert.Single(result.Details));
    }

    [Fact]
    public async Task AnUpcomingMeetingThatPointsAtAMissingDriveIsRePointed()
    {
        var windows = new FakeWindows();
        var nextWeek = Dated("CAI5_AIS4_S7 • Technical", Today.AddDays(2));
        windows.Tasks[nextWeek.Id] = OldExe;

        var result = await windows.Repair([nextWeek]).RepairAsync(Today);

        Assert.Equal(1, result.Repaired);
        Assert.Equal(0, result.Failed);
        Assert.Equal(NewExe, windows.Tasks[nextWeek.Id]);
    }

    [Fact]
    public async Task AMeetingThatAlreadyWorksIsLeftExactlyAsItIs()
    {
        var windows = new FakeWindows();
        var working = Dated("CAI5_AIS4_S8 • English", Today.AddDays(3));
        windows.Tasks[working.Id] = NewExe;

        var result = await windows.Repair([working]).RepairAsync(Today);

        Assert.Equal(1, result.Checked);
        Assert.Equal(0, result.Repaired);
        Assert.Empty(windows.Registered);
        Assert.Contains("point at a program that exists", result.Summary);
    }

    [Fact]
    public async Task PastAndDisabledMeetingsAreNeverBroughtBack()
    {
        var windows = new FakeWindows();
        var lastWeek = Dated("finished class", Today.AddDays(-4));
        var switchedOff = Dated("switched off", Today.AddDays(5), enabled: false);
        windows.Tasks[lastWeek.Id] = OldExe;
        windows.Tasks[switchedOff.Id] = OldExe;

        var result = await windows.Repair([lastWeek, switchedOff]).RepairAsync(Today);

        // Neither will run, and re-registering them would resurrect something meant to be over.
        Assert.Equal(0, result.Checked);
        Assert.Empty(windows.Registered);
    }

    [Fact]
    public async Task TodaysMeetingCountsAsUpcoming()
    {
        var windows = new FakeWindows();
        var tonight = Dated("tonight", Today);
        windows.Tasks[tonight.Id] = OldExe;

        var result = await windows.Repair([tonight]).RepairAsync(Today);
        Assert.Equal(1, result.Repaired);
    }

    [Fact]
    public async Task AScheduleWithNoTaskAtAllGetsOne()
    {
        var windows = new FakeWindows();
        var lost = Dated("task was deleted by hand", Today.AddDays(1));

        var result = await windows.Repair([lost]).RepairAsync(Today);

        Assert.Equal(1, result.Repaired);
        Assert.Equal(NewExe, windows.Tasks[lost.Id]);
    }

    [Fact]
    public async Task ADryRunReportsWithoutRegisteringAnything()
    {
        var windows = new FakeWindows();
        var nextWeek = Dated("CAI5_AIS4_S7 • Technical", Today.AddDays(2));
        windows.Tasks[nextWeek.Id] = OldExe;

        var result = await windows.Repair([nextWeek]).RepairAsync(Today, dryRun: true);

        Assert.Equal(1, result.Repaired);
        Assert.Empty(windows.Registered);
        Assert.Equal(OldExe, windows.Tasks[nextWeek.Id]);
        Assert.Contains(OldExe, Assert.Single(result.Details));
    }

    [Fact]
    public async Task ARegistrationThatDidNotTakeIsReportedAsStillBroken()
    {
        // Registering from a copy of the app that is itself missing leaves the task broken; a
        // repair that only checked the call returned would have counted that as fixed.
        var windows = new FakeWindows { RegisterWith = @"Z:\gone\ZoomAutoAdmit.Inspector.exe" };
        var nextWeek = Dated("CAI5_AIS4_S7 • Technical", Today.AddDays(2));
        windows.Tasks[nextWeek.Id] = OldExe;

        var result = await windows.Repair([nextWeek]).RepairAsync(Today);

        Assert.Equal(0, result.Repaired);
        Assert.Equal(1, result.Failed);
        Assert.Contains("still broken", Assert.Single(result.Details));
    }

    // ---------------------------------------------------------------- reading a task's program

    private static string TaskXml(string command, string arguments = "") =>
        "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" +
        "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
        "<Actions Context=\"Author\"><Exec>" +
        $"<Command>{command}</Command><Arguments>{arguments}</Arguments>" +
        "</Exec></Actions></Task>";

    [Fact]
    public void AProgramNamedDirectlyIsReadAsIs()
    {
        string target = WindowsTaskSchedulerService.ExtractTaskTarget(
            TaskXml(OldExe, "meeting-start --schedule-id 07b46283"), _ => null);
        Assert.Equal(OldExe, target);
    }

    [Fact]
    public void ALauncherScriptIsFollowedToTheProgramInsideIt()
    {
        const string launcher = @"C:\Users\me\AppData\Local\ZoomAutoAdmit\Schedules\launch_abc.cmd";
        string script = "@echo off\r\n\"" + OldExe + "\" meeting-start --schedule-id abc\r\n";

        string target = WindowsTaskSchedulerService.ExtractTaskTarget(
            TaskXml("\"" + launcher + "\""), path => path == launcher ? script : null);

        // The task names the script, but the script is where the dead path actually lives.
        Assert.Equal(OldExe, target);
    }

    [Fact]
    public void AMissingLauncherScriptIsReportedAsTheBrokenThing()
    {
        const string launcher = @"C:\gone\launch_abc.cmd";
        string target = WindowsTaskSchedulerService.ExtractTaskTarget(TaskXml(launcher), _ => null);
        Assert.Equal(launcher, target);
    }

    [Fact]
    public void DotnetRunningADllIsReadAsTheDll()
    {
        const string dll = @"E:\app\ZoomAutoAdmit.Inspector.dll";
        string target = WindowsTaskSchedulerService.ExtractTaskTarget(
            TaskXml("dotnet.exe", "\"" + dll + "\" meeting-start --schedule-id 1"), _ => null);
        Assert.Equal(dll, target);
    }

    [Fact]
    public void UnreadableTaskXmlIsTreatedAsBrokenNotAsFine()
    {
        Assert.Equal(string.Empty, WindowsTaskSchedulerService.ExtractTaskTarget("not xml at all", _ => null));
        // An empty target needs repair, so a task nobody can read is never assumed to work.
        Assert.True(ScheduleTaskRepair.NeedsRepair(
            Dated("x", Today.AddDays(1)), Today, string.Empty, _ => true));
    }
}
