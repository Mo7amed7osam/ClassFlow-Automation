using ZoomAutoAdmit.Core.Sessions;

namespace ZoomAutoAdmit.WaitingRoomAutomation;

public sealed class DesktopWaitingRoomMonitor : IWaitingRoomMonitor
{
    private readonly IDesktopWaitingRoomDetector _detector;

    public DesktopWaitingRoomMonitor(
        Guid sessionId,
        IWaitingRoomLogger logger)
        : this(sessionId, new DesktopWaitingRoomDetector(sessionId, logger)) { }

    public DesktopWaitingRoomMonitor(
        Guid sessionId,
        IDesktopWaitingRoomDetector detector)
    {
        SessionId = sessionId;
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
    }

    public Guid SessionId { get; }
    public SessionEngineType EngineType => SessionEngineType.Desktop;

    public Task RunAsync(
        WaitingRoomMonitorContext context,
        CancellationToken cancellationToken)
    {
        ValidateContext(context);
        return _detector.StartWatcherAsync(cancellationToken);
    }

    private void ValidateContext(WaitingRoomMonitorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.SessionId != SessionId)
            throw new InvalidOperationException("The Desktop monitor context belongs to another session.");
    }
}

public sealed class WebWaitingRoomMonitor : IWaitingRoomMonitor
{
    private readonly IWebWaitingRoomDetector _detector;

    public WebWaitingRoomMonitor(
        Guid sessionId,
        string profileName,
        IWaitingRoomLogger logger)
        : this(sessionId, profileName, new WebWaitingRoomDetector(sessionId, logger)) { }

    public WebWaitingRoomMonitor(
        Guid sessionId,
        string profileName,
        IWebWaitingRoomDetector detector)
    {
        SessionId = sessionId;
        ProfileName = string.IsNullOrWhiteSpace(profileName)
            ? throw new ArgumentException("A Web profile is required.", nameof(profileName))
            : profileName;
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
    }

    public Guid SessionId { get; }
    public string ProfileName { get; }
    public SessionEngineType EngineType => SessionEngineType.Web;

    public Task RunAsync(
        WaitingRoomMonitorContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.SessionId != SessionId)
            throw new InvalidOperationException("The Web monitor context belongs to another session.");
        if (!string.Equals(context.ProfileName, ProfileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Web monitor context belongs to another profile.");
        if (context.BrowserContext == null || context.MeetingPage == null)
            throw new InvalidOperationException("A Web monitor requires one browser context and one meeting page.");

        return _detector.StartPageWatcherAsync(context.MeetingPage, cancellationToken);
    }
}

public sealed class WaitingRoomMonitorFactory(IWaitingRoomLogger logger)
    : IWaitingRoomMonitorFactory
{
    private readonly IWaitingRoomLogger _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public IWaitingRoomMonitor CreateDesktop(Guid sessionId) =>
        new DesktopWaitingRoomMonitor(sessionId, _logger);

    public IWaitingRoomMonitor CreateWeb(Guid sessionId, string profileName) =>
        new WebWaitingRoomMonitor(sessionId, profileName, _logger);
}
