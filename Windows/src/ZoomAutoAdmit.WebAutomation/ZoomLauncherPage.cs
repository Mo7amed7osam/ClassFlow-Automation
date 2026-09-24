using System.Text.RegularExpressions;
using Microsoft.Playwright;
using ZoomAutoAdmit.Core.Formatting;

namespace ZoomAutoAdmit.WebAutomation;

/// <summary>
/// Zoom's "open the Zoom app" page (zoom.us/j/… or /s/…: "Join from Zoom Workplace app" /
/// "Join from browser", often under Chrome's "Open Zoom Meetings?" prompt). The Web engine never
/// wants the app: it presses "Join from browser" itself, the way a person does. The prompt belongs
/// to the browser window, not the page, so the page underneath can still be pressed; once the page
/// moves on, the prompt goes with it.
/// </summary>
public static class ZoomLauncherPage
{
    private static readonly Regex JoinFromBrowser = new(@"^\s*(join|start)\s+from\s+(your\s+)?browser\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LaunchMeeting = new(@"^\s*launch\s+meeting\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True when "Join from browser" (or, on older pages, "Launch Meeting") was pressed.</summary>
    public static async Task<bool> TryJoinFromBrowserAsync(IBrowserContext context)
    {
        foreach (var page in context.Pages.Where(p => !p.IsClosed && IsZoom(p.Url)).ToArray())
            if (await TryJoinFromBrowserAsync(page)) return true;
        return false;
    }

    public static async Task<bool> TryJoinFromBrowserAsync(IPage page)
    {
        if (page.IsClosed || !IsZoom(page.Url) || page.Url.Contains("/wc/", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            if (await PressAsync(page, JoinFromBrowser)) { ConsoleLogger.Info("WEB_LAUNCHER_PAGE: pressed \"Join from browser\"."); return true; }
            // Older launcher pages show "Join from your browser" only after "Launch Meeting".
            if (await PressAsync(page, LaunchMeeting)) { ConsoleLogger.Info("WEB_LAUNCHER_PAGE: pressed \"Launch Meeting\" to show the browser link."); return true; }
        }
        catch (PlaywrightException ex) when (PlaywrightNavigationFailurePolicy.IsTransient(ex)) { }
        catch (PlaywrightException ex) { ConsoleLogger.Debug($"WEB_LAUNCHER_PAGE: {ex.Message}"); }
        return false;
    }

    private static readonly Regex PreviewJoin = new(@"^\s*join\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// What Zoom offers when a join did not go through: "Retry", "Try again", "Rejoin", "Reconnect".
    /// A person presses it; so does this. Nothing else is pressed - "Leave", "End" and the rest are
    /// decisions, not retries.
    /// </summary>
    private static readonly Regex TryAgain = new(
        @"^\s*(retry|try\s+again|rejoin|re-?connect|join\s+again)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Zoom's own "the meeting could not be joined" panel: "Joining Meeting Timeout or Browser
    /// restriction", "Your network connection has timed out". It sits over the meeting, so the
    /// participants list is gone with it and nothing can be admitted or counted until it is away.
    /// </summary>
    private static readonly Regex JoinFailedText = new(
        @"joining meeting timeout|connection has timed out|unable to (join|connect)|connection (failed|lost)|reconnecting",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Whether a page's own words are Zoom saying the meeting could not be joined.</summary>
    internal static bool LooksLikeJoinFailed(string? pageText) =>
        pageText is { Length: > 0 } text && JoinFailedText.IsMatch(text);

    /// <summary>Whether Zoom is showing that panel right now.</summary>
    public static async Task<bool> IsJoinFailedShownAsync(IBrowserContext context)
    {
        foreach (var page in context.Pages.Where(p => !p.IsClosed && IsZoom(p.Url)).ToArray())
            foreach (var frame in page.Frames)
            {
                try
                {
                    string text = await frame.Locator("body").InnerTextAsync(new() { Timeout = 2000 });
                    if (LooksLikeJoinFailed(text)) return true;
                }
                catch (PlaywrightException) { }
                catch (TimeoutException) { }
            }
        return false;
    }

    /// <summary>Presses Zoom's own "Retry" when a join failed. True when there was one to press.</summary>
    public static async Task<bool> TryPressRetryAsync(IBrowserContext context)
    {
        foreach (var page in context.Pages.Where(p => !p.IsClosed && IsZoom(p.Url)).ToArray())
            foreach (var frame in page.Frames)
            {
                try
                {
                    foreach (var button in await frame.GetByRole(AriaRole.Button, new() { NameRegex = TryAgain }).AllAsync())
                    {
                        if (!await button.IsVisibleAsync() || !await button.IsEnabledAsync()) continue;
                        await button.EvaluateAsync<object?>("element => element.click()");
                        ConsoleLogger.Info($"WEB_RETRY: pressed \"{(await button.InnerTextAsync()).Trim()}\".");
                        return true;
                    }
                }
                catch (PlaywrightException) { }
            }
        return false;
    }

    /// <summary>
    /// The web client's preview before joining (camera, mic, name, "Join"): pressed once the name is
    /// filled, so a signed-in profile goes straight into its meeting.
    /// </summary>
    public static async Task<bool> TryPressJoinOnPreviewAsync(IBrowserContext context)
    {
        foreach (var page in context.Pages.Where(p => !p.IsClosed && p.Url.Contains("/wc/", StringComparison.OrdinalIgnoreCase)).ToArray())
            foreach (var frame in page.Frames)
            {
                try
                {
                    foreach (var button in await frame.GetByRole(AriaRole.Button, new() { NameRegex = PreviewJoin }).AllAsync())
                    {
                        if (!await button.IsVisibleAsync() || !await button.IsEnabledAsync()) continue;
                        await button.EvaluateAsync<object?>("element => element.click()");
                        ConsoleLogger.Info("WEB_PREVIEW: pressed \"Join\".");
                        return true;
                    }
                }
                catch (PlaywrightException) { }
            }
        return false;
    }

    private static async Task<bool> PressAsync(IPage page, Regex name)
    {
        foreach (var role in new[] { AriaRole.Link, AriaRole.Button })
            foreach (var element in await page.GetByRole(role, new() { NameRegex = name }).AllAsync())
            {
                if (!await element.IsVisibleAsync()) continue;
                try { await element.EvaluateAsync<object?>("element => element.click()"); }
                catch (PlaywrightException) { await element.ClickAsync(new() { Timeout = 3000, Force = true }); }
                return true;
            }
        return false;
    }

    private static bool IsZoom(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Host.Equals("zoom.us", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".zoom.us", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("zoom.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".zoom.com", StringComparison.OrdinalIgnoreCase));
}
