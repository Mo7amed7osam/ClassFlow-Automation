using System.Text.Json;
using System.Text.Json.Serialization;
using ZoomAutoAdmit.Core.Central;

namespace ZoomAutoAdmit.WebAutomation.Lms;

/// <summary>What still has to happen to a class after its meeting has started.</summary>
public enum LmsFollowUpStep
{
    /// <summary>Fill in the attendance sheet, an hour and a half in (with a fresh name match first).</summary>
    TakeAttendance,
    /// <summary>Move whoever turned up late from Not-joined to Joined, three hours in.</summary>
    CorrectAttendance,
    /// <summary>Mark the LMS session complete after the late-attendance correction.</summary>
    CompleteSession,
    /// <summary>
    /// Put the Zoom recording's share link on the finished session (read from My Recordings, even
    /// while Zoom is still processing it). The Drive link n8n reports later replaces it.
    /// </summary>
    AttachZoomRecording,
    /// <summary>Run Session when the meeting went live. Never queued: kept in the history only.</summary>
    RunSession,
    /// <summary>The Drive link found in the recordings sheet, put on the session. History only.</summary>
    AttachDriveLink,
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
    private readonly ActivityLog _activity;

    public LmsFollowUpQueue(string? path = null, ActivityLog? activity = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZoomAutoAdmit", "Lms", "follow-up.json");
        // A queue given its own file is a test's or a tool's: its notes stay beside it, never in the
        // folder the agent sends from.
        _activity = activity ?? (path is null
            ? new ActivityLog()
            : new ActivityLog(Path.Combine(Path.GetDirectoryName(_path)!, "activity")));
    }

    public string FilePath => _path;

    /// <summary>What was done (or last failed) for each step, kept after the step leaves the queue.</summary>
    public sealed record Outcome(string Id, string Group, DateOnly SessionDate, TimeOnly SessionStart, LmsFollowUpStep Step,
        bool Succeeded, string Message, DateTimeOffset At, int Attempts);

    /// <summary>follow-up.json -> follow-up-history.json, beside it.</summary>
    private string HistoryPath => Path.Combine(Path.GetDirectoryName(_path)!, Path.GetFileNameWithoutExtension(_path) + "-history.json");

    public async Task RecordAsync(LmsFollowUp item, bool succeeded, string message, CancellationToken cancellationToken = default)
    {
        // The same note goes to the central server: this PC does its classes on its own, and this is
        // how the server is told what was done. Written here, sent by the agent whenever it answers.
        _activity.Write($"lms.{item.Step}", succeeded ? "done" : "failed", message, item.Group, item.SessionDate);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var all = LoadHistory();
            all.RemoveAll(o => o.Id == item.Id);
            all.Add(new Outcome(item.Id, item.Group, item.SessionDate, item.SessionStart, item.Step, succeeded, message,
                DateTimeOffset.Now, item.Attempts + 1));
            // A few months is plenty for the Sessions page.
            all = [.. all.Where(o => o.At > DateTimeOffset.Now.AddDays(-120))];
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            string temporary = HistoryPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(all, Json));
            File.Move(temporary, HistoryPath, overwrite: true);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<Outcome>> ReadHistoryAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return LoadHistory(); }
        finally { _gate.Release(); }
    }

    private List<Outcome> LoadHistory()
    {
        try { return File.Exists(HistoryPath) ? JsonSerializer.Deserialize<List<Outcome>>(File.ReadAllText(HistoryPath), Json) ?? [] : []; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

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
            Build(group, date, start, LmsFollowUpStep.CompleteSession, startedAt + CorrectAttendanceAfter),
            Build(group, date, start, LmsFollowUpStep.AttachZoomRecording, startedAt + CorrectAttendanceAfter),
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
    /// The class's meeting has ended: the late-joiner correction runs once more with everyone the
    /// meeting saw until its last minute (added again when it was already done at 3 h), and the
    /// steps after it are brought forward to the same moment. Complete Session is never added back.
    /// </summary>
    public Task<IReadOnlyList<LmsFollowUp>> ScheduleFinalAttendanceAsync(
        string group, DateOnly date, TimeOnly start, DateTimeOffset due, CancellationToken cancellationToken = default) =>
        UpdateAsync(items =>
        {
            bool IsClass(LmsFollowUp item) => item.Group.Equals(group, StringComparison.OrdinalIgnoreCase) &&
                                              item.SessionDate == date && item.SessionStart == start;
            var correct = Build(group, date, start, LmsFollowUpStep.CorrectAttendance, due);
            if (items.FindIndex(item => item.Id == correct.Id) < 0) items.Add(correct);
            for (int i = 0; i < items.Count; i++)
                if (IsClass(items[i]) &&
                    items[i].Step is LmsFollowUpStep.CorrectAttendance or LmsFollowUpStep.CompleteSession or LmsFollowUpStep.AttachZoomRecording &&
                    items[i].DueAt > due)
                    items[i] = items[i] with { DueAt = due };
            return items;
        }, cancellationToken);

    /// <summary>Not due yet after all (its meeting is still running): due again later, attempts untouched.</summary>
    public Task<IReadOnlyList<LmsFollowUp>> PostponeAsync(
        LmsFollowUp item, DateTimeOffset until, string reason, CancellationToken cancellationToken = default) =>
        UpdateAsync(items =>
        {
            int index = items.FindIndex(existing => existing.Id == item.Id);
            if (index >= 0) items[index] = items[index] with { DueAt = until, LastError = reason };
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

    /// <summary>
    /// Forgets a class altogether (the admin deleting a test session): what it still owed and what
    /// was done for it. <paramref name="isThisClass"/> says which start times are that class (a
    /// meeting opened by hand at 18:51 is the 19:00 class). Returns how many entries went.
    /// </summary>
    public async Task<int> ForgetClassAsync(string group, DateOnly date, Func<TimeOnly, bool> isThisClass, CancellationToken cancellationToken = default)
    {
        bool Match(string g, DateOnly d, TimeOnly t) =>
            g.Equals(group.Trim(), StringComparison.OrdinalIgnoreCase) && d == date && isThisClass(t);
        int removed = 0;
        await UpdateAsync(items => { removed += items.RemoveAll(i => Match(i.Group, i.SessionDate, i.SessionStart)); return items; }, cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var all = LoadHistory();
            int gone = all.RemoveAll(o => Match(o.Group, o.SessionDate, o.SessionStart));
            if (gone > 0)
            {
                removed += gone;
                string temporary = HistoryPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(all, Json));
                File.Move(temporary, HistoryPath, overwrite: true);
            }
        }
        finally { _gate.Release(); }
        return removed;
    }

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
