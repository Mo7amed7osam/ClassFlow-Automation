using ZoomAutoAdmit.WebAutomation.Browser;
using Xunit;

namespace ZoomAutoAdmit.WebAutomation.Tests;

/// <summary>
/// Which profile a meeting's browser may open. Chromium takes a user-data directory for itself, so
/// a class whose profile is still open elsewhere never opened at all: every retry asked for the
/// same directory and got the same refusal (2026-09-22, S7 at 18:45).
/// </summary>
public sealed class ZoomProfileLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ZoomProfileLockTests", Guid.NewGuid().ToString("N"));

    private ZoomBrowserProfile Profile(string name)
    {
        var profile = new ZoomProfileManager(_root).GetOrCreate(name);
        File.WriteAllText(Path.Combine(profile.DirectoryPath, "Preferences"), "{}");     // a profile with something in it
        return profile;
    }

    private static FileStream Hold(ZoomBrowserProfile profile) =>
        new(Path.Combine(profile.DirectoryPath, "SingletonLock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);

    [Fact]
    public void AProfileNobodyIsUsingIsTheOneUsed()
    {
        var profile = Profile("s7");
        Assert.Equal(profile.Name, ZoomProfileLock.Free(profile, out string? said).Name);
        Assert.Null(said);
    }

    [Fact]
    public void ALockLeftByABrowserThatIsGoneIsCleared()
    {
        var profile = Profile("s7");
        File.WriteAllText(Path.Combine(profile.DirectoryPath, "SingletonLock"), "dead browser");

        var chosen = ZoomProfileLock.Free(profile, out string? said);

        Assert.Equal(profile.Name, chosen.Name);                                   // the same profile, now free
        Assert.False(File.Exists(Path.Combine(profile.DirectoryPath, "SingletonLock")));
        Assert.Contains("cleared", said);
    }

    [Fact]
    public void AProfileAnotherBrowserHoldsMovesTheMeetingToItsNextCopy()
    {
        var profile = Profile("s7");
        using var held = Hold(profile);

        var chosen = ZoomProfileLock.Free(profile, out string? said);

        Assert.Equal("s7-2", chosen.Name);
        Assert.True(Directory.Exists(chosen.DirectoryPath));
        Assert.True(File.Exists(Path.Combine(chosen.DirectoryPath, "Preferences")));   // seeded, so it is signed in
        Assert.Contains("s7-2", said);
    }

    [Fact]
    public void ACopyThatIsBusyTooIsPassedOver()
    {
        var profile = Profile("s7");
        var second = Profile("s7-2");
        using var first = Hold(profile);
        using var alsoHeld = Hold(second);

        Assert.Equal("s7-3", ZoomProfileLock.Free(profile, out _).Name);
    }

    [Fact]
    public void AMeetingAlreadyOnACopyMovesToAnotherCopyNotBackToTheBase()
    {
        Profile("s7");
        var second = Profile("s7-2");
        using var held = Hold(second);

        Assert.Equal("s7-3", ZoomProfileLock.Free(second, out _).Name);
    }

    [Fact]
    public void EveryCopyBusyLeavesTheAskedForProfileAndSaysSo()
    {
        var profile = Profile("s7");
        var locks = new List<FileStream> { Hold(profile) };
        for (int instance = 2; instance <= ZoomProfileLock.MostCopies; instance++) locks.Add(Hold(Profile($"s7-{instance}")));
        try
        {
            // Chromium's own refusal is then what the class reports, rather than a profile of
            // somebody else's being used quietly.
            Assert.Equal("s7", ZoomProfileLock.Free(profile, out string? said).Name);
            Assert.Contains("every copy", said);
        }
        finally { foreach (var held in locks) held.Dispose(); }
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }
}
