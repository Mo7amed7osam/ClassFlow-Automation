using ZoomAutoAdmit.Core.Engines;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Meetings;
using ZoomAutoAdmit.Core.Models;
using ZoomAutoAdmit.Core.Sessions;

namespace ZoomAutoAdmit.WindowsRuntime;

public interface IWindowsDesktopMeetingPlatform
{
    Task<MeetingOperationResult> SwitchAccountAsync(
        MeetingAccount account,
        CancellationToken cancellationToken);
    Task<MeetingOperationResult> LaunchMeetingAsync(Uri meetingUrl, CancellationToken cancellationToken);
    Task<MeetingOperationResult> VerifyJoinedAsync(CancellationToken cancellationToken);
    Task<MeetingOperationResult> DisableMicrophoneAsync(CancellationToken cancellationToken);
    Task<MeetingOperationResult> DisableCameraAsync(CancellationToken cancellationToken);
    Task<MeetingOperationResult> StopAsync(CancellationToken cancellationToken);
}

public sealed class WindowsDesktopMeetingLauncher : IMeetingEngineRuntime, IAsyncDisposable
{
    private readonly IAutoAdmitEngine _autoAdmitEngine;
    private readonly IWindowsDesktopMeetingPlatform _platform;
    private readonly IWindowsDesktopAutoAdmitPreparation _autoAdmitPreparation;
    private readonly object _monitorSync = new();
    private CancellationTokenSource? _monitorCancellation;
    private Task<int>? _monitorTask;

