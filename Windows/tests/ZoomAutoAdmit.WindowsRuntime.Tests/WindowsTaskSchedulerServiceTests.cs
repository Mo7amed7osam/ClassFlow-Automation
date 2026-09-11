using ZoomAutoAdmit.WindowsRuntime.Scheduling;
using Xunit;

namespace ZoomAutoAdmit.WindowsRuntime.Tests;

public sealed class WindowsTaskSchedulerServiceTests : IDisposable
{
    private readonly string _testLogPath = Path.Combine(
        Path.GetTempPath(),
        $"test_scheduler_{Guid.NewGuid():N}.log");

    public WindowsTaskSchedulerServiceTests()
    {
        WindowsSchedulerLog.FilePath = _testLogPath;
    }

    [Fact]
    public void TaskNameIsFormattedCorrectly()
    {
        var id = Guid.Parse("12345678-1234-1234-1234-123456789abc");
        string taskName = WindowsTaskSchedulerService.GetTaskName(id);

        Assert.Equal(@"ZoomAutoAdmit\Schedule_12345678123412341234123456789abc", taskName);
    }

    [Fact]
    public void BuildTaskRunCommandContainsRequiredArguments()
    {
        var service = new WindowsTaskSchedulerService("C:\\FakePath\\ZoomAutoAdmit.Inspector.exe");
        var schedule = new MeetingSchedule(
            Guid.NewGuid(),
            "Daily Standup",
            "https://zoom.us/j/9876543210",
            "teacher-account-1",
            new TimeOnly(14, 30),
            ScheduleDays.EveryDay,
            true);

        string command = service.BuildTaskRunCommand(schedule);

        Assert.Contains("meeting-start", command);
        Assert.Contains("--account-id \"teacher-account-1\"", command);
        Assert.Contains("--meeting-url \"https://zoom.us/j/9876543210\"", command);
        Assert.Contains($"--schedule-id {schedule.Id}", command);
    }

