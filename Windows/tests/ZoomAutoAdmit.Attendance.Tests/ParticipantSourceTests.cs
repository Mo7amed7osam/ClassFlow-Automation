using Microsoft.Playwright;
using Moq;
using ZoomAutoAdmit.Core.Models;
using Xunit;

namespace ZoomAutoAdmit.Attendance.Tests;

public class ParticipantSourceTests
{
    [Fact]
    public void DesktopUsesOnlyNameElementsAndKeepsRawDuplicateNames()
    {
        var list = new InspectElementInfo { Name = "Joined" };
        list.Children.Add(new() { AutomationId = "name", Name = "Same name (Guest)", ControlType = "Text" });
        list.Children.Add(new() { AutomationId = "name", Name = "Same name (Guest)", ControlType = "Text" });
        list.Children.Add(new() { AutomationId = "name", Name = "View", ControlType = "Button" });
        var result = DesktopAttendanceParticipantSource.ExtractParticipants(list, "name");
        Assert.Equal(new[] { "Same name (Guest)", "Same name (Guest)" }, result.Participants.Select(p => p.Name));
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void DesktopRejectsWaitingRoomAndUnreadableTrees()
    {
        Assert.Throws<InvalidOperationException>(() => DesktopAttendanceParticipantSource.ExtractParticipants(
            new() { Name = "Waiting Room (1)" }, "name"));
        Assert.Throws<InvalidOperationException>(() => DesktopAttendanceParticipantSource.ExtractParticipants(
            new() { DiagnosticError = "stale element" }, "name"));
    }

    [Fact]
    public async Task WebReadsExistingPageOnlyWithoutClickHoverOrDisposal()
    {
        var page = new Mock<IPage>(MockBehavior.Strict);
        page.SetupGet(p => p.IsClosed).Returns(false);
        var list = new Mock<ILocator>(MockBehavior.Strict);
        list.Setup(l => l.CountAsync()).ReturnsAsync(1);
        list.Setup(l => l.IsVisibleAsync(It.IsAny<LocatorIsVisibleOptions>())).ReturnsAsync(true);
        list.Setup(l => l.EvaluateAsync<string[]>(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<LocatorEvaluateOptions>()))
            .ReturnsAsync(["eyouth coordinator", "eyouth coordinator"]);
        var source = new WebAttendanceParticipantSource(page.Object, p =>
        {
            Assert.Same(page.Object, p);
            return list.Object;
        });
        var read = await source.ReadAsync(default);
        Assert.Equal(2, read.Participants.Count);
        Assert.False(read.IsComplete);
        Assert.Equal(AttendanceSource.Web, source.Source);
        // Strict mocks reject any browser/page mutation and pointer action.
    }

    [Fact]
    public async Task WebClosedPrimaryPageDoesNotRediscover()
    {
        var page = new Mock<IPage>(MockBehavior.Strict);
        page.SetupGet(p => p.IsClosed).Returns(true);
        var source = new WebAttendanceParticipantSource(page.Object, _ => throw new Exception("should not resolve"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.ReadAsync(default));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task WebMissingOrAmbiguousJoinedListFailsInsteadOfEmptySnapshot(int count)
    {
        var page = new Mock<IPage>();
        var list = new Mock<ILocator>();
        list.Setup(l => l.CountAsync()).ReturnsAsync(count);
        var source = new WebAttendanceParticipantSource(page.Object, _ => list.Object);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.ReadAsync(default));
    }

    [Fact]
    public async Task WebStaleFrameCanBeReadAgainOnSamePage()
    {
        var page = new Mock<IPage>();
        var list = new Mock<ILocator>();
        list.Setup(l => l.CountAsync()).ReturnsAsync(1);
        list.Setup(l => l.IsVisibleAsync(It.IsAny<LocatorIsVisibleOptions>())).ReturnsAsync(true);
        list.SetupSequence(l => l.EvaluateAsync<string[]>(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<LocatorEvaluateOptions>()))
            .ThrowsAsync(new PlaywrightException("Frame detached"))
            .ReturnsAsync(["Participant"]);
        var source = new WebAttendanceParticipantSource(page.Object, _ => list.Object);
        await Assert.ThrowsAsync<PlaywrightException>(() => source.ReadAsync(default));
        Assert.Single((await source.ReadAsync(default)).Participants);
    }
}