    public WindowsDesktopMeetingLauncher(
        IAutoAdmitEngine autoAdmitEngine,
        IWindowsDesktopMeetingPlatform platform,
        IWindowsDesktopAutoAdmitPreparation? autoAdmitPreparation = null)
    {
        _autoAdmitEngine = autoAdmitEngine ?? throw new ArgumentNullException(nameof(autoAdmitEngine));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _autoAdmitPreparation = autoAdmitPreparation ?? NoOpWindowsDesktopAutoAdmitPreparation.Instance;
        if (!_autoAdmitEngine.Name.Equals("windows", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The Desktop launcher requires the Windows Auto Admit engine.", nameof(autoAdmitEngine));
    }

    public SessionEngineType EngineType => SessionEngineType.Desktop;

    public Task<MeetingOperationResult> SwitchAccountAsync(
        MeetingAccount account,
        CancellationToken cancellationToken = default) =>
        SafeAsync(() => _platform.SwitchAccountAsync(account, cancellationToken));

    public Task<MeetingOperationResult> LaunchAsync(
        MeetingLaunchContext context,
        CancellationToken cancellationToken = default) =>
        SafeAsync(() => _platform.LaunchMeetingAsync(context.Session.MeetingUrl, cancellationToken));

    public Task<MeetingOperationResult> VerifyJoinedAsync(
        MeetingLaunchContext context,
        CancellationToken cancellationToken = default) =>
        SafeAsync(() => _platform.VerifyJoinedAsync(cancellationToken));

    public Task<MeetingOperationResult> DisableMicrophoneAsync(
        MeetingLaunchContext context,
        CancellationToken cancellationToken = default) =>
        PrepareMediaControlAsync(
            "microphone",
            () => _platform.DisableMicrophoneAsync(cancellationToken));

    public Task<MeetingOperationResult> DisableCameraAsync(
        MeetingLaunchContext context,
        CancellationToken cancellationToken = default) =>
        PrepareMediaControlAsync(
            "camera",
            () => _platform.DisableCameraAsync(cancellationToken));

    public async Task<MeetingOperationResult> StartAutoAdmitAsync(
        MeetingLaunchContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<int> monitorTask;
        CancellationToken monitorToken;

        lock (_monitorSync)
        {
            if (_monitorTask is { IsCompleted: false })
                return MeetingOperationResult.Success();
            // Startup cancellation belongs to RunAsync only. Once monitoring has started, its
            // lifetime is owned by StopAutoAdmitAsync/DisposeAsync so a completed UI command
            // cannot silently stop admission for the live meeting.
            var monitorCancellation = new CancellationTokenSource();
            _monitorCancellation = monitorCancellation;
            var options = new CliOptions
            {
                Command = "waiting-room-auto-admit",
                CommandExplicitlySet = true,
                Engine = "windows",
                TimeoutSeconds = 0,
                TimeoutExplicitlySet = false
            };
            _monitorTask = Task.Run(
                async () =>
                {
                    ConsoleLogger.Success("[AUTO_ADMIT] Windows monitor running");
                    try
                    {
                        int exitCode = await _autoAdmitEngine.RunAsync(options, monitorCancellation.Token);
                        if (!monitorCancellation.IsCancellationRequested)
                            ConsoleLogger.Error($"[AUTO_ADMIT] Windows monitor stopped unexpectedly; exitCode={exitCode}");
                        return exitCode;
                    }
                    // Queue observer work without extending the engine task's completion time.
                    finally { _ = MeetingAdmissionScope.NotifyMonitorStoppedAsync(); }
                },
                CancellationToken.None);
            monitorTask = _monitorTask;
            monitorToken = monitorCancellation.Token;
        }

        // Readiness work (opening the Participants panel once the meeting window exists) runs
        // beside the monitor, never ahead of it: notifications are handled from the first scan.
        _ = RunPreparationAsync(monitorToken);

        var completed = await Task.WhenAny(
            monitorTask,
            Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken));
        if (!ReferenceEquals(completed, monitorTask)) return MeetingOperationResult.Success();
        int exitCode = await monitorTask;
        return MeetingOperationResult.Failure(
            $"Windows Auto Admit stopped during startup with exit code {exitCode}.");
    }

    private async Task RunPreparationAsync(CancellationToken monitorCancellation)
    {
        try { await _autoAdmitPreparation.PrepareAsync(monitorCancellation); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ConsoleLogger.Warn(
                $"[AUTO_ADMIT] Participants readiness step failed; notification monitoring continues: {ex.Message}");
        }
    }

    public async Task<MeetingOperationResult> StopAutoAdmitAsync(
        MeetingLaunchContext context,
        CancellationToken cancellationToken = default)
    {
        Cancel();
        var platformResult = await SafeAsync(() => _platform.StopAsync(cancellationToken));
        Task<int>? monitorTask;
        lock (_monitorSync) monitorTask = _monitorTask;
        if (monitorTask is { IsCompleted: false })
        {
            var completed = await Task.WhenAny(
                monitorTask,
                Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
            if (!ReferenceEquals(completed, monitorTask))
                return MeetingOperationResult.Failure("Windows Auto Admit did not stop within five seconds.");
        }
        return platformResult;
    }

    public void Cancel()
    {
        lock (_monitorSync) _monitorCancellation?.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        Cancel();
        Task<int>? monitorTask;
        lock (_monitorSync)
        {
            monitorTask = _monitorTask;
            _monitorTask = null;
            _monitorCancellation?.Dispose();
            _monitorCancellation = null;
        }
        if (monitorTask != null)
        {
            try { await monitorTask; }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task<MeetingOperationResult> SafeAsync(
        Func<Task<MeetingOperationResult>> operation)
    {
        try { return await operation(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return MeetingOperationResult.Failure(ex.Message); }
    }

    private static async Task<MeetingOperationResult> PrepareMediaControlAsync(
        string controlLabel,
        Func<Task<MeetingOperationResult>> operation)
    {
        var result = await SafeAsync(operation);
        if (result.IsSuccess) return result;

        // Zoom frequently hides the meeting toolbar from UI Automation while the controls
        // are already off (minimized/detached Participants layouts are common examples).
        // A missing accessibility element is therefore an unknown state, not a meeting
        // launch failure. Keep real platform failures fatal, but never prevent the safety
        // monitor from starting solely because Zoom did not expose this optional control.
        if (result.ErrorMessage?.Contains("control was not found", StringComparison.OrdinalIgnoreCase) == true)
        {
            ConsoleLogger.Warn(
                $"[PREPARE] Zoom Desktop {controlLabel} control is not exposed; " +
                "continuing so Auto Admit remains active.");
            return MeetingOperationResult.Success();
        }

        return result;
    }
}
