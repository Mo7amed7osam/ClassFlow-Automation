using ZoomAutoAdmit.WebAutomation.Zoom;
using Xunit;

namespace ZoomAutoAdmit.WebAutomation.Tests;

public sealed class ZoomRecordingLinkTests
{
    private static ZoomRecordingEntry Entry(string topic) =>
        new(topic, "https://zoom.us/recording/detail?meeting_id=" + topic.GetHashCode());

    [Fact]
    public void TheGroupsOwnRecordingIsPickedAndAnotherClassesNeverIs()
    {
        ZoomRecordingEntry[] listed =
        [
            Entry("CAI5_AIS4_S8"),
            Entry("CAI5_AIS4_S7"),
            Entry("Weekly standup"),
        ];

        Assert.Equal("CAI5_AIS4_S7", ZoomRecordingLinkReader.PickRecording(listed, "CAI5_AIS4_S7")!.Topic);
        Assert.Equal("CAI5_AIS4_S8", ZoomRecordingLinkReader.PickRecording(listed, "CAI5_AIS4_S8")!.Topic);
        // A group with no recording gets nothing rather than somebody else's class.
        Assert.Null(ZoomRecordingLinkReader.PickRecording(listed, "CAI5_AIS4_S9"));
        Assert.Null(ZoomRecordingLinkReader.PickRecording([], "CAI5_AIS4_S7"));
    }

    [Fact]
    public void AnExactNameWinsOverOneThatMerelyContainsIt()
    {
        ZoomRecordingEntry[] listed = [Entry("Recap of CAI5_AIS4_S7"), Entry("CAI5_AIS4_S7")];
        Assert.Equal("CAI5_AIS4_S7", ZoomRecordingLinkReader.PickRecording(listed, "CAI5_AIS4_S7")!.Topic);

        // The list carries the row's whole text, so a title inside it still matches.
        ZoomRecordingEntry[] rows = [Entry("0 03:29:13 CAI5_AIS4_S7 EC eyouth coordinator Sep 1, 2026")];
        Assert.Same(rows[0], ZoomRecordingLinkReader.PickRecording(rows, "CAI5_AIS4_S7"));
    }

    private static ZoomRecordingEntry At(string topic, string recordedAt) =>
        new(topic, "https://zoom.us/recording/detail?meeting_id=" + recordedAt)
        { RecordedAt = ZoomRecordingLinkReader.ReadRecordedAt(recordedAt) };

    [Fact]
    public void TheDateZoomPrintsOnTheRowIsRead()
    {
        Assert.Equal(new DateTime(2026, 9, 1, 8, 58, 0),
            ZoomRecordingLinkReader.ReadRecordedAt("0 03:29:13 CAI5_AIS4_S7 EC eyouth coordinator Sep 1, 2026 08:58 AM"));
        Assert.Equal(new DateTime(2026, 8, 28, 18, 5, 0),
            ZoomRecordingLinkReader.ReadRecordedAt("CAI5_AIS4_S8" + (char)10 + "August 28, 2026 6:05 PM"));
        Assert.Null(ZoomRecordingLinkReader.ReadRecordedAt("CAI5_AIS4_S7 no date here"));
        Assert.Null(ZoomRecordingLinkReader.ReadRecordedAt(""));
    }

    [Fact]
    public void TheRecordingOfThisSessionIsPickedNotLastWeeks()
    {
        ZoomRecordingEntry[] listed =
        [
            At("CAI5_AIS4_S7", "Aug 25, 2026 06:00 PM"),
            At("CAI5_AIS4_S7", "Sep 1, 2026 06:02 PM"),
            At("CAI5_AIS4_S7", "Sep 1, 2026 09:58 AM"),
        ];

        // Same name every week, so the day decides.
        var evening = ZoomRecordingLinkReader.PickRecording(
            listed, "CAI5_AIS4_S7", new DateOnly(2026, 9, 1), new TimeOnly(18, 0));
        Assert.Equal(new DateTime(2026, 9, 1, 18, 2, 0), evening!.RecordedAt);

        // Two classes the same day: the one that started nearest the scheduled time.
        var morning = ZoomRecordingLinkReader.PickRecording(
            listed, "CAI5_AIS4_S7", new DateOnly(2026, 9, 1), new TimeOnly(10, 0));
        Assert.Equal(new DateTime(2026, 9, 1, 9, 58, 0), morning!.RecordedAt);

        // A day with no recording gets nothing rather than another day's video.
        Assert.Null(ZoomRecordingLinkReader.PickRecording(
            listed, "CAI5_AIS4_S7", new DateOnly(2026, 9, 2), new TimeOnly(18, 0)));

        // A recording whose own date could not be read is not eligible for a dated search.
        ZoomRecordingEntry[] undated = [Entry("CAI5_AIS4_S7")];
        Assert.Null(ZoomRecordingLinkReader.PickRecording(undated, "CAI5_AIS4_S7", new DateOnly(2026, 9, 1)));
        Assert.NotNull(ZoomRecordingLinkReader.PickRecording(undated, "CAI5_AIS4_S7"));
    }

