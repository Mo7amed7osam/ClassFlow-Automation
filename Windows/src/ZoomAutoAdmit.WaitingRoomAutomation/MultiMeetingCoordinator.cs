using ZoomAutoAdmit.Core.Sessions;

namespace ZoomAutoAdmit.WaitingRoomAutomation;

public sealed class MultiMeetingCoordinator(
    IWaitingRoomMonitorService monitorService) : IMultiMeetingCoordinator
{
    private readonly IWaitingRoomMonitorService _monitorService =
        monitorService ?? throw new ArgumentNullException(nameof(monitorService));

    public async Task<IReadOnlyList<WaitingRoomSessionHandle>> StartAsync(
        MultiMeetingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Sessions);
        if (request.Sessions.Count == 0)
            return Array.Empty<WaitingRoomSessionHandle>();
        if (request.Sessions.Count(session => session.EngineType == SessionEngineType.Desktop) > 1)
            throw new InvalidOperationException("Only one Desktop Waiting Room monitor can run at a time.");

        string[] duplicateProfiles = request.Sessions
            .Where(session => session.EngineType == SessionEngineType.Web)
            .Select(session => session.ProfileName ?? string.Empty)
            .GroupBy(profile => profile, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateProfiles.Length > 0)
            throw new InvalidOperationException(
                $"Each Web meeting requires a distinct profile. Duplicate: {duplicateProfiles[0]}");

        var handles = new List<WaitingRoomSessionHandle>();
        foreach (var session in request.Sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            handles.Add(session.EngineType == SessionEngineType.Desktop
                ? await _monitorService.StartDesktopAsync(session, cancellationToken)
                : await _monitorService.StartWebAsync(session, cancellationToken));
        }
        return handles;
    }

    public Task StopAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        _monitorService.StopAsync(sessionId, cancellationToken);

    public Task StopAllAsync(CancellationToken cancellationToken = default) =>
        _monitorService.StopAllAsync(cancellationToken);
}
