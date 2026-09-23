using ZoomAutoAdmit.WebAutomation.Zoom;
using Xunit;

namespace ZoomAutoAdmit.WebAutomation.Tests;

/// <summary>
/// The three clocks, taken from a real recording (CAI5_AIS4_S7, 1 September 2026):
///   the Zoom share link said startTime=1788278291000   -> 15:58:11 UTC
///   the Drive file was CAI5_AIS4_S7_2026-09-01_1558.mp4  -> 15:58, i.e. UTC
///   My Recordings printed "Sep 1, 2026 08:58 AM"         -> the Zoom account's zone, UTC-7 then
///   the class was at 19:00 in Cairo                      -> 18:58 local, two minutes before it
/// </summary>
public sealed class ZoomRecordingClockTests
{
    private static readonly TimeZoneInfo Cairo = TimeZoneInfo.FindSystemTimeZoneById("Egypt Standard Time");
    private static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
    private const string RealLink = "https://zoom.us/rec/share/FAKE-TOKEN-FOR-TESTS.NotARealLink?startTime=1788278291000";

    [Fact]
    public void TheShareLinkCarriesTheRealStartInUtc()
    {
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 15, 58, 11, TimeSpan.Zero), ZoomRecordingLinkReader.ReadShareLinkStart(RealLink));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1788278291),
            ZoomRecordingLinkReader.ReadShareLinkStart("https://zoom.us/rec/share/x?pwd=1&startTime=1788278291"));
        Assert.Null(ZoomRecordingLinkReader.ReadShareLinkStart("https://zoom.us/rec/share/x"));
        Assert.Null(ZoomRecordingLinkReader.ReadShareLinkStart("https://zoom.us/rec/share/x?startTime=abc"));
        Assert.Null(ZoomRecordingLinkReader.ReadShareLinkStart(null));
    }

    [Fact]
    public void AnAccountListingInAnotherTimeZoneStillHasItsClassFound()
    {
        // G1, 21 September 2026: the class opened at 18:02 in Cairo; depi+10's Zoom shows Pacific
        // time, so My Recordings printed it at 08:02 - next to a 27-minute and a 3-second leftover.
        static ZoomRecordingEntry Listed(int h, int m, TimeSpan length, int day = 21) =>
            new("CAI5_IND1_G1", $"u{h}{m}") { RecordedAt = new DateTime(2026, 9, day, h, m, 0), Duration = length };
        var list = new[]
        {
            Listed(10, 51, TimeSpan.Parse("00:27:07")),
            Listed(10, 3, TimeSpan.Parse("00:00:03")),
            Listed(8, 2, TimeSpan.Parse("01:49:42")),
            Listed(4, 9, TimeSpan.Parse("03:02:56"), day: 19),
        };
        var day = new DateOnly(2026, 9, 21);
        Assert.Null(ZoomRecordingLinkReader.PickRecording(list, "CAI5_IND1_G1", day, new TimeOnly(18, 0)));

        var found = ZoomRecordingLinkReader.PickAcrossZones(list, "CAI5_IND1_G1", day, new TimeOnly(18, 0));

        Assert.NotNull(found);
        Assert.Equal(TimeSpan.Parse("01:49:42"), found!.Value.Entry.Duration);       // the class, not a leftover
        Assert.Equal(TimeSpan.FromHours(10), found.Value.Shift);                       // Pacific to Cairo that day
        // Another group's name, or another day, is never taken this way.
        Assert.Null(ZoomRecordingLinkReader.PickAcrossZones(list, "CAI5_IND1_G2", day, new TimeOnly(18, 0)));
        Assert.Null(ZoomRecordingLinkReader.PickAcrossZones(list, "CAI5_IND1_G1", day.AddDays(3), new TimeOnly(18, 0)));
    }

    [Fact]
    public void TheListsTimeIsMovedFromTheZoomAccountsClockToThisComputers()
    {
        var shown = new ZoomRecordingEntry("CAI5_AIS4_S7", "u") { RecordedAt = new DateTime(2026, 9, 1, 8, 58, 0) };
        var local = ZoomRecordingLinkReader.ToLocalTimes([shown], Pacific, Cairo).Single();
        Assert.Equal(new DateTime(2026, 9, 1, 18, 58, 0), local.RecordedAt);

        // Not configured: untouched, exactly as before the zone was known.
        Assert.Same(shown, ZoomRecordingLinkReader.ToLocalTimes([shown], null, Cairo).Single());
    }

    [Fact]
    public void WithTheZoneSetTheRealClassIsPicked()
    {
        var listed = ZoomRecordingLinkReader.ToLocalTimes(
            [new ZoomRecordingEntry("CAI5_AIS4_S7", "u") { RecordedAt = new DateTime(2026, 9, 1, 8, 58, 0), Duration = TimeSpan.FromHours(3.5) }],
            Pacific, Cairo);
        Assert.NotNull(ZoomRecordingLinkReader.PickRecording(listed, "CAI5_AIS4_S7", new DateOnly(2026, 9, 1), new TimeOnly(19, 0)));
    }

    [Fact]
    public void WithoutTheZoneTheTimeOnlyDecidesBetweenSeveralRecordingsOfADay()
    {
        // One recording that day is taken whatever the misread time says - and the share link is
        // what then confirms it. The zone matters when a group recorded more than once that day.
        var one = new[] { new ZoomRecordingEntry("CAI5_AIS4_S7", "u") { RecordedAt = new DateTime(2026, 9, 1, 8, 58, 0) } };
        Assert.NotNull(ZoomRecordingLinkReader.PickRecording(one, "CAI5_AIS4_S7", new DateOnly(2026, 9, 1), new TimeOnly(19, 0)));

        // Two that day, both printed in Zoom's clock: read as Cairo time neither sits in a 19:00
        // class, so nothing is picked rather than the wrong one.
        var two = new[]
        {
            new ZoomRecordingEntry("CAI5_AIS4_S7", "a") { RecordedAt = new DateTime(2026, 9, 1, 3, 58, 0) },
            new ZoomRecordingEntry("CAI5_AIS4_S7", "b") { RecordedAt = new DateTime(2026, 9, 1, 8, 58, 0) },
        };
        Assert.Null(ZoomRecordingLinkReader.PickRecording(two, "CAI5_AIS4_S7", new DateOnly(2026, 9, 1), new TimeOnly(19, 0)));
        // Converted, the 08:58 one is the 18:58 recording of the 19:00 class.
        var converted = ZoomRecordingLinkReader.ToLocalTimes(two, Pacific, Cairo);
        Assert.Equal("b", ZoomRecordingLinkReader.PickRecording(converted, "CAI5_AIS4_S7", new DateOnly(2026, 9, 1), new TimeOnly(19, 0))!.DetailUrl);
    }

    [Fact]
    public void TheLinkConfirmsTheRecordingBelongsToTheSession()
    {
        var started = ZoomRecordingLinkReader.ReadShareLinkStart(RealLink);

        Assert.True(ZoomRecordingLinkReader.StartsWithinSession(started, new DateOnly(2026, 9, 1), new TimeOnly(19, 0), Cairo, out _));
        Assert.True(ZoomRecordingLinkReader.StartsWithinSession(started, new DateOnly(2026, 9, 1), null, Cairo, out _));
        // The recording starting at the requested time itself is inside its own session too.
        Assert.True(ZoomRecordingLinkReader.StartsWithinSession(started, new DateOnly(2026, 9, 1), new TimeOnly(18, 58), Cairo, out _));
    }

    [Fact]
    public void ARecordingFromAnotherSessionOrDayIsRefusedWithTheReason()
    {
        var started = ZoomRecordingLinkReader.ReadShareLinkStart(RealLink);

        // A class runs from half an hour before its time to three and a half hours after. A 20:00
        // class cannot own a recording that started at 18:58, nor a 10:00 class one from 18:58.
        Assert.False(ZoomRecordingLinkReader.StartsWithinSession(started, new DateOnly(2026, 9, 1), new TimeOnly(20, 0), Cairo, out string why));
        Assert.Contains("18:58", why);
        Assert.Contains("20:00", why);
        Assert.False(ZoomRecordingLinkReader.StartsWithinSession(started, new DateOnly(2026, 9, 1), new TimeOnly(10, 0), Cairo, out _));

        Assert.False(ZoomRecordingLinkReader.StartsWithinSession(started, new DateOnly(2026, 9, 2), null, Cairo, out why));
        Assert.Contains("not on 2026-09-02", why);
    }

    [Fact]
    public void ALinkWithoutAStartIsNotEvidenceEitherWay()
    {
        Assert.True(ZoomRecordingLinkReader.StartsWithinSession(null, new DateOnly(2026, 9, 1), new TimeOnly(19, 0), Cairo, out _));
    }

    [Fact]
    public void AnUnknownZoneSettingIsIgnoredRatherThanGuessed()
    {
        Assert.Null(ZoomRecordingLinkReader.ConfiguredZoomTimeZone(_ => "Not/AZone"));
        Assert.Null(ZoomRecordingLinkReader.ConfiguredZoomTimeZone(_ => null));
        Assert.Equal(Pacific.Id, ZoomRecordingLinkReader.ConfiguredZoomTimeZone(_ => "Pacific Standard Time")!.Id);
        Assert.NotNull(ZoomRecordingLinkReader.ConfiguredZoomTimeZone(_ => "America/Los_Angeles"));
    }
}
