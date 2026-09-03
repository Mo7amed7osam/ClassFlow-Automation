using ZoomAutoAdmit.Core.Sessions;

namespace ZoomAutoAdmit.WaitingRoomAutomation;

public interface IWaitingRoomLogger
{
    void Log(
        Guid sessionId,
        SessionEngineType engine,
        string category,
        string message);
}

public sealed class NullWaitingRoomLogger : IWaitingRoomLogger
{
    public static NullWaitingRoomLogger Instance { get; } = new();

    private NullWaitingRoomLogger() { }

    public void Log(Guid sessionId, SessionEngineType engine, string category, string message) { }
}

internal sealed class WaitingRoomSessionLog
{
    private readonly IWaitingRoomLogger _logger;
    private readonly Guid _sessionId;
    private readonly SessionEngineType _engine;

    public WaitingRoomSessionLog(
        IWaitingRoomLogger logger,
        Guid sessionId,
        SessionEngineType engine)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sessionId = sessionId;
        _engine = engine;
    }

    public void Desktop(string message) => Write("DESKTOP", message);
    public void Web(string message) => Write("WEB", message);
    public void Action(string message) => Write("ACTION", message);
    public void WaitingRoom(string message) => Write("WAITING_ROOM", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string category, string message) =>
        _logger.Log(_sessionId, _engine, category, message);
}
