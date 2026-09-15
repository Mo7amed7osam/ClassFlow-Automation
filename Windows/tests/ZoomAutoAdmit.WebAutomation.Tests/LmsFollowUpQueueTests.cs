using ZoomAutoAdmit.WebAutomation.Lms;
using Xunit;

namespace ZoomAutoAdmit.WebAutomation.Tests;

public sealed class LmsFollowUpQueueTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "lms-followup-" + Guid.NewGuid() + ".json");
    private LmsFollowUpQueue Queue => new(_path);

    private static readonly DateOnly Day = new(2026, 9, 3);
    private static readonly TimeOnly Start = new(18, 0);
    private static DateTimeOffset At(int hour, int minute = 0) =>
        new(Day.ToDateTime(new TimeOnly(hour, minute)), DateTimeOffset.Now.Offset);

    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    [Fact]
    public async Task AClassLeavesBothStepsBehindAtTheirOwnTimes()
    {
        var scheduled = await Queue.ScheduleAsync("CAI5_AIS4_S8", Day, Start);

        Assert.Equal(4, scheduled.Count);
        var upload = scheduled.Single(item => item.Step == LmsFollowUpStep.TakeAttendance);
        var correct = scheduled.Single(item => item.Step == LmsFollowUpStep.CorrectAttendance);
        var complete = scheduled.Single(item => item.Step == LmsFollowUpStep.CompleteSession);
        Assert.Equal(At(19, 30), upload.DueAt);
        Assert.Equal(At(21, 0), correct.DueAt);
        Assert.Equal(At(21, 0), complete.DueAt);
    }

    [Fact]
    public async Task NothingIsDueBeforeItsTimeAndTheUploadComesFirst()
    {
        await Queue.ScheduleAsync("CAI5_AIS4_S8", Day, Start);

        Assert.Empty(await Queue.DueAsync(At(19, 0)));
        var atNineThirty = await Queue.DueAsync(At(19, 45));
        Assert.Equal(LmsFollowUpStep.TakeAttendance, Assert.Single(atNineThirty).Step);
        var later = await Queue.DueAsync(At(21, 30));
        Assert.Equal(4, later.Count);
        Assert.Equal(LmsFollowUpStep.TakeAttendance, later[0].Step);   // oldest first
    }

    [Fact]
    public async Task TheSameClassScheduledTwiceIsStillOneJobEach()
    {
        await Queue.ScheduleAsync("CAI5_AIS4_S8", Day, Start);
        var again = await Queue.ScheduleAsync("CAI5_AIS4_S8", Day, Start);

        // A meeting reopened, or the app restarted, must not take attendance twice.
        Assert.Equal(4, again.Count);
    }

    [Fact]
    public async Task WorkOutlivesTheAppBeingClosed()
    {
        await Queue.ScheduleAsync("CAI5_AIS4_S8", Day, Start);

        // A new queue over the same file is what the app sees after it is restarted.
        var afterRestart = new LmsFollowUpQueue(_path);
        var due = await afterRestart.DueAsync(At(22, 0));
        Assert.Equal(4, due.Count);
        Assert.All(due, item => Assert.Equal("CAI5_AIS4_S8", item.Group));
    }

    [Fact]
    public async Task DoneWorkGoesAndFailedWorkComesBackWithItsReason()
    {
        await Queue.ScheduleAsync("CAI5_AIS4_S8", Day, Start);
        var upload = (await Queue.DueAsync(At(19, 45)))[0];

        await Queue.RetryAsync(upload, "the dashboard did not answer", At(19, 45));
        Assert.Empty(await Queue.DueAsync(At(19, 50)));                 // waiting out its retry
        var back = Assert.Single(await Queue.DueAsync(At(20, 5)));
        Assert.Equal(1, back.Attempts);
        Assert.Equal("the dashboard did not answer", back.LastError);

        await Queue.CompleteAsync(back);
        Assert.DoesNotContain(await Queue.ReadAsync(), item => item.Step == LmsFollowUpStep.TakeAttendance);
    }

    [Fact]
    public async Task WorkThatKeepsFailingStopsBeingTriedButIsNotThrownAway()
    {
        await Queue.ScheduleAsync("CAI5_AIS4_S8", Day, Start);
        var upload = (await Queue.DueAsync(At(19, 45)))[0];
        var now = At(19, 45);
        for (int attempt = 0; attempt < LmsFollowUpQueue.MaximumAttempts; attempt++)
        {
            await Queue.RetryAsync(upload, "still failing", now);
            now = now.AddMinutes(20);
        }

        Assert.DoesNotContain(await Queue.DueAsync(now), item => item.Step == LmsFollowUpStep.TakeAttendance);
        // Still on the list, with its reason, so a class that never got its attendance is visible.
        var kept = (await Queue.ReadAsync()).Single(item => item.Step == LmsFollowUpStep.TakeAttendance);
        Assert.Equal(LmsFollowUpQueue.MaximumAttempts, kept.Attempts);
        Assert.Equal("still failing", kept.LastError);
    }

    [Fact]
    public async Task LastWeeksClassIsLeftForAPersonRatherThanWrittenNow()
    {
        await Queue.ScheduleAsync("CAI5_AIS4_S8", Day, Start);
        // Two days later nobody knows what happened in that class any more.
        Assert.Empty(await Queue.DueAsync(At(19, 30).AddDays(2)));
        Assert.Equal(4, (await Queue.ReadAsync()).Count);
    }

    [Fact]
    public async Task AnUnreadableFileStartsCleanInsteadOfCrashing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ this is not json");
        Assert.Empty(await Queue.ReadAsync());
        Assert.Equal(4, (await Queue.ScheduleAsync("CAI5_AIS4_S8", Day, Start)).Count);
    }
}
