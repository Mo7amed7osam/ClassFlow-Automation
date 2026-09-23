using ZoomAutoAdmit.WebAutomation.Browser;
using Xunit;

namespace ZoomAutoAdmit.WebAutomation.Tests;

public sealed class ZoomProfileManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ZoomAutoAdmitTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void FirstUseCreatesDefaultProfileAndRequiresVisibleBrowser()
    {
        var manager = new ZoomProfileManager(_root);

        var profile = manager.GetOrCreate("default");
        var plan = manager.CreateLaunchPlan(profile, forceHeaded: false);

        Assert.Equal("Default", profile.Name);
        Assert.True(Directory.Exists(profile.DirectoryPath));
        Assert.False(profile.HasReusableSession);
        Assert.False(plan.Headless);
    }

    [Fact]
    public void ReadyMarkerIsReusedByFutureManagerAndEnablesHeadlessMode()
    {
        var firstManager = new ZoomProfileManager(_root);
        var initialized = firstManager.MarkSessionReady(firstManager.GetOrCreate("account1"));

        var futureManager = new ZoomProfileManager(_root);
        var reused = futureManager.GetOrCreate("account1");
        var plan = futureManager.CreateLaunchPlan(reused, forceHeaded: false);

        Assert.True(initialized.HasReusableSession);
        Assert.True(reused.HasReusableSession);
        Assert.True(plan.Headless);
        Assert.Single(Directory.GetFiles(reused.DirectoryPath));
        Assert.DoesNotContain("password", File.ReadAllText(reused.ReadyMarkerPath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HeadedOptionOverridesReusableSessionForManualRefresh()
    {
        var manager = new ZoomProfileManager(_root);
        var profile = manager.MarkSessionReady(manager.GetOrCreate("account2"));

        Assert.False(manager.CreateLaunchPlan(profile, forceHeaded: true).Headless);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("account/other")]
    [InlineData("")]
    public void UnsafeProfileNamesAreRejected(string name)
    {
        var manager = new ZoomProfileManager(_root);

        Assert.Throws<ArgumentException>(() => manager.GetOrCreate(name));
    }

    [Fact]
    public void ACopyOfASignedInProfileCarriesItsSessionButNotTheClaimToBeSignedIn()
    {
        var manager = new ZoomProfileManager(_root);
        var account = manager.GetOrCreate("s8");
        File.WriteAllText(Path.Combine(account.DirectoryPath, "Preferences"), "{}");
        Directory.CreateDirectory(Path.Combine(account.DirectoryPath, "Default"));
        File.WriteAllText(Path.Combine(account.DirectoryPath, "Default", "Cookies"), "the session");
        File.WriteAllText(account.ReadyMarkerPath, "signed in");

        var copy = manager.GetOrCreate("s8-2");

        // What Zoom might still accept comes along...
        Assert.True(File.Exists(Path.Combine(copy.DirectoryPath, "Default", "Cookies")));
        // ...but a copy is never taken for signed in: Zoom does not always accept a session in
        // another profile, and one that claims to be signed in is never asked to sign in, so it
        // joined the class as a guest and admitted nobody (2026-09-23, s8-2).
        Assert.False(copy.HasReusableSession);
        Assert.False(File.Exists(Path.Combine(copy.DirectoryPath, ".zoom-session-ready")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
