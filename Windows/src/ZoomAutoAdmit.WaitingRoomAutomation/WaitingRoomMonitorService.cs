using System.Collections.Concurrent;
using ZoomAutoAdmit.Core.Sessions;

namespace ZoomAutoAdmit.WaitingRoomAutomation;

public sealed class WaitingRoomMonitorService : IWaitingRoomMonitorService
{
    private readonly ConcurrentDictionary<Guid, WaitingRoomSessionHandle> _sessions = new();
    private readonly IWaitingRoomMonitorFactory _monitorFactory;
    private readonly IProfileSessionManager _profileSessions;
    private readonly IWaitingRoomLogger _logger;
    private int _disposed;

    public WaitingRoomMonitorService(
        IWaitingRoomMonitorFactory monitorFactory,
        IProfileSessionManager profileSessions,
        IWaitingRoomLogger logger)
    {
        _monitorFactory = monitorFactory ?? throw new ArgumentNullException(nameof(monitorFactory));
        _profileSessions = profileSessions ?? throw new ArgumentNullException(nameof(profileSessions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IReadOnlyCollection<WaitingRoomSessionHandle> ActiveSessions =>
        _sessions.Values
            .Where(session => session.Status is SessionStatus.Starting or SessionStatus.Active or SessionStatus.Stopping)
            .OrderBy(session => session.StartTime)
            .ToArray();

    public Task<WaitingRoomSessionHandle> StartDesktopAsync(
        WaitingRoomSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateRequest(request, SessionEngineType.Desktop);
        if (ActiveSessions.Any(session => session.EngineType == SessionEngineType.Desktop))
            throw new InvalidOperationException("A Desktop Waiting Room monitor is already running.");

        Guid sessionId = request.SessionId ?? Guid.NewGuid();
        var monitor = _monitorFactory.CreateDesktop(sessionId);
        var context = new WaitingRoomMonitorContext(sessionId, request.AccountId);
        return Task.FromResult(StartMonitor(request, sessionId, monitor, context, null, cancellationToken));
    }

    public async Task<WaitingRoomSessionHandle> StartWebAsync(
        WaitingRoomSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateRequest(request, SessionEngineType.Web);
        string profileName = request.ProfileName!;
        Uri meetingUrl = request.MeetingUrl!;
        Guid sessionId = request.SessionId ?? Guid.NewGuid();

        var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IWebProfileSession? profileSession = null;
        try
        {
            profileSession = await _profileSessions.OpenMeetingAsync(
                sessionId,
                profileName,
                meetingUrl,
                linkedCancellation.Token);
            var monitor = _monitorFactory.CreateWeb(sessionId, profileName);
            var context = new WaitingRoomMonitorContext(
                sessionId,
                request.AccountId,
                profileName,
                meetingUrl,
                profileSession.Context,
                profileSession.MeetingPage);
            return StartMonitor(
                request,
                sessionId,
                monitor,
                context,
                profileSession,
                cancellationToken,
                linkedCancellation);
        }
        catch
        {
            linkedCancellation.Dispose();
            if (profileSession != null) await profileSession.DisposeAsync();
            throw;
        }
    }

    public async Task<IReadOnlyList<WaitingRoomSessionHandle>> StartMultipleWebAsync(
        IEnumerable<WaitingRoomSessionRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var results = new List<WaitingRoomSessionHandle>();
        foreach (var request in requests)
            results.Add(await StartWebAsync(request, cancellationToken));
        return results;
    }

    public async Task StopAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var handle)) return;
        handle.SetStatus(SessionStatus.Stopping);
        handle.Cancellation.Cancel();
        try
        {
            await handle.RunningTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (handle.Cancellation.IsCancellationRequested) { }
        finally
        {
            _sessions.TryRemove(sessionId, out _);
            handle.Cancellation.Dispose();
        }
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var sessionId in _sessions.Keys.ToArray())
            await StopAsync(sessionId, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAllAsync();
        await _profileSessions.DisposeAsync();
    }

    private WaitingRoomSessionHandle StartMonitor(
        WaitingRoomSessionRequest request,
        Guid sessionId,
        IWaitingRoomMonitor monitor,
        WaitingRoomMonitorContext context,
        IWebProfileSession? profileSession,
        CancellationToken externalCancellation,
        CancellationTokenSource? existingCancellation = null)
    {
        var cancellation = existingCancellation ??
            CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);
        var handle = new WaitingRoomSessionHandle(
            sessionId,
            request.EngineType,
            request.AccountId,
            request.ProfileName,
            DateTimeOffset.UtcNow,
            cancellation);

        if (!_sessions.TryAdd(sessionId, handle))
        {
            cancellation.Dispose();
            throw new InvalidOperationException($"Waiting Room session '{sessionId}' is already running.");
        }

        Task runningTask = Task.Run(
            () => RunMonitorAsync(handle, monitor, context, profileSession),
            CancellationToken.None);
        handle.Attach(runningTask);
        return handle;
    }

    private async Task RunMonitorAsync(
        WaitingRoomSessionHandle handle,
        IWaitingRoomMonitor monitor,
        WaitingRoomMonitorContext context,
        IWebProfileSession? profileSession)
    {
        try
        {
            handle.SetStatus(SessionStatus.Active);
            await monitor.RunAsync(context, handle.Cancellation.Token);
            handle.SetStatus(SessionStatus.Completed);
        }
        catch (OperationCanceledException) when (handle.Cancellation.IsCancellationRequested)
        {
            handle.SetStatus(SessionStatus.Completed);
        }
        catch (Exception ex)
        {
            handle.SetStatus(SessionStatus.Failed, ex);
            _logger.Log(handle.SessionId, handle.EngineType, "ERROR", ex.Message);
        }
        finally
        {
            if (profileSession != null)
                await _profileSessions.CloseAsync(handle.SessionId);
        }
    }

    private static void ValidateRequest(
        WaitingRoomSessionRequest request,
        SessionEngineType expectedEngine)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.EngineType != expectedEngine)
            throw new ArgumentException($"Expected a {expectedEngine} request.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.AccountId))
            throw new ArgumentException("An account ID is required.", nameof(request));
        if (expectedEngine == SessionEngineType.Web)
        {
            if (string.IsNullOrWhiteSpace(request.ProfileName))
                throw new ArgumentException("A Web profile is required.", nameof(request));
            if (request.MeetingUrl == null)
                throw new ArgumentException("A Web meeting URL is required.", nameof(request));
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
