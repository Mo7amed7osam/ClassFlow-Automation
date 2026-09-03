using ZoomAutoAdmit.Core.Meetings;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Models;
using ZoomAutoAdmit.Core.Sessions;
using ZoomAutoAdmit.WebAutomation;

namespace ZoomAutoAdmit.WindowsRuntime;

public interface IWindowsWebAutoAdmitLifecycle
{
    Task StartAsync(CliOptions options, CancellationToken cancellationToken);
    Task MonitorAsync(CliOptions options, CancellationToken cancellationToken);
    Task<bool> DisableMicrophoneAsync(CancellationToken cancellationToken);
    Task<bool> DisableCameraAsync(CancellationToken cancellationToken);
    Task StopAsync();
}

public sealed class WindowsWebAutoAdmitLifecycle(WebAutoAdmitEngine engine)
    : IWindowsWebAutoAdmitLifecycle
{
    public Task StartAsync(CliOptions options, CancellationToken cancellationToken) =>
        engine.StartAsync(options, cancellationToken);
    public Task MonitorAsync(CliOptions options, CancellationToken cancellationToken) =>
        engine.MonitorAsync(options, cancellationToken);
    public Task<bool> DisableMicrophoneAsync(CancellationToken cancellationToken) =>
        engine.DisableMicrophoneAsync(cancellationToken);
    public Task<bool> DisableCameraAsync(CancellationToken cancellationToken) =>
        engine.DisableCameraAsync(cancellationToken);
    public Task StopAsync() => engine.StopAsync();
}

public interface IWindowsWebMeetingPreparation
{
    Task<MeetingOperationResult> DisableMicrophoneAsync(CancellationToken cancellationToken);
    Task<MeetingOperationResult> DisableCameraAsync(CancellationToken cancellationToken);
}

public sealed class WindowsWebMeetingPreparation(IWindowsWebAutoAdmitLifecycle engine)
    : IWindowsWebMeetingPreparation
{
    public async Task<MeetingOperationResult> DisableMicrophoneAsync(
        CancellationToken cancellationToken) =>
        await engine.DisableMicrophoneAsync(cancellationToken)
            ? MeetingOperationResult.Success()
            : MeetingOperationResult.Failure("The Zoom Web microphone control was not found.");

    public async Task<MeetingOperationResult> DisableCameraAsync(
        CancellationToken cancellationToken) =>
        await engine.DisableCameraAsync(cancellationToken)
            ? MeetingOperationResult.Success()
            : MeetingOperationResult.Failure("The Zoom Web camera control was not found.");
}

public sealed class WindowsWebMeetingLauncher : IMeetingEngineRuntime, IAsyncDisposable
{
    private readonly IWindowsWebAutoAdmitLifecycle _engine;
    private readonly IWindowsWebMeetingPreparation _preparation;
    private readonly object _sync = new();
    private CancellationTokenSource? _sessionCancellation;
    private CancellationTokenSource? _monitorCancellation;
    private Task? _monitorTask;
    private CliOptions? _options;
    private bool _joined;

