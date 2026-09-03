using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZoomAutoAdmit.Attendance;

namespace ZoomAutoAdmit.WindowsUI.Services;

public sealed record SnapshotDisplay(string Id, AttendanceSnapshot Snapshot)
{
    public string Label => $"{Snapshot.Timestamp.LocalDateTime:g} · {Snapshot.Meeting?.AccountDisplayName ?? "Unknown account"} · {Snapshot.Source} · {Snapshot.Reason} · {Snapshot.SessionId.ToString()[..8]}";
}
public sealed record AttendanceHistory(IReadOnlyList<SnapshotDisplay> Snapshots, int Unreadable, bool Limited,
    IReadOnlyList<AttendanceCaptureIssue>? Issues = null)
{
    public IReadOnlyList<AttendanceCaptureIssue> CaptureIssues => Issues ?? [];
}
public interface IAttendanceHistoryReader
{
    Task<AttendanceHistory> ReadAsync(CancellationToken token = default);
}
/// <summary>
/// Removes whole sessions from the collector's store. A session is a folder, so its readings and
/// its failed-read records go together; nothing is left behind to reappear on the next refresh.
/// </summary>
public interface IAttendanceHistoryEraser
{
    Task<int> DeleteSessionsAsync(IReadOnlyList<Guid> sessionIds, CancellationToken token = default);
}

public interface IAttendanceUiActions
{
    Task<UiOperationResult> CaptureAttendanceAsync(Guid sessionId, CancellationToken token = default);
    /// <summary>Meetings running right now, so a capture is possible before the first snapshot exists.</summary>
    Task<IReadOnlyList<Guid>> GetActiveSessionIdsAsync(CancellationToken token = default) =>
        Task.FromResult<IReadOnlyList<Guid>>([]);
}

// Presentation-side reader of the existing append-only collector store; never rewrites snapshots.
public sealed class AttendanceHistoryReader(string? root = null) : IAttendanceHistoryReader, IAttendanceHistoryEraser
{
    private readonly string _root = root ?? new JsonAttendanceSnapshotStore().RootDirectory;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };
    private readonly Dictionary<string, (long Length, DateTime Written, SnapshotDisplay Value)> _cache = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<AttendanceHistory> ReadAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try { return await Task.Run(() => Read(token), token); }
        finally { _gate.Release(); }
    }

    public async Task<int> DeleteSessionsAsync(IReadOnlyList<Guid> sessionIds, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);
        await _gate.WaitAsync(token);
        try { return await Task.Run(() => Delete(sessionIds, token), token); }
        finally { _gate.Release(); }
    }

    private int Delete(IReadOnlyList<Guid> sessionIds, CancellationToken token)
    {
        int deleted = 0;
        foreach (var sessionId in sessionIds.Where(id => id != Guid.Empty).Distinct())
        {
            token.ThrowIfCancellationRequested();
            // Built from the store root and a parsed Guid, so this can only ever name a session folder.
            var folder = Path.Combine(_root, sessionId.ToString());
            try
            {
                if (!Directory.Exists(folder)) continue;
                Directory.Delete(folder, recursive: true);
                deleted++;
                foreach (var cached in _cache.Keys.Where(path => path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToArray())
                    _cache.Remove(cached);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return deleted;
    }

    // Failed captures are stored beside the snapshots with their own extension; they are never attendance.
    private IReadOnlyList<AttendanceCaptureIssue> ReadIssues(EnumerationOptions enumeration, CancellationToken token)
    {
        List<AttendanceCaptureIssue> issues = [];
        foreach (var path in Directory.EnumerateDirectories(_root, "*", enumeration)
                     .Where(p => Guid.TryParse(Path.GetFileName(p), out _))
                     .SelectMany(p => Directory.EnumerateFiles(p, "*.issue", enumeration))
                     .OrderByDescending(Path.GetFileName).Take(200))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (new FileInfo(path).Length > 64 * 1024) continue;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var issue = JsonSerializer.Deserialize<AttendanceCaptureIssue>(stream, Options);
                if (issue != null && issue.SessionId != Guid.Empty && !string.IsNullOrWhiteSpace(issue.Reason))
                    issues.Add(issue with { Reason = issue.Reason.Length > 300 ? issue.Reason[..300] : issue.Reason });
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException) { }
        }
        return issues.OrderByDescending(issue => issue.Timestamp).ToArray();
    }

    private AttendanceHistory Read(CancellationToken token)
    {
        if (!Directory.Exists(_root)) return new([], 0, false);
        var enumeration = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        var paths = Directory.EnumerateDirectories(_root, "*", enumeration)
            .Where(p => Guid.TryParse(Path.GetFileName(p), out _))
            .SelectMany(p => Directory.EnumerateFiles(p, "*.json", enumeration))
            .OrderByDescending(Path.GetFileName).Take(2001).ToArray();
        List<SnapshotDisplay> results = [];
        int unreadable = 0;
        foreach (var path in paths.Take(2000))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                if (info.Length > 4 * 1024 * 1024) throw new InvalidDataException();
                if (_cache.TryGetValue(path, out var cached) && cached.Length == info.Length && cached.Written == info.LastWriteTimeUtc)
                { results.Add(cached.Value); continue; }
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var snapshot = JsonSerializer.Deserialize<AttendanceSnapshot>(stream, Options);
                if (snapshot == null || snapshot.SessionId != Guid.Parse(Path.GetFileName(Path.GetDirectoryName(path))!) ||
                    snapshot.Participants == null || snapshot.Participants.Count > 10000 ||
                    snapshot.Participants.Any(p => p == null || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 1000))
                    throw new InvalidDataException();
                var item = new SnapshotDisplay(path, snapshot);
                _cache[path] = (info.Length, info.LastWriteTimeUtc, item);
                results.Add(item);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or ArgumentException) { unreadable++; }
        }
        var retained = paths.Take(2000).ToHashSet();
        foreach (var removed in _cache.Keys.Where(k => !retained.Contains(k)).ToArray()) _cache.Remove(removed);
        return new(results.OrderByDescending(s => s.Snapshot.Timestamp).ToArray(), unreadable, paths.Length > 2000,
            ReadIssues(enumeration, token));
    }
}
