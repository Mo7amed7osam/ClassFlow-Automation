using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Meetings;

namespace ZoomAutoAdmit.Attendance;

/// <summary>Session-scoped event wiring. Admission handlers enqueue reads, never block clicking.</summary>
public sealed class AttendanceLifecycleBridge : IAsyncDisposable
{
    private sealed class Entry(AttendanceCollector collector)
    {
        public AttendanceCollector Collector { get; } = collector;
        public Task Pending { get; set; } = Task.CompletedTask;
        public bool Ending { get; set; }
    }

    private readonly object _sync = new();
    private readonly Dictionary<Guid, Entry> _entries = new();
    private readonly HashSet<Guid> _ended = new();
    private readonly MeetingLifecycleEvents _events;
    private readonly Func<MeetingLaunchContext, IAttendanceParticipantSource> _sources;
    private readonly IAttendanceSnapshotStore _store;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private bool _disposed;

    /// <summary>
    /// How often the participant list is read during a meeting. Every minute, like the Chrome
    /// extension's frequent reads (it read every 15 s): join, leave and time in the meeting are only
    /// as exact as the reads are close together, and a 15-minute gap made them guesses.
    /// </summary>
    public static readonly TimeSpan DefaultCaptureInterval = TimeSpan.FromMinutes(1);
    private readonly TimeSpan _captureInterval;

    public AttendanceLifecycleBridge(MeetingLifecycleEvents events,
        Func<MeetingLaunchContext, IAttendanceParticipantSource> sources,
        IAttendanceSnapshotStore? store = null, TimeProvider? timeProvider = null, Action<string>? log = null,
        TimeSpan? captureInterval = null)
    {
        _captureInterval = captureInterval ?? DefaultCaptureInterval;
        _events = events;
        _sources = sources;
        _store = store ?? new JsonAttendanceSnapshotStore();
        _time = timeProvider ?? TimeProvider.System;
        _log = log ?? ConsoleLogger.Info;
        _events.Lifecycle += OnLifecycleAsync;
        _events.AdmissionVerified += OnAdmission;
    }

    private Task OnLifecycleAsync(MeetingLifecycleEvent message)
    {
        var context = message.Context;
        var id = context.Session.SessionId;
        lock (_sync)
        {
            if (_disposed || _ended.Contains(id)) return Task.CompletedTask;
            if (message.Kind == MeetingLifecycleEventKind.Ending) return EndLocked(id);
            if (_entries.TryGetValue(id, out var existing)) return existing.Pending;
            var metadata = new AttendanceMeetingMetadata(context.Session.GroupId, context.Account.DisplayName,
                context.Session.MeetingUrl.GetLeftPart(UriPartial.Path), context.Session.StartTime,
                context.EngineType.ToString());
            var collector = new AttendanceCollector(id, _sources(context), _store, _time,
                interval: _captureInterval, log: _log, metadata: metadata);
            var entry = new Entry(collector);
            _entries.Add(id, entry);
            return QueueLocked(entry, () => collector.StartAsync());
        }
    }

    private void OnAdmission(Guid id)
    {
        lock (_sync)
        {
            if (!_disposed && _entries.TryGetValue(id, out var entry) && !entry.Ending)
                QueueLocked(entry, async () => { await entry.Collector.OnAdmissionAsync(id); });
        }
    }

    public Task CaptureManualAsync(Guid sessionId)
    {
        lock (_sync)
        {
            if (_disposed || !_entries.TryGetValue(sessionId, out var entry) || entry.Ending)
                return Task.CompletedTask;
            return QueueLocked(entry, async () => { await entry.Collector.CaptureManualAsync(); });
        }
    }

    // Useful at host shutdown and for deterministic integration tests.
    public Task DrainAsync(Guid sessionId)
    {
        lock (_sync) return _entries.TryGetValue(sessionId, out var entry) ? entry.Pending : Task.CompletedTask;
    }

    private Task QueueLocked(Entry entry, Func<Task> operation)
    {
        var previous = entry.Pending;
        return entry.Pending = Task.Run(async () =>
        {
            try { await previous; await operation(); }
            catch (Exception ex)
            {
                try { _log($"[ATTENDANCE] Event failed; sessionId={entry.Collector.SessionId}; {ex.Message}"); }
                catch { }
            }
        });
    }

    private Task EndLocked(Guid id)
    {
        if (!_entries.TryGetValue(id, out var entry)) return Task.CompletedTask;
        if (entry.Ending) return entry.Pending;
        entry.Ending = true;
        return QueueLocked(entry, async () =>
        {
            try { await entry.Collector.StopAsync(); }
            finally
            {
                lock (_sync) { _entries.Remove(id); _ended.Add(id); }
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (_sync)
        {
            _disposed = true;
            _events.Lifecycle -= OnLifecycleAsync;
            _events.AdmissionVerified -= OnAdmission;
            pending = _entries.Keys.ToArray().Select(EndLocked).ToArray();
        }
        await Task.WhenAll(pending);
    }
}
