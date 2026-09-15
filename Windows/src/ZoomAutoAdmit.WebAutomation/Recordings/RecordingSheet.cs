using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZoomAutoAdmit.WebAutomation.Recordings;

/// <summary>One recording as the recordings sheet lists it: its file, the class it is of, its Drive link.</summary>
public sealed record SheetRecording(string FileName, DateOnly Date, TimeOnly? Start, string Link);

/// <summary>
/// Where the recordings sheet is. The sheet is shared "anyone with the link", one tab per group;
/// the link is pasted once in the app, and a group's own tab link only when its tab is not named
/// after the group. Kept in %LOCALAPPDATA%\ZoomAutoAdmit\Lms\recordings-sheet.json.
/// </summary>
public sealed class RecordingSheetSettings
{
    public string? Url { get; set; }
    /// <summary>Group -> that group's tab link (with its gid), for tabs not named after their group.</summary>
    public Dictionary<string, string> Tabs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "Lms", "recordings-sheet.json");

    public static RecordingSheetSettings Load(string? path = null)
    {
        try
        {
            string file = path ?? DefaultPath;
            if (!File.Exists(file)) return new();
            var loaded = JsonSerializer.Deserialize<RecordingSheetSettings>(File.ReadAllText(file), Json) ?? new();
            loaded.Tabs = new(loaded.Tabs ?? [], StringComparer.OrdinalIgnoreCase);
            return loaded;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save(string? path = null)
    {
        string file = path ?? DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(this, Json));
    }
}

