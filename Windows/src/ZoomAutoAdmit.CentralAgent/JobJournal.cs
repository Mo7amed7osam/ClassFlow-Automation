using System.Text.Json;

namespace ZoomAutoAdmit.CentralAgent;

public enum JobState
{
    Accepted,
    Running,
    Succeeded,
    Failed,
    /// <summary>Given up before starting (the backend revoked it, or it was turned down); it may be offered again.</summary>
    Released,
}

/// <summary>
/// What this agent has done with each job id, and the job messages the backend has not yet
/// acknowledged - on disk, so neither a reconnect nor a restart runs a job twice or loses a result.
///
/// It holds job ids, states and the final result/error only: never a payload, a recording link, a
/// token or anything else from the job.
/// </summary>
public sealed class JobJournal
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly object _gate = new();
    private readonly string? _path;
    private readonly int _keep;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly List<Pending> _outbox = [];

    public sealed record Entry(string JobId, JobState State, DateTimeOffset UpdatedAt, string? FinalMessage);
    public sealed record Pending(string JobId, string Type, string Message);
    private sealed record Snapshot(List<Entry> Entries, List<Pending> Outbox);

    /// <param name="path">Where it is kept; null keeps it in memory only (tests).</param>
    public JobJournal(string? path, int keep = 500, Func<DateTimeOffset>? clock = null)
    {
        _path = path;
        _keep = keep;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        if (_path != null && File.Exists(_path))
        {
            try
            {
                var snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(_path), Json);
                foreach (var entry in snapshot?.Entries ?? []) _entries[entry.JobId] = entry;
                _outbox.AddRange(snapshot?.Outbox ?? []);
            }
            catch (JsonException)
            {
                // A damaged journal is set aside, not silently trusted or deleted.
                File.Move(_path, _path + ".damaged-" + _clock().ToUnixTimeSeconds(), overwrite: true);
            }
        }
    }

    public static string DefaultPath => Path.Combine(CentralAgentPaths.Folder, "jobs.json");

    public JobState? StateOf(string jobId)
    {
        lock (_gate) return _entries.TryGetValue(jobId, out var entry) ? entry.State : null;
    }

    public string? FinalMessageOf(string jobId)
    {
        lock (_gate) return _entries.TryGetValue(jobId, out var entry) ? entry.FinalMessage : null;
    }

    /// <summary>Records an acceptance; false if the job is already accepted, running or finished here.</summary>
    public bool TryAccept(string jobId)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(jobId, out var entry) && entry.State != JobState.Released) return false;
            Set(jobId, JobState.Accepted, null);
            return true;
        }
    }

    /// <summary>Records the start - before the work begins, so a crash mid-job is known afterwards.</summary>
    public bool TryStart(string jobId)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(jobId, out var entry) && entry.State is JobState.Running or JobState.Succeeded or JobState.Failed)
                return false;
            Set(jobId, JobState.Running, null);
            return true;
        }
    }

    public void Release(string jobId)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(jobId, out var entry) && entry.State == JobState.Accepted)
                Set(jobId, JobState.Released, null);
        }
    }

    /// <summary>The result, kept for answering repeats, and queued for the backend until acknowledged.</summary>
    public void Finish(string jobId, bool succeeded, string finalMessage)
    {
        lock (_gate)
        {
            _outbox.RemoveAll(p => p.JobId == jobId && p.Type == "job.started");
            _outbox.Add(new Pending(jobId, succeeded ? "job.succeeded" : "job.failed", finalMessage));
            Set(jobId, succeeded ? JobState.Succeeded : JobState.Failed, finalMessage);
        }
    }

    /// <summary>A job message to keep sending until the backend acknowledges it.</summary>
    public void Enqueue(string jobId, string type, string message)
    {
        lock (_gate)
        {
            _outbox.Add(new Pending(jobId, type, message));
            Save();
        }
    }

    public void Acknowledge(string jobId, string type)
    {
        lock (_gate)
        {
            if (_outbox.RemoveAll(p => p.JobId == jobId && p.Type == type) > 0) Save();
        }
    }

    public IReadOnlyList<Pending> PendingMessages()
    {
        lock (_gate) return [.. _outbox];
    }

    /// <summary>
    /// After a restart: jobs that were running when the agent stopped (their outcome is unknown) are
    /// returned so they can be reported; jobs only accepted are released, so the backend can start them.
    /// </summary>
    public IReadOnlyList<string> RecoverAfterRestart()
    {
        lock (_gate)
        {
            var interrupted = _entries.Values.Where(e => e.State == JobState.Running).Select(e => e.JobId).ToList();
            foreach (var accepted in _entries.Values.Where(e => e.State == JobState.Accepted).ToList())
                Set(accepted.JobId, JobState.Released, null);
            return interrupted;
        }
    }

    private void Set(string jobId, JobState state, string? finalMessage)
    {
        _entries[jobId] = new Entry(jobId, state, _clock(), finalMessage);
        Prune();
        Save();
    }

    private void Prune()
    {
        if (_entries.Count <= _keep) return;
        var waiting = _outbox.Select(p => p.JobId).ToHashSet(StringComparer.Ordinal);
        foreach (var old in _entries.Values
                     .Where(e => (e.State is JobState.Succeeded or JobState.Failed or JobState.Released) && !waiting.Contains(e.JobId))
                     .OrderBy(e => e.UpdatedAt)
                     .Take(_entries.Count - _keep)
                     .ToList())
            _entries.Remove(old.JobId);
    }

    private void Save()
    {
        if (_path == null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Snapshot([.. _entries.Values], [.. _outbox]), Json));
        File.Move(temporary, _path, overwrite: true);
    }
}
