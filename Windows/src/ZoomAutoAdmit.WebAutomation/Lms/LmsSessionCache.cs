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
            try { return File.Exists(Path) ? JsonSerializer.Deserialize<Document>(File.ReadAllText(Path), Json)?.Sessions ?? [] : []; }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }
        }
    }

    /// <summary>
    /// Replaces what is known for the days read. A quick read (list only) keeps the link and
    /// attendance learned by an earlier full read of the same session.
    /// </summary>
    public void Merge(IEnumerable<LmsSessionRunner.LmsSessionInfo> sessions, DateOnly from, DateOnly to, string account, bool listOnly)
    {
        lock (Gate)
        {
            var now = DateTimeOffset.Now;
            var old = Read().ToList();
            var fresh = new List<Entry>();
            foreach (var s in sessions)
            {
                var before = old.FirstOrDefault(e => Same(e.Session, s));
                var session = listOnly && before != null
                    ? s with
                    {
                        PageUrl = before.Session.PageUrl, RecordLink = before.Session.RecordLink, LinkKind = before.Session.LinkKind,
                        AttendanceTaken = before.Session.AttendanceTaken, PageStatus = s.ListStatus.Length > 0 ? s.ListStatus : before.Session.PageStatus,
                        Actions = before.Session.Actions,
                        Attachments = before.Session.Attachments, HasAssignment = before.Session.HasAssignment,
                        DetailsReadAt = before.Session.DetailsReadAt,
                    }
                    : s;
                fresh.Add(new Entry(session, now, account));
            }
            var kept = old.Where(e => e.Session.Date is not { } d || d < from || d > to).ToList();
            var doc = new Document { Sessions = [.. kept, .. fresh] };
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            string temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(doc, Json));
            File.Move(temporary, Path, overwrite: true);
        }
    }

    private static bool Same(LmsSessionRunner.LmsSessionInfo a, LmsSessionRunner.LmsSessionInfo b) =>
        a.Group.Equals(b.Group, StringComparison.OrdinalIgnoreCase) && a.Date == b.Date && a.Start == b.Start;
}
