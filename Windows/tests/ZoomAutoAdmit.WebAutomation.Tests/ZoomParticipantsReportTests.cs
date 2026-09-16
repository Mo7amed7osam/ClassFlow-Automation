using ZoomAutoAdmit.WebAutomation.Zoom;
using Xunit;

namespace ZoomAutoAdmit.WebAutomation.Tests;

public sealed class ZoomParticipantsReportTests
{
    // The columns Zoom's "Meeting Participants" dialog shows (read live, 2026-09-16).
    private static readonly string[] Header =
        ["Name (Original Name)", "User Email", "Join Time", "Leave Time", "Duration (Minutes)", "Guest", "Recording Disclaimer Response", "In Waiting Room"];

    private static string[] Row(string name, int minutes, bool waiting = false) =>
        [name, "", "09/16/2026 08:54:33 AM", "09/16/2026 11:48:46 AM", minutes.ToString(), "Yes", "OK", waiting ? "Yes" : "No"];

    [Fact]
    public void EachPersonIsOnceWithEveryJoinAddedUpAndTheWaitingRoomLeftOut()
    {
        var people = ZoomParticipantsReportReader.Summarize(Header,
        [
            Row("Ahmed Mohamed", 1, waiting: true),
            Row("Ahmed Mohamed", 100),
            Row("ahmed  mohamed", 75),          // the same person after a drop, spelled with Zoom's spacing
            Row("Ziad Waleed", 1),
            Row("Mohanad Yasser", 1, waiting: true),
        ]);

        Assert.Equal(2, people.Count);
        Assert.Equal(175, people.Single(p => p.Name.StartsWith("Ahmed", StringComparison.OrdinalIgnoreCase)).Minutes);
        Assert.Equal(1, people.Single(p => p.Name == "Ziad Waleed").Minutes);
        Assert.DoesNotContain(people, p => p.Name == "Mohanad Yasser");      // only ever waited
    }

    [Theory]
    [InlineData("https://zoom.us/j/92844609413", "92844609413")]
    [InlineData("https://app.zoom.us/wc/92844609413/start?fromPWA=1", "92844609413")]
    [InlineData("https://zoom.us/j/92844609413?pwd=x", "92844609413")]
    public void TheMeetingNumberComesFromTheLink(string url, string number) =>
        Assert.Equal(number, ZoomParticipantsReportReader.MeetingNumber(url));
}
