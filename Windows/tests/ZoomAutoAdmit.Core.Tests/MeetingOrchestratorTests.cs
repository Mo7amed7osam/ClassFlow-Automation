using ZoomAutoAdmit.Core.Meetings;
using ZoomAutoAdmit.Core.Sessions;
using Xunit;

namespace ZoomAutoAdmit.Core.Tests;

public sealed class MeetingOrchestratorTests
{
    [Fact]
    public async Task DesktopSuccessfulFlowReachesMonitoring()
    {
        var accountManager = new FakeAccountManager(Account("desktop-account"));
        var desktop = new FakeRuntime(SessionEngineType.Desktop);
        var web = new FakeRuntime(SessionEngineType.Web);
        var coordinator = new SessionCoordinator();
        var orchestrator = Orchestrator(accountManager, coordinator, desktop, web);

        var session = await orchestrator.RunAsync(Meeting("desktop-account"));

        Assert.Equal(MeetingState.Monitoring, session.State);
        Assert.Equal(SessionEngineType.Desktop, session.Allocation!.EngineType);
        Assert.Equal(
            ["switch-account", "launch", "start-auto-admit", "verify-joined", "disable-mic", "disable-camera"],
            desktop.Calls);
        Assert.Empty(web.Calls);
        Assert.Equal(SessionStatus.Active,
            coordinator.ActiveSessions.Single(item => item.SessionId == session.SessionId).Status);
        Assert.Equal(["desktop-account"], accountManager.LoadedAccountIds);
    }

    [Fact]
    public async Task DesktopBusyFallsBackToWebWithAccountProfile()
    {
        var coordinator = new SessionCoordinator();
        Assert.True(coordinator.Allocate("occupied-desktop-account").IsSuccess);
        var desktop = new FakeRuntime(SessionEngineType.Desktop);
        var web = new FakeRuntime(SessionEngineType.Web);
        var orchestrator = Orchestrator(
            new FakeAccountManager(Account("web-account")),
            coordinator,
            desktop,
            web);

        var session = await orchestrator.RunAsync(Meeting("web-account"));

        Assert.Equal(MeetingState.Monitoring, session.State);
        Assert.Equal(SessionEngineType.Web, session.Allocation!.EngineType);
        Assert.Equal("web-account", session.Allocation.WebProfileName);
        Assert.DoesNotContain("switch-account", web.Calls);
        Assert.Equal(
            ["launch", "start-auto-admit", "verify-joined", "disable-mic", "disable-camera"],
            web.Calls);
        Assert.Empty(desktop.Calls);
    }

    [Fact]
    public async Task ExplicitWebPreferenceUsesExistingWebAllocationPath()
    {
        var desktop = new FakeRuntime(SessionEngineType.Desktop);
        var web = new FakeRuntime(SessionEngineType.Web);
        var orchestrator = Orchestrator(
            new FakeAccountManager(Account("web-preferred-account")),
            new SessionCoordinator(),
            desktop,
            web);
        var meeting = Meeting("web-preferred-account") with
        {
            PreferredEngine = SessionEngineType.Web
        };

        var session = await orchestrator.RunAsync(meeting);

        Assert.Equal(MeetingState.Monitoring, session.State);
        Assert.Equal(SessionEngineType.Web, session.Allocation!.EngineType);
        Assert.Empty(desktop.Calls);
        Assert.Equal(
            ["launch", "start-auto-admit", "verify-joined", "disable-mic", "disable-camera"],
            web.Calls);
    }

