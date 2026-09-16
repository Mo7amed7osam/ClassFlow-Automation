using Microsoft.Playwright;
using ZoomAutoAdmit.WebAutomation.Browser;

namespace ZoomAutoAdmit.Attendance;

/// <summary>Reads an explicitly scoped Joined list on the existing authoritative page.</summary>
public sealed class WebAttendanceParticipantSource : IAttendanceParticipantSource
{
    private readonly IPage _page;
    private readonly Func<IPage, ILocator> _joinedList;
    private readonly string _rowSelector;
    private readonly string _nameSelector;
    public AttendanceSource Source => AttendanceSource.Web;

    // The host supplies the Joined-list locator for its Zoom layout, including iframe scope.
    // Never pass the whole Participants panel: it also contains the Waiting Room.
    public WebAttendanceParticipantSource(IPage primaryPage, Func<IPage, ILocator> joinedList,
        string rowSelector = "[role='application'], [role='listitem'], [role='row'], [role='option'], [role='treeitem']",
        string nameSelector = "[data-testid*='participant-name' i], [data-name], [class*='participant-name' i], [class*='display-name' i], [class*='user-name' i]")
    {
        _page = primaryPage ?? throw new ArgumentNullException(nameof(primaryPage));
        _joinedList = joinedList ?? throw new ArgumentNullException(nameof(joinedList));
        ArgumentException.ThrowIfNullOrWhiteSpace(rowSelector);
        ArgumentException.ThrowIfNullOrWhiteSpace(nameSelector);
        _rowSelector = rowSelector;
        _nameSelector = nameSelector;
    }

    public WebAttendanceParticipantSource(ZoomBrowserSession session, Func<IPage, ILocator> joinedList)
        : this(session.ActiveMeetingPage ?? throw new InvalidOperationException("No primary meeting page selected."), joinedList) { }

