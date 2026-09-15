using Microsoft.Playwright;
using Moq;
using ZoomAutoAdmit.WebAutomation.Browser;
using Xunit;

namespace ZoomAutoAdmit.WebAutomation.Tests;

public class ZoomWebMeetingControllerTests
{
    [Theory]
    [InlineData("http://example.zoom.us/j/123")]
    [InlineData("https://example.com/j/123")]
    [InlineData("not-a-url")]
    public void NonHttpsOrNonZoomMeetingUrlsAreRejected(string value)
    {
        Assert.Throws<ArgumentException>(() => ZoomWebMeetingController.ValidateMeetingUrl(value));
    }

    [Theory]
    [InlineData("https://zoom.us/j/91473108490", "https://zoom.us/j/91473108490")]
    [InlineData("https://zoom.us/91473108490", "https://zoom.us/j/91473108490")]                          // the "j" forgotten
    [InlineData("https://us06web.zoom.us/wc/join/91473108490?pwd=abc", "https://us06web.zoom.us/j/91473108490?pwd=abc")]
    [InlineData("https://zoom.us/wc/91473108490/join", "https://zoom.us/j/91473108490")]
    [InlineData("https://zoom.us/my/some.teacher", "https://zoom.us/my/some.teacher")]                     // not a meeting number
    [InlineData("https://zoom.us/s/96059366847", "https://zoom.us/j/96059366847")]                          // a host's start link
    [InlineData("https://zoom.us/s/96059366847?zak=secret&pwd=abc#success", "https://zoom.us/j/96059366847?pwd=abc")]
    public void MeetingLinksAlwaysOpenInTheirJForm(string link, string expected)
    {
        Assert.Equal(expected, ZoomWebMeetingController.ValidateMeetingUrl(link).AbsoluteUri);
    }

    [Fact]
    public async Task ManagedContextOpensMeetingUrlWithoutExistingChrome()
    {
        const string meetingUrl = "https://example.zoom.us/j/123456789?pwd=secret";
        var page = new Mock<IPage>(MockBehavior.Strict);
        page.SetupGet(item => item.IsClosed).Returns(false);
        page.SetupGet(item => item.Url).Returns("about:blank");
        page.Setup(item => item.GotoAsync(
                meetingUrl,
                It.Is<PageGotoOptions>(options =>
                    options.WaitUntil == WaitUntilState.DOMContentLoaded && options.Timeout == 30000)))
            .ReturnsAsync((IResponse?)null);
        var context = new Mock<IBrowserContext>(MockBehavior.Strict);
        context.SetupGet(item => item.Pages).Returns([page.Object]);

        var opened = await ZoomWebMeetingController.OpenMeetingPageAsync(
            context.Object,
            new Uri(meetingUrl));

        Assert.Same(page.Object, opened);
        page.VerifyAll();
    }

    [Fact]
    public async Task ExistingLoginPageIsReusedInsteadOfCreatingSecondTab()
    {
        const string meetingUrl = "https://example.zoom.us/j/123456789";
        var page = new Mock<IPage>(MockBehavior.Strict);
        page.SetupGet(item => item.IsClosed).Returns(false);
        page.SetupGet(item => item.Url).Returns("https://example.zoom.us/signin");
        page.Setup(item => item.GotoAsync(
                meetingUrl,
                It.IsAny<PageGotoOptions>()))
            .ReturnsAsync((IResponse?)null);
        var context = new Mock<IBrowserContext>(MockBehavior.Strict);
        context.SetupGet(item => item.Pages).Returns([page.Object]);

        var opened = await ZoomWebMeetingController.OpenMeetingPageAsync(
            context.Object,
            new Uri(meetingUrl));

        Assert.Same(page.Object, opened);
        context.Verify(item => item.NewPageAsync(), Times.Never);
    }

    /// <summary>A meeting frame that shows the host's "End" button, or only what a guest has.</summary>
    private static Mock<IFrame> MeetingFrame(bool host)
    {
        var frame = new Mock<IFrame>();
        var end = new Mock<ILocator>();
        end.Setup(b => b.IsVisibleAsync(It.IsAny<LocatorIsVisibleOptions>())).ReturnsAsync(true);
        var buttons = new Mock<ILocator>();
        buttons.Setup(b => b.AllAsync()).ReturnsAsync(host ? [end.Object] : []);
        frame.Setup(f => f.GetByRole(AriaRole.Button, It.IsAny<FrameGetByRoleOptions>())).Returns(buttons.Object);
        return frame;
    }

