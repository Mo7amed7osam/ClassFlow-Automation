using ZoomAutoAdmit.Core.Meetings;
using Xunit;

namespace ZoomAutoAdmit.Core.Tests;

public sealed class AutoEndRuleTests
{
    private const string Host = "eyouth coordinator,(Host, me), Computer audio muted,Video off,Recording to the cloud, Press tab for more options";
    private static string Guest(string name, bool talking = false) =>
        $"{name},(Guest), Computer audio {(talking ? "unmuted" : "muted")},Video off, Press tab for more options";
    private static IReadOnlyList<ParticipantRow> Rows(params string[] labels) => [.. labels.Select(ParticipantRow.Parse)];

    [Fact]
    public void ZoomRowsAreReadForWhoIsTheHostAndWhoIsTalking()
    {
        var host = ParticipantRow.Parse(Host);
        Assert.True(host.IsMe); Assert.True(host.IsHostMe); Assert.Equal(ParticipantAudio.Muted, host.Audio);
        Assert.Equal(ParticipantAudio.Unmuted, ParticipantRow.Parse("Mostafa Badr,(Guest), Screen sharing, Computer audio unmuted,Video on, Press tab for more options").Audio);
        Assert.Equal(ParticipantAudio.Muted, ParticipantRow.Parse(Guest("Rowida Amr")).Audio);
        Assert.Equal(ParticipantAudio.NoAudio, ParticipantRow.Parse("Sara Ahmed,(Guest), Video on").Audio);
        Assert.Equal(ParticipantAudio.Unknown, ParticipantRow.Parse("Some Name").Audio);
        Assert.False(ParticipantRow.Parse("Helper,(Co-host, me), Computer audio muted").IsHostMe);   // a co-host cannot end it
    }

    [Fact]
    public void TheRoomIsBusyWhileAnyoneTalksOrCannotBeRead()
    {
        Assert.Equal(RoomState.HostAlone, AutoEndRule.Classify(Rows(Host), readComplete: true));
        Assert.Equal(RoomState.SmallAndSilent, AutoEndRule.Classify(Rows(Host, Guest("A"), Guest("B"), Guest("C"), Guest("D")), true));
        Assert.Equal(RoomState.Busy, AutoEndRule.Classify(Rows(Host, Guest("A"), Guest("B"), Guest("C"), Guest("D"), Guest("E")), true));
        Assert.Equal(RoomState.Busy, AutoEndRule.Classify(Rows(Host, Guest("A", talking: true)), true));
        Assert.Equal(RoomState.Busy, AutoEndRule.Classify(Rows(Host, "Someone"), true));                // mic unknown
        Assert.Equal(RoomState.Unreadable, AutoEndRule.Classify(Rows(Host, Guest("A")), readComplete: false));
    }

    [Theory]
    [InlineData(2, 59, RoomState.HostAlone, 600, AutoEndAction.Wait)]           // before three hours: never
    [InlineData(3, 1, RoomState.HostAlone, 60, AutoEndAction.EndForAll)]        // only the host left
    [InlineData(3, 1, RoomState.HostAlone, 20, AutoEndAction.Wait)]             // ...for a moment only
    [InlineData(3, 2, RoomState.SmallAndSilent, 3600, AutoEndAction.Wait)]      // quiet since before 3 h: the 5 minutes start at 3 h
    [InlineData(3, 10, RoomState.SmallAndSilent, 299, AutoEndAction.Wait)]      // under five, muted, not five minutes yet
    [InlineData(3, 10, RoomState.SmallAndSilent, 300, AutoEndAction.EndForAll)]
    [InlineData(4, 0, RoomState.Busy, 3600, AutoEndAction.Wait)]                // people there, or talking
    [InlineData(4, 0, RoomState.Unreadable, 3600, AutoEndAction.Wait)]
    public void AClassIsEndedOnlyWhenItIsReallyOver(int hours, int minutes, RoomState state, int heldSeconds, AutoEndAction expected)
    {
        var decision = AutoEndRule.Decide(new TimeSpan(hours, minutes, 0), state, TimeSpan.FromSeconds(heldSeconds));
        Assert.Equal(expected, decision.Action);
    }

