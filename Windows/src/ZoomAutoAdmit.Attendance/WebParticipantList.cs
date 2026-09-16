using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace ZoomAutoAdmit.Attendance;

/// <summary>
/// Finds the joined-participants list on Zoom's web meeting page. First by its name ("Participants
/// list"); when that is not exactly one list - it stopped matching the moment someone shared their
/// screen (S8, 2026-09-16 20:46) - by what it holds: the visible list whose rows carry Zoom's own
/// role tags ("(Host, me)", "(Guest)", "(Co-host)"). The waiting room list is never taken.
/// </summary>
public static class WebParticipantList
{
    public static readonly Regex JoinedListName = new(
        @"^participants? list\b|^(joined|in[- ]meeting)(\s+participants)?(\s+list)?(\s*\(\d+\))?$", RegexOptions.IgnoreCase);

    private const string ListSelector = "[role='list'], [role='listbox'], [role='grid'], [role='tree'], ul";
    private const string RowSelector = "[role='application'], [role='listitem'], [role='row'], [role='option'], [role='treeitem'], li";

    public sealed record Found(ILocator? List, string Seen);

    public static async Task<Found> FindAsync(IPage page, CancellationToken token = default)
    {
        var named = new List<ILocator>();
        foreach (var frame in page.Frames)
        {
            token.ThrowIfCancellationRequested();
            foreach (var role in new[] { AriaRole.List, AriaRole.Listbox })
                foreach (var list in await frame.GetByRole(role, new() { NameRegex = JoinedListName }).AllAsync())
                    if (await SafeVisibleAsync(list)) named.Add(list);
        }
        if (named.Count == 1) return new(named[0], "by name");

        // By content: how many of the list's own rows carry a Zoom role tag.
        ILocator? best = null;
        int bestTags = 0;
        var seen = new List<string>();
        foreach (var frame in page.Frames)
        {
            token.ThrowIfCancellationRequested();
            IReadOnlyList<ILocator> lists;
            try { lists = await frame.Locator(ListSelector).AllAsync(); }
            catch (PlaywrightException) { continue; }
            foreach (var list in lists)
            {
                try
                {
                    var info = await list.EvaluateAsync<ListInfo>("""
                        (list, rows) => {
                          const label = (list.getAttribute('aria-label') || '').trim();
                          const rect = list.getBoundingClientRect();
                          const visible = rect.width > 0 && rect.height > 0 && getComputedStyle(list).visibility !== 'hidden';
                          const own = [...list.querySelectorAll(rows)].filter(row => row.closest('[role="list"],[role="listbox"],[role="grid"],[role="tree"],ul') === list);
                          const tags = own.filter(row => /\((host|co-?host|guest)\b/i.test(row.getAttribute('aria-label') || row.innerText || '')).length;
                          return { label: label.slice(0, 40), role: list.getAttribute('role') || list.tagName.toLowerCase(), rows: own.length, tags, visible,
                                   waiting: /waiting/i.test(label) };
                        }
                        """, RowSelector, new() { Timeout = 2000 });
                    if (info.Rows > 0) seen.Add($"{info.Role} \"{info.Label}\" rows={info.Rows} tagged={info.Tags}{(info.Visible ? "" : " hidden")}");
                    if (!info.Visible || info.Waiting || info.Tags <= bestTags) continue;
                    best = list;
                    bestTags = info.Tags;
                }
                catch (PlaywrightException) { }
            }
        }
        string description = $"named={named.Count}; lists: {(seen.Count == 0 ? "(none with rows)" : string.Join(" | ", seen.Take(8)))}";
        return new(best, best == null ? description : "by its rows' role tags; " + description);
    }

    private static async Task<bool> SafeVisibleAsync(ILocator locator)
    {
        try { return await locator.IsVisibleAsync(); }
        catch (PlaywrightException) { return false; }
    }

    public sealed class ListInfo
    {
        public string Label { get; set; } = "";
        public string Role { get; set; } = "";
        public int Rows { get; set; }
        public int Tags { get; set; }
        public bool Visible { get; set; }
        public bool Waiting { get; set; }
    }
}
