using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Moq;
using Xunit;

namespace ZoomAutoAdmit.WebAutomation.Tests;

public class ZoomWaitingRoomDomMockTests
{
    [Fact]
    public async Task MockedPlaywrightDomDetectsCountAdmitAllAndParticipantRows()
    {
        var headerItem = Locator();
        headerItem.Setup(item => item.IsVisibleAsync(It.IsAny<LocatorIsVisibleOptions>())).ReturnsAsync(true);
        headerItem.Setup(item => item.InnerTextAsync(It.IsAny<LocatorInnerTextOptions>())).ReturnsAsync("Waiting room (2)");
        var headers = Collection(headerItem.Object);

        var admitAllItem = Locator();
        admitAllItem.Setup(item => item.IsVisibleAsync(It.IsAny<LocatorIsVisibleOptions>())).ReturnsAsync(true);
        var admitAll = Collection(admitAllItem.Object);

        var alice = ParticipantRow("View Alice (Guest) Admit");
        var bob = ParticipantRow("Bob (Guest) More Admit");
        var rows = Collection(alice.Row.Object, bob.Row.Object);

        var frame = new Mock<IFrame>(MockBehavior.Strict);
        frame.Setup(item => item.Locator(
                It.Is<string>(selector => selector.Contains("notification-message-wrap__layer")),
                It.IsAny<FrameLocatorOptions>()))
            .Returns(Collection().Object);
        frame.Setup(item => item.GetByText(
                It.Is<Regex>(pattern => pattern.ToString().Contains("Waiting")),
                It.IsAny<FrameGetByTextOptions>()))
            .Returns(headers.Object);
        frame.Setup(item => item.GetByText(
                It.Is<Regex>(pattern => pattern.ToString().Contains("Joined")),
                It.IsAny<FrameGetByTextOptions>()))
            .Returns(Collection().Object);
        frame.Setup(item => item.GetByText(
                It.Is<Regex>(pattern => pattern.ToString().Contains("entered")),
                It.IsAny<FrameGetByTextOptions>()))
            .Returns(Collection().Object);
        frame.Setup(item => item.GetByRole(
                AriaRole.Button,
                It.Is<FrameGetByRoleOptions>(options => options.NameRegex!.ToString().Contains("all"))))
            .Returns(admitAll.Object);
        frame.Setup(item => item.Locator(
                It.Is<string>(selector => selector.Contains("Waiting room list")),
                It.IsAny<FrameLocatorOptions>()))
            .Returns(rows.Object);
        var page = new Mock<IPage>();
        page.SetupGet(item => item.IsClosed).Returns(false);

        var snapshot = await new ZoomWaitingRoomDom()
            .CaptureAsync(new(page.Object, frame.Object));

        Assert.True(snapshot.WaitingRoomExists);
        Assert.Equal(2, snapshot.WaitingCount);
        Assert.True(snapshot.AdmitAllAvailable);
        Assert.Equal(["Alice", "Bob"], snapshot.Participants.Select(item => item.Name));
        Assert.True(alice.Hovered());
        Assert.True(bob.Hovered());
    }

    [Fact]
    public async Task HiddenAdmitAppearsOnlyAfterRowHoverAndRowsStayIndependent()
    {
        var alice = ParticipantRow("View Alice (Guest) More Admit");
        var bob = ParticipantRow("View Bob (Guest) More Admit");
        bob.AdmitButton.Setup(item => item.EvaluateAsync<object?>(
                It.Is<string>(script => script.Contains("element.click()")),
                It.IsAny<object?>(),
                It.IsAny<LocatorEvaluateOptions>()))
            .ReturnsAsync((object?)null);
        var rows = Collection(alice.Row.Object, bob.Row.Object);
        var frame = new Mock<IFrame>(MockBehavior.Strict);
        frame.Setup(item => item.Locator(
                It.Is<string>(selector => selector.Contains("notification-message-wrap__layer")),
                It.IsAny<FrameLocatorOptions>()))
            .Returns(Collection().Object);
        frame.Setup(item => item.Locator(
                It.Is<string>(selector => selector.Contains("Waiting room list")),
                It.IsAny<FrameLocatorOptions>()))
            .Returns(rows.Object);
        frame.Setup(item => item.GetByText(
                It.Is<Regex>(pattern => pattern.ToString().Contains("entered")),
                It.IsAny<FrameGetByTextOptions>()))
            .Returns(Collection().Object);
        var page = new Mock<IPage>();
        Assert.False(alice.Hovered());
        Assert.False(bob.Hovered());

        bool clicked = await new ZoomWaitingRoomDom().ClickParticipantAsync(
            new(page.Object, frame.Object),
            WebParticipantIdentity.Normalize("Bob"));

        Assert.True(clicked);
        Assert.False(alice.Hovered());
        Assert.True(bob.Hovered());
        alice.AdmitButton.Verify(
            item => item.EvaluateAsync<object?>(
                It.Is<string>(script => script.Contains("element.click()")),
                It.IsAny<object?>(),
                It.IsAny<LocatorEvaluateOptions>()),
            Times.Never);
        bob.AdmitButton.Verify(
            item => item.EvaluateAsync<object?>(
                It.Is<string>(script => script.Contains("element.click()")),
                It.IsAny<object?>(),
                It.IsAny<LocatorEvaluateOptions>()),
            Times.Once);
    }

