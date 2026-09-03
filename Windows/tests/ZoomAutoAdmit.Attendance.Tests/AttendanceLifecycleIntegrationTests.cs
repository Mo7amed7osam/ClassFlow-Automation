using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;
using ZoomAutoAdmit.Core.Meetings;
using ZoomAutoAdmit.Core.Sessions;

namespace ZoomAutoAdmit.Attendance.Tests;

public class AttendanceLifecycleIntegrationTests
{
    private sealed class Store : IAttendanceSnapshotStore
    {
        public ConcurrentQueue<AttendanceSnapshot> Snapshots { get; } = new();
        public Task SaveAsync(AttendanceSnapshot snapshot, CancellationToken token)
        {
            Snapshots.Enqueue(snapshot);
            return Task.CompletedTask;
        }
    }

    private static IAttendanceParticipantSource Source(MeetingLaunchContext context)
    {
        var source = new Mock<IAttendanceParticipantSource>();
        source.SetupGet(s => s.Source).Returns(context.EngineType == SessionEngineType.Web
            ? AttendanceSource.Web : AttendanceSource.Desktop);
        source.Setup(s => s.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            new ParticipantReadResult([new("Same name"), new("Same name")], true));
        return source.Object;
    }

    private static MeetingLaunchContext Context(string account, SessionEngineType engine = SessionEngineType.Desktop)
    {
        var meeting = new ScheduledMeeting(new Uri("https://zoom.us/j/12345678901?pwd=SECRET"), account,
            DateTimeOffset.UtcNow);
        return new(new MeetingSession(Guid.NewGuid(), meeting, DateTimeOffset.UtcNow),
            new MeetingAccount(account, "Group " + account, "CREDENTIAL-DO-NOT-STORE"), engine, account);
    }

    [Fact]
    public async Task MockLifecycleCapturesAllReasonsMetadataAndStops()
    {
        var events = new MeetingLifecycleEvents();
        var context = Context("S7");
        var store = new Store();
        var clock = new FakeTimeProvider();
        var logs = new ConcurrentQueue<string>();
        await using var bridge = new AttendanceLifecycleBridge(events, Source, store, clock, logs.Enqueue);
        await events.PublishAsync(context, MeetingLifecycleEventKind.Active);
        await events.PublishAsync(context, MeetingLifecycleEventKind.Active);
        events.PublishAdmission(context.Session.SessionId);
        await bridge.DrainAsync(context.Session.SessionId);
        await bridge.CaptureManualAsync(context.Session.SessionId);
        clock.Advance(TimeSpan.FromMinutes(15));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (store.Snapshots.Count < 4) await Task.Delay(10, timeout.Token);
        await events.PublishAsync(context, MeetingLifecycleEventKind.Ending);
        events.PublishAdmission(context.Session.SessionId);
        await bridge.CaptureManualAsync(context.Session.SessionId);
        await events.PublishAsync(context, MeetingLifecycleEventKind.Active);
        Assert.Equal(new[] { "MeetingStart", "AdmitEvent", "Manual", "Scheduled", "MeetingEnd" },
            store.Snapshots.Select(s => s.Reason));
        Assert.All(store.Snapshots, snapshot =>
        {
            Assert.Equal(context.Session.SessionId, snapshot.SessionId);
            Assert.Equal("S7", snapshot.Meeting!.AccountId);
            Assert.Equal("Group S7", snapshot.Meeting.AccountDisplayName);
            Assert.Equal("https://zoom.us/j/12345678901", snapshot.Meeting.MeetingUrl);
            Assert.Equal(2, snapshot.Participants.Count);
        });
        Assert.Contains(logs, s => s.Contains("Reason: AdmitEvent"));
    }

    [Fact]
    public async Task AsyncMonitorScopesKeepParallelDesktopWebEventsIsolated()
    {
        var events = new MeetingLifecycleEvents();
        var desktop = Context("A");
        var web = Context("B", SessionEngineType.Web);
        var store = new Store();
        await using var bridge = new AttendanceLifecycleBridge(events, Source, store);
        await Task.WhenAll(events.PublishAsync(desktop, MeetingLifecycleEventKind.Active),
            events.PublishAsync(web, MeetingLifecycleEventKind.Active));
        Task desktopTask, webTask;
        using (MeetingAdmissionScope.Begin(desktop.Session.SessionId, events))
            desktopTask = Task.Run(MeetingAdmissionScope.NotifyVerified);
        using (MeetingAdmissionScope.Begin(web.Session.SessionId, events))
            webTask = Task.Run(MeetingAdmissionScope.NotifyVerified);
        await Task.WhenAll(desktopTask, webTask);
        MeetingAdmissionScope.NotifyVerified(); // Outside a session: must not publish.
        events.PublishAdmission(Guid.NewGuid()); // Unknown session: ignored.
        await Task.WhenAll(bridge.DrainAsync(desktop.Session.SessionId), bridge.DrainAsync(web.Session.SessionId));
        Assert.Equal(4, store.Snapshots.Count);
        Assert.Single(store.Snapshots.Where(s => s.SessionId == desktop.Session.SessionId && s.Reason == "AdmitEvent"));
        Assert.Single(store.Snapshots.Where(s => s.SessionId == web.Session.SessionId && s.Reason == "AdmitEvent"));
    }

