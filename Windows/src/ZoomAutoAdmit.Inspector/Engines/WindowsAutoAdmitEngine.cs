using ZoomAutoAdmit.Core.Engines;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Models;
using ZoomAutoAdmit.Core.Sessions;
using ZoomAutoAdmit.WaitingRoomAutomation;

namespace ZoomAutoAdmit.Inspector.Engines;

public sealed class WindowsAutoAdmitEngine : IAutoAdmitEngine
{
    private readonly IWaitingRoomLogger _logger;

    public WindowsAutoAdmitEngine()
        : this(new ApplicationWaitingRoomLogger()) { }

    public WindowsAutoAdmitEngine(IWaitingRoomLogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Name => "windows";

    public async Task<int> RunAsync(CliOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var sessionId = Guid.NewGuid();
        var detector = new DesktopWaitingRoomDetector(sessionId, _logger);
        using var timeoutCancellation = options.TimeoutSeconds > 0
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        timeoutCancellation?.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        await detector.StartWatcherAsync(timeoutCancellation?.Token ?? cancellationToken);
        return 0;
    }

    private sealed class ApplicationWaitingRoomLogger : IWaitingRoomLogger
    {
        public void Log(
            Guid sessionId,
            SessionEngineType engine,
            string category,
            string message)
        {
            string formatted =
                $"[AUTO_ADMIT] [{category}] [session:{sessionId.ToString()[..8]}] {message}";

            if (category.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
            {
                ConsoleLogger.Error(formatted);
                return;
            }

            if (message.Contains("clicked", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("admitted", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("success", StringComparison.OrdinalIgnoreCase))
            {
                ConsoleLogger.Success(formatted);
                return;
            }

            ConsoleLogger.Info(formatted);
        }
    }
}