    [Fact]
    public async Task AdmitDisappearingAfterHoverDoesNotCrashDomCapture()
    {
        var headerItem = Locator();
        headerItem.Setup(item => item.IsVisibleAsync(It.IsAny<LocatorIsVisibleOptions>())).ReturnsAsync(true);
        headerItem.Setup(item => item.InnerTextAsync(It.IsAny<LocatorInnerTextOptions>())).ReturnsAsync("Waiting room (1)");
        var row = ParticipantRow("eyouth coordinator");
        row.AdmitButton.Setup(item => item.WaitForAsync(It.IsAny<LocatorWaitForOptions>()))
            .ThrowsAsync(new TimeoutException("Timeout 2000ms exceeded."));
        var frame = new Mock<IFrame>(MockBehavior.Strict);
        frame.Setup(item => item.Locator(
                It.Is<string>(selector => selector.Contains("notification-message-wrap__layer")),
                It.IsAny<FrameLocatorOptions>()))
            .Returns(Collection().Object);
        frame.Setup(item => item.GetByText(
                It.Is<Regex>(pattern => pattern.ToString().Contains("Waiting")),
                It.IsAny<FrameGetByTextOptions>()))
            .Returns(Collection(headerItem.Object).Object);
        frame.Setup(item => item.GetByText(
                It.Is<Regex>(pattern => pattern.ToString().Contains("Joined")),
                It.IsAny<FrameGetByTextOptions>()))
            .Returns(Collection().Object);
        frame.Setup(item => item.GetByText(
                It.Is<Regex>(pattern => pattern.ToString().Contains("entered")),
                It.IsAny<FrameGetByTextOptions>()))
            .Returns(Collection().Object);
        frame.Setup(item => item.GetByRole(AriaRole.Button, It.IsAny<FrameGetByRoleOptions>()))
            .Returns(Collection().Object);
        frame.Setup(item => item.Locator(
                It.Is<string>(selector => selector.Contains("Waiting room list")),
                It.IsAny<FrameLocatorOptions>()))
            .Returns(Collection(row.Row.Object).Object);
        var page = new Mock<IPage>();
        page.SetupGet(item => item.IsClosed).Returns(false);

        var snapshot = await new ZoomWaitingRoomDom().CaptureAsync(new(page.Object, frame.Object));

        Assert.Empty(snapshot.Participants);
        Assert.True(row.Hovered());
    }

