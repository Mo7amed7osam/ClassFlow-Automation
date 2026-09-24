using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.WebAutomation.Browser;

namespace ZoomAutoAdmit.WebAutomation.Zoom;

/// <summary>One person in Zoom's participants report: every join of the same name added up.</summary>
public sealed record ZoomReportPerson(string Name, int Minutes);

/// <param name="EndedAt">When Zoom says the class's last run ended, on this PC's clock.</param>
/// <param name="Pages">How many pages of participants were read, over every run of the meeting.</param>
public sealed record ZoomParticipantsReport(IReadOnlyList<ZoomReportPerson> People, int Instances, int Rows,
    DateTimeOffset? EndedAt = null, int Pages = 1);

/// <summary>
/// Reads Zoom's own participants report for a meeting that has just ended, on the group's signed-in
/// web profile: Reports -> Usage (zoom.us/account/my/report) lists each run of the meeting with a
/// "Participants" link; its dialog has one row per join (Name, Join/Leave Time, Duration (Minutes),
/// In Waiting Room). Rows spent in the waiting room are left out, and each name's joins are added up
/// - a person who dropped and came back three times is one person with their whole time.
///
/// Neither list is one page. A class of thirty people fills several pages of the dialog, and a day of
/// classes fills several pages of the usage list, so both are read to their end: every page of the
/// dialog for each run, and every page of the list until the meeting's runs are found. Rows are kept
/// by their own content, so a page read twice - by turning back, or by a list that loads as it is
/// scrolled - cannot put anybody in the class twice or double their minutes.
/// Read live on S8, 2026-09-16: 73 rows, 23 people.
/// </summary>
public sealed class ZoomParticipantsReportReader
{
    public const string ReportUrl = "https://zoom.us/account/my/report";
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(45);
    /// <summary>Pages of one run's participants dialog, and of the usage list, before reading stops.</summary>
    private const int MostPages = 60;
    private const int MostListPages = 25;

    private readonly ZoomProfileManager _profiles;

    public ZoomParticipantsReportReader(ZoomProfileManager? profiles = null) => _profiles = profiles ?? new ZoomProfileManager();

    /// <summary>
    /// The Zoom account's clock, which the report's times are written in: the configured one, else
    /// Pacific time (the eyouth accounts' zone - a class at 18:45 in Cairo reads 08:45 AM).
    /// </summary>
    public static TimeZoneInfo ReportZone() =>
        ZoomRecordingLinkReader.ConfiguredZoomTimeZone() ?? TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");

    /// <summary>
    /// The class's participants, or null when the report has no run of its meeting from the class's
    /// time yet. Zoom lists a meeting only once it has ended, so null means the class is still going
    /// (or has not started).
    /// </summary>
    /// <param name="meetingUrl">The class's Zoom link; only its meeting number is used.</param>
    /// <param name="classStart">The class's scheduled start on this PC's clock.</param>
    public async Task<ZoomParticipantsReport?> ReadAsync(string profileName, string meetingUrl, DateTime classStart,
        CancellationToken cancellationToken = default, TimeZoneInfo? zoomZone = null)
    {
        var zone = zoomZone ?? ReportZone();
        DateTimeOffset Local(DateTime reportTime) =>
            new(TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(reportTime, DateTimeKind.Unspecified), zone, TimeZoneInfo.Local));
        string meetingId = MeetingNumber(meetingUrl) ?? throw new ArgumentException("The Zoom link has no meeting number.", nameof(meetingUrl));
        var plan = new ZoomBrowserLaunchPlan(_profiles.GetOrCreate(profileName), Headless: true);
        await using var session = await new ZoomBrowserLauncher().LaunchAsync(plan, cancellationToken);
        var page = session.Context.Pages.Count > 0 ? session.Context.Pages[0] : await session.Context.NewPageAsync();
        page.SetDefaultTimeout((float)StepTimeout.TotalMilliseconds);
        await page.GotoAsync(ReportUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        var rows = new List<string[]>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string[] header = [];
        int instances = 0, pages = 0, listPages = 0;
        DateTimeOffset? lastEnd = null;

        // The usage list, page by page: a day with many classes puts the class's own meeting on any
        // of them, and the meeting can appear more than once (it was closed and opened again).
        while (listPages < MostListPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            listPages++;
            var found = await FindRunsAsync(page, meetingId, listPages == 1 ? StepTimeout : TimeSpan.FromSeconds(8), cancellationToken);
            // The class: every run of its meeting that started from an hour and a half before the
            // class's time until the class was over. Another day's class on the same meeting number
            // is not this one.
            foreach (var run in found.Runs.Where(run => IsThisClass(Local(run.Start).DateTime, classStart)))
            {
                instances++;
                var ended = Local(run.End);
                if (lastEnd == null || ended > lastEnd) lastEnd = ended;
                var link = run.Row.Locator("td a, td button, td [role=button]").Last;
                await link.ClickAsync();
                var read = await ReadOpenDialogAsync(page, rows, seen, cancellationToken);
                if (read.Header.Length > 0) header = read.Header;
                pages += read.Pages;
                // Closed again before the next run's link is pressed.
                await page.Keyboard.PressAsync("Escape");
                await Task.Delay(800, cancellationToken);
            }
            if (!found.SawRows) break;                          // nothing dated on this page: the list is read out
            if (!await TurnPageAsync(UsageTable(page), cancellationToken)) break;
        }
        if (instances == 0) return null;
        var people = Summarize(header, rows);
        ConsoleLogger.Info($"[REPORT] Meeting {meetingId}: {people.Count} people from {rows.Count} rows, " +
                           $"{pages} page(s) of participants over {instances} run(s), {listPages} page(s) of the usage list.");
        return new(people, instances, rows.Count, lastEnd, Math.Max(pages, 1));
    }

