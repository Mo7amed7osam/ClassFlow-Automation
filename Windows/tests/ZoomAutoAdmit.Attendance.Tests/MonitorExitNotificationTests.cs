using Moq;
using Xunit;
using ZoomAutoAdmit.Core.Engines;
using ZoomAutoAdmit.Core.Meetings;
using ZoomAutoAdmit.Core.Models;
using ZoomAutoAdmit.Core.Sessions;
using ZoomAutoAdmit.WindowsRuntime;

namespace ZoomAutoAdmit.Attendance.Tests;

public class MonitorExitNotificationTests
{
    [Theory]
    [InlineData(SessionEngineType.Desktop)]
    [InlineData(SessionEngineType.Web)]
    public async Task SlowEndingObserverDoesNotHideImmediateEngineExit(SessionEngineType engineType)
    {
        var events = new MeetingLifecycleEvents();
        var endingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        events.Lifecycle += message =>
        {
            Assert.Equal(MeetingLifecycleEventKind.Ending, message.Kind);
            endingEntered.TrySetResult();
            return release.Task;
        };
        var meeting = new ScheduledMeeting(new Uri("https://zoom.us/j/12345678901"), "account", DateTimeOffset.UtcNow);
        var session = new MeetingSession(Guid.NewGuid(), meeting, DateTimeOffset.UtcNow);
        var context = new MeetingLaunchContext(session, new("account", "Account", "reference"), engineType, "account");
        IMeetingEngineRuntime runtime;
        if (engineType == SessionEngineType.Desktop)
        {
            var engine = new Mock<IAutoAdmitEngine>();
            engine.SetupGet(e => e.Name).Returns("windows");
            engine.Setup(e => e.RunAsync(It.IsAny<CliOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);
            runtime = new WindowsDesktopMeetingLauncher(engine.Object, Mock.Of<IWindowsDesktopMeetingPlatform>());
        }
        else
        {
            var engine = new Mock<IWindowsWebAutoAdmitLifecycle>();
            engine.Setup(e => e.StartAsync(It.IsAny<CliOptions>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            engine.Setup(e => e.MonitorAsync(It.IsAny<CliOptions>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            engine.Setup(e => e.StopAsync()).Returns(Task.CompletedTask);
            runtime = new WindowsWebMeetingLauncher(engine.Object, Mock.Of<IWindowsWebMeetingPreparation>());
            Assert.True((await runtime.LaunchAsync(context)).IsSuccess);
        }
        try
        {
            using var scope = MeetingAdmissionScope.Begin(session.SessionId, events, context);
            var result = await runtime.StartAutoAdmitAsync(context).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(result.IsSuccess);
            await endingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
            await ((IAsyncDisposable)runtime).DisposeAsync();
        }
    }
}