    [Fact]
    public async Task AGuestJoinIsNotRememberedAsASignedInProfile()
    {
        string profilesRoot = Path.Combine(Path.GetTempPath(), $"zoom-web-guest-{Guid.NewGuid():N}");
        try
        {
            const string meetingUrl = "https://example.zoom.us/j/123456789";
            var page = new Mock<IPage>();
            page.SetupGet(item => item.IsClosed).Returns(false);
            page.SetupGet(item => item.Url).Returns(meetingUrl);
            var context = new Mock<IBrowserContext>();
            context.SetupGet(item => item.Pages).Returns([page.Object]);
            var profileManager = new ZoomProfileManager(profilesRoot);
            var session = new ZoomBrowserSession(new Mock<IPlaywright>().Object, context.Object,
                new ZoomBrowserLaunchPlan(profileManager.GetOrCreate("new-account"), Headless: false));
            var surface = new ZoomMeetingSurface(page.Object, MeetingFrame(host: false).Object);
            var controller = new ZoomWebMeetingController(new SequenceMeetingLocator(surface), (_, _) => Task.CompletedTask);

            Assert.Same(surface, await controller.OpenAndWaitForHostControlsAsync(session, meetingUrl, profileManager, CancellationToken.None));
            Assert.False(session.Profile.HasReusableSession);     // it stays "sign in first" for the next launch
        }
        finally
        {
            if (Directory.Exists(profilesRoot)) Directory.Delete(profilesRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ManualLoginSurvivesDetachedFrameAndReacquiresJoinedMeeting()
    {
        string profilesRoot = Path.Combine(Path.GetTempPath(), $"zoom-web-login-{Guid.NewGuid():N}");
        try
        {
            const string meetingUrl = "https://example.zoom.us/j/123456789";
            var page = new Mock<IPage>();
            page.SetupGet(item => item.IsClosed).Returns(false);
            page.SetupGet(item => item.Url).Returns(meetingUrl);
            var frame = MeetingFrame(host: true);
            var context = new Mock<IBrowserContext>();
            context.SetupGet(item => item.Pages).Returns([page.Object]);
            var playwright = new Mock<IPlaywright>();
            var profileManager = new ZoomProfileManager(profilesRoot);
            var profile = profileManager.GetOrCreate("manual-login");
            var session = new ZoomBrowserSession(
                playwright.Object,
                context.Object,
                new ZoomBrowserLaunchPlan(profile, Headless: false));
            var expectedSurface = new ZoomMeetingSurface(page.Object, frame.Object);
            var locator = new SequenceMeetingLocator(
                new PlaywrightException("Frame was detached"),
                null,
                expectedSurface);
            var delays = new List<TimeSpan>();
            var controller = new ZoomWebMeetingController(
                locator,
                (delay, _) =>
                {
                    delays.Add(delay);
                    return Task.CompletedTask;
                });

            var surface = await controller.OpenAndWaitForHostControlsAsync(
                session,
                meetingUrl,
                profileManager,
                CancellationToken.None);

            Assert.Same(expectedSurface, surface);
            Assert.True(session.Profile.HasReusableSession);
            Assert.True(File.Exists(session.Profile.ReadyMarkerPath));
            Assert.Equal(2, delays.Count);
            Assert.All(delays, delay => Assert.Equal(TimeSpan.FromSeconds(2), delay));
        }
        finally
        {
            if (Directory.Exists(profilesRoot)) Directory.Delete(profilesRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("Frame was detached")]
    [InlineData("Execution context was destroyed, most likely because of a navigation")]
    [InlineData("Target page has been closed")]
    public void NavigationFailuresAreClassifiedAsTransient(string message)
    {
        Assert.True(PlaywrightNavigationFailurePolicy.IsTransient(new PlaywrightException(message)));
    }

    [Fact]
    public void BrowserClosureIsNotClassifiedAsTransient()
    {
        Assert.False(PlaywrightNavigationFailurePolicy.IsTransient(
            new PlaywrightException("Target page, context or browser has been closed")));
    }

    [Fact]
    public async Task OpenAuthoritativeMeetingPageIsNotReplacedWhenDomIsTemporarilyUnavailable()
    {
        var page = new Mock<IPage>();
        page.SetupGet(item => item.IsClosed).Returns(false);
        page.SetupGet(item => item.Url).Returns("https://example.zoom.us/wc/123/start");
        var context = new Mock<IBrowserContext>();
        context.SetupGet(item => item.Pages).Returns([page.Object]);
        var playwright = new Mock<IPlaywright>();
        var profile = new ZoomBrowserProfile("test", "C:\\test", "C:\\test\\ready", true);
        var session = new ZoomBrowserSession(
            playwright.Object,
            context.Object,
            new ZoomBrowserLaunchPlan(profile, Headless: false));
        await session.SelectMeetingPageAsync(page.Object);
        var locator = new OwnedPageMeetingLocator();
        var controller = new ZoomWebMeetingController(locator);

        var surface = await controller.FindActiveMeetingAsync(session);

        Assert.Null(surface);
        Assert.Same(page.Object, session.ActiveMeetingPage);
        Assert.True(ZoomWebMeetingController.HasOpenMeetingPage(session));
        Assert.Equal(1, locator.PagePolls);
        Assert.Equal(0, locator.ContextPolls);
    }

    private sealed class SequenceMeetingLocator(params object?[] results) : IZoomWebMeetingLocator
    {
        private readonly Queue<object?> _results = new(results);

        public Task<ZoomMeetingSurface?> FindAsync(IBrowserContext context)
        {
            object? result = _results.Dequeue();
            if (result is Exception exception) throw exception;
            return Task.FromResult((ZoomMeetingSurface?)result);
        }

        public Task<ZoomMeetingSurface?> FindAsync(IPage page)
        {
            object? result = _results.Dequeue();
            if (result is Exception exception) throw exception;
            return Task.FromResult((ZoomMeetingSurface?)result);
        }
    }

    private sealed class OwnedPageMeetingLocator : IZoomWebMeetingLocator
    {
        public int ContextPolls { get; private set; }
        public int PagePolls { get; private set; }

        public Task<ZoomMeetingSurface?> FindAsync(IBrowserContext context)
        {
            ContextPolls++;
            return Task.FromResult<ZoomMeetingSurface?>(null);
        }

        public Task<ZoomMeetingSurface?> FindAsync(IPage page)
        {
            PagePolls++;
            return Task.FromResult<ZoomMeetingSurface?>(null);
        }
    }
}
