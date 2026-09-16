using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace ZoomAutoAdmit.WebAutomation;

/// <summary>
/// Whether breakout rooms are open in Zoom's web client.
///
/// The main participants list leaves out whoever is inside a room, so a class can look empty while
/// everyone is a click away. "Close All Rooms" (and the broadcast control) only exist while rooms
/// are actually running - rooms that are merely prepared offer "Open All Rooms" - so that is what
/// is looked for. Nothing is pressed.
/// </summary>
public static class ZoomWebBreakoutRooms
{
    private static readonly Regex OpenNow = new(
        @"^\s*close\s+all\s+rooms\s*$|^\s*broadcast\s+(a\s+)?message\b|\bbreakout\s+rooms?\s*[-–—]\s*in\s+progress\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly AriaRole[] Roles = [AriaRole.Button, AriaRole.Menuitem, AriaRole.Heading];

    /// <summary>True only when the page shows that rooms are open; a page that cannot be read is false.</summary>
    public static async Task<bool> AreOpenAsync(IPage? page, CancellationToken token = default)
    {
        if (page is null || page.IsClosed) return false;
        try
        {
            foreach (var frame in page.Frames)
            {
                token.ThrowIfCancellationRequested();
                foreach (var role in Roles)
                    foreach (var element in await frame.GetByRole(role, new() { NameRegex = OpenNow }).AllAsync())
                        if (await element.IsVisibleAsync()) return true;
            }
        }
        catch (PlaywrightException) { }
        return false;
    }
}