    [Theory]
    [InlineData(SessionEngineType.Desktop)]
    [InlineData(SessionEngineType.Web)]
    public async Task RealOrchestratorPublishesActiveAndFinalBeforeRuntimeStop(SessionEngineType engine)
    {
        var events = new MeetingLifecycleEvents();
        var store = new Store();
        var runtime = Runtime(engine);
        // The monitor is started as soon as the meeting is launched, before the Active event.
        // Like the real monitors, it inherits the admission scope at start and reports verified
        // admissions later, once the meeting is active.
        var activeReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? admission = null;
        runtime.Setup(r => r.StartAutoAdmitAsync(It.IsAny<MeetingLaunchContext>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                admission = Task.Run(async () =>
                {
                    await activeReached.Task;
                    MeetingAdmissionScope.NotifyVerified();
                });
                return Task.FromResult(MeetingOperationResult.Success());
            });
        runtime.Setup(r => r.StopAutoAdmitAsync(It.IsAny<MeetingLaunchContext>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                Assert.Equal("MeetingEnd", store.Snapshots.Last().Reason);
                return Task.FromResult(MeetingOperationResult.Success());
            });
        await using var bridge = new AttendanceLifecycleBridge(events, Source, store);
        events.Lifecycle += message =>
        {
            if (message.Kind == MeetingLifecycleEventKind.Active) activeReached.TrySetResult();
            return Task.CompletedTask;
        };
        var orchestrator = Orchestrator(events, runtime);
        var meeting = new ScheduledMeeting(new Uri("https://zoom.us/j/12345678901"), "account", DateTimeOffset.UtcNow,
            PreferredEngine: engine);
        var session = await orchestrator.RunAsync(meeting);
        Assert.Equal(MeetingState.Monitoring, session.State);
        await admission!.WaitAsync(TimeSpan.FromSeconds(5));
        await bridge.DrainAsync(session.SessionId);
        Assert.True(await orchestrator.EndAsync(session));
        Assert.Equal(new[] { "MeetingStart", "AdmitEvent", "MeetingEnd" }, store.Snapshots.Select(s => s.Reason));
    }

    [Fact]
    public async Task AutoAdmitStartFailureHappensBeforeAnyCollectorStarts()
    {
        var events = new MeetingLifecycleEvents();
        var store = new Store();
        var runtime = Runtime(SessionEngineType.Desktop);
        runtime.Setup(r => r.StartAutoAdmitAsync(It.IsAny<MeetingLaunchContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MeetingOperationResult.Failure("failed"));
        await using var bridge = new AttendanceLifecycleBridge(events, Source, store);
        var session = await Orchestrator(events, runtime).RunAsync(
            new(new Uri("https://zoom.us/j/12345678901"), "account", DateTimeOffset.UtcNow));
        Assert.Equal(MeetingState.Failed, session.State);
        // The monitor starts right after launch, before the Active event, so nothing was collected.
        Assert.Empty(store.Snapshots);
    }

    [Fact]
    public async Task JoinFailureAfterMonitorStartStopsRuntimeWithoutCollector()
    {
        var events = new MeetingLifecycleEvents();
        var store = new Store();
        var runtime = Runtime(SessionEngineType.Desktop);
        runtime.Setup(r => r.VerifyJoinedAsync(It.IsAny<MeetingLaunchContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MeetingOperationResult.Failure("not joined"));
        await using var bridge = new AttendanceLifecycleBridge(events, Source, store);
        var session = await Orchestrator(events, runtime).RunAsync(
            new(new Uri("https://zoom.us/j/12345678901"), "account", DateTimeOffset.UtcNow));
        Assert.Equal(MeetingState.Failed, session.State);
        runtime.Verify(r => r.StopAutoAdmitAsync(It.IsAny<MeetingLaunchContext>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Empty(store.Snapshots);
    }

    [Fact]
    public async Task ShutdownAndMonitorExitDrainAdmissionThenCaptureEndOnce()
    {
        var events = new MeetingLifecycleEvents();
        var context = Context("account");
        var store = new Store();
        var bridge = new AttendanceLifecycleBridge(events, Source, store);
        await events.PublishAsync(context, MeetingLifecycleEventKind.Active);
        using (MeetingAdmissionScope.Begin(context.Session.SessionId, events, context))
        {
            MeetingAdmissionScope.NotifyVerified();
            await MeetingAdmissionScope.NotifyMonitorStoppedAsync();
        }
        await bridge.DisposeAsync();
        await bridge.DisposeAsync();
        events.PublishAdmission(context.Session.SessionId);
        Assert.Equal(new[] { "MeetingStart", "AdmitEvent", "MeetingEnd" }, store.Snapshots.Select(s => s.Reason));
    }

    [Fact]
    public async Task ObserverFailureDoesNotFailMeetingOrAllocation()
    {
        var events = new MeetingLifecycleEvents();
        events.Lifecycle += _ => throw new InvalidOperationException("broken observer");
        var session = await Orchestrator(events, Runtime(SessionEngineType.Desktop)).RunAsync(
            new(new Uri("https://zoom.us/j/12345678901"), "account", DateTimeOffset.UtcNow));
        Assert.Equal(MeetingState.Monitoring, session.State);
    }

    private static Mock<IMeetingEngineRuntime> Runtime(SessionEngineType engine)
    {
        var runtime = new Mock<IMeetingEngineRuntime>();
        runtime.SetupGet(r => r.EngineType).Returns(engine);
        runtime.SetReturnsDefault(Task.FromResult(MeetingOperationResult.Success()));
        return runtime;
    }

    private static MeetingOrchestrator Orchestrator(MeetingLifecycleEvents events, Mock<IMeetingEngineRuntime> runtime)
    {
        var accounts = new Mock<IMeetingAccountManager>();
        accounts.Setup(a => a.LoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MeetingAccount("account", "Account", "reference"));
        var factory = new Mock<IMeetingEngineRuntimeFactory>();
        factory.Setup(f => f.Get(It.IsAny<SessionEngineType>())).Returns(runtime.Object);
        return new(accounts.Object, new SessionCoordinator(), factory.Object, events);
    }
}
