using ZoomAutoAdmit.WebAutomation.Lms;
using ZoomAutoAdmit.WindowsUI.Services;
using Xunit;

namespace ZoomAutoAdmit.WindowsUI.Tests;

public sealed class LmsFollowUpProcessorTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"lms-follow-up-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task DueFlowRunsAttendanceCorrectionThenCompletionAndClearsQueue()
    {
        var queue = new LmsFollowUpQueue(_path);
        var now = new DateTimeOffset(2026, 9, 14, 22, 1, 0, TimeSpan.FromHours(3));
        await queue.ScheduleAsync("CAI5_AIS4_S7", new DateOnly(2026, 9, 14), new TimeOnly(19, 0));
        List<LmsFollowUpStep> calls = [];
        var processor = new LmsFollowUpProcessor(queue,
            presentNames: (_, _) => Task.FromResult<IReadOnlyCollection<string>>(["Student One"]),
            runAction: (item, present, _, _) =>
            {
                calls.Add(item.Step);
                if (item.Step is LmsFollowUpStep.TakeAttendance or LmsFollowUpStep.CorrectAttendance) Assert.Equal(["Student One"], present); else Assert.Empty(present);
                return Task.FromResult((true, "ok"));
            });

        await processor.ProcessDueAsync(now);

        Assert.Equal([LmsFollowUpStep.TakeAttendance, LmsFollowUpStep.CorrectAttendance, LmsFollowUpStep.CompleteSession, LmsFollowUpStep.AttachZoomRecording], calls);
        Assert.Empty(await queue.ReadAsync());
    }

    [Fact]
    public async Task StepsAfterTheClassWaitWhileItsMeetingIsStillRunning()
    {
        var queue = new LmsFollowUpQueue(_path);
        var now = new DateTimeOffset(2026, 9, 14, 22, 1, 0, TimeSpan.FromHours(3));
        await queue.ScheduleAsync("CAI5_AIS4_S7", new DateOnly(2026, 9, 14), new TimeOnly(19, 0));
        List<LmsFollowUpStep> calls = [];
        bool live = true;
        var processor = new LmsFollowUpProcessor(queue,
            presentNames: (_, _) => Task.FromResult<IReadOnlyCollection<string>>(["Student One"]),
            runAction: (item, _, _, _) => { calls.Add(item.Step); return Task.FromResult((true, "ok")); },
            isLive: _ => live);

        await processor.ProcessDueAsync(now);

        // Attendance is taken; the correction, Complete and the recording wait, without using up attempts.
        Assert.Equal([LmsFollowUpStep.TakeAttendance], calls);
        var waiting = await queue.ReadAsync();
        Assert.Equal(3, waiting.Count);
        Assert.All(waiting, item => Assert.Equal(0, item.Attempts));
        Assert.All(waiting.Where(item => item.Step != LmsFollowUpStep.CompleteSession), item => Assert.True(item.DueAt > now));

        // The meeting ended: its final correction is brought forward and everything runs.
        live = false;
        await queue.ScheduleFinalAttendanceAsync("CAI5_AIS4_S7", new DateOnly(2026, 9, 14), new TimeOnly(19, 0), now.AddMinutes(2));
        await processor.ProcessDueAsync(now.AddMinutes(3));
        Assert.Equal([LmsFollowUpStep.TakeAttendance, LmsFollowUpStep.CorrectAttendance, LmsFollowUpStep.CompleteSession, LmsFollowUpStep.AttachZoomRecording], calls);
        Assert.Empty(await queue.ReadAsync());
    }

    [Fact]
    public async Task AMeetingThatEndsAfterTheCorrectionGetsItOnceMore()
    {
        var queue = new LmsFollowUpQueue(_path);
        var day = new DateOnly(2026, 9, 14);
        var due = new DateTimeOffset(2026, 9, 14, 22, 32, 0, TimeSpan.FromHours(3));

        // Nothing is owed any more (all done at 3 h), and the meeting only ends now.
        await queue.ScheduleFinalAttendanceAsync("CAI5_AIS4_S7", day, new TimeOnly(19, 0), due);

        var item = Assert.Single(await queue.ReadAsync());
        Assert.Equal(LmsFollowUpStep.CorrectAttendance, item.Step);
        Assert.Equal(due, item.DueAt);
    }

    [Fact]
    public async Task FailedCorrectionIsRetriedAndBlocksCompletion()
    {
        var queue = new LmsFollowUpQueue(_path);
        var now = new DateTimeOffset(2026, 9, 14, 22, 1, 0, TimeSpan.FromHours(3));
        await queue.ScheduleAsync("CAI5_AIS4_S7", new DateOnly(2026, 9, 14), new TimeOnly(19, 0));
        List<LmsFollowUpStep> calls = [];
        var processor = new LmsFollowUpProcessor(queue,
            presentNames: (_, _) => Task.FromResult<IReadOnlyCollection<string>>([]),
            runAction: (item, _, _, _) =>
            {
                calls.Add(item.Step);
                return Task.FromResult((item.Step != LmsFollowUpStep.CorrectAttendance, "result"));
            });

        await processor.ProcessDueAsync(now);

        // The recording does not wait for attendance; Complete does.
        Assert.Equal([LmsFollowUpStep.TakeAttendance, LmsFollowUpStep.CorrectAttendance, LmsFollowUpStep.AttachZoomRecording], calls);
        var remaining = await queue.ReadAsync();
        Assert.Equal(2, remaining.Count);
        Assert.Contains(remaining, item => item.Step == LmsFollowUpStep.CorrectAttendance && item.Attempts == 1);
        Assert.Contains(remaining, item => item.Step == LmsFollowUpStep.CompleteSession && item.Attempts == 0);
    }

    [Fact]
    public async Task DryRunExercisesAllStepsWithoutChangingQueue()
    {
        var queue = new LmsFollowUpQueue(_path);
        var now = new DateTimeOffset(2026, 9, 14, 22, 1, 0, TimeSpan.FromHours(3));
        await queue.ScheduleAsync("CAI5_AIS4_S7", new DateOnly(2026, 9, 14), new TimeOnly(19, 0));
        int calls = 0;
        var processor = new LmsFollowUpProcessor(queue,
            presentNames: (_, _) => Task.FromResult<IReadOnlyCollection<string>>([]),
            runAction: (_, _, dryRun, _) =>
            {
                Assert.True(dryRun);
                calls++;
                return Task.FromResult((true, "preview"));
            });

        await processor.ProcessDueAsync(now, dryRun: true);

        Assert.Equal(4, calls);
        Assert.Equal(4, (await queue.ReadAsync()).Count);
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
