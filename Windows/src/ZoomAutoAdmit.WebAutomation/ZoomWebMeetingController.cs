using Microsoft.Playwright;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.WebAutomation.Browser;

namespace ZoomAutoAdmit.WebAutomation;

/// <summary>Raised when a Zoom Web profile needs someone to sign in before it can host.</summary>
public sealed class ZoomWebSignInRequiredException(string profileName, string message)
    : InvalidOperationException(message)
{
    public string ProfileName { get; } = profileName;
}

public sealed class ZoomWebMeetingController
{
    // Zoom's PWA meeting page loads its client frame well after the first paint; 30 s used to
    // expire while a perfectly good saved session was still connecting.
    private static readonly TimeSpan HeadlessStartupTimeout = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan ManualLoginPollInterval = TimeSpan.FromSeconds(2);
    private readonly IZoomWebMeetingLocator _locator;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public ZoomWebMeetingController(
        IZoomWebMeetingLocator? locator = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _locator = locator ?? new ZoomWebMeetingLocator();
        _delay = delay ?? Task.Delay;
    }

    /// <param name="manualLoginTimeout">
    /// How long a visible browser waits for someone to sign in. Null waits indefinitely, which is
    /// right when a person asked for the browser; a scheduled run passes a limit so it can report
    /// instead of hanging.
    /// </param>
    public async Task<ZoomMeetingSurface> OpenAndWaitForHostControlsAsync(
        ZoomBrowserSession session,
        string meetingUrl,
        ZoomProfileManager profileManager,
        CancellationToken cancellationToken,
        TimeSpan? manualLoginTimeout = null)
    {
        Uri validatedUrl = ValidateMeetingUrl(meetingUrl);
        var openingPage = await OpenMeetingPageAsync(session.Context, validatedUrl);
        ConsoleLogger.Success("WEB_MEETING_OPENED");
        bool waitingForManualLogin = !session.Profile.HasReusableSession;
        if (waitingForManualLogin)
        {
            ConsoleLogger.Info("WEB_LOGIN_REQUIRED: Complete Zoom login and join the meeting in the visible managed browser.");
            ConsoleLogger.Info("Waiting for manual login...");
        }

        DateTimeOffset? deadline = session.IsHeadless
            ? DateTimeOffset.UtcNow + HeadlessStartupTimeout
            : manualLoginTimeout.HasValue
                ? DateTimeOffset.UtcNow + manualLoginTimeout.Value
                : null;
        var nextLauncherCheck = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
        while (!cancellationToken.IsCancellationRequested)
        {
            ZoomMeetingSurface? surface;
            try
            {
                surface = openingPage.IsClosed
                    ? await _locator.FindAsync(session.Context)
                    : await _locator.FindAsync(openingPage);
            }
            catch (PlaywrightException ex) when (PlaywrightNavigationFailurePolicy.IsTransient(ex))
            {
                ConsoleLogger.Info("WEB_NAVIGATION_RETRY: Zoom changed pages or frames; reconnecting.");
                await _delay(ManualLoginPollInterval, cancellationToken);
                continue;
            }

            if (surface != null)
            {
                ConsoleLogger.Success("Login detected");
                ConsoleLogger.Success("Meeting joined");
                await session.SelectMeetingPageAsync(
                    surface.Page,
                    rediscovered: !ReferenceEquals(surface.Page, openingPage));
                // Only a join as the host proves the profile is signed in; a guest join (a profile
                // nobody signed in) must not be remembered as ready, or it would stay a guest.
                if (!session.Profile.HasReusableSession)
                {
                    // The host proof is the End button, which an auto-hidden toolbar does not show.
                    await ZoomWebToolbar.WakeAsync(surface.Page, cancellationToken);
                    if (await ZoomWebMeetingLocator.IsHostAsync(surface.Frame))
                        session.Profile = profileManager.MarkSessionReady(session.Profile);
                    else ConsoleLogger.Warn($"WEB_JOINED_AS_GUEST: profile '{session.Profile.Name}' is in the meeting but not as its host; sign it in to Zoom.");
                }
                return surface;
            }

            // Zoom's "open the app" page: press "Join from browser", as a person would.
            if (DateTimeOffset.UtcNow >= nextLauncherCheck)
            {
                nextLauncherCheck = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(4);
                try
                {
                    if (!await ZoomLauncherPage.TryJoinFromBrowserAsync(session.Context))
                        await ZoomLauncherPage.TryPressJoinOnPreviewAsync(session.Context);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { ConsoleLogger.Debug($"WEB_LAUNCHER_PAGE: {ex.Message}"); }
            }

            if (deadline != null && DateTimeOffset.UtcNow >= deadline.Value)
                throw new ZoomWebSignInRequiredException(
                    session.Profile.Name,
                    session.IsHeadless
                        ? $"The saved Zoom sign-in for profile '{session.Profile.Name}' is no longer accepted."
                        : $"Nobody signed in to Zoom for profile '{session.Profile.Name}' within the allowed time.");
            await _delay(
                session.IsHeadless ? TimeSpan.FromMilliseconds(500) : ManualLoginPollInterval,
                cancellationToken);
        }
        throw new OperationCanceledException(cancellationToken);
    }

    public async Task<ZoomMeetingSurface?> FindActiveMeetingAsync(ZoomBrowserSession session)
    {
        var activePage = session.ActiveMeetingPage;
        if (activePage != null && !activePage.IsClosed)
            return await _locator.FindAsync(activePage);

        var rediscovered = await _locator.FindAsync(session.Context);
        if (rediscovered == null) return null;
        await session.SelectMeetingPageAsync(rediscovered.Page, rediscovered: session.MeetingPageWasSelected);
        return rediscovered;
    }

    public static bool HasOpenMeetingPage(ZoomBrowserSession session) =>
        session.ActiveMeetingPage is { IsClosed: false };

    public async Task KeepMeetingPageAliveAsync(
        ZoomBrowserSession session,
        string meetingUrl)
    {
        Uri validatedUrl = ValidateMeetingUrl(meetingUrl);
        if (HasOpenMeetingPage(session)) return;
        var rediscovered = await _locator.FindAsync(session.Context);
        if (rediscovered != null)
        {
            await session.SelectMeetingPageAsync(rediscovered.Page, rediscovered: true);
            return;
        }
        bool hasOpenZoomPage = session.Context.Pages.Any(page =>
            !page.IsClosed && IsSameMeetingAddress(page.Url, validatedUrl));
        if (!hasOpenZoomPage) await OpenMeetingPageAsync(session.Context, validatedUrl);
    }

    public static Uri ValidateMeetingUrl(string meetingUrl)
    {
        if (!Uri.TryCreate(meetingUrl, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("--meeting-url must be an absolute HTTPS Zoom meeting URL.", nameof(meetingUrl));
        bool zoomHost = uri.Host.Equals("zoom.us", StringComparison.OrdinalIgnoreCase) ||
                        uri.Host.EndsWith(".zoom.us", StringComparison.OrdinalIgnoreCase) ||
                        uri.Host.Equals("zoom.com", StringComparison.OrdinalIgnoreCase) ||
                        uri.Host.EndsWith(".zoom.com", StringComparison.OrdinalIgnoreCase);
        if (!zoomHost)
            throw new ArgumentException("--meeting-url must use a Zoom domain.", nameof(meetingUrl));
        return JoinForm(uri);
    }

    private static readonly System.Text.RegularExpressions.Regex MeetingNumber = new(@"^\d{9,12}$");

    /// <summary>
    /// A meeting's link in its "/j/&lt;number&gt;" form, which the Web client needs (the user's rule:
    /// the "j" must be there): "zoom.us/91473108490", "/wc/join/91473108490" and
    /// "/wc/91473108490/join" all become "zoom.us/j/91473108490", keeping the passcode (?pwd=…).
    /// Any other link (personal /my/ links, /s/ start links) is left as it is.
    /// </summary>
    public static Uri JoinForm(Uri uri)
    {
        string[] parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? number = parts switch
        {
            [var n] when MeetingNumber.IsMatch(n) => n,
            ["wc", "join", var n] when MeetingNumber.IsMatch(n) => n,
            ["wc", var n, "join" or "start"] when MeetingNumber.IsMatch(n) => n,
            // A host's start link opens Zoom's "open the app" page; the signed-in profile joins
            // through the same meeting's /j/ link as its host.
            ["s", var n] when MeetingNumber.IsMatch(n) => n,
            _ => null,
        };
        if (number == null) return uri;
        // Keep the passcode; a start link's one-time host token (zak) is never carried over.
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        query.Remove("zak");
        string rest = query.Count == 0 ? "" : "?" + query;
        return new UriBuilder(uri) { Path = "/j/" + number, Query = rest.TrimStart('?'), Fragment = "" }.Uri;
    }

    public static async Task<IPage> OpenMeetingPageAsync(IBrowserContext context, Uri meetingUrl)
    {
        var page = context.Pages.FirstOrDefault(candidate =>
            !candidate.IsClosed && IsSameMeetingAddress(candidate.Url, meetingUrl));
        page ??= context.Pages.FirstOrDefault(candidate =>
            !candidate.IsClosed && candidate.Url.Equals("about:blank", StringComparison.OrdinalIgnoreCase));
        page ??= context.Pages.FirstOrDefault(candidate => !candidate.IsClosed);
        page ??= await context.NewPageAsync();
        if (!IsSameMeetingAddress(page.Url, meetingUrl))
        {
            try
            {
                await page.GotoAsync(
                    meetingUrl.AbsoluteUri,
                    new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 30000
                    });
            }
            catch (PlaywrightException ex) when (PlaywrightNavigationFailurePolicy.IsTransient(ex))
            {
                // Login and meeting-join redirects can replace the page/frame while Goto is
                // awaiting DOMContentLoaded. The startup loop reacquires the current page.
            }
        }
        return page;
    }

    private static bool IsSameMeetingAddress(string candidateUrl, Uri meetingUrl)
    {
        if (!Uri.TryCreate(candidateUrl, UriKind.Absolute, out var candidate)) return false;
        return candidate.Scheme.Equals(meetingUrl.Scheme, StringComparison.OrdinalIgnoreCase) &&
               candidate.Host.Equals(meetingUrl.Host, StringComparison.OrdinalIgnoreCase) &&
               candidate.AbsolutePath.TrimEnd('/').Equals(
                   meetingUrl.AbsolutePath.TrimEnd('/'),
                   StringComparison.OrdinalIgnoreCase);
    }
}
