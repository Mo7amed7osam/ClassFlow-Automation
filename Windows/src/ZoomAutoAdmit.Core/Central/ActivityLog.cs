using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZoomAutoAdmit.Core.Central;

/// <summary>One thing this PC did on its own, as it goes to the central server.</summary>
public sealed record ActivityEvent
{
    /// <summary>Unique for this event, for as long as it may be re-sent: the server keeps it once.</summary>
    public required string EventId { get; init; }
    public required DateTimeOffset At { get; init; }
    /// <summary>What it was, e.g. "lms.TakeAttendance", "class.opened", "class.ended".</summary>
    public required string Kind { get; init; }
    /// <summary>done, failed or skipped.</summary>
    public required string Outcome { get; init; }
    public string? Group { get; init; }
    /// <summary>The class's own date, yyyy-MM-dd.</summary>
    public string? Date { get; init; }
    public string Summary { get; init; } = "";
    public Dictionary<string, JsonElement>? Detail { get; init; }

    public static string NewId(string kind) => $"{Environment.MachineName}:{kind}:{Guid.NewGuid():N}";
}

/// <summary>
/// What this PC did, written down here as it happens.
///
/// Every PC runs its own classes without waiting for anyone, so what it did has to be kept on the
/// PC first and sent afterwards: one small file per event, in order, sent by the agent and removed
/// once the server has it. A server that was off for a day misses nothing, and a class is never
/// held up by the network. Nothing here is an instruction - it is a note about something that has
/// already happened.
/// </summary>
public sealed class ActivityLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly object _gate = new();

    /// <summary>Events older than this are dropped unsent: nobody needs last month's steps.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);

    public string Folder { get; }

    public ActivityLog(string? folder = null) =>
        Folder = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZoomAutoAdmit", "Central", "activity");

    /// <summary>Writes one event down. Never throws: a note must not break the work it describes.</summary>
    public void Write(ActivityEvent activity)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Folder);
                // The name orders the files by when they happened; the id keeps two in one millisecond apart.
                string name = $"{activity.At.ToUniversalTime():yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.json";
                string path = Path.Combine(Folder, name);
                File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(activity, Json));
                File.Move(path + ".tmp", path, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
    }

    /// <summary>
    /// A shorthand for the usual note: what was done, for which class, and how it went.
    /// </summary>
    public void Write(string kind, string outcome, string summary, string? group = null, DateOnly? day = null,
        DateTimeOffset? at = null, Dictionary<string, JsonElement>? detail = null) =>
        Write(new ActivityEvent
        {
            EventId = ActivityEvent.NewId(kind),
            At = at ?? DateTimeOffset.Now,
            Kind = kind,
            Outcome = outcome,
            Group = group,
            Date = day?.ToString("yyyy-MM-dd"),
            Summary = summary.Length <= 500 ? summary : summary[..500],
            Detail = detail,
        });

    /// <summary>The events waiting to be sent, oldest first. Unreadable files are removed.</summary>
    public IReadOnlyList<(string Path, ActivityEvent Event)> Pending(int maximum = 200, DateTimeOffset? now = null)
    {
        var waiting = new List<(string, ActivityEvent)>();
        if (!Directory.Exists(Folder)) return waiting;
        var cutoff = (now ?? DateTimeOffset.Now) - KeepFor;
        foreach (var path in Directory.GetFiles(Folder, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            if (waiting.Count >= maximum) break;
            ActivityEvent? activity;
            try { activity = JsonSerializer.Deserialize<ActivityEvent>(File.ReadAllText(path), Json); }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                if (ex is JsonException) Forget(path);
                continue;
            }
            if (activity is null || string.IsNullOrEmpty(activity.EventId)) { Forget(path); continue; }
            if (activity.At < cutoff) { Forget(path); continue; }
            waiting.Add((path, activity));
        }
        return waiting;
    }

    /// <summary>The server has it (or it is past saving): this PC need not keep it.</summary>
    public void Forget(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