    [Fact]
    public async Task ToastOnlyParticipantIsDetectedAndAdmittedWithinToastContainer()
    {
        var admit = Locator();
        admit.Setup(item => item.IsVisibleAsync(It.IsAny<LocatorIsVisibleOptions>())).ReturnsAsync(true);
        admit.Setup(item => item.InnerTextAsync(It.IsAny<LocatorInnerTextOptions>())).ReturnsAsync("Admit");
        admit.Setup(item => item.EvaluateAsync<object?>(
                It.Is<string>(script => script.Contains("element.click()")),
                It.IsAny<object?>(),
                It.IsAny<LocatorEvaluateOptions>()))
            .ReturnsAsync((object?)null);
        var toast = Locator();
        toast.Setup(item => item.IsVisibleAsync(It.IsAny<LocatorIsVisibleOptions>())).ReturnsAsync(true);
        var message = Locator();
        message.Setup(item => item.IsVisibleAsync(It.IsAny<LocatorIsVisibleOptions>())).ReturnsAsync(true);
        message.Setup(item => item.InnerTextAsync(It.IsAny<LocatorInnerTextOptions>()))
            .ReturnsAsync("eyouth coordinator entered the waiting room");
        toast.Setup(item => item.Locator(
                It.Is<string>(selector => selector.Contains("button.zmu-btn--primary")),
                It.IsAny<LocatorLocatorOptions>()))
            .Returns(Collection(admit.Object).Object);
        toast.Setup(item => item.Locator(
                It.Is<string>(selector => selector.Contains("notification-message-wrap__txt")),
                It.IsAny<LocatorLocatorOptions>()))
            .Returns(Collection(message.Object).Object);
        var frame = new Mock<IFrame>(MockBehavior.Strict);
        frame.Setup(item => item.Locator(
                It.Is<string>(selector => selector.Contains("notification-message-wrap__layer")),
                It.IsAny<FrameLocatorOptions>()))
            .Returns(Collection(toast.Object).Object);
        frame.Setup(item => item.GetByText(
                It.Is<Regex>(pattern => pattern.ToString().Contains("Waiting")),
                It.IsAny<FrameGetByTextOptions>()))
            .Returns(Collection().Object);
        frame.Setup(item => item.GetByText(
                It.Is<Regex>(pattern => pattern.ToString().Contains("Joined")),
                It.IsAny<FrameGetByTextOptions>()))
            .Returns(Collection().Object);
        var page = new Mock<IPage>();
        page.SetupGet(item => item.IsClosed).Returns(false);
        var dom = new ZoomWaitingRoomDom();
        var surface = new ZoomMeetingSurface(page.Object, frame.Object);

        var snapshot = await dom.CaptureAsync(surface);
        bool clicked = await dom.ClickParticipantAsync(
            surface,
            WebParticipantIdentity.Normalize("eyouth coordinator"));

        Assert.Single(snapshot.Participants);
        Assert.Equal("eyouth coordinator", snapshot.Participants[0].Name);
        Assert.True(clicked);
        admit.Verify(
            item => item.EvaluateAsync<object?>(
                It.Is<string>(script => script.Contains("element.click()")),
                It.IsAny<object?>(),
                It.IsAny<LocatorEvaluateOptions>()),
            Times.Once);
    }

    [Fact]
    public async Task ParticipantRowOnlyStrategySkipsNotificationAfterUnverifiedNotificationAttempt()
    {
        var notificationAdmit = Locator();
        notificationAdmit.Setup(item => item.IsVisibleAsync(It.IsAny<LocatorIsVisibleOptions>()))
            .ReturnsAsync(true);
        notificationAdmit.Setup(item => item.InnerTextAsync(It.IsAny<LocatorInnerTextOptions>()))
            .ReturnsAsync("Admit");
        var notification = Locator();
        notification.Setup(item => item.IsVisibleAsync(It.IsAny<LocatorIsVisibleOptions>()))
            .ReturnsAsync(true);
        notification.Setup(item => item.Locator(
                It.Is<string>(selector => selector.Contains("button.zmu-btn--primary")),
                It.IsAny<LocatorLocatorOptions>()))
            .Returns(Collection(notificationAdmit.Object).Object);

        var row = ParticipantRow("eyouth coordinator");
        row.AdmitButton.Setup(item => item.EvaluateAsync<object?>(
                It.Is<string>(script => script.Contains("element.click()")),
                It.IsAny<object?>(),
                It.IsAny<LocatorEvaluateOptions>()))
            .ReturnsAsync((object?)null);
        var frame = new Mock<IFrame>(MockBehavior.Strict);
        frame.Setup(item => item.Locator(
                It.Is<string>(selector => selector.Contains("Waiting room list")),
                It.IsAny<FrameLocatorOptions>()))
            .Returns(Collection(row.Row.Object).Object);
        var page = new Mock<IPage>();

        bool clicked = await new ZoomWaitingRoomDom().ClickParticipantAsync(
            new(page.Object, frame.Object),
            WebParticipantIdentity.Normalize("eyouth coordinator"),
            AdmitStrategy.ParticipantRowOnly);

        Assert.True(clicked);
        Assert.True(row.Hovered());
        notificationAdmit.Verify(
            item => item.EvaluateAsync<object?>(
                It.Is<string>(script => script.Contains("element.click()")),
                It.IsAny<object?>(),
                It.IsAny<LocatorEvaluateOptions>()),
            Times.Never);
        row.AdmitButton.Verify(
            item => item.EvaluateAsync<object?>(
                It.Is<string>(script => script.Contains("element.click()")),
                It.IsAny<object?>(),
                It.IsAny<LocatorEvaluateOptions>()),
            Times.Once);
    }

