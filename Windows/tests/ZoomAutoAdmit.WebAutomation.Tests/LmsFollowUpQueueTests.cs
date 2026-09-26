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

        Assert.Equal(5, scheduled.Count);
        var upload = scheduled.Single(item => item.Step == LmsFollowUpStep.TakeAttendance);
        var correct = scheduled.Single(item => item.Step == LmsFollowUpStep.CorrectAttendance);
        var complete = scheduled.Single(item => item.Step == LmsFollowUpStep.CompleteSession);
        Assert.Equal(At(19, 30), upload.DueAt);                 // an hour and a half in
        Assert.Equal(At(21, 0), correct.DueAt);
        Assert.Equal(At(21, 0), complete.DueAt);
    }

    [Fact]
    public async Task APhysicalClassOwesCompleteAndItsRecordingButNothingFromZoom()
    {
        // A meeting was opened by hand for it first, which wrote down the online steps.
        await Queue.ScheduleAsync("CAI5_AIS4_S8", Day, Start);

        var owed = await Queue.SchedulePhysicalAsync("CAI5_AIS4_S8", Day, Start);

        Assert.Equal(new[] { LmsFollowUpStep.CompleteSession, LmsFollowUpStep.AttachZoomRecording },
            owed.Select(item => item.Step).OrderBy(step => step).ToArray());
        Assert.All(owed, item => Assert.Equal(At(21, 0), item.DueAt));        // three hours on, like any class
        Assert.Equal(2, (await Queue.SchedulePhysicalAsync("CAI5_AIS4_S8", Day, Start)).Count);   // written once
    }

    [Fact]
    public async Task NothingIsDueBeforeItsTimeAndTheUploadComesFirst()
    {
        await Queue.ScheduleAsync("CAI5_AIS4_S8", Day, Start);

        Assert.Empty(await Queue.DueAsync(At(19, 29)));
        var atNineThirty = await Queue.DueAsync(At(19, 45));
        Assert.Equal(LmsFollowUpStep.TakeAttendance, Assert.Single(atNineThirty).Step);
        var later = await Queue.DueAsync(At(21, 30));
        Assert.Equal(5, later.Count);
        Assert.Equal(LmsFollowUpStep.TakeAttendance, later[0].Step);   // oldest first
    }

    [Fact]
    public async Task TheSameClassScheduledTwiceIsStillOneJobEach()
    {
        await Queue.ScheduleAsync("CAI5_AIS4_S8", Day, Start);
        var again = await Queue.ScheduleAsync("CAI5_AIS4_S8", Day, Start);

        // A meeting reopened, or the app restarted, must not take attendance twice.
        Assert.Equal(5, again.Count);
    }

    [Fact]
    public async Task ADeletedClassIsForgottenWithItsHistoryAndNothingElseIs()
    {
        await Queue.ScheduleAsync("CAI5_AIS9_S4", Day, new TimeOnly(0, 54));
        await Queue.ScheduleAsync("CAI5_AIS9_S4", Day, new TimeOnly(0, 57));
        await Queue.ScheduleAsync("CAI5_AIS4_S8", Day, Start);
        var run = (await Queue.ReadAsync()).First(i => i.SessionStart == new TimeOnly(0, 54));
        await Queue.RecordAsync(run with { Step = LmsFollowUpStep.RunSession, Id = "CAI5_AIS9_S4|2026-09-03|00:54|RunSession" }, false, "Run Session failed");

        int removed = await Queue.ForgetClassAsync("cai5_ais9_s4", Day, t => t == new TimeOnly(0, 54));

        Assert.Equal(6, removed);                                            // five steps owed + one done
        var left = await Queue.ReadAsync();
        Assert.Equal(10, left.Count);                                        // the 00:57 test and S8 stay
        Assert.DoesNotContain(left, i => i.SessionStart == new TimeOnly(0, 54));
        Assert.Empty(await Queue.ReadHistoryAsync());
    }

    [Fact]
    public async Task WorkOutlivesTheAppBeingClosed()
    {
        await Queue.ScheduleAsync("CAI5_AIS4_S8", Day, Start);

        // A new queue over the same file is what the app sees after it is restarted.
        var afterRestart = new LmsFollowUpQueue(_path);
        var due = await afterRestart.DueAsync(At(22, 0));
        Assert.Equal(5, due.Count);
        Assert.All(due, item => Assert.Equal("CAI5_AIS4_S8", item.Group));
    }

    [Fact]
    public async Task DoneWorkGoesAndFailedWorkComesBackWithItsReason()
    {
        await Queue.ScheduleAsync("CAI5_AIS4_S8", Day, Start);
        var upload = (await Queue.DueAsync(At(19, 45)))[0];

        await Queue.RetryAsync(upload, "the dashboard did not answer", At(19, 45));
        Assert.Empty(await Queue.DueAsync(At(19, 46)));                 // waiting out its retry
        var back = Assert.Single(await Queue.DueAsync(At(19, 48)));
        Assert.Equal(1, back.Attempts);
        Assert.Equal("the dashboard did not answer", back.LastError);

        await Queue.CompleteAsync(back);
        Assert.DoesNotContain(await Queue.ReadAsync(), item => item.Step == LmsFollowUpStep.TakeAttendance);
    }

    [Fact]
    public async Task WorkThatKeepsFailingIsTriedAgainAndAgainUntilItWorks()
    {
        await Queue.ScheduleAsync("CAI5_AIS4_S8", Day, Start);
        var upload = (await Queue.DueAsync(At(19, 45)))[0];
        var now = At(19, 45);
        for (int attempt = 0; attempt < 20; attempt++)
        {
            var again = Assert.Single(await Queue.DueAsync(now), item => item.Step == LmsFollowUpStep.TakeAttendance);
            Assert.Equal(attempt, again.Attempts);
            await Queue.RetryAsync(again, "still failing", now);
            now += LmsFollowUpQueue.LongestRetryAfter;
        }

        var kept = (await Queue.ReadAsync()).Single(item => item.Step == LmsFollowUpStep.TakeAttendance);
        Assert.Equal(20, kept.Attempts);
        Assert.Equal("still failing", kept.LastError);
        Assert.NotEmpty(await Queue.DueAsync(now));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 4)]
    [InlineData(2, 8)]
    [InlineData(3, 16)]
    [InlineData(4, 30)]
    [InlineData(40, 30)]
    public void EachFailureWaitsALittleLongerUpToHalfAnHour(int attempts, int minutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(minutes), LmsFollowUpQueue.RetryAfter(attempts));
    }

    [Fact]
    public async Task LastWeeksClassIsLeftForAPersonRatherThanWrittenNow()
    {
        await Queue.ScheduleAsync("CAI5_AIS4_S8", Day, Start);
        // Two days later nobody knows what happened in that class any more.
        Assert.Empty(await Queue.DueAsync(At(19, 30).AddDays(2)));
        Assert.Equal(5, (await Queue.ReadAsync()).Count);
    }

    [Fact]
    public async Task AnUnreadableFileStartsCleanInsteadOfCrashing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ this is not json");
        Assert.Empty(await Queue.ReadAsync());
        Assert.Equal(5, (await Queue.ScheduleAsync("CAI5_AIS4_S8", Day, Start)).Count);
    }
}