    [Fact]
    public void SomeoneUnmutingStartsTheFiveMinutesAgain()
    {
        var tracker = new AutoEndTracker();
        var t = new DateTimeOffset(2026, 9, 16, 22, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, tracker.Observe(t, RoomState.SmallAndSilent));
        Assert.Equal(TimeSpan.FromMinutes(4), tracker.Observe(t.AddMinutes(4), RoomState.SmallAndSilent));
        Assert.Equal(TimeSpan.Zero, tracker.Observe(t.AddMinutes(4.5), RoomState.Busy));             // somebody spoke
        Assert.Equal(TimeSpan.Zero, tracker.Observe(t.AddMinutes(5), RoomState.SmallAndSilent));
        Assert.Equal(TimeSpan.FromMinutes(5), tracker.Observe(t.AddMinutes(10), RoomState.SmallAndSilent));
    }
    private static string CoHost(string name, bool talking = false) =>
        $"{name},(Co-host), Computer audio {(talking ? "unmuted" : "muted")},Video off, Press tab for more options";

    [Fact]
    public void TheInstructorSittingThereMutedDoesNotMakeItAClass()
    {
        // "Fewer than five" is five students: the host's row and the co-host's are not students.
        Assert.Equal(RoomState.SmallAndSilent, AutoEndRule.Classify(
            Rows(Host, CoHost("Mostafa"), Guest("A"), Guest("B"), Guest("C"), Guest("D")), true));
        // Five students and the instructor: still a class.
        Assert.Equal(RoomState.Busy, AutoEndRule.Classify(
            Rows(Host, CoHost("Mostafa"), Guest("A"), Guest("B"), Guest("C"), Guest("D"), Guest("E")), true));
        // Only the instructor, muted: not "the host alone" (that is 50 s), but a small quiet room (5 min).
        Assert.Equal(RoomState.SmallAndSilent, AutoEndRule.Classify(Rows(Host, CoHost("Mostafa")), true));
        // And the instructor talking keeps it going, like anyone else.
        Assert.Equal(RoomState.Busy, AutoEndRule.Classify(Rows(Host, CoHost("Mostafa", talking: true)), true));
    }

    [Fact]
    public void AMicComingOnStartsTheFiveMinutesAgainFromTheMomentItGoesQuiet()
    {
        var tracker = new AutoEndTracker();
        var three = TimeSpan.FromHours(3);
        var t = DateTimeOffset.Now;

        // Quiet for four minutes past the three hours - nearly there.
        tracker.Observe(t, RoomState.SmallAndSilent);
        var held = tracker.Observe(t.AddMinutes(4), RoomState.SmallAndSilent);
        Assert.Equal(AutoEndAction.Wait, AutoEndRule.Decide(three + TimeSpan.FromMinutes(4), RoomState.SmallAndSilent, held).Action);

        // Someone starts talking; a minute later they stop.
        tracker.Observe(t.AddMinutes(4.5), RoomState.Busy);
        held = tracker.Observe(t.AddMinutes(5.5), RoomState.SmallAndSilent);
        Assert.Equal(TimeSpan.Zero, held);
        Assert.Equal(AutoEndAction.Wait, AutoEndRule.Decide(three + TimeSpan.FromMinutes(5.5), RoomState.SmallAndSilent, held).Action);

        // The five minutes run from the moment the last mic went off, not from before.
        held = tracker.Observe(t.AddMinutes(10.4), RoomState.SmallAndSilent);
        Assert.Equal(AutoEndAction.Wait, AutoEndRule.Decide(three + TimeSpan.FromMinutes(10.4), RoomState.SmallAndSilent, held).Action);
        held = tracker.Observe(t.AddMinutes(10.5), RoomState.SmallAndSilent);
        Assert.Equal(AutoEndAction.EndForAll, AutoEndRule.Decide(three + TimeSpan.FromMinutes(10.5), RoomState.SmallAndSilent, held).Action);
    }

}
