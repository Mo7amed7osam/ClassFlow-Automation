using Xunit;
using ZoomAutoAdmit.WindowsUI.Services;

namespace ZoomAutoAdmit.WindowsUI.Tests;

/// <summary>
/// Who this PC is signed in as is kept here, so a coordinator whose server is off is still the same
/// coordinator - before this, losing the network showed them the admin's pages.
/// </summary>
public sealed class CentralIdentityCacheTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"zaa-identity-{Guid.NewGuid():N}.json");

    private static CentralMe Coordinator(string username, DateTimeOffset expires) =>
        new("u1", username, "Omar Fathy", "coordinator", AllGroups: false,
            [new CentralGroupRef("g1", "CAI5_AIS4_S7", null, false)], expires);

    [Fact]
    public void TheAccountTheServerLastDescribedComesBackWhileItsSessionLasts()
    {
        var cache = new CentralIdentityCache(_path);
        cache.Save(Coordinator("omar", DateTimeOffset.UtcNow.AddDays(30)));

        var remembered = cache.Read("omar");
        Assert.NotNull(remembered);
        Assert.False(remembered!.IsAdmin);
        Assert.Equal("Omar Fathy", remembered.DisplayName);
        Assert.Equal("CAI5_AIS4_S7", Assert.Single(remembered.Groups!).Name);
        Assert.Equal(remembered.Username, cache.Read("OMAR")!.Username);       // the name is not case-sensitive
    }

    [Fact]
    public void ASessionThatWouldHaveRunOutIsNotUsed()
    {
        var cache = new CentralIdentityCache(_path);
        cache.Save(Coordinator("omar", DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.Null(cache.Read("omar"));
    }

    [Fact]
    public void SigningOutRemovesOnlyThatAccount()
    {
        var cache = new CentralIdentityCache(_path);
        cache.Save(Coordinator("omar", DateTimeOffset.UtcNow.AddDays(30)));
        cache.Save(Coordinator("mona", DateTimeOffset.UtcNow.AddDays(30)));

        cache.Forget("omar");

        Assert.Null(cache.Read("omar"));
        Assert.NotNull(cache.Read("mona"));
        Assert.Null(new CentralIdentityCache(_path).Read("omar"));             // and it stays gone
    }

    [Fact]
    public void NothingSavedYetIsNotAnError() => Assert.Null(new CentralIdentityCache(_path).Read("nobody"));

    public void Dispose()
    {
        try { File.Delete(_path); } catch (IOException) { }
    }
}
