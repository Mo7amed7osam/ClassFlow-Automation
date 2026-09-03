using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Sessions;

namespace ZoomAutoAdmit.Core.Meetings;

public sealed record MeetingAccount(
    string AccountId,
    string DisplayName,
    string CredentialReference,
    SessionEngineType? PreferredEngine = null)
{
    // Explicit account identity; credential references identify secure storage, not Zoom accounts.
    public string? ZoomEmail { get; init; }
    /// <summary>Browser profile this account signs in with. Null falls back to the account id.</summary>
    public string? WebProfileName { get; init; }
}

public interface IMeetingAccountManager
{
    Task<MeetingAccount?> LoadAsync(string accountId, CancellationToken cancellationToken = default);
}

public sealed record MeetingOperationResult(bool IsSuccess, string? ErrorMessage)
{
    public static MeetingOperationResult Success() => new(true, null);
    public static MeetingOperationResult Failure(string message) => new(false, message);
}

public sealed record MeetingLaunchContext(
    MeetingSession Session,
    MeetingAccount Account,
    SessionEngineType EngineType,
    string? WebProfileName);

/// <summary>
/// Adapter boundary between orchestration and the existing Desktop/Web implementations.
/// Implementations may delegate to the current account switcher, meeting launcher, media
/// controls, and Auto Admit engine without changing those components.
/// </summary>
public interface IMeetingEngineRuntime
{
    SessionEngineType EngineType { get; }
    Task<MeetingOperationResult> SwitchAccountAsync(
        MeetingAccount account,
        CancellationToken cancellationToken = default);
    Task<MeetingOperationResult> LaunchAsync(
        MeetingLaunchContext context,
        CancellationToken cancellationToken = default);
    Task<MeetingOperationResult> VerifyJoinedAsync(
        MeetingLaunchContext context,
        CancellationToken cancellationToken = default);
    Task<MeetingOperationResult> DisableMicrophoneAsync(
        MeetingLaunchContext context,
        CancellationToken cancellationToken = default);
    Task<MeetingOperationResult> DisableCameraAsync(
        MeetingLaunchContext context,
        CancellationToken cancellationToken = default);
    Task<MeetingOperationResult> StartAutoAdmitAsync(
        MeetingLaunchContext context,
        CancellationToken cancellationToken = default);
    Task<MeetingOperationResult> StopAutoAdmitAsync(
        MeetingLaunchContext context,
        CancellationToken cancellationToken = default);
}

public interface IMeetingEngineRuntimeFactory
{
    IMeetingEngineRuntime Get(SessionEngineType engineType);
}

public sealed class MeetingOrchestrator
{
    private readonly IMeetingAccountManager _accountManager;
    private readonly SessionCoordinator _sessionCoordinator;
    private readonly IMeetingEngineRuntimeFactory _runtimeFactory;
    private readonly MeetingLifecycleEvents _lifecycleEvents;

    public MeetingOrchestrator(
        IMeetingAccountManager accountManager,
        SessionCoordinator sessionCoordinator,
        IMeetingEngineRuntimeFactory runtimeFactory,
        MeetingLifecycleEvents? lifecycleEvents = null)
    {
        _accountManager = accountManager ?? throw new ArgumentNullException(nameof(accountManager));
        _sessionCoordinator = sessionCoordinator ?? throw new ArgumentNullException(nameof(sessionCoordinator));
        _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
        _lifecycleEvents = lifecycleEvents ?? new MeetingLifecycleEvents();
    }

