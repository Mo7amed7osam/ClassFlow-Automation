using ZoomAutoAdmit.WebAutomation.Lms;
using Xunit;

namespace ZoomAutoAdmit.WebAutomation.Tests;

/// <summary>The session list as the admin sees it: many pages a day, every group listed.</summary>
public class LmsSessionListTests
{
    [Theory]
    [InlineData("Showing 1-10 of 45 items 1 2 3 4 5", 5)]
    [InlineData("Showing 1-10 of 40 items", 4)]
    [InlineData("Showing 1-7 of 7 items", 1)]
    [InlineData("Showing 11-20 of 45 items", 1)]      // only the first page tells the page size
    [InlineData("", 1)]
    public void PagesComeFromThePager(string pager, int pages) =>
        Assert.Equal(pages, LmsSessionRunner.PageCount(pager));

    [Theory]
    [InlineData("Week 9 - Session 3\tCAI5_AIS4_S1\t2026-09-14 19:00", "CAI5_AIS4_S1", true)]
    [InlineData("Week 9 - Session 3\tCAI5_AIS4_S10\t2026-09-14 19:00", "CAI5_AIS4_S1", false)]
    [InlineData("Week 9 - Session 3\tcai5_ais4_s8\t2026-09-14", "CAI5_AIS4_S8", true)]
    [InlineData("Week 9 - Session 3\tXCAI5_AIS4_S8", "CAI5_AIS4_S8", false)]
    public void AGroupIsMatchedAsAWholeName(string row, string group, bool matches) =>
        Assert.Equal(matches, LmsSessionRunner.RowHasGroup(row, group));

    [Theory]
    [InlineData("Session Attachments | Add Attachment | 6.4.1-Databases and SQL for Data Science with Python", "6.4.1-Databases and SQL for Data  Science with Python", true)]
    [InlineData("Session Attachments | 1- Portfolio Building Handout", "1- portfolio building handout", true)]
    [InlineData("Session Attachments | No attachments available.", "1- Portfolio Building Handout", false)]
    public void AnAttachmentIsRecognisedWhateverItsSpacing(string page, string title, bool shown) =>
        Assert.Equal(shown, LmsSessionRunner.Shows(page, title));
}

/// <summary>
/// What a class is called, taken from its row. The dashboard puts the columns in a different order
/// for a coordinator than for an admin, which is how every class of Hosam's came out named after
/// its number - "29.0" - instead of the week it belongs to (2026-09-21).
/// </summary>
public class LmsSessionTitleTests
{
    [Theory]
    // As the admin's account lists it: the name first.
    [InlineData("Week 10 - Session 2\tCAI5_IND1_G1\t2026-09-23 18:00\tpending", "Week 10 - Session 2")]
    // As the coordinator's own account lists it: the session number first, the name further along.
    [InlineData("29.0\tCoaching\tCAI5_IND1_G1\tWeek 10 - Session 2\t2026-09-23 18:00\tpending", "Week 10 - Session 2")]
    [InlineData("30.0\nWeek 11 – Session 1\nCAI5_IND1_G2\n2026-09-26 14:00", "Week 11 – Session 1")]
    // No week anywhere: whatever the row actually says, never the number, the date or the status.
    [InlineData("29.0\tCoaching\tCAI5_IND1_G1\t2026-09-23 18:00\tpending", "Coaching")]
    [InlineData("31.0\t2026-09-26\t14:00\tfinished", "31.0")]
    public void TheNameIsTakenFromTheRowWhateverTheColumnOrder(string row, string expected) =>
        Assert.Equal(expected, LmsSessionRunner.TitleOfRow(row));
}

/// <summary>The type and focus columns of the session list (read live on 2026-09-26).</summary>
public class LmsSessionModeTests
{
    [Theory]
    [InlineData("Week 10 - Session 1\n\tM\n\nMostafa Mohamed\n\n\t2026-09-25\n18:00\tCAI5_AIS4_S8\tsecond\tyth\tCAI\tPhysical\tTechnical\tFinished\t\nLocation\n\t", "Physical", "Technical")]
    [InlineData("Week 11 - Session 1\n\tG\n\nGehad Wahed\n\n\t2026-09-25\n14:00\tCAI5_AIS4_S7\tsecond\tyth\tCAI\tPhysical\tFreelancing\tFinished\t\nLocation\n\t", "Physical", "Freelancing")]
    [InlineData("Week 9 - Session 2\t2026-09-21\n19:00\tCAI5_AIS4_S7\tCAI\tOnline\tCoaching\tPending\t", "Online", "Coaching")]
    [InlineData("Week 9 - Session 2\t2026-09-21\n19:00\tCAI5_AIS4_S7\tCAI\tOnline\tFinished", "Online", "")]
    [InlineData("Week 9 - Session 2\t2026-09-21 19:00\tCAI5_AIS4_S7", "", "")]
    // The list as it reads since 2026-09-26: lower case, "live" for an online class, "coach" for Coaching.
    [InlineData("Week 11 - Session 1\tYYossef ayoub\t2026-09-26 14:00\tCAI5_IND1_G1\tsecond\tyth\tCAI\tlive\tcoach\tpending\tJoin Session", "Online", "coach")]
    [InlineData("Week 11 - Session 1\tGGehad Wahed\t2026-09-25 14:00\tCAI5_AIS4_S7\tsecond\tyth\tCAI\tphysical\tfreelancing\tfinished\tLocation", "Physical", "freelancing")]
    public void TheRowSaysWhetherTheClassIsInARoomAndWhatItIsAbout(string row, string mode, string focus) =>
        Assert.Equal((mode, focus), LmsSessionRunner.ModeOfRow(row));
}
