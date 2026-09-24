using ZoomAutoAdmit.WebAutomation.Zoom;
using Xunit;

namespace ZoomAutoAdmit.WebAutomation.Tests;

public sealed class ZoomParticipantsReportTests
{
    // The columns Zoom's "Meeting Participants" dialog shows (read live, 2026-09-16).
    private static readonly string[] Header =
        ["Name (Original Name)", "User Email", "Join Time", "Leave Time", "Duration (Minutes)", "Guest", "Recording Disclaimer Response", "In Waiting Room"];

    private static string[] Row(string name, int minutes, bool waiting = false, string joined = "08:54:33 AM") =>
        [name, "", $"09/16/2026 {joined}", "09/16/2026 11:48:46 AM", minutes.ToString(), "Yes", "OK", waiting ? "Yes" : "No"];

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

    [Fact]
    public void EveryPageOfTheDialogIsInTheClassAndAPageReadTwiceAddsNobody()
    {
        // Zoom shows a long class ten rows at a time, and the reader reads each page and scrolls it
        // - so the same page comes back more than once and must not count anybody twice.
        string[][] first = [Row("Ahmed Mohamed", 100), Row("Ziad Waleed", 90)];
        string[][] second = [Row("Mohanad Yasser", 80, joined: "09:10:00 AM"), Row("Nada Sherif", 70, joined: "09:11:00 AM")];

        var rows = new List<string[]>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Assert.Equal(2, ZoomParticipantsReportReader.Keep(first, rows, seen));
        Assert.Equal(0, ZoomParticipantsReportReader.Keep(first, rows, seen));       // the page read again
        Assert.Equal(2, ZoomParticipantsReportReader.Keep(second, rows, seen));
        Assert.Equal(0, ZoomParticipantsReportReader.Keep(second, rows, seen));

        var people = ZoomParticipantsReportReader.Summarize(Header, rows);
        Assert.Equal(4, people.Count);
        Assert.Equal(people.Select(person => person.Name), people.Select(person => person.Name).Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(100, people.Single(person => person.Name == "Ahmed Mohamed").Minutes);   // not 200
        Assert.Equal(80, people.Single(person => person.Name == "Mohanad Yasser").Minutes);
    }

    [Fact]
    public void AJoinFromAnotherPageIsTheSamePersonAgain()
    {
        // The same person on two pages: one run left them at 09:00, the next at 10:00.
        var rows = new List<string[]>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        ZoomParticipantsReportReader.Keep([Row("Ziad Waleed", 40)], rows, seen);
        ZoomParticipantsReportReader.Keep([Row("Ziad Waleed", 55, joined: "10:00:00 AM")], rows, seen);

        var people = ZoomParticipantsReportReader.Summarize(Header, rows);
        Assert.Equal(95, Assert.Single(people).Minutes);
    }

    [Theory]
    [InlineData("Ahmed‏ Mohamed")]          // the mark a phone keyboard leaves in an Arabic name
    [InlineData("‪Ahmed Mohamed‬")]
    [InlineData("  Ahmed   Mohamed  ")]
    public void AnInvisibleMarkOrZoomsSpacingDoesNotMakeASecondPerson(string spelling)
    {
        var people = ZoomParticipantsReportReader.Summarize(Header,
            [Row("Ahmed Mohamed", 100), Row(spelling, 20, joined: "10:00:00 AM")]);

        Assert.Equal("Ahmed Mohamed", Assert.Single(people).Name);
        Assert.Equal(120, people[0].Minutes);
    }

    [Theory]
    [InlineData("https://zoom.us/j/92844609413", "92844609413")]
    [InlineData("https://app.zoom.us/wc/92844609413/start?fromPWA=1", "92844609413")]
    [InlineData("https://zoom.us/j/92844609413?pwd=x", "92844609413")]
    public void TheMeetingNumberComesFromTheLink(string url, string number) =>
        Assert.Equal(number, ZoomParticipantsReportReader.MeetingNumber(url));
}

public sealed class ZoomReportClassRunTests
{
    [Theory]
    [InlineData("2026-09-16 18:45", true)]     // opened 15 minutes early
    [InlineData("2026-09-16 20:32", true)]     // reopened during the class
    [InlineData("2026-09-15 18:45", false)]    // yesterday's class on the same meeting number
    [InlineData("2026-09-16 23:00", false)]    // after the class was over
    public void OnlyRunsOfThisClassCount(string runStart, bool expected) =>
        Assert.Equal(expected, ZoomParticipantsReportReader.IsThisClass(DateTime.Parse(runStart), new DateTime(2026, 9, 16, 19, 0, 0)));
}