    public async Task<MeetingSession> RunAsync(
        ScheduledMeeting meeting,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(meeting);
        Guid sessionId = meeting.SessionId ?? Guid.NewGuid();
        var session = new MeetingSession(sessionId, meeting, DateTimeOffset.UtcNow);
        ConsoleLogger.Info($"[SESSION] Created: {session.SessionId}");
        MeetingLaunchContext? activeContext = null;
        IMeetingEngineRuntime? monitorRuntime = null;
        MeetingLaunchContext? monitorContext = null;

        try
        {
            TimeSpan delay = meeting.StartTime - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);

            var account = await _accountManager.LoadAsync(meeting.AccountId, cancellationToken);
            if (account == null)
                return Fail(session, "The requested account could not be loaded.");

            var preferredEngine = meeting.PreferredEngine ?? account.PreferredEngine;
            var allocationResult = preferredEngine == SessionEngineType.Web
                ? _sessionCoordinator.AllocateWeb(
                    meeting.AccountId,
                    meeting.StartTime,
                    sessionId,
                    account.WebProfileName)
                : _sessionCoordinator.Allocate(
                    meeting.AccountId,
                    meeting.StartTime,
                    sessionId,
                    account.WebProfileName);
            if (!allocationResult.IsSuccess || allocationResult.Session == null)
                return Fail(session, allocationResult.ErrorMessage ?? "Engine allocation failed.");

            var allocation = allocationResult.Session;
            session.SetAllocation(allocation);
            ConsoleLogger.Info($"[ALLOCATOR] {allocation.EngineType} selected");
            _sessionCoordinator.TryUpdateStatus(sessionId, SessionStatus.Starting, out _);

            var runtime = _runtimeFactory.Get(allocation.EngineType);
            if (runtime.EngineType != allocation.EngineType)
                return Fail(session, "The selected meeting runtime does not match the allocated engine.");

            var context = new MeetingLaunchContext(
                session,
                account,
                allocation.EngineType,
                allocation.WebProfileName);

            // Moves this session onto the Web engine, keeping its id and account. Returns false
            // when no Web engine could be reserved; the caller then reports that reason.
            async Task<bool> TrySwitchToWebAsync(string reason)
            {
                ConsoleLogger.Warn($"[ALLOCATOR] {reason}; releasing the Desktop reservation and moving to Web");
                await runtime.StopAutoAdmitAsync(context, cancellationToken);
                _sessionCoordinator.Release(sessionId);

                var webAllocationResult = _sessionCoordinator.AllocateWeb(
                    meeting.AccountId,
                    meeting.StartTime,
                    sessionId,
                    account.WebProfileName);
                if (!webAllocationResult.IsSuccess || webAllocationResult.Session == null)
                {
                    Fail(session, webAllocationResult.ErrorMessage ?? "Web fallback allocation failed.");
                    return false;
                }

                allocation = webAllocationResult.Session;
                session.SetAllocation(allocation);
                _sessionCoordinator.TryUpdateStatus(sessionId, SessionStatus.Starting, out _);
                ConsoleLogger.Info($"[ALLOCATOR] Web selected with profile '{allocation.WebProfileName}'");
                runtime = _runtimeFactory.Get(SessionEngineType.Web);
                if (runtime.EngineType != SessionEngineType.Web)
                {
                    Fail(session, "The Web fallback runtime is not available.");
                    return false;
                }
                context = new MeetingLaunchContext(
                    session,
                    account,
                    SessionEngineType.Web,
                    allocation.WebProfileName);
                return true;
            }

            if (allocation.EngineType == SessionEngineType.Desktop)
            {
                session.TransitionTo(MeetingState.SwitchingAccount, "Switching Zoom Desktop account.");
                ConsoleLogger.Info("[SESSION] Switching account");
                var switchResult = await runtime.SwitchAccountAsync(account, cancellationToken);
                if (!switchResult.IsSuccess)
                {
                    // Zoom Desktop does not have this account signed in, or would not switch to
                    // it. The meeting still runs: the Web engine hosts it from the account's own
                    // browser profile, signing in there if needed.
                    string reason = switchResult.ErrorMessage ?? "Zoom Desktop account switch failed.";
                    if (!await TrySwitchToWebAsync(reason)) return session;
                }
            }

            session.TransitionTo(MeetingState.Launching, "Launching the allocated meeting engine.");
            ConsoleLogger.Info("[MEETING] Launching");
            var launchResult = await runtime.LaunchAsync(context, cancellationToken);
            if (!launchResult.IsSuccess && allocation.EngineType == SessionEngineType.Desktop)
            {
                // Zoom can report a launch failure while it is already opening the meeting. Moving
                // to Web then joins the same meeting a second time and leaves the Desktop meeting
                // with nobody watching it, so check for a live meeting window before giving up.
                var desktopWindow = await runtime.VerifyJoinedAsync(context, cancellationToken);
                if (desktopWindow.IsSuccess)
                {
                    ConsoleLogger.Info(
                        $"[MEETING] Desktop launch reported '{launchResult.ErrorMessage}', but the meeting is open; staying on Desktop");
                    launchResult = MeetingOperationResult.Success();
                }
                else
                {
                    if (!await TrySwitchToWebAsync(
                            launchResult.ErrorMessage ?? "The Zoom Desktop meeting could not be launched"))
                        return session;
                    launchResult = await runtime.LaunchAsync(context, cancellationToken);
                }
            }
            if (!launchResult.IsSuccess)
                return Fail(session, launchResult.ErrorMessage ?? "Meeting launch failed.");

            // Auto Admit starts the moment the meeting is launched. Join verification and the
            // microphone/camera preparation run afterwards, so a slow or missing Zoom control can
            // never delay or prevent admission of people who are already waiting.
            MeetingOperationResult monitorResult;
            using (MeetingAdmissionScope.Begin(sessionId, _lifecycleEvents, context))
                monitorResult = await runtime.StartAutoAdmitAsync(context, cancellationToken);
            if (!monitorResult.IsSuccess)
                return Fail(session, monitorResult.ErrorMessage ?? "Auto Admit monitor failed to start.");
            monitorRuntime = runtime;
            monitorContext = context;
            ConsoleLogger.Success("[AUTO_ADMIT] Started; watching the Waiting Room while the meeting opens");

            session.TransitionTo(MeetingState.Joining, "Waiting for the meeting to be joined.");
            ConsoleLogger.Info("[MEETING] Joining");
            var joinedResult = await runtime.VerifyJoinedAsync(context, cancellationToken);
            if (!joinedResult.IsSuccess)
                return Fail(session, joinedResult.ErrorMessage ?? "Meeting join verification failed.");

            // Media preparation is best effort: the monitor is already admitting, and a control
            // that Zoom does not expose must not turn a live meeting into a failed session.
            session.TransitionTo(MeetingState.Preparing, "Preparing microphone and camera state.");
            var microphoneResult = await runtime.DisableMicrophoneAsync(context, cancellationToken);
            if (microphoneResult.IsSuccess)
                ConsoleLogger.Success("[PREPARE] Mic disabled");
            else
                ConsoleLogger.Warn($"[PREPARE] Microphone was not disabled; Auto Admit keeps running: {microphoneResult.ErrorMessage ?? "unknown reason"}");

            var cameraResult = await runtime.DisableCameraAsync(context, cancellationToken);
            if (cameraResult.IsSuccess)
                ConsoleLogger.Success("[PREPARE] Camera disabled");
            else
                ConsoleLogger.Warn($"[PREPARE] Camera was not disabled; Auto Admit keeps running: {cameraResult.ErrorMessage ?? "unknown reason"}");

            session.TransitionTo(MeetingState.Active, "Meeting joined and prepared.");
            _sessionCoordinator.TryUpdateStatus(sessionId, SessionStatus.Active, out _);
            activeContext = context;
            await _lifecycleEvents.PublishAsync(context, MeetingLifecycleEventKind.Active);

            session.TransitionTo(MeetingState.Monitoring, "Auto Admit monitor running.");
            ConsoleLogger.Success("[AUTO_ADMIT] Monitoring");
            return session;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Fail(session, "Meeting session startup was cancelled.");
        }
        catch (Exception ex)
        {
            return Fail(session, ex.Message);
        }
        finally
        {
            if (session.State == MeetingState.Failed)
            {
                if (activeContext != null)
                    await _lifecycleEvents.PublishAsync(activeContext, MeetingLifecycleEventKind.Ending);
                // The monitor was started before the failure; do not leave it admitting for a
                // session that no longer exists.
                if (monitorRuntime != null && monitorContext != null)
                    await StopMonitorAfterFailureAsync(monitorRuntime, monitorContext);
            }
        }
    }

    private static async Task StopMonitorAfterFailureAsync(
        IMeetingEngineRuntime runtime,
        MeetingLaunchContext context)
    {
        try
        {
            var stopped = await runtime.StopAutoAdmitAsync(context, CancellationToken.None);
            if (!stopped.IsSuccess)
                ConsoleLogger.Warn($"[AUTO_ADMIT] Monitor did not stop after session failure: {stopped.ErrorMessage}");
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"[AUTO_ADMIT] Monitor stop after session failure threw: {ex.Message}");
        }
    }

    public async Task<bool> EndAsync(
        MeetingSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var allocation = session.Allocation;
        if (allocation == null || session.State is MeetingState.Ended or MeetingState.Failed)
            return false;

        var account = await _accountManager.LoadAsync(session.AccountId, cancellationToken);
        if (account == null) return false;
        var runtime = _runtimeFactory.Get(allocation.EngineType);
        var context = new MeetingLaunchContext(
            session,
            account,
            allocation.EngineType,
            allocation.WebProfileName);
        // Observers capture the final roster while the runtime still owns the live page/window.
        await _lifecycleEvents.PublishAsync(context, MeetingLifecycleEventKind.Ending);
        var stopped = await runtime.StopAutoAdmitAsync(context, cancellationToken);
        if (!stopped.IsSuccess) return false;

        session.TransitionTo(MeetingState.Ended, "Meeting session ended.");
        _sessionCoordinator.TryUpdateStatus(session.SessionId, SessionStatus.Completed, out _);
        ConsoleLogger.Info($"[SESSION] Ended: {session.SessionId}");
        return true;
    }

    private MeetingSession Fail(MeetingSession session, string reason)
    {
        session.Fail(reason);
        if (session.Allocation != null)
            _sessionCoordinator.TryUpdateStatus(session.SessionId, SessionStatus.Failed, out _);
        ConsoleLogger.Error($"[SESSION] Failed: {reason}");
        return session;
    }
}