    public WindowsWebMeetingLauncher(
        IWindowsWebAutoAdmitLifecycle engine,
        IWindowsWebMeetingPreparation preparation)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _preparation = preparation ?? throw new ArgumentNullException(nameof(preparation));
    }

    public SessionEngineType EngineType => SessionEngineType.Web;

    public Task<MeetingOperationResult> SwitchAccountAsync(
        MeetingAccount account,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(MeetingOperationResult.Success());

    public async Task<MeetingOperationResult> LaunchAsync(
        MeetingLaunchContext context,
        CancellationToken cancellationToken = default)
    {
        // The account's own profile, or one of its per-session copies when the same account runs
        // several Web meetings at once.
        string accountProfile = string.IsNullOrWhiteSpace(context.Account.WebProfileName)
            ? AccountWebProfile.ForAccount(context.Account.AccountId)
            : context.Account.WebProfileName!.Trim();
        string? allocated = context.WebProfileName;
        bool belongsToAccount = string.Equals(allocated, accountProfile, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(AccountWebProfile.BaseProfileOf(allocated ?? string.Empty), accountProfile, StringComparison.OrdinalIgnoreCase);
        if (!belongsToAccount)
            return MeetingOperationResult.Failure(
                "The allocated Web profile does not match the meeting account.");
        string expectedProfile = allocated!;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _options = new CliOptions
            {
                Command = "waiting-room-auto-admit",
                CommandExplicitlySet = true,
                Engine = "web",
                WebProfile = expectedProfile,
                MeetingUrl = context.Session.MeetingUrl.AbsoluteUri,
                TimeoutSeconds = 0,
                TimeoutExplicitlySet = false,
                // Show the browser unless it was deliberately hidden, so a Web meeting is
                // something you can watch instead of a window that never appears.
                WebHeaded = AdmissionControl.ShowWebBrowser
            };
            // Browser/session lifetime is explicitly owned by this launcher. Do not link it to
            // the short-lived meeting-start command token after startup has been accepted.
            _sessionCancellation?.Dispose();
            _sessionCancellation = new CancellationTokenSource();
            await _engine.StartAsync(_options, _sessionCancellation.Token);
            _joined = true;
            return MeetingOperationResult.Success();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return MeetingOperationResult.Failure(ex.Message); }
    }

    public Task<MeetingOperationResult> VerifyJoinedAsync(
        MeetingLaunchContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_joined
            ? MeetingOperationResult.Success()
            : MeetingOperationResult.Failure("The Web meeting was not joined."));

    public Task<MeetingOperationResult> DisableMicrophoneAsync(
        MeetingLaunchContext context,
        CancellationToken cancellationToken = default) =>
        _preparation.DisableMicrophoneAsync(cancellationToken);

    public Task<MeetingOperationResult> DisableCameraAsync(
        MeetingLaunchContext context,
        CancellationToken cancellationToken = default) =>
        _preparation.DisableCameraAsync(cancellationToken);

    public async Task<MeetingOperationResult> StartAutoAdmitAsync(
        MeetingLaunchContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_joined || _options == null)
            return MeetingOperationResult.Failure("The Web meeting must be joined before monitoring starts.");
        lock (_sync)
        {
            if (_monitorTask is { IsCompleted: false })
                return MeetingOperationResult.Success();
            cancellationToken.ThrowIfCancellationRequested();
            // The monitor belongs to the meeting session, not to the short-lived startup call.
            var monitorCancellation = new CancellationTokenSource();
            _monitorCancellation = monitorCancellation;
            _monitorTask = Task.Run(
                async () =>
                {
                    ConsoleLogger.Success("[AUTO_ADMIT] Web monitor running");
                    try
                    {
                        await _engine.MonitorAsync(_options, monitorCancellation.Token);
                        if (!monitorCancellation.IsCancellationRequested)
                            ConsoleLogger.Error("[AUTO_ADMIT] Web monitor stopped unexpectedly");
                    }
                    // Preserve monitor startup/exit timing; the bridge owns and drains this work.
                    finally { _ = MeetingAdmissionScope.NotifyMonitorStoppedAsync(); }
                },
                CancellationToken.None);
        }
        var completed = await Task.WhenAny(
            _monitorTask,
            Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken));
        if (!ReferenceEquals(completed, _monitorTask)) return MeetingOperationResult.Success();
        try
        {
            await _monitorTask;
            return MeetingOperationResult.Failure("Web Auto Admit stopped during startup.");
        }
        catch (OperationCanceledException) when (_monitorCancellation?.IsCancellationRequested == true)
        {
            return MeetingOperationResult.Failure("Web Auto Admit was cancelled during startup.");
        }
        catch (Exception ex)
        {
            return MeetingOperationResult.Failure(ex.Message);
        }
    }

    public async Task<MeetingOperationResult> StopAutoAdmitAsync(
        MeetingLaunchContext context,
        CancellationToken cancellationToken = default)
    {
        Cancel();
        try
        {
            await _engine.StopAsync();
            Task? monitorTask;
            lock (_sync) monitorTask = _monitorTask;
            if (monitorTask != null)
            {
                try { await monitorTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
                catch (OperationCanceledException) when (_monitorCancellation?.IsCancellationRequested == true) { }
            }
            _joined = false;
            return MeetingOperationResult.Success();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return MeetingOperationResult.Failure(ex.Message); }
    }

    public void Cancel()
    {
        lock (_sync)
        {
            _monitorCancellation?.Cancel();
            _sessionCancellation?.Cancel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Cancel();
        await _engine.StopAsync();
        lock (_sync)
        {
            _monitorCancellation?.Dispose();
            _monitorCancellation = null;
            _sessionCancellation?.Dispose();
            _sessionCancellation = null;
            _monitorTask = null;
        }
    }
}
