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

    public static async Task<(bool Ended, string Message)> EndForAllAsync(IPage? page, CancellationToken token)
    {
        if (page == null || page.IsClosed) return (true, "The meeting page is already closed.");
        var surface = await new ZoomWebMeetingLocator().FindAsync(page);
        if (surface == null) return (true, "The meeting is no longer on the page.");
        if (!await PressAsync(surface.Frame, EndButton))
            return (false, "The web client shows no End button (is this account the host?); nothing was pressed.");

        for (int i = 0; i < 15; i++)
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
            foreach (var button in await frame.GetByRole(AriaRole.Button, new() { NameRegex = name }).AllAsync())
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
