using Microsoft.Playwright;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Models;
using ZoomAutoAdmit.WebAutomation.Browser;

namespace ZoomAutoAdmit.Inspector.Commands;

/// <summary>
/// Read-only diagnostic for the Zoom Web client. It opens a meeting in a managed profile and
/// prints every frame, every button and every waiting-room text it can see, so the join and
/// admission selectors can be matched against what Zoom actually renders. It clicks nothing.
/// </summary>
public static class WebDomProbeCommand
{
    public static async Task<int> ExecuteAsync(CliOptions options, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.MeetingUrl))
        {
            ConsoleLogger.Error("web-dom-probe requires --meeting-url <Zoom URL> and --profile <name>.");
            return 1;
        }

        int settleSeconds = options.TimeoutExplicitlySet && options.TimeoutSeconds > 0 ? options.TimeoutSeconds : 25;
        var profileManager = new ZoomProfileManager();
        var profile = profileManager.GetOrCreate(options.WebProfile);
        ConsoleLogger.Info($"Profile: {profile.Name} ({profile.DirectoryPath})");
        ConsoleLogger.Info($"Saved sign-in marker: {profile.HasReusableSession}");

        var plan = profileManager.CreateLaunchPlan(profile, forceHeaded: true);
        await using var session = await new ZoomBrowserLauncher().LaunchAsync(plan, cancellationToken);
        var page = session.Context.Pages.Count > 0
            ? session.Context.Pages[0]
            : await session.Context.NewPageAsync();
        await page.GotoAsync(options.MeetingUrl!, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        ConsoleLogger.Info($"Opened {options.MeetingUrl}; letting the page settle for {settleSeconds}s...");
        await Task.Delay(TimeSpan.FromSeconds(settleSeconds), cancellationToken);

        // --query opens one thing before the dump, for a panel that only exists once it is asked
        // for - a share dialog, say. It is the single click this command is allowed to make.
        if (!string.IsNullOrWhiteSpace(options.Query))
        {
            try
            {
                var target = page.Locator($"[aria-label='{options.Query}']").First;
                await target.ScrollIntoViewIfNeededAsync(new() { Timeout = 5_000 });
                // A DOM click, like the rest of this project: an overlay that sits on top of the
                // button swallows a real mouse press without reporting anything.
                await target.EvaluateAsync("element => element.click()");
                ConsoleLogger.Info($"Clicked '{options.Query}'; waiting 6s for what it opens...");
                await Task.Delay(TimeSpan.FromSeconds(6), cancellationToken);
                // Some buttons answer on the clipboard rather than on the page.
                try
                {
                    await session.Context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"],
                        new() { Origin = new Uri(page.Url).GetLeftPart(UriPartial.Authority) });
                    string clipboard = await page.EvaluateAsync<string>("() => navigator.clipboard.readText()");
                    ConsoleLogger.Info($"CLIPBOARD: {clipboard}");
                }
                catch (Exception ex) { ConsoleLogger.Warn($"Clipboard unreadable: {ex.GetType().Name}: {ex.Message}"); }
            }
            catch (Exception ex) { ConsoleLogger.Warn($"Could not click '{options.Query}': {ex.GetType().Name}: {ex.Message}"); }
        }

        foreach (var current in session.Context.Pages.Where(candidate => !candidate.IsClosed).ToArray())
        {
            Console.WriteLine();
            Console.WriteLine("================================================================================");
            Console.WriteLine($"PAGE  url={current.Url}");
            Console.WriteLine($"      title={await SafeTitleAsync(current)}");
            Console.WriteLine("================================================================================");

            foreach (var frame in current.Frames)
            {
                Console.WriteLine();
                Console.WriteLine($"--- FRAME name='{frame.Name}' url={frame.Url}");
                await DumpButtonsAsync(frame);
                await DumpFieldsAndLinksAsync(frame);
                await DumpWaitingRoomTextAsync(frame);
            }
        }

        Console.WriteLine();
        ConsoleLogger.Info("Probe complete. The browser stays open until you press Enter.");
        Console.ReadLine();
        return 0;
    }

    private static async Task DumpButtonsAsync(IFrame frame)
    {
        try
        {
            var buttons = await frame.Locator("button, [role='button'], a[role='button']").AllAsync();
            Console.WriteLine($"    buttons: {buttons.Count}");
            int index = 0;
            foreach (var button in buttons)
            {
                if (index++ >= 120) { Console.WriteLine("    ... (truncated)"); break; }
                bool visible;
                try { visible = await button.IsVisibleAsync(); } catch { continue; }
                string name = await SafeAttributeAsync(button, "aria-label");
                string text = await SafeTextAsync(button);
                if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(text)) continue;
                Console.WriteLine($"      [{(visible ? "visible" : "hidden ")}] aria-label='{name}' text='{Shorten(text)}'");
            }
        }
        catch (Exception ex) { Console.WriteLine($"    buttons unavailable: {ex.GetType().Name}"); }
    }

    /// <summary>
    /// Text boxes and links, which is where a share panel keeps the URL it is offering to copy.
    /// </summary>
    private static async Task DumpFieldsAndLinksAsync(IFrame frame)
    {
        try
        {
            var fields = await frame.Locator("input, textarea").AllAsync();
            if (fields.Count > 0) Console.WriteLine($"    fields: {fields.Count}");
            int index = 0;
            foreach (var field in fields)
            {
                if (index++ >= 40) { Console.WriteLine("    ... (truncated)"); break; }
                bool visible;
                try { visible = await field.IsVisibleAsync(); } catch { continue; }
                string name = await SafeAttributeAsync(field, "aria-label");
                string placeholder = await SafeAttributeAsync(field, "placeholder");
                string id = await SafeAttributeAsync(field, "id");
                string value = "";
                try { value = await field.InputValueAsync(new() { Timeout = 1500 }); } catch { }
                if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(placeholder) &&
                    string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(value)) continue;
                Console.WriteLine($"      [{(visible ? "visible" : "hidden ")}] field id='{id}' aria-label='{name}' placeholder='{Shorten(placeholder)}' value='{Shorten(value)}'");
            }

            var links = await frame.Locator("a[href*='zoom.us/rec'], a[href*='/rec/share'], a[href*='/rec/play']").AllAsync();
            if (links.Count == 0) return;
            Console.WriteLine($"    recording links: {links.Count}");
            index = 0;
            foreach (var link in links)
            {
                if (index++ >= 20) { Console.WriteLine("    ... (truncated)"); break; }
                Console.WriteLine($"      href='{await SafeAttributeAsync(link, "href")}' text='{Shorten(await SafeTextAsync(link))}'");
            }
        }
        catch (Exception ex) { Console.WriteLine($"    fields unavailable: {ex.GetType().Name}"); }
    }

    private static async Task DumpWaitingRoomTextAsync(IFrame frame)
    {
        try
        {
            var matches = await frame
                .GetByText(new System.Text.RegularExpressions.Regex(
                    "waiting room|admit",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                .AllAsync();
            if (matches.Count == 0) return;
            Console.WriteLine($"    waiting-room texts: {matches.Count}");
            int index = 0;
            foreach (var match in matches)
            {
                if (index++ >= 25) { Console.WriteLine("    ... (truncated)"); break; }
                bool visible;
                try { visible = await match.IsVisibleAsync(); } catch { continue; }
                Console.WriteLine($"      [{(visible ? "visible" : "hidden ")}] '{Shorten(await SafeTextAsync(match))}'");
            }
        }
        catch (Exception ex) { Console.WriteLine($"    waiting-room text unavailable: {ex.GetType().Name}"); }
    }

    private static async Task<string> SafeTitleAsync(IPage page)
    {
        try { return await page.TitleAsync(); } catch { return "(unavailable)"; }
    }

    private static async Task<string> SafeTextAsync(ILocator locator)
    {
        try { return (await locator.InnerTextAsync()).Replace('\n', ' ').Trim(); } catch { return string.Empty; }
    }

    private static async Task<string> SafeAttributeAsync(ILocator locator, string attribute)
    {
        try { return (await locator.GetAttributeAsync(attribute) ?? string.Empty).Trim(); } catch { return string.Empty; }
    }

    private static string Shorten(string value) =>
        value.Length <= 90 ? value : value[..90] + "...";
}