    /// <summary>
    /// The rows of the usage list that carry this meeting number, with each run's start and end, and
    /// whether the page held any dated row at all - a page with none means the list is read out.
    /// </summary>
    private static async Task<(IReadOnlyList<(ILocator Row, DateTime Start, DateTime End)> Runs, bool SawRows)> FindRunsAsync(
        IPage page, string meetingId, TimeSpan wait, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool sawRows = false;
            foreach (var frame in page.Frames)
            {
                var runs = new List<(ILocator Row, DateTime Start, DateTime End)>();
                foreach (var row in await frame.Locator("tr").AllAsync())
                {
                    string[] cells;
                    try { cells = await row.EvaluateAsync<string[]>("tr => [...tr.querySelectorAll('td')].map(td => td.innerText.replace(/\\s+/g, ' ').trim())"); }
                    catch (PlaywrightException) { continue; }
                    var times = cells.Select(ParseReportTime).Where(time => time.HasValue).Select(time => time!.Value).ToArray();
                    // Creation, Start, End: the last two are this run.
                    if (times.Length >= 2) sawRows = true;
                    if (!cells.Any(cell => Digits(cell) == meetingId) || times.Length < 2) continue;
                    runs.Add((row, times[^2], times[^1]));
                }
                if (runs.Count > 0) return (runs, true);
            }
            // The list is there but this meeting is not on this page; or nothing has arrived yet.
            if (sawRows) return ([], true);
            if (DateTime.UtcNow >= deadline) return ([], false);
            await Task.Delay(1000, cancellationToken);
        }
    }

    /// <summary>
    /// Every row of the participants dialog that is open now, to its last page. A dialog that only
    /// fetches more rows as it is scrolled gives them up here too, and each row is kept once.
    /// </summary>
    private static async Task<(string[] Header, int Pages)> ReadOpenDialogAsync(IPage page, List<string[]> rows,
        HashSet<string> seen, CancellationToken cancellationToken)
    {
        var table = page.Locator("table").Filter(new() { HasTextRegex = new Regex("Join Time", RegexOptions.IgnoreCase) }).Last;
        await table.WaitForAsync(new() { State = WaitForSelectorState.Visible });
        await Task.Delay(1500, cancellationToken);              // the rows arrive after the dialog

        string[] header = [];
        string mark = "";
        int pages = 0;
        while (pages < MostPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pages++;
            for (int quiet = 0; quiet < 2;)
            {
                var read = await ReadTableAsync(table);
                if (read.Header.Length > 0) header = read.Header;
                mark = Mark(read.Rows);
                quiet = Keep(read.Rows, rows, seen) > 0 ? 0 : quiet + 1;
                if (quiet >= 2) break;
                try { await table.EvaluateAsync(ScrollToTheEnd); } catch (PlaywrightException) { }
                await Task.Delay(600, cancellationToken);
            }
            if (!await TurnPageAsync(table, cancellationToken, mark)) break;
        }
        return (header, pages);
    }

    /// <summary>The table's header and its rows as they stand, whatever the dialog calls its parts.</summary>
    private static async Task<(string[] Header, IReadOnlyList<string[]> Rows)> ReadTableAsync(ILocator table)
    {
        string json;
        try { json = await table.EvaluateAsync<string>(ReadTheTable); }
        catch (PlaywrightException) { return ([], []); }
        using var parsed = JsonDocument.Parse(json);
        return ([.. parsed.RootElement.GetProperty("header").EnumerateArray().Select(value => value.GetString() ?? "")],
                [.. parsed.RootElement.GetProperty("rows").EnumerateArray()
                    .Select(row => row.EnumerateArray().Select(value => value.GetString() ?? "").ToArray())]);
    }

    /// <summary>The usage list's own table, whichever frame Zoom draws it in.</summary>
    private static ILocator UsageTable(IPage page)
    {
        foreach (var frame in page.Frames)
        {
            var table = frame.Locator("table").Filter(new() { HasTextRegex = new Regex(@"Meeting\s*ID|Participants", RegexOptions.IgnoreCase) }).Last;
            try { if (table.CountAsync().GetAwaiter().GetResult() > 0) return table; }
            catch (PlaywrightException) { }
        }
        return page.Locator("table").Last;
    }

    /// <summary>
    /// Presses the table's "next page", and answers whether the page actually turned: a pager that is
    /// not there, one already on its last page, and one whose rows never change all end the reading,
    /// so a list can never be read round and round.
    /// </summary>
    private static async Task<bool> TurnPageAsync(ILocator table, CancellationToken cancellationToken, string? mark = null)
    {
        string was;
        try
        {
            was = mark ?? Mark((await ReadTableAsync(table)).Rows);
            if (await table.EvaluateAsync<string>(PressNextPage) != "clicked") return false;
        }
        catch (PlaywrightException) { return false; }
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500, cancellationToken);
            if (Mark((await ReadTableAsync(table)).Rows) != was) return true;
        }
        return false;
    }

    /// <summary>Which page a table is showing, as far as reading it twice is concerned.</summary>
    private static string Mark(IReadOnlyList<string[]> rows) =>
        rows.Count == 0 ? "" : rows.Count + "#" + string.Join("\u001f", rows[0]) + "#" + string.Join("\u001f", rows[^1]);

    /// <summary>
    /// Adds the rows nobody has seen yet, and answers how many were new. A row is itself - the name,
    /// the times it joined and left, its minutes - so the same row read on two passes is one row.
    /// </summary>
    public static int Keep(IEnumerable<string[]> read, List<string[]> rows, HashSet<string> seen)
    {
        int added = 0;
        foreach (var row in read)
        {
            string signature = string.Join("\u001f", row);
            if (signature.Replace("\u001f", "").Trim().Length == 0 || !seen.Add(signature)) continue;
            rows.Add(row);
            added++;
        }
        return added;
    }

    /// <summary>A run that started from 90 minutes before the class's time until 3 h 15 after it.</summary>
    public static bool IsThisClass(DateTime runStartLocal, DateTime classStart)
    {
        var gap = runStartLocal - classStart;
        return gap >= -TimeSpan.FromMinutes(90) && gap <= TimeSpan.FromHours(3.25);
    }

    /// <summary>
    /// The one name a person is counted under: Zoom's own spacing, and the invisible marks a phone
    /// keyboard leaves in an Arabic name, do not make two people out of one.
    /// </summary>
    public static string SameName(string? name) =>
        Regex.Replace(Regex.Replace(name ?? "", @"[​-‏‪-‮⁦-⁩﻿]", ""), @"\s+", " ").Trim();

    /// <summary>One entry per name: the waiting room left out, every join's minutes added up.</summary>
    public static IReadOnlyList<ZoomReportPerson> Summarize(IReadOnlyList<string> header, IEnumerable<string[]> rows)
    {
        int nameAt = IndexOf(header, "Name");
        int minutesAt = IndexOf(header, "Duration");
        int waitingAt = IndexOf(header, "In Waiting Room");
        if (nameAt < 0 || minutesAt < 0)
            throw new InvalidOperationException("Zoom's participants report has no Name or Duration column.");
        return rows
            .Where(row => row.Length > Math.Max(nameAt, minutesAt))
            .Where(row => waitingAt < 0 || waitingAt >= row.Length || !row[waitingAt].Equals("Yes", StringComparison.OrdinalIgnoreCase))
            .Select(row => (Name: SameName(row[nameAt]),
                            Minutes: int.TryParse(row[minutesAt].Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes) ? minutes : 0))
            .Where(entry => entry.Name.Length > 0)
            .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ZoomReportPerson(group.First().Name, group.Sum(entry => entry.Minutes)))
            .OrderBy(person => person.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int IndexOf(IReadOnlyList<string> header, string starts)
    {
        for (int i = 0; i < header.Count; i++)
            if (header[i].StartsWith(starts, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    public static string? MeetingNumber(string meetingUrl)
    {
        var match = Regex.Match(meetingUrl ?? "", @"/(?:j|wc|s)/(\d{9,12})");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string Digits(string text) => new(text.Where(char.IsDigit).ToArray());

    private static DateTime? ParseReportTime(string text) =>
        DateTime.TryParseExact(text, "MM/dd/yyyy hh:mm:ss tt", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;

    /// <summary>A cell is read from its title where it has one, because Zoom cuts long names short.</summary>
    private const string ReadTheTable = """
        table => JSON.stringify({
          header: [...table.querySelectorAll('thead th, tr:first-child th')].filter(th => th.offsetParent !== null).map(th => th.innerText.replace(/\s+/g, ' ').trim()),
          rows: [...table.querySelectorAll('tbody tr, tr')].filter(tr => tr.querySelector('td')).map(tr => [...tr.querySelectorAll('td')].filter(td => td.offsetParent !== null).map(td => (td.getAttribute('title') || td.innerText).replace(/\s+/g, ' ').trim()))
        })
        """;

    /// <summary>Whatever the dialog scrolls in, taken to its end, for a list that grows as it is read.</summary>
    private const string ScrollToTheEnd = """
        table => {
          let box = table.parentElement;
          for (let hop = 0; box && hop < 8; hop++, box = box.parentElement)
            if (box.scrollHeight > box.clientHeight + 4) { box.scrollTop = box.scrollHeight; return; }
        }
        """;

    /// <summary>
    /// The pager Zoom puts under a table, pressed one page on. It answers 'clicked', 'last' when the
    /// arrow is there but dead, or 'none' when the list has no pager at all.
    /// </summary>
    private const string PressNextPage = """
        table => {
          const pager = '.zm-pagination, .pagination, [class*=pagination], [class*=pager], [aria-label*=Next], [aria-label*=next], [title*=Next], [title*=next]';
          let box = table.parentElement;
          for (let hop = 0; box && hop < 10 && !box.querySelector(pager); hop++) box = box.parentElement;
          box = box || document.body;
          const words = (el) => {
            const cls = typeof el.className === 'string' ? el.className : '';
            const own = el.children.length === 0 ? (el.innerText || el.textContent || '') : '';
            return ((el.getAttribute('aria-label') || '') + ' ' + (el.getAttribute('title') || '') + ' ' + cls + ' ' + own).toLowerCase();
          };
          const dead = (el) => {
            for (let n = el; n && n !== box; n = n.parentElement) {
              if (n.hasAttribute('disabled') || n.getAttribute('aria-disabled') === 'true') return true;
              const cls = typeof n.className === 'string' ? n.className.toLowerCase() : '';
              if (/disabled|unavailable/.test(cls)) return true;
            }
            return false;
          };
          const looksNext = (el) => {
            const w = words(el);
            if (!/(^|[^a-z])next([^a-z]|$)|next[-_]?page|page[-_]?next|›|»|→|arrow[-_]?right|caret[-_]?right|angle[-_]?right|chevron[-_]?right/.test(w)) return false;
            return el.getClientRects().length > 0;
          };
          const found = [...box.querySelectorAll('a, button, li, span, i, div')].filter(looksNext);
          if (found.length === 0) return 'none';
          const live = found.filter(el => !dead(el));
          if (live.length === 0) return 'last';
          const target = live[live.length - 1];
          (target.closest('a, button, li') || target).click();
          return 'clicked';
        }
        """;
}
