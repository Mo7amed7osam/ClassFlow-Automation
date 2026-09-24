using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZoomAutoAdmit.Roster;

namespace ZoomAutoAdmit.WindowsUI.Services;

/// <summary>
/// What the extension's attendance page (WebAttendance, in the app) is fed: the app's Zoom meetings
/// as "tabs" - one per class, a group at its scheduled time - and every Participants read the app
/// takes of them, from the snapshot files the attendance collector writes
/// (%LOCALAPPDATA%\ZoomAutoAdmit\Attendance\&lt;session&gt;\*.json). A class keeps one id however many
/// app sessions it spans (a restart mid-class), so the page keeps one meeting for it.
/// </summary>
public sealed class ExtensionAttendanceFeed(string? root = null, IGroupRosterService? rosters = null)
{
    public string Root { get; } = root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "Attendance");
    private readonly IGroupRosterService _rosters = rosters ?? new GroupRosterStore();
    private static readonly Regex ZoomMeetingUrl = new(@"^https://([a-z0-9-]+\.)*zoom\.us/(wc/(join/)?|j/|s/|w/)\d{6,15}(/|$|\?)", RegexOptions.IgnoreCase);

    public sealed record Snapshot(string File, string ClassKey, string Group, DateTime Start, string MeetingUrl,
        DateTimeOffset At, string Trigger, IReadOnlyList<string> Names);

    public sealed record MeetingTab(int Id, string Url, string Title, bool Live, string Group, DateTime Start);

    /// <summary>One snapshot file, or null when it is not a snapshot of a known class.</summary>
    public static Snapshot? Read(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("meeting", out var meeting) || meeting.ValueKind != JsonValueKind.Object) return null;
            string group = meeting.TryGetProperty("accountId", out var g) ? g.GetString() ?? "" : "";
            if (group.Length == 0) return null;
            var start = meeting.TryGetProperty("scheduledStart", out var s) && s.TryGetDateTimeOffset(out var st) ? st.ToLocalTime().DateTime : DateTime.MinValue;
            string url = meeting.TryGetProperty("meetingUrl", out var u) ? u.GetString() ?? "" : "";
            var at = root.TryGetProperty("timestamp", out var t) && t.TryGetDateTimeOffset(out var ts) ? ts : File.GetLastWriteTimeUtc(path);
            string trigger = root.TryGetProperty("trigger", out var tr) ? tr.GetString() ?? "" : "";
            var names = root.TryGetProperty("participants", out var p) && p.ValueKind == JsonValueKind.Array
                ? p.EnumerateArray().Select(x => x.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "").Where(n => n.Trim().Length > 0).ToList()
                : [];
            string key = $"{group}|{start:yyyy-MM-dd HH:mm}";
            return new Snapshot(Path.GetFileName(path), key, group, start, url, at, trigger, names);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Snapshots taken since a moment, oldest first.</summary>
    public IReadOnlyList<Snapshot> Since(DateTime sinceLocal)
    {
        if (!Directory.Exists(Root)) return [];
        var list = new List<Snapshot>();
        foreach (var dir in Directory.EnumerateDirectories(Root))
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                if (File.GetLastWriteTime(file) < sinceLocal) continue;
                if (Read(file) is { } s) list.Add(s);
            }
        return [.. list.OrderBy(s => s.At)];
    }

    /// <summary>The page's id for a class: stable across app runs (FNV-1a of its key).</summary>
    public static int TabId(string classKey)
    {
        uint hash = 2166136261;
        foreach (char c in classKey) { hash ^= c; hash *= 16777619; }
        int id = (int)(hash & 0x7FFFFFFF);
        return id == 0 ? 1 : id;
    }

    /// <summary>A Zoom address the page recognises as a meeting (it needs the meeting number in it).</summary>
    public static string PageUrl(string meetingUrl, int tabId) =>
        ZoomMeetingUrl.IsMatch(meetingUrl) ? meetingUrl : $"https://zoom.us/j/{tabId:D10}";

    /// <summary>The classes read in the last twelve hours; live when read in the last five minutes.</summary>
    public IReadOnlyList<MeetingTab> Tabs()
    {
        var now = DateTimeOffset.Now;
        return [.. Since(DateTime.Now.AddHours(-12))
            .GroupBy(s => s.ClassKey)
            .Select(g =>
            {
                var last = g.MaxBy(s => s.At)!;
                int id = TabId(g.Key);
                return new MeetingTab(id, PageUrl(last.MeetingUrl, id), $"{last.Group} · {last.Start:ddd dd MMM HH:mm}",
                    now - last.At < TimeSpan.FromMinutes(5), last.Group, last.Start);
            })
            .OrderByDescending(t => t.Start)];
    }

    /// <summary>The newest read of a class, for "Capture now".</summary>
    public Snapshot? Latest(int tabId) =>
        Since(DateTime.Now.AddHours(-12)).Where(s => TabId(s.ClassKey) == tabId).MaxBy(s => s.At);

    // ------------------------------------------------------------------ the page's results

    public sealed record ClassResult(string Group, DateTime Start, List<string> Present, List<string> Review, List<string> Absent,
        bool Finalized, DateTimeOffset UpdatedAt)
    {
        /// <summary>
        /// The students the match is not sure about, with the Zoom name each might be and how sure
        /// it is - so a person can say yes or no instead of the class quietly going up without them.
        /// </summary>
        public List<ReviewName> Attention { get; init; } = [];
    }

    /// <summary>One student the match wants a person's word on: who, seen as what, and how sure.</summary>
    public sealed record ReviewName(string Student, string SeenAs, int Percent, string Why);

    /// <summary>
    /// A person's answer about one uncertain student: present after all, or not them. The class's
    /// saved result is rewritten, so the next upload carries the answer - nothing is sent from here.
    /// </summary>
    public static ClassResult? AnswerAttention(string group, DateOnly date, TimeOnly start, string student, bool present)
    {
        lock (ResultsGate)
        {
            var all = LoadResults();
            var found = all.FirstOrDefault(pair => pair.Key.EndsWith("|app", StringComparison.Ordinal)
                                                   && pair.Value.Group.Equals(group, StringComparison.OrdinalIgnoreCase)
                                                   && DateOnly.FromDateTime(pair.Value.Start) == date
                                                   && ZoomAutoAdmit.WindowsRuntime.Scheduling.ScheduleTiming.IsSameClass(
                                                       start.ToTimeSpan(), TimeOnly.FromDateTime(pair.Value.Start).ToTimeSpan()));
            if (found.Value == null) return null;

            var was = found.Value;
            var present2 = new List<string>(was.Present);
            if (present && !present2.Contains(student, StringComparer.OrdinalIgnoreCase)) present2.Add(student);
            if (!present) present2.RemoveAll(name => name.Equals(student, StringComparison.OrdinalIgnoreCase));
            var absent = new List<string>(was.Absent);
            if (!present && !absent.Contains(student, StringComparer.OrdinalIgnoreCase)) absent.Add(student);
            if (present) absent.RemoveAll(name => name.Equals(student, StringComparison.OrdinalIgnoreCase));

            var answered = was with
            {
                Present = present2,
                Absent = absent,
                Review = [.. was.Review.Where(name => !name.Equals(student, StringComparison.OrdinalIgnoreCase))],
                UpdatedAt = DateTimeOffset.Now,
                Attention = [.. was.Attention.Where(item => !item.Student.Equals(student, StringComparison.OrdinalIgnoreCase))],
            };
            all[found.Key] = answered;
            Directory.CreateDirectory(Path.GetDirectoryName(ResultsPath)!);
            string temporary = ResultsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(all, ResultsJson));
            File.Move(temporary, ResultsPath, overwrite: true);
            return answered;
        }
    }

    public static string ResultsPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "Lms", "extension-results.json");
    private static readonly object ResultsGate = new();
    private static readonly JsonSerializerOptions ResultsJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Keeps what the page shows for a class, keyed by its class key, for the LMS upload.</summary>
    public void SaveResults(JsonElement p)
    {
        int tabId = p.GetProperty("tabId").GetInt32();
        var tab = Tabs().FirstOrDefault(t => t.Id == tabId);
        if (tab == null) return;
        List<string> List(string name) => p.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? [.. a.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0)] : [];
        var result = new ClassResult(tab.Group, tab.Start, List("present"), List("review"), List("absent"),
            p.TryGetProperty("finalized", out var f) && f.ValueKind == JsonValueKind.True, DateTimeOffset.Now);
        lock (ResultsGate)
        {
            var all = LoadResults();
            all[$"{tab.Group}|{tab.Start:yyyy-MM-dd HH:mm}"] = result;
            Directory.CreateDirectory(Path.GetDirectoryName(ResultsPath)!);
            string temporary = ResultsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(all, ResultsJson));
            File.Move(temporary, ResultsPath, overwrite: true);
        }
    }

    /// <summary>The page's results for a class, if it has shown them.</summary>
    public static ClassResult? ResultsFor(string group, DateOnly date, TimeOnly start)
    {
        lock (ResultsGate)
            return LoadResults().TryGetValue($"{group}|{date.ToDateTime(start):yyyy-MM-dd HH:mm}", out var r) ? r : null;
    }

    /// <summary>
    /// What the app itself matched for a class (AppAttendanceMatcher: the name rules, then the AI),
    /// kept beside the page's results under its own key so neither overwrites the other.
    /// </summary>
    public static void SaveAppResults(string group, DateTime start, IReadOnlyList<string> present, IReadOnlyList<string> review,
        IReadOnlyList<string> absent, IReadOnlyList<ReviewName>? attention = null)
    {
        var result = new ClassResult(group, start, [.. present], [.. review], [.. absent], false, DateTimeOffset.Now)
        {
            Attention = [.. attention ?? []],
        };
        lock (ResultsGate)
        {
            var all = LoadResults();
            all[$"{group}|{start:yyyy-MM-dd HH:mm}|app"] = result;
            Directory.CreateDirectory(Path.GetDirectoryName(ResultsPath)!);
            string temporary = ResultsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(all, ResultsJson));
            File.Move(temporary, ResultsPath, overwrite: true);
        }
    }

    /// <summary>
    /// The page's (or, with <paramref name="app"/>, the app's) results for the group's class nearest a
    /// start time on that day - a class opened by hand is recorded at the minute it went live.
    /// </summary>
    public static ClassResult? ResultsNear(string group, DateOnly date, TimeOnly start, TimeSpan window, bool app)
    {
        var wanted = date.ToDateTime(start);
        lock (ResultsGate)
            return LoadResults()
                .Where(kv => kv.Key.EndsWith("|app", StringComparison.Ordinal) == app &&
                             kv.Value.Group.Equals(group, StringComparison.OrdinalIgnoreCase) &&
                             DateOnly.FromDateTime(kv.Value.Start) == date &&
                             Math.Abs((kv.Value.Start - wanted).TotalMinutes) <= window.TotalMinutes)
                .OrderBy(kv => Math.Abs((kv.Value.Start - wanted).TotalMinutes))
                .Select(kv => kv.Value).FirstOrDefault();
    }

    public static ClassResult? AppResultsFor(string group, DateOnly date, TimeOnly start)
    {
        lock (ResultsGate)
            return LoadResults().TryGetValue($"{group}|{date.ToDateTime(start):yyyy-MM-dd HH:mm}|app", out var r) ? r : null;
    }

    private static Dictionary<string, ClassResult> LoadResults()
    {
        try
        {
            return File.Exists(ResultsPath)
                ? JsonSerializer.Deserialize<Dictionary<string, ClassResult>>(File.ReadAllText(ResultsPath), ResultsJson) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    /// <summary>Each group's roster names, in roster order.</summary>
    public async Task<IReadOnlyList<(string Group, IReadOnlyList<string> Names)>> RostersAsync()
    {
        try
        {
            var groups = await _rosters.ListAsync();
            return [.. groups.Select(g => (g.GroupId, (IReadOnlyList<string>)[.. g.Students.OrderBy(s => s.Order).Select(s => s.FullName)]))];
        }
        catch { return []; }
    }
}
