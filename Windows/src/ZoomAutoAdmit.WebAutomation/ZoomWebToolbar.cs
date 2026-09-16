using Microsoft.Playwright;

namespace ZoomAutoAdmit.WebAutomation;

/// <summary>
/// Zoom's web meeting toolbar hides itself when the pointer stays still, and a hidden toolbar
/// exposes no End, no Participants and no Host tools at all - to a person or to automation. Moving
/// the page's own pointer brings it back; the person's real mouse is never touched.
/// </summary>
public static class ZoomWebToolbar
{
    public static async Task WakeAsync(IPage? page, CancellationToken token = default)
    {
        if (page == null || page.IsClosed) return;
        var size = page.ViewportSize;
        float width = size?.Width ?? 1280;
        float height = size?.Height ?? 720;
        try
        {
            for (int sweep = 0; sweep < 3; sweep++)
            {
                token.ThrowIfCancellationRequested();
                await page.Mouse.MoveAsync(width / 2, height * 0.55f + sweep * 10);
                await Task.Delay(120, token);
                await page.Mouse.MoveAsync(width / 2 + 40, height * 0.85f);
                await Task.Delay(120, token);
            }
        }
        catch (PlaywrightException)
        {
            // A page that navigated or closed mid-sweep simply has no toolbar to wake.
        }
    }
}
