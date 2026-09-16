using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace ZoomAutoAdmit.WebAutomation;

/// <summary>
/// Whether Zoom's web client is saying the meeting is over ("This meeting has been ended by host").
/// Once a class's meeting has ended, nothing may join it again: the host joining its own meeting
/// starts it anew (S8, 2026-09-16 22:01 - the preview's "Join" was pressed after the class ended).
/// </summary>
public static class ZoomWebMeetingEnded
{
    public static readonly Regex EndedText = new(
        @"meeting has been ended|meeting has ended|host has ended (this|the) meeting|this meeting is over|meeting ended by (the )?host",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<bool> IsShownAsync(IBrowserContext context)
    {
        foreach (var page in context.Pages.Where(page => !page.IsClosed).ToArray())
        {
            foreach (var frame in page.Frames)
            {
                try
                {
                    string text = await frame.EvaluateAsync<string>("() => (document.body && document.body.innerText || '').slice(0, 20000)");
                    if (EndedText.IsMatch(text)) return true;
                }
                catch (PlaywrightException) { }
            }
        }
        return false;
    }
}
