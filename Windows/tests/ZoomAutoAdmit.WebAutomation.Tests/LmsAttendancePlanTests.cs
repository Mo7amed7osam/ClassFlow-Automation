using ZoomAutoAdmit.WebAutomation.Lms;
using Xunit;

namespace ZoomAutoAdmit.WebAutomation.Tests;

public sealed class LmsAttendancePlanTests
{
    private static readonly string[] Roster =
    [
        "Ahmed Abbas Abdel Aal",
        "Ahmed emadeldin abdelmonim",
        "Mohamed Yehia Mohamed El Merghany",
        "mostafa mohamed gamal mohamed",
    ];

    [Fact]
    public void EveryStudentTheDashboardListsGetsAnAnswerAndNobodyIsLeftBlank()
    {
        var plan = LmsAttendancePlan.Build(Roster, ["Ahmed Abbas Abdel Aal", "mostafa mohamed gamal mohamed"]);

        Assert.Equal(Roster.Length, plan.Marks.Count);
        Assert.Equal(2, plan.JoinedCount);
        Assert.Equal(2, plan.NotJoinedCount);
        Assert.True(plan.Marks.Single(mark => mark.StudentName == "Ahmed Abbas Abdel Aal").Joined);
        // Seen by nobody is Not-joined, not an empty row: a blank row is not an answer.
        Assert.False(plan.Marks.Single(mark => mark.StudentName == "Ahmed emadeldin abdelmonim").Joined);
        // The rows keep the dashboard's own order, because they are ticked by position.
        Assert.Equal(Roster, plan.Marks.Select(mark => mark.StudentName));
    }

    [Fact]
    public void SpellingIsComparedTheWayTheMatcherComparesIt()
    {
        // Case, extra spaces and Arabic letter forms must not turn a present student into an absent one.
        var plan = LmsAttendancePlan.Build(
            ["Ahmed emadeldin abdelmonim", "Mohamed Yehia Mohamed El Merghany"],
            ["AHMED  EMADELDIN   ABDELMONIM", "mohamed yehia mohamed el merghany"]);
        Assert.Equal(2, plan.JoinedCount);
        Assert.Empty(plan.NotOnTheDashboard);
    }

    [Fact]
    public void SomebodyPresentWhoHasNoRowIsReportedRatherThanLost()
    {
        var plan = LmsAttendancePlan.Build(Roster, ["Ahmed Abbas Abdel Aal", "A Visitor Nobody Enrolled"]);

        Assert.Equal(1, plan.JoinedCount);
        Assert.Equal("A Visitor Nobody Enrolled", Assert.Single(plan.NotOnTheDashboard));
        Assert.Contains("the dashboard does not list", plan.Summary);
    }

    [Fact]
    public void AnEmptyMeetingMarksEverybodyNotJoinedRatherThanRefusing()
    {
        var plan = LmsAttendancePlan.Build(Roster, []);
        Assert.Equal(0, plan.JoinedCount);
        Assert.Equal(Roster.Length, plan.NotJoinedCount);
        Assert.Empty(plan.NotOnTheDashboard);
    }
}
