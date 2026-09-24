using ZoomAutoAdmit.Core.Meetings;
using Xunit;

namespace ZoomAutoAdmit.Core.Tests;

/// <summary>
/// Which meetings are running on this PC right now. Everything that waits for a class to end - the
/// late-joiner pass, the recording that Zoom only publishes afterwards - reads this, so a marker
/// that outlives its meeting leaves those steps waiting for ever.
/// </summary>
[Collection("LiveMeetings")]
public sealed class LiveMeetingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ZoomLiveMeetings", Guid.NewGuid().ToString("N"));
    private readonly string _was = LiveMeetings.Folder;

    public LiveMeetingsTests()
    {
        LiveMeetings.Folder = _folder;
        LiveMeetings.ForgetFinished();
    }

    [Fact]
    public void AGroupCanBeForgottenWhenItsClassIsOpenedAfresh()
    {
        // "Start the meeting again": this PC thought it was hosting a class Zoom had already
        // dropped, so it refused to open it (2026-09-24, G2 at 18:04).
        var dropped = Guid.NewGuid();
        var other = Guid.NewGuid();
        LiveMeetings.Beat(dropped, "CAI5_IND1_G2", "Web", DateTimeOffset.Now);
        LiveMeetings.Beat(other, "CAI5_AIS4_S8", "Desktop", DateTimeOffset.Now);

        LiveMeetings.ClearGroup("cai5_ind1_g2");

        Assert.False(LiveMeetings.IsLive("CAI5_IND1_G2"));
        Assert.True(LiveMeetings.IsLive("CAI5_AIS4_S8"));
        // And the beat of the meeting that was let go cannot bring it back.
        LiveMeetings.Beat(dropped, "CAI5_IND1_G2", "Web", DateTimeOffset.Now);
        Assert.False(LiveMeetings.IsLive("CAI5_IND1_G2"));
    }

    public void Dispose()
    {
        LiveMeetings.Folder = _was;
        LiveMeetings.ForgetFinished();
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }

    [Fact]
    public void AMeetingThatBeatsIsLive()
    {
        var id = Guid.NewGuid();
        LiveMeetings.Beat(id, "CAI5_AIS4_S7", "Web", DateTimeOffset.Now);

        Assert.True(LiveMeetings.IsLive("CAI5_AIS4_S7"));
        Assert.Equal("CAI5_AIS4_S7", Assert.Single(LiveMeetings.List()).Group);
    }

    [Fact]
    public void AMeetingNobodyHasBeatenForAWhileIsNotLive()
    {
        var id = Guid.NewGuid();
        LiveMeetings.Beat(id, "CAI5_AIS4_S7", "Web", DateTimeOffset.Now);

        // The process that was beating is gone; its file goes stale rather than lying for ever.
        Assert.False(LiveMeetings.IsLive("CAI5_AIS4_S7", DateTimeOffset.Now + LiveMeetings.FreshFor + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void AMeetingSaidToBeOverStopsBeingLiveAtOnce()
    {
        var id = Guid.NewGuid();
        LiveMeetings.Beat(id, "CAI5_AIS4_S7", "Web", DateTimeOffset.Now);
        Assert.True(LiveMeetings.IsLive("CAI5_AIS4_S7"));

        LiveMeetings.Finish(id);

        Assert.False(LiveMeetings.IsLive("CAI5_AIS4_S7"));
        Assert.True(LiveMeetings.IsFinished(id));
    }

    [Fact]
    public void ABeatStillInFlightCannotBringAFinishedMeetingBack()
    {
        // The bug this exists for: the watch noticed the meeting was closed elsewhere, but the loop
        // that beats for it was watching its own token, not Zoom. It wrote the marker back every
        // minute and the class stayed "live" for as long as the process ran - so the recording was
        // refused for five hours after the class had ended (2026-09-20).
        var id = Guid.NewGuid();
        LiveMeetings.Beat(id, "CAI5_AIS4_S7", "Web", DateTimeOffset.Now);
        LiveMeetings.Finish(id);

        LiveMeetings.Beat(id, "CAI5_AIS4_S7", "Web", DateTimeOffset.Now);
        LiveMeetings.Beat(id, "CAI5_AIS4_S7", "Web", DateTimeOffset.Now);

        Assert.False(LiveMeetings.IsLive("CAI5_AIS4_S7"));
        Assert.Empty(LiveMeetings.List());
    }

    [Fact]
    public void OneMeetingEndingLeavesAnotherOfItsOwnAlone()
    {
        var ended = Guid.NewGuid();
        var running = Guid.NewGuid();
        LiveMeetings.Beat(ended, "CAI5_AIS4_S7", "Web", DateTimeOffset.Now);
        LiveMeetings.Beat(running, "CAI5_AIS4_S8", "Desktop", DateTimeOffset.Now);

        LiveMeetings.Finish(ended);

        Assert.False(LiveMeetings.IsLive("CAI5_AIS4_S7"));
        Assert.True(LiveMeetings.IsLive("CAI5_AIS4_S8"));
        // The one still running keeps beating, as it should.
        LiveMeetings.Beat(running, "CAI5_AIS4_S8", "Desktop", DateTimeOffset.Now);
        Assert.Equal("CAI5_AIS4_S8", Assert.Single(LiveMeetings.List()).Group);
    }

    [Fact]
    public void SayingAMeetingIsOverTwiceIsHarmless()
    {
        var id = Guid.NewGuid();
        LiveMeetings.Beat(id, "CAI5_AIS4_S7", "Web", DateTimeOffset.Now);
        LiveMeetings.Finish(id);
        LiveMeetings.Finish(id);
        LiveMeetings.Clear(id);

        Assert.False(LiveMeetings.IsLive("CAI5_AIS4_S7"));
    }
}
