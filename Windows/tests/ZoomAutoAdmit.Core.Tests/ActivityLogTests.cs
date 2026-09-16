using Xunit;
using ZoomAutoAdmit.Core.Central;

namespace ZoomAutoAdmit.Core.Tests;

/// <summary>
/// What this PC did is written down as it happens, so a class never waits for the network and a
/// server that was off misses nothing.
/// </summary>
public sealed class ActivityLogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"activity-log-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void NotesComeBackOldestFirst()
    {
        var log = new ActivityLog(_folder);
        log.Write("class.ended", "done", "ended for everyone", "CAI5_AIS4_S7", new DateOnly(2026, 9, 16),
            at: new DateTimeOffset(2026, 9, 16, 22, 0, 0, TimeSpan.FromHours(3)));
        log.Write("class.opened", "done", "live", "CAI5_AIS4_S7", new DateOnly(2026, 9, 16),
            at: new DateTimeOffset(2026, 9, 16, 19, 0, 0, TimeSpan.FromHours(3)));

        var waiting = log.Pending();

        Assert.Equal(new[] { "class.opened", "class.ended" }, waiting.Select(w => w.Event.Kind));
        var opened = waiting[0].Event;
        Assert.Equal("CAI5_AIS4_S7", opened.Group);
        Assert.Equal("2026-09-16", opened.Date);
        Assert.Equal("done", opened.Outcome);
        Assert.NotEqual(waiting[0].Event.EventId, waiting[1].Event.EventId);
    }

    [Fact]
    public void ANoteTheServerHasIsForgotten()
    {
        var log = new ActivityLog(_folder);
        log.Write("lms.RunSession", "done", "pressed", "CAI5_AIS4_S8", new DateOnly(2026, 9, 16));
        var waiting = Assert.Single(log.Pending());

        log.Forget(waiting.Path);

        Assert.Empty(log.Pending());
    }

    [Fact]
    public void NotesNobodyCouldSendForAMonthAreDropped()
    {
        var log = new ActivityLog(_folder);
        log.Write("class.opened", "done", "live", "CAI5_AIS4_S7", new DateOnly(2026, 8, 1),
            at: DateTimeOffset.Now - ActivityLog.KeepFor - TimeSpan.FromDays(1));
        log.Write("class.opened", "done", "live", "CAI5_AIS4_S7", new DateOnly(2026, 9, 16));

        Assert.Single(log.Pending());
    }

    [Fact]
    public void AVeryLongMessageIsCutRatherThanRefusedByTheServer()
    {
        var log = new ActivityLog(_folder);
        log.Write("lms.TakeAttendance", "failed", new string('x', 900), "CAI5_AIS4_S7", new DateOnly(2026, 9, 16));
        Assert.Equal(500, Assert.Single(log.Pending()).Event.Summary.Length);
    }

    [Fact]
    public void ABrokenFileIsRemovedRatherThanBlockingTheRest()
    {
        var log = new ActivityLog(_folder);
        log.Write("class.opened", "done", "live", "CAI5_AIS4_S7", new DateOnly(2026, 9, 16));
        File.WriteAllText(Path.Combine(_folder, "00000000-000000-000-broken.json"), "{ not json");

        Assert.Single(log.Pending());
        Assert.Single(Directory.GetFiles(_folder, "*.json"));
    }

    [Fact]
    public void NothingWrittenYetIsNotAnError() => Assert.Empty(new ActivityLog(_folder).Pending());
}
