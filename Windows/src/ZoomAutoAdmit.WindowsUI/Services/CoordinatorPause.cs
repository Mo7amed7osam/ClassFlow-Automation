using System.IO;
using System.Text.Json;
using ZoomAutoAdmit.WindowsRuntime.Scheduling;

namespace ZoomAutoAdmit.WindowsUI.Services;

/// <summary>
/// "Run their classes" as one switch for everything of a coordinator's on this PC.
///
/// Turning a coordinator off already dropped their sign-ins and the classes their delegation had
/// added. It did not reach this PC's own schedule: classes of their groups imported here by hand
/// kept opening and kept showing on the Sessions page (2026-09-26: Hosam off, G1 and G2 still
/// there). Now their groups are held here while they are off - those entries are disabled and
/// their classes are not shown - and turning them back on enables exactly the entries this paused,
/// never one a person disabled.
///
/// File: %LOCALAPPDATA%\ZoomAutoAdmit\Lms\paused-coordinators.json.
/// </summary>
public sealed class CoordinatorPause(string? path = null)
{
    /// <summary>Where it is kept when no path is given (tests point it elsewhere).</summary>
    public static string DefaultPath { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "Lms", "paused-coordinators.json");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly object Gate = new();

    public string Path { get; } = path ?? DefaultPath;

    private sealed class Document
    {
        /// <summary>The groups of the coordinators turned off here.</summary>
        public List<string> Groups { get; set; } = [];
        /// <summary>This PC's own entries that were enabled and are disabled because of that.</summary>
        public List<Guid> Paused { get; set; } = [];
    }

    /// <summary>The groups not run on this PC now, because their coordinator is turned off.</summary>
    public IReadOnlySet<string> Groups() => Load().Groups.ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Brings this PC's own entries in line with who is on: the groups of the coordinators turned
    /// off (and not also someone's who is on) are paused, and whatever was paused for a group that
    /// is on again is enabled. Answers how many entries were paused and resumed.
    /// </summary>
    public async Task<(int Paused, int Resumed)> ApplyAsync(IEnumerable<string> offGroups, IEnumerable<string> onGroups,
        IReadOnlyList<MeetingSchedule> schedules, Func<MeetingSchedule, CancellationToken, Task> save, CancellationToken token = default)
    {
        var on = onGroups.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var off = offGroups.Where(g => !string.IsNullOrWhiteSpace(g) && !on.Contains(g)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var document = Load();
        int paused = 0, resumed = 0;

        static string GroupOf(MeetingSchedule s) => string.IsNullOrWhiteSpace(s.GroupName) ? s.AccountId : s.GroupName;
        foreach (var id in document.Paused.ToArray())
        {
            var entry = schedules.FirstOrDefault(s => s.Id == id);
            if (entry == null) { document.Paused.Remove(id); continue; }         // deleted meanwhile
            if (off.Contains(GroupOf(entry))) continue;                            // still off
            if (!entry.Enabled) { await save(entry with { Enabled = true }, token); resumed++; }
            document.Paused.Remove(id);
        }
        foreach (var entry in schedules.Where(s => s.CoordinatorId is not { Length: > 0 } && s.Enabled && off.Contains(GroupOf(s))))
        {
            await save(entry with { Enabled = false }, token);
            if (!document.Paused.Contains(entry.Id)) document.Paused.Add(entry.Id);
            paused++;
        }
        document.Groups = [.. off.OrderBy(g => g, StringComparer.OrdinalIgnoreCase)];
        Save(document);
        return (paused, resumed);
    }

    private Document Load()
    {
        lock (Gate)
        {
            try { if (File.Exists(Path)) return JsonSerializer.Deserialize<Document>(File.ReadAllText(Path), Json) ?? new(); }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
            return new();
        }
    }

    private void Save(Document document)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            string temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, Json));
            File.Move(temporary, Path, overwrite: true);
        }
    }
}
