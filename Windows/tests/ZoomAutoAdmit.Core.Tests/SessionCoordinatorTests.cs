using ZoomAutoAdmit.Core.Sessions;
using Xunit;

namespace ZoomAutoAdmit.Core.Tests;

public sealed class SessionCoordinatorTests
{
    [Fact]
    public void DesktopAvailableSelectsDesktop()
    {
        var coordinator = new SessionCoordinator();

        var result = coordinator.Allocate("account-id-1");

        Assert.True(result.IsSuccess);
        Assert.Equal(SessionEngineType.Desktop, result.Session!.EngineType);
        Assert.Null(result.Session.WebProfileName);
    }

    [Fact]
    public void DesktopBusySelectsWebWithAccountProfile()
    {
        var coordinator = new SessionCoordinator();
        Assert.True(coordinator.Allocate("account-id-1").IsSuccess);

        var result = coordinator.Allocate("account-id-2");

        Assert.True(result.IsSuccess);
        Assert.Equal(SessionEngineType.Web, result.Session!.EngineType);
        Assert.Equal("account-id-2", result.Session.WebProfileName);
    }

    [Fact]
    public void TwoWebSessionsWithDifferentProfilesAreAllowed()
    {
        var coordinator = new SessionCoordinator();
        var desktop = coordinator.Allocate("account-id-1");
        var firstWeb = coordinator.Allocate("account-id-2");
        var secondWeb = coordinator.Allocate("account-id-3");

        Assert.True(desktop.IsSuccess);
        Assert.True(firstWeb.IsSuccess);
        Assert.True(secondWeb.IsSuccess);
        Assert.Equal(SessionEngineType.Web, firstWeb.Session!.EngineType);
        Assert.Equal(SessionEngineType.Web, secondWeb.Session!.EngineType);
        Assert.NotEqual(firstWeb.Session.WebProfileName, secondWeb.Session.WebProfileName);
        Assert.Equal(3, coordinator.ActiveSessions.Count);
    }

    [Fact]
    public void SameAccountRunsSeveralWebMeetingsEachWithItsOwnProfile()
    {
        var coordinator = new SessionCoordinator();
        Assert.True(coordinator.Allocate("desktop-account").IsSuccess);
        var first = coordinator.Allocate("web-account");
        var second = coordinator.Allocate("web-account");
        var third = coordinator.Allocate("web-account");

        Assert.True(second.IsSuccess);
        Assert.True(third.IsSuccess);
        Assert.Equal(SessionEngineType.Web, second.Session!.EngineType);
        Assert.Equal("web-account", first.Session!.WebProfileName);
        Assert.Equal("web-account-2", second.Session.WebProfileName);
        Assert.Equal("web-account-3", third.Session.WebProfileName);
        Assert.Equal("web-account", AccountWebProfile.BaseProfileOf(second.Session.WebProfileName!));
    }

    [Fact]
    public void SimultaneousWebMeetingsForOneAccountAreCapped()
    {
        var coordinator = new SessionCoordinator();
        Assert.True(coordinator.Allocate("desktop-account").IsSuccess);
        for (int i = 0; i < SessionAllocationPolicy.MaxWebInstancesPerAccount; i++)
            Assert.True(coordinator.Allocate("web-account").IsSuccess);

        var rejected = coordinator.Allocate("web-account");

        Assert.False(rejected.IsSuccess);
        Assert.Equal(SessionAllocationError.WebProfileLocked, rejected.Error);
        Assert.Contains("simultaneous Web meetings", rejected.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentRequestsNeverShareOneWebProfile()
    {
        var coordinator = new SessionCoordinator();
        Assert.True(coordinator.Allocate("desktop-account").IsSuccess);
        using var start = new ManualResetEventSlim(false);

        Task<SessionAllocationResult>[] requests = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                return coordinator.Allocate("shared-web-account");
            }))
            .ToArray();
        start.Set();
        var results = await Task.WhenAll(requests);

        Assert.All(results, result => Assert.True(result.IsSuccess));
        var profiles = results.Select(result => result.Session!.WebProfileName).ToArray();
        Assert.Equal(profiles.Length, profiles.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void SessionsTrackStatusAndReleaseIndependently()
    {
        var coordinator = new SessionCoordinator();
        var desktop = coordinator.Allocate("account-id-1").Session!;
        var web = coordinator.Allocate("account-id-2").Session!;

        Assert.True(coordinator.TryUpdateStatus(web.SessionId, SessionStatus.Active, out var activeWeb));
        Assert.Equal(SessionStatus.Active, activeWeb!.Status);
        Assert.Equal(SessionStatus.Allocated,
            coordinator.ActiveSessions.Single(session => session.SessionId == desktop.SessionId).Status);

        Assert.True(coordinator.Release(desktop.SessionId));
        Assert.Single(coordinator.ActiveSessions);
        Assert.Equal(web.SessionId, coordinator.ActiveSessions[0].SessionId);
    }
}
