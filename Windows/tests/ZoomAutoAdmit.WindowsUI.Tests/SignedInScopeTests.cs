using ZoomAutoAdmit.WindowsUI.Services;
using Xunit;

namespace ZoomAutoAdmit.WindowsUI.Tests;

/// <summary>
/// What the person signed in to a shared PC may see of what it holds. One PC serves the admin and
/// several coordinators one after another, and everything it keeps sits in one folder; without
/// this, a coordinator opens Accounts and finds somebody else's Zoom accounts.
/// </summary>
public sealed class SignedInScopeTests
{
    private static CentralMe Admin => new("u-admin", "admin", "The Admin", "admin", true, null, DateTimeOffset.Now.AddDays(1));

    private static CentralMe Coordinator(params string[] groups) =>
        new("u-mona", "mona", "Mona", "coordinator", false,
            [.. groups.Select((name, n) => new CentralGroupRef($"g{n}", name, null, false))],
            DateTimeOffset.Now.AddDays(1));

    private static SignedInScope Scope(CentralMe? me) => new(() => me);

    [Fact]
    public void WithNobodySignedInThePcShowsItsOwnWorkExactlyAsBefore()
    {
        var scope = Scope(null);
        Assert.False(scope.IsSignedIn);
        Assert.True(scope.SeesEverything);
        Assert.True(scope.Owns("CAI5_AIS4_S7"));
        Assert.True(scope.Owns(null));                    // a thing that names no group at all
        Assert.Empty(scope.Groups);
    }

    [Fact]
    public void TheAdminSeesEverythingOnThePc()
    {
        var scope = Scope(Admin);
        Assert.True(scope.IsAdmin);
        Assert.True(scope.SeesEverything);
        Assert.True(scope.Owns("CAI5_IND1_G1"));
        Assert.True(scope.Owns("anything at all"));
    }

    [Fact]
    public void ACoordinatorSeesTheirOwnGroupsAndNobodyElses()
    {
        var scope = Scope(Coordinator("CAI5_IND1_G1", "CAI5_IND1_G2"));
        Assert.False(scope.SeesEverything);
        Assert.True(scope.Owns("CAI5_IND1_G1"));
        Assert.True(scope.Owns("cai5_ind1_g2"));          // the case on a folder is not the point
        Assert.True(scope.Owns("  CAI5_IND1_G1  "));
        Assert.False(scope.Owns("CAI5_AIS4_S7"));         // the admin's
        Assert.False(scope.Owns(null));
        Assert.False(scope.Owns(""));
        Assert.Equal(2, scope.Groups.Count);
    }

    [Fact]
    public void AGroupTakenAwayFromThemStopsBeingTheirs()
    {
        var me = Coordinator("CAI5_IND1_G1");
        var scope = Scope(me);
        Assert.True(scope.Owns("CAI5_IND1_G1"));

        // The same account, with the group archived on the server.
        var without = new CentralMe(me.Id, me.Username, me.DisplayName, me.Role, false,
            [new CentralGroupRef("g0", "CAI5_IND1_G1", null, true)], me.ExpiresAt);
        Assert.False(Scope(without).Owns("CAI5_IND1_G1"));
    }

    [Fact]
    public void AThingThatNamesNoGroupCoversTheirsToo()
    {
        var scope = Scope(Coordinator("CAI5_IND1_G1"));
        // A session role profile with no account listed applies to every meeting, theirs included.
        Assert.True(scope.OwnsAny([]));
        Assert.True(scope.OwnsAny(null));
        Assert.True(scope.OwnsAny(["", "   "]));
        // One that names groups is theirs only if one of them is.
        Assert.True(scope.OwnsAny(["CAI5_AIS4_S7", "CAI5_IND1_G1"]));
        Assert.False(scope.OwnsAny(["CAI5_AIS4_S7", "CAI5_AIS4_S8"]));
    }

    [Fact]
    public void ACoordinatorWhoWasGivenEveryGroupSeesEveryGroup()
    {
        var everything = new CentralMe("u-sami", "sami", "Sami", "coordinator", true, null, DateTimeOffset.Now.AddDays(1));
        Assert.True(Scope(everything).Owns("CAI5_AIS4_S7"));
    }

    [Fact]
    public void ThePageSaysHowMuchItIsNotShowing()
    {
        var mine = Scope(Coordinator("CAI5_IND1_G1"));
        Assert.Equal("Showing the 2 Zoom account(s) of your groups; 3 on this PC belong to somebody else.",
            mine.Narrowed(2, 5, "Zoom account(s)"));
        // Nothing hidden, nothing said.
        Assert.Equal("", mine.Narrowed(5, 5, "Zoom account(s)"));
        Assert.Equal("", Scope(Admin).Narrowed(2, 5, "Zoom account(s)"));
        Assert.Equal("", Scope(null).Narrowed(2, 5, "Zoom account(s)"));
    }

    [Fact]
    public void SigningInAsSomebodyElseTellsEveryPageToLookAgain()
    {
        CentralMe? who = null;
        var scope = new SignedInScope(() => who);
        int looked = 0;
        scope.Changed += () => looked++;

        who = Coordinator("CAI5_IND1_G1");
        scope.Refresh();

        Assert.Equal(1, looked);
        Assert.False(scope.Owns("CAI5_AIS4_S7"));         // it answers as the new person at once
    }
}