    public async Task<ParticipantReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_page.IsClosed) throw new InvalidOperationException("The primary meeting page is closed.");
        // Resolve the list/frame again on THIS page after DOM replacement. No page discovery.
        var list = _joinedList(_page);
        if (await list.CountAsync().WaitAsync(cancellationToken) != 1 ||
            !await list.IsVisibleAsync().WaitAsync(cancellationToken))
            throw new InvalidOperationException("Exactly one visible Joined list is required for attendance.");

        // The web list draws only the rows in view, so its own scroller is walked top to bottom inside
        // the page (scrollTop - no pointer, no focus, nothing pressed; the person's screen is not
        // involved) and put back where it was. Each row's own text comes too ("Mohab (Host, me)").
        var read = await list.EvaluateAsync<WebRead>("""
            async (list, selectors) => {
              if (/waiting/i.test(list.getAttribute('aria-label') || ''))
                throw new Error('Attendance requires Joined, not Waiting Room');
              const seen = new Map();
              const take = () => {
                const rows = [...list.querySelectorAll(selectors.rows)]
                  .filter(row => !row.parentElement?.closest(selectors.rows) ||
                    !list.contains(row.parentElement.closest(selectors.rows)));
                for (const row of rows) {
                  const names = [...row.querySelectorAll(selectors.names)].filter(node =>
                    !node.closest('button,[role="button"],menu,[role="menu"]'));
                  const name = names.map(node => node.getAttribute('data-name') || node.textContent || '')
                    .find(value => value.trim().length > 0);
                  const label = (row.getAttribute('aria-label') || row.innerText || row.textContent || '').replace(/\s+/g, ' ').trim();
                  if (!name && !label) continue;
                  // One entry per person: a row's text changes while it is read (muted, unmuted).
                  // Name and role tag, not the status after the comma: two rows of one name (a host rejoining) stay two.
                  const key = (label ? label.split(',')[0] : name).trim().toLowerCase();
                  if (key && !seen.has(key)) seen.set(key, { name: name || '', label });
                }
              };
              const scrolls = e => e.scrollHeight > e.clientHeight + 2 && /auto|scroll/.test(getComputedStyle(e).overflowY);
              // The list itself, something inside it, or - in a short panel (seen while a screen was
              // shared, 2026-09-16) - the panel around it is what scrolls.
              let scroller = [list, ...list.querySelectorAll('*')].find(scrolls) || null;
              for (let up = list.parentElement, depth = 0; !scroller && up && depth < 8; up = up.parentElement, depth++)
                if (scrolls(up)) scroller = up;
              // Zoom's own count, "Participants (19)", when the panel shows it: the read is complete
              // only when it holds that many people.
              let expected = null;
              for (const node of document.querySelectorAll('h1,h2,h3,h4,h5,[role="heading"],[class*="title" i],[class*="header" i] span,[class*="header" i] div')) {
                const match = /^\s*participants\s*\((\d+)\)\s*$/i.exec(node.textContent || '');
                if (match) { expected = parseInt(match[1], 10); break; }
              }
              const enough = () => expected === null || seen.size >= expected;
              if (!scroller) { take(); return { rows: [...seen.values()], complete: enough(), expected }; }
              const settle = () => new Promise(done => requestAnimationFrame(() => setTimeout(done, 120)));
              const original = scroller.scrollTop;
              let complete = false;
              try {
                scroller.scrollTop = 0;
                await settle();
                take();
                const step = Math.max(20, Math.floor(scroller.clientHeight * 0.8));
                for (let page = 0; page < 400; page++) {
                  if (scroller.scrollTop + scroller.clientHeight >= scroller.scrollHeight - 2) { complete = true; break; }
                  const before = scroller.scrollTop;
                  scroller.scrollTop = before + step;
                  await settle();
                  take();
                  if (scroller.scrollTop === before) { complete = true; break; }
                }
              } finally {
                scroller.scrollTop = original;
              }
              return { rows: [...seen.values()], complete: complete && enough(), expected };
            }
            """, new { rows = _rowSelector, names = _nameSelector }, new() { Timeout = 60000 })
            .WaitAsync(cancellationToken);
        // Zoom's own count says rows are missing and nothing on the page scrolls natively: the list
        // moves on wheel events (a short panel while a screen was shared, 2026-09-16: 6 of 17 read).
        // The page's own pointer wheels over the list - the person's mouse is not involved.
        if (!read.Complete && read.Expected is { } expected && read.Rows.Length < expected)
            read = await WheelThroughAsync(list, read, expected, cancellationToken);
        if (read.Rows.Length == 0)
            throw new InvalidOperationException("No participant rows exposed; cannot distinguish empty from unavailable DOM rows.");
        return new(read.Rows.Select(row => new ParticipantPresence(
            // A row with no separate name element (the screen-share layout): its own text, role tail cut off.
            string.IsNullOrWhiteSpace(row.Name) ? RuntimeAttendanceSources.CleanParticipantName(row.Label) : row.Name.Trim()) { RowLabel = row.Label }).ToArray(), read.Complete,
            read.Complete
                ? $"The whole Joined list, walked top to bottom{(read.Expected is { } all ? $" ({read.Rows.Length} of {all})" : "")}."
                : $"Joined rows read: {read.Rows.Length}{(read.Expected is { } count ? $" of {count}" : "")}; the list may not have been fully exposed.");
    }

    private async Task<WebRead> WheelThroughAsync(ILocator list, WebRead first, int expected, CancellationToken token)
    {
        var box = await list.BoundingBoxAsync();
        if (box == null || box.Height < 10) return first;
        var rows = new Dictionary<string, WebRow>(StringComparer.OrdinalIgnoreCase);
        void Merge(IEnumerable<WebRow> found)
        {
            foreach (var row in found)
            {
                string key = (string.IsNullOrWhiteSpace(row.Label) ? row.Name : row.Label.Split(',')[0]).Trim();
                if (key.Length > 0) rows.TryAdd(key, row);
            }
        }
        async Task<int> TakeAsync()
        {
            int before = rows.Count;
            Merge(await list.EvaluateAsync<WebRow[]>("""
                (list, selectors) => [...list.querySelectorAll(selectors.rows)]
                  .filter(row => !row.parentElement?.closest(selectors.rows) || !list.contains(row.parentElement.closest(selectors.rows)))
                  .map(row => {
                    const names = [...row.querySelectorAll(selectors.names)].filter(node => !node.closest('button,[role="button"],menu,[role="menu"]'));
                    const name = names.map(node => node.getAttribute('data-name') || node.textContent || '').find(value => value.trim().length > 0) || '';
                    const label = (row.getAttribute('aria-label') || row.innerText || row.textContent || '').replace(/\s+/g, ' ').trim();
                    return { name, label };
                  })
                  .filter(row => row.name || row.label)
                """, new { rows = _rowSelector, names = _nameSelector }, new() { Timeout = 5000 }).WaitAsync(token));
            return rows.Count - before;
        }

        Merge(first.Rows);
        float x = box.X + box.Width / 2, y = box.Y + Math.Min(box.Height / 2, 60);
        bool end = false;
        try
        {
            await _page.Mouse.MoveAsync(x, y);
            await _page.Mouse.WheelAsync(0, -20000);           // to the top
            await Task.Delay(250, token);
            await TakeAsync();
            float step = (float)Math.Max(40, box.Height * 0.7);
            for (int still = 0, turn = 0; turn < 200 && rows.Count < expected; turn++)
            {
                token.ThrowIfCancellationRequested();
                await _page.Mouse.WheelAsync(0, step);
                await Task.Delay(250, token);
                if (await TakeAsync() > 0) still = 0;
                else if (++still >= 3) break;                    // nothing new three times: the bottom
            }
            end = true;
        }
        catch (PlaywrightException) { }
        finally
        {
            try { await _page.Mouse.WheelAsync(0, -20000); } catch (PlaywrightException) { }
        }
        return new WebRead
        {
            Rows = [.. rows.Values],
            Expected = expected,
            Complete = end && rows.Count >= expected,
        };
    }

    /// <summary>What one read of the list returns: each row's name and whole text, and whether every row was on the page.</summary>
    public sealed class WebRead
    {
        public WebRow[] Rows { get; set; } = [];
        public bool Complete { get; set; }
        /// <summary>Zoom's "Participants (n)", when the page shows it.</summary>
        public int? Expected { get; set; }
    }

    public sealed class WebRow
    {
        public string Name { get; set; } = "";
        public string Label { get; set; } = "";
    }
}