/// <summary>
/// Reads the recordings sheet straight from Google - the same sheet n8n reads - so a Drive link that
/// n8n never sent can still be found and put on its session. Read-only: it downloads the tab as CSV.
/// </summary>
public sealed class RecordingSheetReader(HttpClient? http = null)
{
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(25) };
    private readonly HttpClient _http = http ?? Shared;

    private static readonly Regex SheetId = new(@"/spreadsheets/d/(?<id>[A-Za-z0-9_-]{20,})", RegexOptions.CultureInvariant);
    private static readonly Regex GidPattern = new(@"[?#&]gid=(?<gid>\d+)", RegexOptions.CultureInvariant);
    private static readonly Regex FileStamp = new(@"(?<d>\d{4}-\d{2}-\d{2})(?:[ _T-]+(?<h>[01]\d|2[0-3])[:.]?(?<m>[0-5]\d))?", RegexOptions.CultureInvariant);

    public static string? SpreadsheetIdOf(string? url) => url is null ? null : SheetId.Match(url) is { Success: true } m ? m.Groups["id"].Value : null;
    public static string? GidOf(string? url) => url is null ? null : GidPattern.Match(url) is { Success: true } m ? m.Groups["gid"].Value : null;

    /// <summary>
    /// The group's recordings: its own tab when one was given, else the tab named after the group.
    /// Only rows that name the group are kept, so another group's tab can never lend its link.
    /// </summary>
    public async Task<IReadOnlyList<SheetRecording>> ReadGroupAsync(RecordingSheetSettings settings, string group, CancellationToken token = default)
    {
        string? id = SpreadsheetIdOf(settings.Url) ?? (settings.Tabs.TryGetValue(group, out var t) ? SpreadsheetIdOf(t) : null)
            ?? throw new InvalidOperationException("Paste the recordings sheet's link on the Sessions page first.");
        var urls = new List<string>();
        if (settings.Tabs.TryGetValue(group, out var tab) && GidOf(tab) is { } tabGid)
            urls.Add($"https://docs.google.com/spreadsheets/d/{SpreadsheetIdOf(tab) ?? id}/gviz/tq?tqx=out:csv&gid={tabGid}");
        urls.Add($"https://docs.google.com/spreadsheets/d/{id}/gviz/tq?tqx=out:csv&sheet={Uri.EscapeDataString(group)}");
        if (GidOf(settings.Url) is { } gid) urls.Add($"https://docs.google.com/spreadsheets/d/{id}/gviz/tq?tqx=out:csv&gid={gid}");

        string? lastProblem = null;
        foreach (string url in urls)
        {
            try
            {
                using var response = await _http.GetAsync(url, token);
                if (!response.IsSuccessStatusCode) { lastProblem = $"Google answered {(int)response.StatusCode}"; continue; }
                string csv = await response.Content.ReadAsStringAsync(token);
                if (csv.TrimStart().StartsWith('<')) { lastProblem = "the sheet is not shared with \"anyone with the link\""; continue; }
                var rows = Parse(csv, group);
                if (rows.Count > 0) return rows;
                lastProblem = $"no row of the sheet names {group}";
            }
            catch (HttpRequestException ex) { lastProblem = ex.Message; }
            catch (TaskCanceledException) when (!token.IsCancellationRequested) { lastProblem = "Google did not answer in time"; }
        }
        throw new InvalidOperationException($"The recordings sheet could not be read for {group}: {lastProblem}.");
    }

    /// <summary>Rows that name the group and carry a Drive file link, with the day and time they are of.</summary>
    public static IReadOnlyList<SheetRecording> Parse(string csv, string group, TimeZoneInfo? localZone = null)
    {
        var zone = localZone ?? TimeZoneInfo.Local;
        var list = new List<SheetRecording>();
        foreach (var cells in ReadCsv(csv))
        {
            string? link = cells.Select(c => c.Trim()).FirstOrDefault(RecordingLinks.IsGoogleDriveFileLink);
            if (link == null) continue;
            string text = string.Join(" ", cells.Where(c => !c.Contains("drive.google.com", StringComparison.OrdinalIgnoreCase)));
            if (!text.Contains(group, StringComparison.OrdinalIgnoreCase)) continue;
            string fileName = cells.FirstOrDefault(c => c.Contains(group, StringComparison.OrdinalIgnoreCase))?.Trim() ?? text;
            DateOnly? date = null; TimeOnly? start = null;
            if (FileStamp.Match(fileName) is { Success: true } stamp && DateOnly.TryParseExact(stamp.Groups["d"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fromName))
            {
                date = fromName;
                if (stamp.Groups["h"].Success)
                {
                    // The copy's file name carries the recording's start in UTC (18:45 in Cairo is _1545):
                    // it is moved to this computer's clock, which can also move it to the next day.
                    var utc = fromName.ToDateTime(new TimeOnly(int.Parse(stamp.Groups["h"].Value), int.Parse(stamp.Groups["m"].Value)), DateTimeKind.Utc);
                    var local = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
                    date = DateOnly.FromDateTime(local);
                    start = TimeOnly.FromDateTime(local);
                }
            }
            date ??= cells.Select(ParseDate).FirstOrDefault(d => d != null);
            if (date == null) continue;
            list.Add(new SheetRecording(fileName, date.Value, start, link));
        }
        return list;
    }

    /// <summary>
    /// The recording of that class: the group's row of that day; of several, the one whose time is
    /// inside the class (or nearest its start). Null when the sheet has none for that day.
    /// </summary>
    public static SheetRecording? Pick(IReadOnlyList<SheetRecording> rows, DateOnly date, TimeOnly? start)
    {
        var sameDay = rows.Where(r => r.Date == date).ToList();
        if (sameDay.Count <= 1 || start == null) return sameDay.LastOrDefault();
        return sameDay.OrderBy(r => r.Start is { } t ? Math.Abs((t.ToTimeSpan() - start.Value.ToTimeSpan()).TotalMinutes) : 9999).First();
    }

    private static DateOnly? ParseDate(string cell)
    {
        string value = cell.Trim();
        string[] formats = ["yyyy-MM-dd", "yyyy/MM/dd", "d/M/yyyy", "dd/MM/yyyy", "M/d/yyyy", "MM/dd/yyyy", "yyyy-MM-dd HH:mm:ss", "d MMM yyyy", "MMM d, yyyy"];
        return DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)
            ? DateOnly.FromDateTime(parsed) : null;
    }

    /// <summary>RFC 4180: quoted cells, doubled quotes, line breaks inside quotes.</summary>
    public static IEnumerable<List<string>> ReadCsv(string csv)
    {
        var row = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < csv.Length; i++)
        {
            char c = csv[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < csv.Length && csv[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': quoted = true; break;
                case ',': row.Add(cell.ToString()); cell.Clear(); break;
                case '\r': break;
                case '\n': row.Add(cell.ToString()); cell.Clear(); yield return row; row = []; break;
                default: cell.Append(c); break;
            }
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); yield return row; }
    }
}
