using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Playwright;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.WebAutomation.Browser;

namespace ZoomAutoAdmit.WebAutomation.Lms;

/// <summary>
/// Reading the LMS through the API its own dashboard calls, instead of reading its pages.
///
/// The dashboard is a web app over back.depi.eyouthbusiness.com/api/v1: its session list asks
/// <c>admin/sessions/?date_from&amp;date_to</c>, and a session's page asks for its attachments and
/// its assessment. Asked directly, a day of sessions is one answer - each with its focus, whether
/// it is live or physical, its status, its record link and whether its attendance was taken - and
/// what is on a session is two more. Reading the pages instead took a browser per page and broke
/// whenever the page changed how it lays a row out (2026-09-26: the list gained a hidden copy
/// whose rows came out glued into one word).
///
/// The token is the dashboard's own: the account signs in once, in the usual browser profile, and
/// the Authorization header the dashboard sends is kept for this run of the app. Nothing here
/// changes anything on the LMS; the steps that do still press the page's own buttons.
///
/// The LMS's times are not trusted (the list and the page disagree by hours), so a session is
/// matched to its class by group and day; the class's time is the timetable's.
/// </summary>
public sealed partial class LmsSessionRunner
{
    public const string ApiBase = "https://back.depi.eyouthbusiness.com/api/v1";
    private const string DashboardSessionUrl = "https://dashboard.depi.eyouthbusiness.com/group_admin/sessions/";

    private static readonly HttpClient Api = new() { Timeout = TimeSpan.FromSeconds(60) };
    /// <summary>Each account's dashboard token, and until when it is used before signing in again.</summary>
    private static readonly ConcurrentDictionary<string, (string Header, DateTimeOffset Until)> Tokens = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan TokenKeptFor = TimeSpan.FromMinutes(40);
    /// <summary>More sessions than this in one read have only the ones asked for opened.</summary>
    private const int OpenAllUpTo = 80;

    /// <summary>Turned off, every read goes through the pages as before.</summary>
    public static bool UseApi { get; set; } = true;