    [Fact]
    public async Task NotificationClickUsesDomClickWhenReactModalInterceptsPointerEvents()
    {
        var admit = Locator();
        admit.Setup(item => item.IsVisibleAsync(It.IsAny<LocatorIsVisibleOptions>())).ReturnsAsync(true);
        admit.Setup(item => item.InnerTextAsync(It.IsAny<LocatorInnerTextOptions>())).ReturnsAsync("Admit");
        admit.Setup(item => item.ClickAsync(It.IsAny<LocatorClickOptions>()))
            .ThrowsAsync(new PlaywrightException(
                "<div class=\"window-header\"> from <div class=\"ReactModalPortal\"> intercepts pointer events"));
        admit.Setup(item => item.EvaluateAsync<object?>(
                It.Is<string>(script => script.Contains("element.click()")),
                It.IsAny<object?>(),
                It.IsAny<LocatorEvaluateOptions>()))
            .ReturnsAsync((object?)null);
        var toast = NotificationToast("eyouth coordinator entered the waiting room", admit.Object);
        var frame = new Mock<IFrame>(MockBehavior.Strict);
        frame.Setup(item => item.Locator(
                It.Is<string>(selector => selector.Contains("notification-message-wrap__layer")),
                It.IsAny<FrameLocatorOptions>()))
            .Returns(Collection(toast.Object).Object);
        var page = new Mock<IPage>();

        bool clicked = await new ZoomWaitingRoomDom().ClickParticipantAsync(
            new(page.Object, frame.Object),
            WebParticipantIdentity.Normalize("eyouth coordinator"));

        Assert.True(clicked);
        admit.Verify(item => item.EvaluateAsync<object?>(
            It.Is<string>(script => script.Contains("element.click()")),
            It.IsAny<object?>(),
            It.IsAny<LocatorEvaluateOptions>()), Times.Once);
        admit.Verify(item => item.ClickAsync(
            It.Is<LocatorClickOptions>(options => options.Force == true)), Times.Never);
    }

    [Fact]
    public async Task NotificationClickUsesForceAfterPointerInterceptionAndDomClickFailure()
    {
        var admit = Locator();
        admit.Setup(item => item.IsVisibleAsync(It.IsAny<LocatorIsVisibleOptions>())).ReturnsAsync(true);
        admit.Setup(item => item.InnerTextAsync(It.IsAny<LocatorInnerTextOptions>())).ReturnsAsync("Admit");
        admit.Setup(item => item.ClickAsync(
                It.Is<LocatorClickOptions>(options => options.Force != true)))
            .ThrowsAsync(new PlaywrightException("ReactModalPortal intercepts pointer events"));
        admit.Setup(item => item.EvaluateAsync<object?>(
                It.IsAny<string>(),
                It.IsAny<object?>(),
                It.IsAny<LocatorEvaluateOptions>()))
            .ThrowsAsync(new PlaywrightException("Execution context was destroyed"));
        admit.Setup(item => item.ClickAsync(
                It.Is<LocatorClickOptions>(options => options.Force == true)))
            .Returns(Task.CompletedTask);
        var toast = NotificationToast("eyouth coordinator entered the waiting room", admit.Object);
        var frame = new Mock<IFrame>(MockBehavior.Strict);
        frame.Setup(item => item.Locator(
                It.Is<string>(selector => selector.Contains("notification-message-wrap__layer")),
                It.IsAny<FrameLocatorOptions>()))
            .Returns(Collection(toast.Object).Object);
        var page = new Mock<IPage>();

        bool clicked = await new ZoomWaitingRoomDom().ClickParticipantAsync(
            new(page.Object, frame.Object),
            WebParticipantIdentity.Normalize("eyouth coordinator"));

        Assert.True(clicked);
        admit.Verify(item => item.ClickAsync(
            It.Is<LocatorClickOptions>(options => options.Force == true)), Times.Once);
    }

