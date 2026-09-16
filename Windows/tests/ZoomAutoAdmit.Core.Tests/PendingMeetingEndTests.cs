using Xunit;
using ZoomAutoAdmit.Core.Meetings;

namespace ZoomAutoAdmit.Core.Tests;

/// <summary>
/// The minute between deciding a class is over and ending it: announced by whichever process is
/// watching the class, answered by whoever is looking at the app.
/// </summary>
public sealed class PendingMeetingEndTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"ending-{Guid.NewGuid():N}");
    private readonly Guid _session = Guid.NewGuid();

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private PendingMeetingEnds Ends() => new(_folder);

    private PendingMeetingEndNotice Notice(DateTimeOffset endsAt) => new()
    {
        SessionId = _session,
        Group = "CAI5_AIS4_S7",
        ClassStart = new DateTimeOffset(2026, 9, 16, 19, 0, 0, TimeSpan.FromHours(3)),
        Reason = "only the host is left",
        EndsAt = endsAt,
    };

    [Fact]
    public void AnotherProcessSeesTheCountdownAndCanAnswerIt()
    {
        var now = DateTimeOffset.Now;
        Ends().Announce(Notice(now + TimeSpan.FromMinutes(1)));

        // The app, a different process with its own instance, finds it and presses a button.
        var app = Ends();
        var waiting = Assert.Single(app.Waiting(now));
        Assert.Equal("CAI5_AIS4_S7", waiting.Group);
        Assert.Equal("only the host is left", waiting.Reason);
        Assert.InRange(waiting.Left(now).TotalSeconds, 55, 60);
        app.Answer(waiting.SessionId, PendingEndAnswer.EndNow);

        Assert.Equal(PendingEndAnswer.EndNow, Ends().Read(_session));
    }

    [Fact]
    public void NobodyAnsweringIsTheOrdinaryCase()
    {
        Ends().Announce(Notice(DateTimeOffset.Now + TimeSpan.FromMinutes(1)));
        Assert.Equal(PendingEndAnswer.NoAnswer, Ends().Read(_session));
    }

    [Fact]
    public void AnAnswerToTheLastClassDoesNotDecideTheNextOne()
    {
        var ends = Ends();
        ends.Announce(Notice(DateTimeOffset.Now + TimeSpan.FromMinutes(1)));
        ends.Answer(_session, PendingEndAnswer.EndManually);
        ends.Withdraw(_session);

        ends.Announce(Notice(DateTimeOffset.Now + TimeSpan.FromMinutes(1)));
        Assert.Equal(PendingEndAnswer.NoAnswer, ends.Read(_session));
    }

    [Fact]
    public void WithdrawingLeavesNothingToAnswer()
    {
        var ends = Ends();
        ends.Announce(Notice(DateTimeOffset.Now + TimeSpan.FromMinutes(1)));
        ends.Withdraw(_session);
        Assert.Empty(ends.Waiting(DateTimeOffset.Now));
    }

    [Fact]
    public void ACountdownLeftBehindByAProcessThatDiedIsIgnored()
    {
        var ends = Ends();
        var now = DateTimeOffset.Now;
        ends.Announce(Notice(now - PendingMeetingEnds.Stale - TimeSpan.FromMinutes(1)));
        Assert.Empty(ends.Waiting(now));
    }
}