    /// <summary>The dashboard's Authorization header for this account, signing in for it when none is kept.</summary>
    private async Task<string> ApiTokenAsync(CancellationToken cancellationToken)
    {
        var account = credentials.Read() ?? throw new InvalidOperationException("No LMS sign-in is saved.");
        if (Tokens.TryGetValue(account.Email, out var kept) && kept.Until > DateTimeOffset.Now) return kept.Header;

        var profile = _profiles.GetOrCreate(ProfileName + "-view");
        var browser = await new ZoomBrowserLauncher().LaunchAsync(
            new ZoomBrowserLaunchPlan(profile, Headless: true) { Arguments = ChromeSwitches }, cancellationToken);
        await using var closing = browser;
        var page = browser.Context.Pages.Count > 0 ? browser.Context.Pages[0] : await browser.Context.NewPageAsync();
        page.SetDefaultTimeout((float)StepTimeout.TotalMilliseconds);
        string? header = null;
        page.Request += (_, request) =>
        {
            if (header == null && request.Url.StartsWith(ApiBase, StringComparison.OrdinalIgnoreCase)
                && request.Headers.TryGetValue("authorization", out var value) && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                header = value;
        };
        await SignInAsync(page, account, cancellationToken);
        // The dashboard asks for the signed-in profile at once; its sessions page certainly asks.
        for (int wait = 0; wait < 20 && header == null; wait++) await Task.Delay(500, cancellationToken);
        if (header == null)
        {
            await page.GotoAsync(SessionsUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            for (int wait = 0; wait < 30 && header == null; wait++) await Task.Delay(500, cancellationToken);
        }
        if (header == null) throw new InvalidOperationException("the dashboard did not call its API after signing in");
        Tokens[account.Email] = (header, DateTimeOffset.Now + TokenKeptFor);
        return header;
    }

    /// <summary>One answer of the API; null for 404 (a session with no assessment, for one).</summary>
    private async Task<JsonElement?> ApiGetAsync(string url, CancellationToken cancellationToken, bool retried = false)
    {
        string header = await ApiTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", header);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await Api.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // Expired or taken back: sign in once more, then give up.
            var account = credentials.Read();
            if (account != null) Tokens.TryRemove(account.Email, out _);
            if (!retried) return await ApiGetAsync(url, cancellationToken, retried: true);
            throw new UnauthorizedAccessException($"the LMS API refused the sign-in ({(int)response.StatusCode})");
        }
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The sessions between two days, with what is on each: the same answer the page reading gives,
    /// read from the API. Null when the API cannot be used (the caller then reads the pages).
    /// </summary>
    private async Task<IReadOnlyList<LmsSessionInfo>?> SurveyByApiAsync(DateOnly from, DateOnly to, IReadOnlyCollection<string>? groups,
        bool openEach, Func<string, DateOnly?, TimeOnly?, bool>? openWhen, CancellationToken cancellationToken)
    {
        if (!UseApi) return null;
        try
        {
            var listed = new List<JsonElement>();
            string? next = $"{ApiBase}/admin/sessions/?date_from={from:yyyy-MM-dd}&date_to={to:yyyy-MM-dd}&page_size=500";
            for (int pages = 0; next != null && pages < 20; pages++)
            {
                var answer = await ApiGetAsync(next, cancellationToken) ?? throw new InvalidOperationException("the session list was not found");
                if (answer.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    listed.AddRange(data.EnumerateArray());
                next = answer.TryGetProperty("pagination", out var pagination) && pagination.TryGetProperty("next", out var n)
                       && n.ValueKind == JsonValueKind.String ? n.GetString()!.Replace("http://", "https://") : null;
            }

            var sessions = listed.Select(ApiSession.From).Where(s => s != null).Select(s => s!)
                .Where(s => groups == null || groups.Count == 0 || groups.Contains(s.Group, StringComparer.OrdinalIgnoreCase))
                .GroupBy(s => s.Id).Select(g => g.First())
                .ToList();
            bool openAll = openEach || sessions.Count <= OpenAllUpTo;
            var result = new List<LmsSessionInfo>();
            foreach (var s in sessions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<string>? attachments = null;
                bool? hasAssignment = null;
                DateTimeOffset? detailsAt = null;
                if (openAll || openWhen?.Invoke(s.Group, s.Date, s.Time) == true)
                {
                    var files = await ApiGetAsync($"{ApiBase}/sessions/{s.Id}/attachments/", cancellationToken);
                    attachments = files is { } f && f.TryGetProperty("data", out var list) && list.ValueKind == JsonValueKind.Array
                        ? [.. list.EnumerateArray().Select(a => Text(a, "title")).Where(t => t.Length > 0)]
                        : [];
                    var assessment = await ApiGetAsync($"{ApiBase}/sessions/{s.Id}/assessment/", cancellationToken);
                    hasAssignment = assessment is { } a && a.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object;
                    detailsAt = DateTimeOffset.Now;
                }
                result.Add(new LmsSessionInfo(s.Group, s.Date, s.Time, s.Name, s.Status, DashboardSessionUrl + s.Id, s.Status,
                    s.RecordLink, KindOfLink(s.RecordLink), s.AttendanceTaken, [])
                {
                    Attachments = attachments, HasAssignment = hasAssignment, DetailsReadAt = detailsAt, Mode = s.Mode, Focus = s.Focus,
                });
            }
            ConsoleLogger.Info($"[LMS] Read {result.Count} session(s) of {from:dd MMM}-{to:dd MMM} from the LMS API.");
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"[LMS] The LMS API could not be read ({ex.Message}); reading the pages instead.");
            return null;
        }
    }

    private static string KindOfLink(string link) =>
        link.Length == 0 ? "none"
        : link.Contains("drive.google", StringComparison.OrdinalIgnoreCase) ? "drive"
        : link.Contains("zoom.us", StringComparison.OrdinalIgnoreCase) ? "zoom"
        : "other";

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : "";

    /// <summary>One session as the API lists it.</summary>
    internal sealed record ApiSession(string Id, string Group, DateOnly? Date, TimeOnly? Time, string Name, string Status,
        string Mode, string Focus, string RecordLink, bool? AttendanceTaken)
    {
        public static ApiSession? From(JsonElement row)
        {
            string id = Text(row, "id");
            string group = row.TryGetProperty("group", out var g) && g.ValueKind == JsonValueKind.Object ? Text(g, "label") : "";
            if (id.Length == 0 || !LmsSessionCache.IsGroupCode(group)) return null;
            DateOnly? date = DateOnly.TryParse(Text(row, "date"), System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
            TimeOnly? time = TimeOnly.TryParse(Text(row, "time"), System.Globalization.CultureInfo.InvariantCulture, out var t) ? new TimeOnly(t.Hour, t.Minute) : null;
            string mode = Text(row, "delivery_mode").ToLowerInvariant() switch
            {
                "physical" or "offline" => "Physical",
                "live" or "online" => "Online",
                "hybrid" => "Hybrid",
                _ => "",
            };
            bool? taken = row.TryGetProperty("is_attendance_taken", out var a) && a.ValueKind is JsonValueKind.True or JsonValueKind.False ? a.GetBoolean() : null;
            return new ApiSession(id, group.Trim(), date, time, Text(row, "name"), Text(row, "status_by_trainer").ToLowerInvariant(),
                mode, Text(row, "focus"), Text(row, "recorded_link"), taken);
        }
    }
}
