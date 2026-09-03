using Microsoft.Playwright;
using ZoomAutoAdmit.Core.Sessions;

namespace ZoomAutoAdmit.WaitingRoomAutomation;

public sealed record WaitingRoomMonitorContext(
    Guid SessionId,
    string AccountId,
    string? ProfileName = null,
    Uri? MeetingUrl = null,
    IBrowserContext? BrowserContext = null,
    IPage? MeetingPage = null);

public interface IWaitingRoomMonitor
{
    Guid SessionId { get; }
    SessionEngineType EngineType { get; }

    Task RunAsync(
        WaitingRoomMonitorContext context,
        CancellationToken cancellationToken);
}

public interface IWaitingRoomMonitorFactory
{
    IWaitingRoomMonitor CreateDesktop(Guid sessionId);
    IWaitingRoomMonitor CreateWeb(Guid sessionId, string profileName);
}

public sealed record WaitingRoomSessionRequest(
    SessionEngineType EngineType,
    string AccountId,
    string? ProfileName = null,
    Uri? MeetingUrl = null,
    Guid? SessionId = null);

public sealed record MultiMeetingRequest(
    IReadOnlyList<WaitingRoomSessionRequest> Sessions);

public sealed class WaitingRoomSessionHandle
{
    private readonly object _sync = new();
    private SessionStatus _status;
    private Exception? _failure;
    private Task _runningTask = Task.CompletedTask;

    internal WaitingRoomSessionHandle(
        Guid sessionId,
        SessionEngineType engineType,
        string accountId,
        string? profileName,
        DateTimeOffset startTime,
        CancellationTokenSource cancellation)
    {
        SessionId = sessionId;
        EngineType = engineType;
        AccountId = accountId;
        ProfileName = profileName;
        StartTime = startTime;
        Cancellation = cancellation;
        _status = SessionStatus.Starting;
    }

    public Guid SessionId { get; }
    public SessionEngineType EngineType { get; }
    public string AccountId { get; }
    public string? ProfileName { get; }
    public DateTimeOffset StartTime { get; }
    public CancellationTokenSource Cancellation { get; }
    public Task RunningTask { get { lock (_sync) return _runningTask; } }
    public SessionStatus Status { get { lock (_sync) return _status; } }
    public Exception? Failure { get { lock (_sync) return _failure; } }

    internal void Attach(Task runningTask)
    {
        lock (_sync) _runningTask = runningTask;
    }

    internal void SetStatus(SessionStatus status, Exception? failure = null)
    {
        lock (_sync)
        {
            _status = status;
            _failure = failure;
        }
    }
}

public interface IWaitingRoomMonitorService : IAsyncDisposable
{
    IReadOnlyCollection<WaitingRoomSessionHandle> ActiveSessions { get; }

    Task<WaitingRoomSessionHandle> StartDesktopAsync(
        WaitingRoomSessionRequest request,
        CancellationToken cancellationToken = default);

    Task<WaitingRoomSessionHandle> StartWebAsync(
        WaitingRoomSessionRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WaitingRoomSessionHandle>> StartMultipleWebAsync(
        IEnumerable<WaitingRoomSessionRequest> requests,
        CancellationToken cancellationToken = default);

    Task StopAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task StopAllAsync(CancellationToken cancellationToken = default);
}

public interface IMultiMeetingCoordinator
{
    Task<IReadOnlyList<WaitingRoomSessionHandle>> StartAsync(
        MultiMeetingRequest request,
        CancellationToken cancellationToken = default);

    Task StopAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task StopAllAsync(CancellationToken cancellationToken = default);
}
