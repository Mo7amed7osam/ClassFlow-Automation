using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace ZoomAutoAdmit.WebAutomation;

/// <summary>
/// "End Meeting for All" in Zoom's web client, as the host: the footer's End, then the dialog's
/// "End Meeting for All". "Leave Meeting" is never pressed.
/// </summary>
public static class ZoomWebMeetingEnder
{
    private static readonly Regex EndButton = new(@"^\s*end(\s+meeting)?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EndForAllButton = new(@"^\s*end\s+meeting\s+for\s+all\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // When the app's browser rejoins a meeting its own account already hosts elsewhere, Zoom makes it
    // a co-host and offers the host role back. A co-host has no End at all.
    private static readonly Regex ReclaimHostButton = new(@"^\s*reclaim\s+host\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Zoom's confirmation puts role="menuitem" on real <button> elements, which hides them from a
    // search for buttons: both roles have to be looked at (verified live, 2026-09-16).
    private static readonly AriaRole[] PressableRoles = [AriaRole.Button, AriaRole.Menuitem];

    public static async Task<(bool Ended, string Message)> EndForAllAsync(IPage? page, CancellationToken token)
    {
        if (page == null || page.IsClosed) return (true, "The meeting page is already closed.");
        // A class nobody is watching has an auto-hidden toolbar, and a hidden End cannot be pressed.
        await ZoomWebToolbar.WakeAsync(page, token);
        var surface = await new ZoomWebMeetingLocator().FindAsync(page);
        if (surface == null) return (true, "The meeting is no longer on the page.");
        if (!await PressAsync(surface.Frame, EndButton))
        {
            bool reclaimed = false;
            foreach (var frame in page.Frames)
                if (await PressAsync(frame, ReclaimHostButton)) { reclaimed = true; break; }
            if (!reclaimed)
                return (false, "The web client shows no End button (is this account the host?); nothing was pressed.");
            await Task.Delay(5000, token);
            await ZoomWebToolbar.WakeAsync(page, token);
            surface = await new ZoomWebMeetingLocator().FindAsync(page);
            if (surface == null) return (true, "The meeting is no longer on the page.");
            if (!await PressAsync(surface.Frame, EndButton))
                return (false, "The host role did not come back after \"Reclaim Host\"; nothing was ended.");
        }

        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(300, token);
            foreach (var frame in page.Frames)
                if (await PressAsync(frame, EndForAllButton))
                {
                    for (int j = 0; j < 20; j++)
                    {
                        await Task.Delay(500, token);
                        if (page.IsClosed || await new ZoomWebMeetingLocator().FindAsync(page) == null)
                            return (true, "The meeting was ended for everyone.");
                    }
                    return (false, "\"End Meeting for All\" was pressed but the meeting is still on the page.");
                }
        }
        return (false, "\"End Meeting for All\" did not appear; nothing else was pressed.");
    }

    private static async Task<bool> PressAsync(IFrame frame, Regex name)
    {
        try
        {
            foreach (var role in PressableRoles)
            foreach (var button in await frame.GetByRole(role, new() { NameRegex = name }).AllAsync())
            {
                if (!await button.IsVisibleAsync()) continue;
                try { await button.EvaluateAsync<object?>("element => element.click()"); }
                catch (PlaywrightException) { await button.ClickAsync(new() { Timeout = 2000, Force = true }); }
                return true;
            }
        }
        catch (PlaywrightException) { }
        return false;
    }
}
