using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace ZoomAutoAdmit.WebAutomation;

public sealed class ZoomWebMeetingLocator : IZoomWebMeetingLocator
{
    // Accessible names of controls only a joined meeting shows. Zoom's web client labels them by
    // purpose rather than by the visible word, so the participants button reads
    // "open the manage participants list pane,N particpants" and a host leaves through "End".
    // Matching the visible words alone made a joined meeting look like it never joined.
    private static readonly Regex MeetingControlPattern = new(
        @"^(?:End(?:\s+Meeting)?|Leave(?:\s+Meeting)?|Participants|Host\s+tools)$" +
        @"|manage\s+participants|participants\s+list|participants\s+pane|security\s+options",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<ZoomMeetingSurface?> FindAsync(IBrowserContext context)
    {
        foreach (var page in context.Pages.Where(page => !page.IsClosed).ToArray())
        {
            var surface = await FindAsync(page);
            if (surface != null) return surface;
        }

        return null;
    }

    public async Task<ZoomMeetingSurface?> FindAsync(IPage page)
    {
        if (page.IsClosed) return null;
        string title = await SafeTitleAsync(page);
        var meetingFrames = page.Frames
            .Where(frame => IsPotentialZoomMeeting(page.Url, frame.Url, title))
            .ToArray();

        foreach (var frame in meetingFrames)
        {
            try
            {
                if (await ZoomWaitingRoomDom.HasWaitingRoomHeaderAsync(frame))
                    return new ZoomMeetingSurface(page, frame);
            }
            catch (PlaywrightException ex) when (PlaywrightNavigationFailurePolicy.IsTransient(ex))
            {
                // Zoom replaces frames during SPA updates. The next poll reacquires
                // the current frame while retaining this same page.
            }
        }

        foreach (var frame in meetingFrames)
        {
            try
            {
                if (await HasVisibleHostControlAsync(frame))
                    return new ZoomMeetingSurface(page, frame);
            }
            catch (PlaywrightException ex) when (PlaywrightNavigationFailurePolicy.IsTransient(ex))
            {
                // Zoom replaces frames during SPA updates. The next poll reacquires
                // the current frame while retaining this same page.
            }
        }

        // A visible arrival notification is proof of a live meeting even when Zoom has hidden the
        // toolbar, and it is the surface admission would act on anyway.
        foreach (var frame in meetingFrames)
        {
            try
            {
                if (await ZoomWaitingRoomDom.HasVisibleArrivalNotificationAsync(frame))
                    return new ZoomMeetingSurface(page, frame);
            }
            catch (PlaywrightException ex) when (PlaywrightNavigationFailurePolicy.IsTransient(ex))
            {
            }
        }

        return null;
    }

    private static bool IsPotentialZoomMeeting(string pageUrl, string frameUrl, string title)
    {
        string combinedUrl = $"{pageUrl} {frameUrl}";
        bool zoomHost = combinedUrl.Contains("zoom.us", StringComparison.OrdinalIgnoreCase) ||
                        combinedUrl.Contains("zoom.com", StringComparison.OrdinalIgnoreCase);
        bool meetingPath = combinedUrl.Contains("/wc/", StringComparison.OrdinalIgnoreCase) ||
                           combinedUrl.Contains("/j/", StringComparison.OrdinalIgnoreCase) ||
                           combinedUrl.Contains("/meeting", StringComparison.OrdinalIgnoreCase);
        bool meetingTitle = title.Contains("Zoom Meeting", StringComparison.OrdinalIgnoreCase);
        return zoomHost && (meetingPath || meetingTitle);
    }

    public static async Task<bool> HasVisibleHostControlAsync(IFrame frame)
    {
        var controls = frame.GetByRole(AriaRole.Button, new() { NameRegex = MeetingControlPattern });
        foreach (var control in await controls.AllAsync())
        {
            if (await control.IsVisibleAsync()) return true;
        }
        return false;
    }

    private static async Task<string> SafeTitleAsync(IPage page)
    {
        try { return await page.TitleAsync(); }
        catch (PlaywrightException ex) when (PlaywrightNavigationFailurePolicy.IsTransient(ex))
        {
            return string.Empty;
        }
    }
}
