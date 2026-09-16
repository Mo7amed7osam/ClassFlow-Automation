using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using ZoomAutoAdmit.WebAutomation.Browser;

namespace ZoomAutoAdmit.WebAutomation.Zoom;

/// <summary>One person in Zoom's participants report: every join of the same name added up.</summary>
public sealed record ZoomReportPerson(string Name, int Minutes);

/// <param name="EndedAt">When Zoom says the class's last run ended, on this PC's clock.</param>
public sealed record ZoomParticipantsReport(IReadOnlyList<ZoomReportPerson> People, int Instances, int Rows, DateTimeOffset? EndedAt = null);

/// <summary>
/// Reads Zoom's own participants report for a meeting that has just ended, on the group's signed-in
/// web profile: Reports → Usage (zoom.us/account/my/report) lists each run of the meeting with a
/// "Participants" link; its dialog has one row per join (Name, Join/Leave Time, Duration (Minutes),
/// In Waiting Room). Rows spent in the waiting room are left out, and each name's joins are added up
/// - a person who dropped and came back three times is one person with their whole time.
/// Read live on S8, 2026-09-16: 73 rows, 23 people.
/// </summary>
public sealed class ZoomParticipantsReportReader
{
    public const string ReportUrl = "https://zoom.us/account/my/report";
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(45);

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

        // The report's rows for this meeting number, whichever frame holds the table.
        var runs = new List<(ILocator Row, DateTime Start, DateTime End)>();
        var deadline = DateTime.UtcNow + StepTimeout;
        while (runs.Count == 0 && DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var frame in page.Frames)
            {
                foreach (var row in await frame.Locator("tr").AllAsync())
                {
                    string[] cells;
                    try { cells = await row.EvaluateAsync<string[]>("tr => [...tr.querySelectorAll('td')].map(td => td.innerText.replace(/\\s+/g, ' ').trim())"); }
                    catch (PlaywrightException) { continue; }
                    if (!cells.Any(cell => Digits(cell) == meetingId)) continue;
                    var times = cells.Select(ParseReportTime).Where(time => time.HasValue).Select(time => time!.Value).ToArray();
                    // Creation, Start, End: the last two are this run.
                    if (times.Length < 2) continue;
                    runs.Add((row, times[^2], times[^1]));
                }
                if (runs.Count > 0) break;
            }
            if (runs.Count == 0) await Task.Delay(1000, cancellationToken);
        }
        // The class: every run of its meeting that started from an hour and a half before the class's
        // time until the class was over (a meeting ended and started again during the class too).
        // Another day's class on the same meeting number is not this one.
        var classRuns = runs.Where(run => IsThisClass(Local(run.Start).DateTime, classStart)).ToArray();
        if (classRuns.Length == 0) return null;
        var lastEnd = Local(classRuns.Max(run => run.End));
        var rows = new List<string[]>();
        string[] header = [];
        foreach (var run in classRuns)
        {
            var link = run.Row.Locator("td a, td button, td [role=button]").Last;
            await link.ClickAsync();
            var table = page.Locator("table").Filter(new() { HasTextRegex = new Regex("Join Time", RegexOptions.IgnoreCase) }).Last;
            await table.WaitForAsync(new() { State = WaitForSelectorState.Visible });
            await Task.Delay(1500, cancellationToken);                // the rows arrive after the dialog
            string json = await table.EvaluateAsync<string>("""
                table => JSON.stringify({
                  header: [...table.querySelectorAll('thead th, tr:first-child th')].filter(th => th.offsetParent !== null).map(th => th.innerText.replace(/\s+/g, ' ').trim()),
                  rows: [...table.querySelectorAll('tbody tr')].map(tr => [...tr.querySelectorAll('td')].filter(td => td.offsetParent !== null).map(td => (td.getAttribute('title') || td.innerText).replace(/\s+/g, ' ').trim()))
                })
                """);
            using var parsed = JsonDocument.Parse(json);
            header = [.. parsed.RootElement.GetProperty("header").EnumerateArray().Select(value => value.GetString() ?? "")];
            rows.AddRange(parsed.RootElement.GetProperty("rows").EnumerateArray()
                .Select(row => row.EnumerateArray().Select(value => value.GetString() ?? "").ToArray()));
            // Closed again before the next run's link is pressed.
            await page.Keyboard.PressAsync("Escape");
            await Task.Delay(800, cancellationToken);
        }
        return new(Summarize(header, rows), classRuns.Length, rows.Count, lastEnd);
    }

    /// <summary>A run that started from 90 minutes before the class's time until 3 h 15 after it.</summary>
    public static bool IsThisClass(DateTime runStartLocal, DateTime classStart)
    {
        var gap = runStartLocal - classStart;
        return gap >= -TimeSpan.FromMinutes(90) && gap <= TimeSpan.FromHours(3.25);
    }

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
            .Select(row => (Name: Regex.Replace(row[nameAt], @"\s+", " ").Trim(),
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
}