    [Fact]
    public async Task MockedAdmitAllButtonIsClickedExactlyOnce()
    {
        var button = Locator();
        button.Setup(item => item.IsVisibleAsync(It.IsAny<LocatorIsVisibleOptions>())).ReturnsAsync(true);
        button.Setup(item => item.EvaluateAsync<object?>(
                It.Is<string>(script => script.Contains("element.click()")),
                It.IsAny<object?>(),
                It.IsAny<LocatorEvaluateOptions>()))
            .ReturnsAsync((object?)null);
        var buttons = Collection(button.Object);
        var frame = new Mock<IFrame>(MockBehavior.Strict);
        frame.Setup(item => item.GetByRole(
                AriaRole.Button,
                It.IsAny<FrameGetByRoleOptions>()))
            .Returns(buttons.Object);
        var page = new Mock<IPage>();

        bool clicked = await new ZoomWaitingRoomDom().ClickAdmitAllAsync(new(page.Object, frame.Object));

        Assert.True(clicked);
        button.Verify(
            item => item.EvaluateAsync<object?>(
                It.Is<string>(script => script.Contains("element.click()")),
                It.IsAny<object?>(),
                It.IsAny<LocatorEvaluateOptions>()),
            Times.Once);
    }

    private static ParticipantRowMock ParticipantRow(
        string rowText)
    {
        bool hovered = false;
        bool Hovered() => hovered;
        var admitButton = Locator();
        admitButton.Setup(item => item.WaitForAsync(It.IsAny<LocatorWaitForOptions>()))
            .Callback(() => Assert.True(
                Hovered(),
                "Admit visibility was checked before hovering its participant row."))
            .Returns(Task.CompletedTask);
        admitButton.Setup(item => item.IsVisibleAsync(It.IsAny<LocatorIsVisibleOptions>()))
            .ReturnsAsync(Hovered);
        var scopedButtons = Collection(admitButton.Object);
        scopedButtons.SetupGet(item => item.First).Returns(admitButton.Object);
        var row = Locator();
        row.Setup(item => item.EvaluateAsync<bool>(
                It.IsAny<string>(),
                It.IsAny<object?>(),
                It.IsAny<LocatorEvaluateOptions>()))
            .ReturnsAsync(true);
        row.Setup(item => item.EvaluateAsync<string>(
                It.IsAny<string>(),
                It.IsAny<object?>(),
                It.IsAny<LocatorEvaluateOptions>()))
            .ReturnsAsync(rowText);
        row.Setup(item => item.HoverAsync(It.IsAny<LocatorHoverOptions>()))
            .Callback(() => hovered = true)
            .Returns(Task.CompletedTask);
        row.Setup(item => item.GetByRole(
                AriaRole.Button,
                It.IsAny<LocatorGetByRoleOptions>()))
            .Returns(scopedButtons.Object);
        return new(row, admitButton, Hovered);
    }

    private static Mock<ILocator> NotificationToast(string text, ILocator admitButton)
    {
        var message = Locator();
        message.Setup(item => item.IsVisibleAsync(It.IsAny<LocatorIsVisibleOptions>())).ReturnsAsync(true);
        message.Setup(item => item.InnerTextAsync(It.IsAny<LocatorInnerTextOptions>())).ReturnsAsync(text);
        var toast = Locator();
        toast.Setup(item => item.IsVisibleAsync(It.IsAny<LocatorIsVisibleOptions>())).ReturnsAsync(true);
        toast.Setup(item => item.Locator(
                It.Is<string>(selector => selector.Contains("notification-message-wrap__txt")),
                It.IsAny<LocatorLocatorOptions>()))
            .Returns(Collection(message.Object).Object);
        toast.Setup(item => item.Locator(
                It.Is<string>(selector => selector.Contains("button.zmu-btn--primary")),
                It.IsAny<LocatorLocatorOptions>()))
            .Returns(Collection(admitButton).Object);
        return toast;
    }

    private static Mock<ILocator> Collection(params ILocator[] items)
    {
        var locator = Locator();
        locator.Setup(item => item.CountAsync()).ReturnsAsync(items.Length);
        locator.Setup(item => item.AllAsync()).ReturnsAsync(items);
        for (int index = 0; index < items.Length; index++)
        {
            int captured = index;
            locator.Setup(item => item.Nth(captured)).Returns(items[captured]);
        }
        return locator;
    }

    private static Mock<ILocator> Locator() => new(MockBehavior.Strict);

    private sealed record ParticipantRowMock(
        Mock<ILocator> Row,
        Mock<ILocator> AdmitButton,
        Func<bool> Hovered);
}
