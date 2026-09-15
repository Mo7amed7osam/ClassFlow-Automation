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
        string rowSelector = "[role='application'], [role='listitem'], [role='row']",
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

        // One DOM read avoids mixing names from separate re-renders. No hover/click/scroll.
        // Each row's own text comes too ("Mohab (Host, me)", "Mostafa (Co-host)"), and whether the
        // list shows everyone: a list that does not scroll has every row rendered.
        var read = await list.EvaluateAsync<WebRead>("""
            (list, selectors) => {
              if (/waiting/i.test(list.getAttribute('aria-label') || ''))
                throw new Error('Attendance requires Joined, not Waiting Room');
              const rows = [...list.querySelectorAll(selectors.rows)]
                .filter(row => !row.parentElement?.closest(selectors.rows) ||
                  !list.contains(row.parentElement.closest(selectors.rows)));
              const read = rows.map(row => {
                const names = [...row.querySelectorAll(selectors.names)].filter(node =>
                  !node.closest('button,[role="button"],menu,[role="menu"]'));
                const name = names.map(node => node.getAttribute('data-name') || node.textContent || '')
                  .find(value => value.trim().length > 0);
                if (!name) throw new Error('Participant row has no readable name');
                const label = (row.getAttribute('aria-label') || row.innerText || row.textContent || '').replace(/\s+/g, ' ').trim();
                return { name, label };
              });
              const scroller = [list, ...list.querySelectorAll('*')].find(e => e.scrollHeight > e.clientHeight + 2 && /auto|scroll/.test(getComputedStyle(e).overflowY));
              return { rows: read, complete: !scroller };
            }
            """, new { rows = _rowSelector, names = _nameSelector }, new() { Timeout = 3000 })
            .WaitAsync(cancellationToken);
        if (read.Rows.Length == 0)
            throw new InvalidOperationException("No participant rows exposed; cannot distinguish empty from unavailable DOM rows.");
        return new(read.Rows.Select(row => new ParticipantPresence(row.Name) { RowLabel = row.Label }).ToArray(), read.Complete,
            read.Complete ? "Every Joined-list row was on the page." : "Rendered Joined-list rows only; virtualized/collapsed rows may not be exposed.");
    }

    /// <summary>What one read of the list returns: each row's name and whole text, and whether every row was on the page.</summary>
    public sealed class WebRead
    {
        public WebRow[] Rows { get; set; } = [];
        public bool Complete { get; set; }
    }

    public sealed class WebRow
    {
        public string Name { get; set; } = "";
        public string Label { get; set; } = "";
    }
}
