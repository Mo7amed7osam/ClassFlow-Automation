using System.Text.Json;

namespace ZoomAutoAdmit.WebAutomation.Lms;

/// <summary>
/// The last reading of the LMS - each session's status, record link and attendance - kept on disk
/// so the Sessions page shows it at once and after a restart, and says how old it is.
/// File: %LOCALAPPDATA%\ZoomAutoAdmit\Lms\sessions-cache.json.
/// </summary>
public sealed class LmsSessionCache(string? path = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly object Gate = new();

    public string Path { get; } = path ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "Lms", "sessions-cache.json");

    public sealed record Entry(LmsSessionRunner.LmsSessionInfo Session, DateTimeOffset ReadAt, string Account);

    private sealed class Document { public List<Entry> Sessions { get; set; } = []; }

    public IReadOnlyList<Entry> Read()
    {
        lock (Gate)
        {
            // A row read without its group code (the LMS's list of 2026-09-26, read whole, gave the
            // entire row as the "group") is not a class of anybody's: it is not shown or kept.
            try
            {
                var sessions = File.Exists(Path) ? JsonSerializer.Deserialize<Document>(File.ReadAllText(Path), Json)?.Sessions ?? [] : [];
                return [.. sessions.Where(e => IsGroupCode(e.Session.Group))];
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }
        }
    }

    /// <summary>
    /// Replaces what is known for the days read. A quick read (list only) keeps the link and
    /// attendance learned by an earlier full read of the same session.
    /// </summary>
    public void Merge(IEnumerable<LmsSessionRunner.LmsSessionInfo> sessions, DateOnly from, DateOnly to, string account, bool listOnly) =>
        Merge(sessions, from, to, _ => account, listOnly);

    /// <summary>
    /// The same, for a reading made with several sign-ins at once - each coordinator's classes read
    /// with their own - so each session keeps the account it was read with.
    /// </summary>
    public void Merge(IEnumerable<LmsSessionRunner.LmsSessionInfo> sessions, DateOnly from, DateOnly to,
        Func<LmsSessionRunner.LmsSessionInfo, string> accountOf, bool listOnly)
    {
        lock (Gate)
        {
            var now = DateTimeOffset.Now;
            var old = Read().ToList();
            var fresh = new List<Entry>();
            foreach (var s in sessions)
            {
                var before = old.FirstOrDefault(e => Same(e.Session, s));
                // A session a quick read opened anyway brings its own details, which replace the old ones.
                var session = listOnly && before != null && s.DetailsReadAt == null
                    ? s with
                    {
                        PageUrl = before.Session.PageUrl, RecordLink = before.Session.RecordLink, LinkKind = before.Session.LinkKind,
                        AttendanceTaken = before.Session.AttendanceTaken, PageStatus = s.ListStatus.Length > 0 ? s.ListStatus : before.Session.PageStatus,
                        Actions = before.Session.Actions,
                        Attachments = before.Session.Attachments, HasAssignment = before.Session.HasAssignment,
                        DetailsReadAt = before.Session.DetailsReadAt,
                    }
                    : s;
                fresh.Add(new Entry(session, now, accountOf(s)));
            }
            var kept = old.Where(e => e.Session.Date is not { } d || d < from || d > to).ToList();
            var doc = new Document { Sessions = [.. kept, .. fresh] };
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            string temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(doc, Json));
            File.Move(temporary, Path, overwrite: true);
        }
    }

    /// <summary>A group as the LMS names it: CAI5_AIS4_S8, CAI5_IND1_G2.</summary>
    public static bool IsGroupCode(string? group) =>
        group != null && System.Text.RegularExpressions.Regex.IsMatch(group.Trim(), @"^[A-Za-z]{2,6}\d*_[A-Za-z0-9]+_[A-Za-z0-9]+$");

    private static bool Same(LmsSessionRunner.LmsSessionInfo a, LmsSessionRunner.LmsSessionInfo b) =>
        a.Group.Equals(b.Group, StringComparison.OrdinalIgnoreCase) && a.Date == b.Date && a.Start == b.Start;
}