    private static ZoomRecordingEntry Recording(string topic, string recordedAt, string duration) =>
        new(topic, "https://zoom.us/recording/detail?meeting_id=" + recordedAt + duration)
        {
            RecordedAt = ZoomRecordingLinkReader.ReadRecordedAt(recordedAt),
            Duration = ZoomRecordingLinkReader.ReadDuration(duration)
        };

    [Fact]
    public void TheLengthZoomPrintsOnTheRowIsReadAndAClockTimeIsNotMistakenForIt()
    {
        Assert.Equal(TimeSpan.FromSeconds(3 * 3600 + 29 * 60 + 13),
            ZoomRecordingLinkReader.ReadDuration("0 03:29:13 CAI5_AIS4_S7 EC Sep 1, 2026 08:58 AM"));
        // "08:58 AM" is a time of day, not a length: two parts are never read as a duration.
        Assert.Null(ZoomRecordingLinkReader.ReadDuration("CAI5_AIS4_S7 Sep 1, 2026 08:58 AM"));
        Assert.Null(ZoomRecordingLinkReader.ReadDuration(""));
    }

    [Fact]
    public void ARestartedClassKeepsItsLongestRecordingNotTheLeftover()
    {
        // One class at 18:00 that was stopped and restarted, plus an earlier class the same day.
        ZoomRecordingEntry[] listed =
        [
            Recording("CAI5_AIS4_S8", "Jul 24, 2026 02:44 PM", "02:55:41"),
            Recording("CAI5_AIS4_S8", "Jul 24, 2026 06:06 PM", "00:12:03"),
            Recording("CAI5_AIS4_S8", "Jul 24, 2026 06:53 PM", "02:41:18"),
        ];

        var evening = ZoomRecordingLinkReader.PickRecording(
            listed, "CAI5_AIS4_S8", new DateOnly(2026, 7, 24), new TimeOnly(18, 0));
        Assert.Equal(TimeSpan.FromSeconds(2 * 3600 + 41 * 60 + 18), evening!.Duration);

        // The afternoon class keeps its own recording; the longer evening one is not borrowed.
        var afternoon = ZoomRecordingLinkReader.PickRecording(
            listed, "CAI5_AIS4_S8", new DateOnly(2026, 7, 24), new TimeOnly(14, 30));
        Assert.Equal(TimeSpan.FromSeconds(2 * 3600 + 55 * 60 + 41), afternoon!.Duration);

        // A class at a time nothing was recorded near gets nothing, not the day's longest.
        Assert.Null(ZoomRecordingLinkReader.PickRecording(
            listed, "CAI5_AIS4_S8", new DateOnly(2026, 7, 24), new TimeOnly(8, 0)));
    }

    [Fact]
    public void WithNoSessionTimeTheLongestOfTheDayIsTaken()
    {
        ZoomRecordingEntry[] listed =
        [
            Recording("CAI5_AIS4_S8", "Jul 24, 2026 06:06 PM", "00:12:03"),
            Recording("CAI5_AIS4_S8", "Jul 24, 2026 06:53 PM", "02:41:18"),
        ];
        var picked = ZoomRecordingLinkReader.PickRecording(listed, "CAI5_AIS4_S8", new DateOnly(2026, 7, 24));
        Assert.Equal(TimeSpan.FromSeconds(2 * 3600 + 41 * 60 + 18), picked!.Duration);
    }

    [Theory]
    [InlineData("https://zoom.us/rec/share/KXYkU1V.VxWnf7?startTime=1788278291000", true)]
    [InlineData("https://us02web.zoom.us/rec/play/abc123", true)]
    [InlineData("https://zoom.us/recording/detail?meeting_id=abc", false)]   // the page, not the link
    [InlineData("http://zoom.us/rec/share/abc", false)]                       // never plain http
    [InlineData("https://evil.example.com/rec/share/abc", false)]             // never another host
    [InlineData("Copied!", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyARealZoomShareLinkIsAccepted(string? value, bool expected) =>
        Assert.Equal(expected, ZoomRecordingLinkReader.IsShareLink(value));
}
