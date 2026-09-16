using Microsoft.Playwright;
using System.Text.RegularExpressions;
using ZoomAutoAdmit.WebAutomation;

namespace ZoomAutoAdmit.SessionRoles;

/// <summary>
/// Grants co-host in Zoom's web client, the way the host would: the person's row in the
/// participants panel → its "More" menu button → "Make Co-Host" → "Yes" in Zoom's confirmation,
/// then verifies from the row, which reads "Name (Co-host, guest)". Recorded live on the web
/// client, 2026-09-16. Only the page's own pointer is used; never "Make Host", never the row's
/// mic button ("Ask to Unmute").
/// </summary>
public sealed class WebCoHostAssigner(Func<IPage?> page, TimeSpan? menuWait = null, TimeSpan? verifyWait = null) : ICoHostAssigner
{
    private static readonly Regex JoinedListName = new(@"^participants? list\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OpenParticipants = new(@"^open the manage participants list pane|^participants(,|\s*\(\d+\))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RowMore = new(@"^more\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MakeCoHost = new(@"^\s*make\s+co-?host\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CoHostRole = new(@"\(\s*co-?host\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private const string RowSelector = "[role='application'], [role='listitem'], [role='row'], [role='option'], [role='treeitem']";

    private readonly TimeSpan _menuWait = menuWait ?? TimeSpan.FromMilliseconds(900);
    private readonly TimeSpan _verifyWait = verifyWait ?? TimeSpan.FromSeconds(3);

    public CoHostOutcome Assign(string observedDisplayName, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(observedDisplayName)) return new(false, "No participant name supplied.");
        try { return AssignAsync(observedDisplayName.Trim(), token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(false, $"The web co-host step failed ({ex.GetType().Name}): {FirstLine(ex.Message)}"); }
    }

    private async Task<CoHostOutcome> AssignAsync(string name, CancellationToken token)
    {
        var current = page();
        if (current == null || current.IsClosed) return new(false, "No web meeting page is open.");
        await ZoomWebToolbar.WakeAsync(current, token);

        var list = await FindListAsync(current);
        if (list == null && await FindVisibleAsync(current, AriaRole.Button, OpenParticipants) is { } open)
        {
            await open.ClickAsync(new() { Force = true, Timeout = 5000 });
            await Task.Delay(_menuWait, token);
            list = await FindListAsync(current);
        }
        if (list == null) return new(false, "The participants panel is not open on the web page.");

        var row = await FindRowAsync(list, name);
        if (row == null) return new(false, $"\"{name}\" is not in the participants list right now.");
        if (CoHostRole.IsMatch(await LabelAsync(row))) return new(true, $"{name} is already a co-host.", AlreadyCoHost: true);

        await row.HoverAsync(new() { Force = true, Timeout = 5000 });
        await Task.Delay(TimeSpan.FromMilliseconds(400), token);
        ILocator? more = null;
        foreach (var button in await row.Locator("button,[role=button]").AllAsync())
        {
            string label = (await button.GetAttributeAsync("aria-label")) ?? (await button.InnerTextAsync()).Trim();
            if (RowMore.IsMatch(label)) { more = button; break; }
        }
        if (more == null) return new(false, "That participant's More button did not appear on the web page.");
        token.ThrowIfCancellationRequested();
        await more.ClickAsync(new() { Force = true, Timeout = 5000 });
        await Task.Delay(_menuWait, token);

        var item = await FindVisibleAsync(current, AriaRole.Menuitem, MakeCoHost);
        if (item == null)
        {
            await CloseMenuAsync(current);
            return new(false, "\"Make Co-Host\" was not in that participant's menu.");
        }
        await item.ClickAsync(new() { Force = true, Timeout = 5000 });
        await Task.Delay(_menuWait, token);

        // "Do you want to make <name> the co-host of this meeting?" No / Yes - only Yes, only in that dialog.
        var yes = await FindConfirmAsync(current);
        if (yes != null) await yes.ClickAsync(new() { Force = true, Timeout = 5000 });

        var deadline = DateTime.UtcNow + _verifyWait;
        do
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), token);
            var refreshed = await FindListAsync(current);
            var again = refreshed == null ? null : await FindRowAsync(refreshed, name);
            if (again != null && CoHostRole.IsMatch(await LabelAsync(again))) return new(true, $"{name} is now a co-host.");
        } while (DateTime.UtcNow < deadline);
        return new(false, $"Zoom did not confirm co-host for {name}; nothing else was changed.");
    }

    // The same finder attendance uses: by name, or by rows carrying Zoom's role tags (screen sharing).
    private static async Task<ILocator?> FindListAsync(IPage current) =>
        (await Attendance.WebParticipantList.FindAsync(current)).List;

    private static async Task<ILocator?> FindRowAsync(ILocator list, string name)
    {
        foreach (var row in await list.Locator(RowSelector).AllAsync())
            if (string.Equals(RowName(await LabelAsync(row)), name, StringComparison.OrdinalIgnoreCase)) return row;
        return null;
    }

    /// <summary>"Cohost Test (Guest),computer audio muted,video off" → "Cohost Test".</summary>
    internal static string RowName(string label) => Attendance.RuntimeAttendanceSources.CleanParticipantName(label);

    private static async Task<string> LabelAsync(ILocator row)
    {
        string? label = await row.GetAttributeAsync("aria-label");
        if (!string.IsNullOrWhiteSpace(label)) return label;
        return Regex.Replace(await row.InnerTextAsync(new() { Timeout = 2000 }), @"\s+", " ").Trim();
    }

    private static async Task<ILocator?> FindConfirmAsync(IPage current)
    {
        foreach (var frame in current.Frames)
            foreach (var dialog in await frame.Locator("[role=dialog], [role=alertdialog], .zm-modal").AllAsync())
            {
                if (!await dialog.IsVisibleAsync()) continue;
                string text = await dialog.InnerTextAsync(new() { Timeout = 2000 });
                if (!Regex.IsMatch(text, @"co-?host", RegexOptions.IgnoreCase)) continue;
                var yes = dialog.GetByRole(AriaRole.Button, new() { NameRegex = new Regex(@"^\s*yes\s*$", RegexOptions.IgnoreCase) });
                if (await yes.CountAsync() > 0) return yes.First;
            }
        // Some builds render the dialog without a dialog role; the Yes beside that question still counts.
        foreach (var frame in current.Frames)
        {
            var question = frame.GetByText(new Regex(@"make .+ the co-?host", RegexOptions.IgnoreCase));
            if (await question.CountAsync() == 0 || !await question.First.IsVisibleAsync()) continue;
            var yes = frame.GetByRole(AriaRole.Button, new() { NameRegex = new Regex(@"^\s*yes\s*$", RegexOptions.IgnoreCase) });
            foreach (var candidate in await yes.AllAsync())
                if (await candidate.IsVisibleAsync()) return candidate;
        }
        return null;
    }

    private static async Task<ILocator?> FindVisibleAsync(IPage current, AriaRole role, Regex name)
    {
        foreach (var frame in current.Frames)
            foreach (var element in await frame.GetByRole(role, new() { NameRegex = name }).AllAsync())
                if (await element.IsVisibleAsync()) return element;
        return null;
    }

    private static async Task CloseMenuAsync(IPage current)
    {
        try { await current.Keyboard.PressAsync("Escape"); } catch (PlaywrightException) { }
    }

    private static string FirstLine(string message)
    {
        int end = message.IndexOfAny(['\r', '\n']);
        return end < 0 ? message : message[..end];
    }
}
