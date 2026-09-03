using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Meetings;

namespace ZoomAutoAdmit.Attendance;

/// <summary>One collector per session. Owns no Zoom engine, window, page, or browser.</summary>
public sealed class AttendanceCollector : IAsyncDisposable
{
    private readonly IAttendanceParticipantSource _source;
    private readonly IAttendanceSnapshotStore _store;
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _timerCancellation = new();
    private Task _timerTask = Task.CompletedTask;
    private bool _started;
    private bool _stopped;
    private readonly AttendanceMeetingMetadata? _metadata;

    public Guid SessionId { get; }

    public AttendanceCollector(Guid sessionId, IAttendanceParticipantSource source,
        IAttendanceSnapshotStore store, TimeProvider? timeProvider = null,
        TimeSpan? interval = null, Action<string>? log = null,
        AttendanceMeetingMetadata? metadata = null)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("Session ID is required.", nameof(sessionId));
        SessionId = sessionId;
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _time = timeProvider ?? TimeProvider.System;
        _interval = interval ?? TimeSpan.FromMinutes(15);
        if (_interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        _log = log ?? ConsoleLogger.Info;
        _metadata = metadata;
    }

    public Task OnMeetingStateChangedAsync(Guid sessionId, MeetingState state,
        CancellationToken cancellationToken = default)
    {
        EnsureSession(sessionId);
        return state switch
        {
            MeetingState.Active or MeetingState.Monitoring => StartAsync(cancellationToken),
            MeetingState.Ended or MeetingState.Failed => StopAsync(cancellationToken),
            _ => Task.CompletedTask
        };
    }

    // Optional observer for the existing state model, without editing the orchestrator.
    public async Task ObserveAsync(MeetingSession session, CancellationToken cancellationToken)
    {
        EnsureSession(session.SessionId);
        try
        {
            while (true)
            {
                var state = session.State;
                await OnMeetingStateChangedAsync(session.SessionId, state, cancellationToken);
                if (state is MeetingState.Ended or MeetingState.Failed) return;
                await Task.Delay(TimeSpan.FromSeconds(1), _time, cancellationToken);
            }
        }
        finally { await StopAsync(); }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_started || _stopped) return;
            _started = true;
            Log("Collector started");
            try { await CaptureCoreAsync(SnapshotTrigger.MeetingStart, cancellationToken); }
            finally { _timerTask = RunTimerAsync(); }
        }
        finally { _gate.Release(); }
    }

    public Task<AttendanceSnapshot?> CaptureManualAsync(CancellationToken cancellationToken = default) =>
        CaptureAsync(SnapshotTrigger.Manual, cancellationToken);

    public Task<AttendanceSnapshot?> OnAdmissionAsync(Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        EnsureSession(sessionId);
        return CaptureAsync(SnapshotTrigger.Admission, cancellationToken);
    }

    private async Task<AttendanceSnapshot?> CaptureAsync(SnapshotTrigger trigger, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!_started || _stopped) return null;
            return await CaptureCoreAsync(trigger, token);
        }
        finally { _gate.Release(); }
    }

    private async Task<AttendanceSnapshot?> CaptureCoreAsync(SnapshotTrigger trigger, CancellationToken token)
    {
        try
        {
            var read = await _source.ReadAsync(token);
            token.ThrowIfCancellationRequested();
            var snapshot = new AttendanceSnapshot(SessionId, _time.GetUtcNow(), _source.Source,
                read.Participants.ToArray(), trigger, read.IsComplete, read.Note) { Meeting = _metadata };
            await _store.SaveAsync(snapshot, token);
            Log($"Snapshot captured; Reason: {snapshot.Reason}; source={_source.Source}");
            Log($"Participants count: {snapshot.Participants.Count}; complete={snapshot.IsComplete}");
            return snapshot;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Attendance failure must not terminate admission or claim a false empty roster.
            Log($"Snapshot failed; trigger={trigger}; error={ex.GetType().Name}: {ex.Message}");
            await RecordIssueAsync(trigger, $"{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // Storing the failure is best effort: it must never turn a read problem into a crash.
    private async Task RecordIssueAsync(SnapshotTrigger trigger, string reason)
    {
        try { await _store.SaveIssueAsync(new(SessionId, _time.GetUtcNow(), trigger, reason), CancellationToken.None); }
        catch (Exception ex) { Log($"Issue not recorded; error={ex.GetType().Name}"); }
    }

    private async Task RunTimerAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(_interval, _time);
            while (await timer.WaitForNextTickAsync(_timerCancellation.Token))
                await CaptureAsync(SnapshotTrigger.Interval, _timerCancellation.Token);
        }
        catch (OperationCanceledException) when (_timerCancellation.IsCancellationRequested) { }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_stopped)
            {
                _stopped = true;
                _timerCancellation.Cancel();
                if (_started)
                {
                    try { await CaptureCoreAsync(SnapshotTrigger.MeetingEnd, cancellationToken); }
                    finally { Log("Collector stopped"); }
                }
            }
        }
        finally { _gate.Release(); }
        await _timerTask;
    }

    private void EnsureSession(Guid sessionId)
    {
        if (sessionId != SessionId) throw new ArgumentException("Attendance event belongs to another session.");
    }

    private void Log(string message)
    {
        try { _log($"[ATTENDANCE] {message}; sessionId={SessionId:D}"); }
        catch { /* Diagnostic sinks cannot interrupt collection. */ }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