    [Fact]
    public void TaskFolderAndLauncherDirectoryCanBeMovedAwayFromTheAppsOwn()
    {
        var id = Guid.Parse("12345678-1234-1234-1234-123456789abc");

        Assert.Equal(
            @"ZoomAutoAdmitTests\Schedule_12345678123412341234123456789abc",
            WindowsTaskSchedulerService.GetTaskName(id, @"\ZoomAutoAdmitTests\"));
        Assert.Equal(
            Path.Combine(@"C:\Temp\Launchers", "launch_12345678123412341234123456789abc.cmd"),
            WindowsTaskSchedulerService.GetLauncherScriptPath(id, @"C:\Temp\Launchers"));
        Assert.EndsWith(
            Path.Combine("ZoomAutoAdmit", "Schedules", "launch_12345678123412341234123456789abc.cmd"),
            WindowsTaskSchedulerService.GetLauncherScriptPath(id));
    }

    /// <summary>
    /// The one test that talks to the real Task Scheduler. It only runs when asked for, because the
    /// machine running the suite is usually the one whose meetings are scheduled: an earlier
    /// version registered straight into \ZoomAutoAdmit\ and, when it failed half-way, left two
    /// daily tasks there. Now it registers in its own folder, writes its launcher to a temp folder,
    /// and deletes the task in a finally block.
    /// </summary>
    [LiveSchedulerFact]
    public async Task LiveTaskSchedulerCreatesAndDeletesRealWindowsTask()
    {
        string launcherDirectory = Path.Combine(Path.GetTempPath(), $"live_scheduler_{Guid.NewGuid():N}");
        var service = new WindowsTaskSchedulerService(
            taskFolder: LiveSchedulerFactAttribute.TaskFolder,
            launcherDirectory: launcherDirectory);
        // An hour ago, so the daily trigger's next run is almost a day away and cannot fire while
        // the test runs. The account and meeting are placeholders; nothing is ever launched.
        var schedule = new MeetingSchedule(
            Guid.NewGuid(),
            "Live Test Task",
            "https://zoom.us/j/00000000000",
            "live-scheduler-test",
            TimeOnly.FromDateTime(DateTime.Now.AddHours(-1)),
            ScheduleDays.EveryDay,
            true);
        string taskName = WindowsTaskSchedulerService.GetTaskName(schedule.Id, LiveSchedulerFactAttribute.TaskFolder);
        Assert.StartsWith(LiveSchedulerFactAttribute.TaskFolder + @"\", taskName);

        try
        {
            await service.RegisterTaskAsync(schedule);

            var (exitCode, output, error) = await QueryTaskAsync(taskName);
            string logFile = File.Exists(_testLogPath) ? File.ReadAllText(_testLogPath) : "NO LOG FILE";
            Assert.True(exitCode == 0, $"Query failed with exit code {exitCode}. Output: '{output}', Error: '{error}', Log: '{logFile}'");
            Assert.Contains(taskName, output);

            await service.DeleteTaskAsync(schedule.Id);

            var (exitCodeAfterDelete, _, _) = await QueryTaskAsync(taskName);
            Assert.NotEqual(0, exitCodeAfterDelete);
            Assert.False(File.Exists(WindowsTaskSchedulerService.GetLauncherScriptPath(schedule.Id, launcherDirectory)));
        }
        finally
        {
            // Runs whether or not an assertion failed, so a failure cannot leave a task behind.
            // Deleting a task that is already gone is harmless.
            await service.DeleteTaskAsync(schedule.Id);
            if (Directory.Exists(launcherDirectory)) Directory.Delete(launcherDirectory, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Output, string Error)> QueryTaskAsync(string taskName)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("/Query");
        psi.ArgumentList.Add("/TN");
        psi.ArgumentList.Add(taskName);
        psi.ArgumentList.Add("/FO");
        psi.ArgumentList.Add("LIST");

        using var process = System.Diagnostics.Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output, await error);
    }

    [Fact]
    public async Task ScheduleStoreNotifiesTaskSchedulerOnUpsertAndDelete()
    {
        var fakeScheduler = new FakeTaskScheduler();
        string storePath = Path.Combine(Path.GetTempPath(), $"sched_store_{Guid.NewGuid():N}.json");
        var store = new WindowsMeetingScheduleStore(storePath, fakeScheduler);

        var schedule = new MeetingSchedule(
            Guid.NewGuid(),
            "Test Meeting",
            "https://zoom.us/j/123456789",
            "acc1",
            new TimeOnly(10, 0),
            ScheduleDays.Monday | ScheduleDays.Wednesday,
            true);

        await store.UpsertAsync(schedule);
        Assert.Single(fakeScheduler.RegisteredSchedules);
        Assert.Equal(schedule.Id, fakeScheduler.RegisteredSchedules[0].Id);

        // Updating only LastTriggeredDate should not re-trigger task scheduler registration
        await store.UpsertAsync(schedule with { LastTriggeredDate = DateOnly.FromDateTime(DateTime.Now) });
        Assert.Single(fakeScheduler.RegisteredSchedules);

        await store.DeleteAsync(schedule.Id);
        Assert.Single(fakeScheduler.DeletedScheduleIds);
        Assert.Equal(schedule.Id, fakeScheduler.DeletedScheduleIds[0]);

        if (File.Exists(storePath)) File.Delete(storePath);
    }

    public void Dispose()
    {
        if (File.Exists(_testLogPath))
        {
            try { File.Delete(_testLogPath); } catch { }
        }
    }

    private sealed class FakeTaskScheduler : IWindowsTaskScheduler
    {
        public List<MeetingSchedule> RegisteredSchedules { get; } = [];
        public List<Guid> DeletedScheduleIds { get; } = [];

        public Task RegisterTaskAsync(MeetingSchedule schedule, CancellationToken cancellationToken = default)
        {
            RegisteredSchedules.Add(schedule);
            return Task.CompletedTask;
        }

        public Task DeleteTaskAsync(Guid scheduleId, CancellationToken cancellationToken = default)
        {
            DeletedScheduleIds.Add(scheduleId);
            return Task.CompletedTask;
        }
    }
}