    [Fact]
    public async Task AccountSwitchFailureMovesTheMeetingToWeb()
    {
        // Zoom Desktop not having this account signed in must not cancel the meeting: the Web
        // engine hosts it from the account's own browser profile instead.
        var desktop = new FakeRuntime(SessionEngineType.Desktop)
        {
            SwitchAccountResult = MeetingOperationResult.Failure("Account switch rejected.")
        };
        var web = new FakeRuntime(SessionEngineType.Web);
        var coordinator = new SessionCoordinator();
        var orchestrator = Orchestrator(
            new FakeAccountManager(Account("desktop-account")),
            coordinator,
            desktop,
            web);

        var session = await orchestrator.RunAsync(Meeting("desktop-account"));

        Assert.Equal(MeetingState.Monitoring, session.State);
        Assert.Equal(SessionEngineType.Web, session.Allocation!.EngineType);
        Assert.Equal("desktop-account", session.Allocation.WebProfileName);
        Assert.Equal(["switch-account", "stop-auto-admit"], desktop.Calls);
        Assert.Equal(
            ["launch", "start-auto-admit", "verify-joined", "disable-mic", "disable-camera"],
            web.Calls);
    }

    [Fact]
    public async Task WebFallbackIsReportedWhenNoWebEngineCanBeReserved()
    {
        var desktop = new FakeRuntime(SessionEngineType.Desktop)
        {
            SwitchAccountResult = MeetingOperationResult.Failure("Account switch rejected.")
        };
        var coordinator = new SessionCoordinator();
        // Every Web profile for this account is already taken, so no fallback is possible.
        for (int i = 0; i < SessionAllocationPolicy.MaxWebInstancesPerAccount; i++)
            Assert.True(coordinator.AllocateWeb("desktop-account").IsSuccess);
        var orchestrator = Orchestrator(
            new FakeAccountManager(Account("desktop-account")),
            coordinator,
            desktop,
            new FakeRuntime(SessionEngineType.Web));

        var session = await orchestrator.RunAsync(Meeting("desktop-account"));

        Assert.Equal(MeetingState.Failed, session.State);
        Assert.Contains("simultaneous Web meetings", session.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MeetingLaunchFailureStopsBeforeJoinVerification()
    {
        var desktop = new FakeRuntime(SessionEngineType.Desktop)
        {
            LaunchResult = MeetingOperationResult.Failure("Desktop meeting launch failed."),
            // No Zoom Desktop meeting window appeared, so the launch really did fail.
            VerifyJoinedResult = MeetingOperationResult.Failure("No Zoom Desktop meeting window.")
        };
        var web = new FakeRuntime(SessionEngineType.Web)
        {
            LaunchResult = MeetingOperationResult.Failure("Web meeting launch failed.")
        };
        var orchestrator = Orchestrator(
            new FakeAccountManager(Account("desktop-account")),
            new SessionCoordinator(),
            desktop,
            web);

        var session = await orchestrator.RunAsync(Meeting("desktop-account"));

        Assert.Equal(MeetingState.Failed, session.State);
        Assert.Equal("Web meeting launch failed.", session.FailureReason);
        Assert.Equal(["switch-account", "launch", "verify-joined", "stop-auto-admit"], desktop.Calls);
        Assert.Equal(["launch"], web.Calls);
    }

    [Fact]
    public async Task DesktopLaunchFailureIsIgnoredWhenTheMeetingWindowIsAlreadyOpen()
    {
        // Zoom reports a failed launch while it is already opening the meeting. Moving to Web then
        // joins the same meeting twice and leaves the Desktop meeting unwatched.
        var desktop = new FakeRuntime(SessionEngineType.Desktop)
        {
            LaunchResult = MeetingOperationResult.Failure("Zoom changed state before fallback.")
        };
        var web = new FakeRuntime(SessionEngineType.Web);
        var orchestrator = Orchestrator(
            new FakeAccountManager(Account("desktop-account")),
            new SessionCoordinator(),
            desktop,
            web);

        var session = await orchestrator.RunAsync(Meeting("desktop-account"));

        Assert.Equal(MeetingState.Monitoring, session.State);
        Assert.Equal(SessionEngineType.Desktop, session.Allocation!.EngineType);
        Assert.Empty(web.Calls);
    }

    [Fact]
    public async Task AutoAdmitStartFailureMarksSessionFailed()
    {
        var desktop = new FakeRuntime(SessionEngineType.Desktop)
        {
            StartAutoAdmitResult = MeetingOperationResult.Failure("Monitor startup failed.")
        };
        var coordinator = new SessionCoordinator();
        var orchestrator = Orchestrator(
            new FakeAccountManager(Account("desktop-account")),
            coordinator,
            desktop,
            new FakeRuntime(SessionEngineType.Web));

        var session = await orchestrator.RunAsync(Meeting("desktop-account"));

        Assert.Equal(MeetingState.Failed, session.State);
        Assert.Equal("Monitor startup failed.", session.FailureReason);
        // The monitor is started straight after launch, before join verification.
        Assert.Equal(["switch-account", "launch", "start-auto-admit"], desktop.Calls);
        Assert.DoesNotContain(MeetingState.Active, session.History.Select(item => item.State));
        Assert.Empty(coordinator.ActiveSessions);
    }

    [Fact]
    public async Task AutoAdmitStartsBeforeJoinVerificationAndPreparation()
    {
        var desktop = new FakeRuntime(SessionEngineType.Desktop);
        var orchestrator = Orchestrator(
            new FakeAccountManager(Account("desktop-account")),
            new SessionCoordinator(),
            desktop,
            new FakeRuntime(SessionEngineType.Web));

        var session = await orchestrator.RunAsync(Meeting("desktop-account"));

        Assert.Equal(MeetingState.Monitoring, session.State);
        Assert.True(
            desktop.Calls.IndexOf("start-auto-admit") < desktop.Calls.IndexOf("verify-joined"),
            "Auto Admit must start as soon as the meeting is launched.");
        Assert.True(desktop.Calls.IndexOf("start-auto-admit") < desktop.Calls.IndexOf("disable-mic"));
    }

    [Fact]
    public async Task JoinVerificationFailureStopsTheRunningMonitor()
    {
        var desktop = new FakeRuntime(SessionEngineType.Desktop)
        {
            VerifyJoinedResult = MeetingOperationResult.Failure("Meeting window never appeared.")
        };
        var coordinator = new SessionCoordinator();
        var orchestrator = Orchestrator(
            new FakeAccountManager(Account("desktop-account")),
            coordinator,
            desktop,
            new FakeRuntime(SessionEngineType.Web));

        var session = await orchestrator.RunAsync(Meeting("desktop-account"));

        Assert.Equal(MeetingState.Failed, session.State);
        Assert.Equal("Meeting window never appeared.", session.FailureReason);
        Assert.Equal(
            ["switch-account", "launch", "start-auto-admit", "verify-joined", "stop-auto-admit"],
            desktop.Calls);
        Assert.Empty(coordinator.ActiveSessions);
    }

    [Fact]
    public async Task MediaPreparationFailuresKeepTheMeetingMonitoring()
    {
        var desktop = new FakeRuntime(SessionEngineType.Desktop)
        {
            DisableMicrophoneResult = MeetingOperationResult.Failure("Microphone control missing."),
            DisableCameraResult = MeetingOperationResult.Failure("Camera control missing.")
        };
        var coordinator = new SessionCoordinator();
        var orchestrator = Orchestrator(
            new FakeAccountManager(Account("desktop-account")),
            coordinator,
            desktop,
            new FakeRuntime(SessionEngineType.Web));

        var session = await orchestrator.RunAsync(Meeting("desktop-account"));

        Assert.Equal(MeetingState.Monitoring, session.State);
        Assert.Null(session.FailureReason);
        Assert.Equal(
            ["switch-account", "launch", "start-auto-admit", "verify-joined", "disable-mic", "disable-camera"],
            desktop.Calls);
        Assert.Equal(SessionStatus.Active,
            coordinator.ActiveSessions.Single(item => item.SessionId == session.SessionId).Status);
    }

    private static MeetingOrchestrator Orchestrator(
        IMeetingAccountManager accountManager,
        SessionCoordinator coordinator,
        FakeRuntime desktop,
        FakeRuntime web) =>
        new(accountManager, coordinator, new FakeRuntimeFactory(desktop, web));

    private static ScheduledMeeting Meeting(string accountId) =>
        new(
            new Uri("https://zoom.us/j/123456789"),
            accountId,
            DateTimeOffset.UtcNow.AddSeconds(-1));

    private static MeetingAccount Account(string accountId) =>
        new(accountId, accountId, $"credential-manager:{accountId}");

    private sealed class FakeAccountManager(params MeetingAccount[] accounts) : IMeetingAccountManager
    {
        private readonly Dictionary<string, MeetingAccount> _accounts = accounts.ToDictionary(
            account => account.AccountId,
            StringComparer.OrdinalIgnoreCase);

        public List<string> LoadedAccountIds { get; } = [];

        public Task<MeetingAccount?> LoadAsync(
            string accountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadedAccountIds.Add(accountId);
            _accounts.TryGetValue(accountId, out var account);
            return Task.FromResult(account);
        }
    }

    private sealed class FakeRuntimeFactory(FakeRuntime desktop, FakeRuntime web)
        : IMeetingEngineRuntimeFactory
    {
        public IMeetingEngineRuntime Get(SessionEngineType engineType) =>
            engineType == SessionEngineType.Desktop ? desktop : web;
    }

    private sealed class FakeRuntime(SessionEngineType engineType) : IMeetingEngineRuntime
    {
        public SessionEngineType EngineType { get; } = engineType;
        public List<string> Calls { get; } = [];
        public MeetingOperationResult SwitchAccountResult { get; init; } = MeetingOperationResult.Success();
        public MeetingOperationResult LaunchResult { get; init; } = MeetingOperationResult.Success();
        public MeetingOperationResult VerifyJoinedResult { get; init; } = MeetingOperationResult.Success();
        public MeetingOperationResult DisableMicrophoneResult { get; init; } = MeetingOperationResult.Success();
        public MeetingOperationResult DisableCameraResult { get; init; } = MeetingOperationResult.Success();
        public MeetingOperationResult StartAutoAdmitResult { get; init; } = MeetingOperationResult.Success();
        public MeetingOperationResult StopAutoAdmitResult { get; init; } = MeetingOperationResult.Success();

        public Task<MeetingOperationResult> SwitchAccountAsync(
            MeetingAccount account,
            CancellationToken cancellationToken = default) =>
            Complete("switch-account", SwitchAccountResult, cancellationToken);

        public Task<MeetingOperationResult> LaunchAsync(
            MeetingLaunchContext context,
            CancellationToken cancellationToken = default) =>
            Complete("launch", LaunchResult, cancellationToken);

        public Task<MeetingOperationResult> VerifyJoinedAsync(
            MeetingLaunchContext context,
            CancellationToken cancellationToken = default) =>
            Complete("verify-joined", VerifyJoinedResult, cancellationToken);

        public Task<MeetingOperationResult> DisableMicrophoneAsync(
            MeetingLaunchContext context,
            CancellationToken cancellationToken = default) =>
            Complete("disable-mic", DisableMicrophoneResult, cancellationToken);

        public Task<MeetingOperationResult> DisableCameraAsync(
            MeetingLaunchContext context,
            CancellationToken cancellationToken = default) =>
            Complete("disable-camera", DisableCameraResult, cancellationToken);

        public Task<MeetingOperationResult> StartAutoAdmitAsync(
            MeetingLaunchContext context,
            CancellationToken cancellationToken = default) =>
            Complete("start-auto-admit", StartAutoAdmitResult, cancellationToken);

        public Task<MeetingOperationResult> StopAutoAdmitAsync(
            MeetingLaunchContext context,
            CancellationToken cancellationToken = default) =>
            Complete("stop-auto-admit", StopAutoAdmitResult, cancellationToken);

        private Task<MeetingOperationResult> Complete(
            string call,
            MeetingOperationResult result,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(call);
            return Task.FromResult(result);
        }
    }
}
