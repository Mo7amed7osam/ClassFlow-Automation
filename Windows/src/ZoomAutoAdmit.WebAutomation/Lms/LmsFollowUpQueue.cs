using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZoomAutoAdmit.WebAutomation.Lms;

/// <summary>What still has to happen to a class after its meeting has started.</summary>
public enum LmsFollowUpStep
{
    /// <summary>Fill in the attendance sheet, an hour and a half in.</summary>
    TakeAttendance,
    /// <summary>Move whoever turned up late from Not-joined to Joined, three hours in.</summary>
    CorrectAttendance,
}

/// <summary>
/// One outstanding job: a class, a step, and the moment it is due. Attempts and the last reason are
/// kept so a job that keeps failing can be seen doing it rather than retrying in silence.
/// </summary>
public sealed record LmsFollowUp
{
    public required string Id { get; init; }
    public required string Group { get; init; }
    public required DateOnly SessionDate { get; init; }
    public required TimeOnly SessionStart { get; init; }
    public required LmsFollowUpStep Step { get; init; }
    public required DateTimeOffset DueAt { get; init; }
    public int Attempts { get; init; }
    public string? LastError { get; init; }

    public string Describe => $"{Group} {SessionDate:yyyy-MM-dd} {SessionStart:HH':'mm} · {Step}";
}

/// <summary>
/// The work a class leaves behind, written down instead of remembered.
///
/// Attendance is filled in an hour and a half after a class starts and corrected at three hours -
/// long after anyone has stopped watching, and long enough that the app will often have been closed
/// and reopened in between. Keeping the list in memory would mean a class that ran overnight simply
/// never got its attendance, with nothing to show that anything was missed. So it lives in a file:
/// what is due is due whenever the app next runs, even a day later, and nothing is silently lost.
///
/// The queue decides what is due and remembers outcomes. It never touches the dashboard itself.
/// </summary>
public sealed class LmsFollowUpQueue
{
    /// <summary>How long after a class starts each step becomes due.</summary>
    public static readonly TimeSpan TakeAttendanceAfter = TimeSpan.FromHours(1.5);
    public static readonly TimeSpan CorrectAttendanceAfter = TimeSpan.FromHours(3);

    /// <summary>
    /// A step that keeps failing is retried a while and then left alone. Giving up quietly forever
    /// is worse than stopping, so the entry stays with its reason on it rather than disappearing.
    /// </summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(15);
    public const int MaximumAttempts = 8;

    /// <summary>
    /// Work older than this is not worth doing. A class from last week whose attendance was never
    /// filled in should be looked at by a person, not written by a program that has no idea what
    /// happened in the meantime.
    /// </summary>
    public static readonly TimeSpan TooOld = TimeSpan.FromHours(36);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LmsFollowUpQueue(string? path = null) =>
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZoomAutoAdmit", "Lms", "follow-up.json");

    public string FilePath => _path;

    /// <summary>
    /// Writes down both steps for a class that has just started. Called again for the same class -
    /// a meeting reopened, the app restarted - it changes nothing, so a class cannot end up with
    /// its attendance taken twice.
    /// </summary>
    public async Task<IReadOnlyList<LmsFollowUp>> ScheduleAsync(
        string group, DateOnly date, TimeOnly start, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        var startedAt = new DateTimeOffset(date.ToDateTime(start), DateTimeOffset.Now.Offset);
        var wanted = new[]
        {
            Build(group, date, start, LmsFollowUpStep.TakeAttendance, startedAt + TakeAttendanceAfter),
            Build(group, date, start, LmsFollowUpStep.CorrectAttendance, startedAt + CorrectAttendanceAfter),
        };
        return await UpdateAsync(items =>
        {
            var known = items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            items.AddRange(wanted.Where(item => !known.Contains(item.Id)));
            return items;
        }, cancellationToken);
    }

    /// <summary>Everything due now, oldest first, skipping what has been left behind.</summary>
    public async Task<IReadOnlyList<LmsFollowUp>> DueAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var all = await ReadAsync(cancellationToken);
        return [.. all
            .Where(item => item.DueAt <= now)
            .Where(item => item.Attempts < MaximumAttempts)
            .Where(item => now - item.DueAt <= TooOld)
            .OrderBy(item => item.DueAt)];
    }

    /// <summary>Done: the entry goes, because it is not owed any more.</summary>
    public Task<IReadOnlyList<LmsFollowUp>> CompleteAsync(LmsFollowUp item, CancellationToken cancellationToken = default) =>
        UpdateAsync(items =>
        {
            items.RemoveAll(existing => existing.Id == item.Id);
            return items;
        }, cancellationToken);

    /// <summary>
    /// Not done: it is due again shortly, with the reason on it. After enough attempts it stops
    /// being retried but stays in the file, so a class that never got its attendance is visible.
    /// </summary>
    public Task<IReadOnlyList<LmsFollowUp>> RetryAsync(
        LmsFollowUp item, string reason, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        UpdateAsync(items =>
        {
            int index = items.FindIndex(existing => existing.Id == item.Id);
            if (index < 0) return items;
            items[index] = items[index] with
            {
                Attempts = items[index].Attempts + 1,
                LastError = reason,
                DueAt = now + RetryAfter,
            };
            return items;
        }, cancellationToken);

    public async Task<IReadOnlyList<LmsFollowUp>> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return Load(); }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<LmsFollowUp>> UpdateAsync(
        Func<List<LmsFollowUp>, List<LmsFollowUp>> change, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var items = change([.. Load()]);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // Written beside and moved into place: a half-written list is worse than an old one.
            string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(items, Json));
                File.Move(temporary, _path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return items;
        }
        finally { _gate.Release(); }
    }

    private List<LmsFollowUp> Load()
    {
        try
        {
            if (!File.Exists(_path)) return [];
            return JsonSerializer.Deserialize<List<LmsFollowUp>>(File.ReadAllText(_path), Json) ?? [];
        }
        // A file that cannot be read is not a reason to fall over; it is a reason to start again.
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>The class and the step name it: the same class twice is the same job.</summary>
    private static LmsFollowUp Build(string group, DateOnly date, TimeOnly start, LmsFollowUpStep step, DateTimeOffset dueAt) =>
        new()
        {
            Id = $"{group.Trim()}|{date:yyyy-MM-dd}|{start:HH\\:mm}|{step}",
            Group = group.Trim(),
            SessionDate = date,
            SessionStart = start,
            Step = step,
            DueAt = dueAt,
        };
}
