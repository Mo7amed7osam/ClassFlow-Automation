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
