using ZoomAutoAdmit.WebAutomation.Lms;
using Xunit;

namespace ZoomAutoAdmit.WebAutomation.Tests;

/// <summary>
/// Which sign-in a class goes up under. One PC runs several people's classes, so this is what
/// stops one coordinator's class being started, filled in and signed off as another's.
/// </summary>
public sealed class ClassLmsAccountsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ZoomClassLmsAccounts", Guid.NewGuid().ToString("N"));

    private ClassLmsAccounts Store() => new(Path.Combine(_folder, "class-accounts.json"));

    public void Dispose() { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }

    [Fact]
    public void AGroupNobodyClaimedIsNotAnyonesInParticular()
    {
        var store = Store();
        Assert.Null(store.Find("CAI5_AIS4_S7"));
        Assert.Equal("", store.Whose("CAI5_AIS4_S7"));
        Assert.Empty(store.List());
    }

    [Fact]
    public void EachCoordinatorsGroupsPointAtTheirOwnSignIn()
    {
        var store = Store();
        store.SetGroups("u-mona", "Mona", "mona", ["CAI5_AIS4_S7", "CAI5_AIS4_S9"], zoomAccount: "S7");
        store.SetGroups("u-sami", "Sami", "sami", ["CAI5_AIS4_S8"]);

        Assert.Equal("mona", store.Find("CAI5_AIS4_S7")!.AccountId);
        Assert.Equal("S7", store.Find("CAI5_AIS4_S9")!.ZoomAccount);
        Assert.Equal("sami", store.Find("CAI5_AIS4_S8")!.AccountId);
        Assert.Equal("Mona", store.Whose("cai5_ais4_s7"));                      // the group's case is not the point
        Assert.Equal(2, store.Of("u-mona").Count);
    }

    [Fact]
    public void AGroupSetAgainReplacesTheWholeSetSoALostGroupStopsBeingTheirs()
    {
        var store = Store();
        store.SetGroups("u-mona", "Mona", "mona", ["CAI5_AIS4_S7", "CAI5_AIS4_S9"]);
        store.SetGroups("u-mona", "Mona", "mona", ["CAI5_AIS4_S7"]);

        Assert.Equal("Mona", store.Whose("CAI5_AIS4_S7"));
        Assert.Null(store.Find("CAI5_AIS4_S9"));
    }

    [Fact]
    public void AGroupHandedToSomebodyElseBelongsToWhoeverWasSetLast()
    {
        var store = Store();
        store.SetGroups("u-mona", "Mona", "mona", ["CAI5_AIS4_S7"]);
        store.SetGroups("u-sami", "Sami", "sami", ["CAI5_AIS4_S7"]);

        Assert.Equal("Sami", store.Whose("CAI5_AIS4_S7"));
        Assert.Single(store.List());
        Assert.Empty(store.Of("u-mona"));
    }

    [Fact]
    public void ForgettingACoordinatorGivesTheirGroupsBackToThisPc()
    {
        var store = Store();
        store.SetGroups("u-mona", "Mona", "mona", ["CAI5_AIS4_S7"]);
        store.SetGroups("u-sami", "Sami", "sami", ["CAI5_AIS4_S8"]);
        store.Forget("u-mona");

        Assert.Null(store.Find("CAI5_AIS4_S7"));
        Assert.Equal("Sami", store.Whose("CAI5_AIS4_S8"));
    }

    [Fact]
    public void WhatWasSetIsStillThereForTheNextProcess()
    {
        Store().SetGroups("u-mona", "Mona", "mona", ["CAI5_AIS4_S7"]);
        // A scheduled class opens in its own process; it has to read the same answer.
        Assert.Equal("Mona", Store().Whose("CAI5_AIS4_S7"));
    }

    [Fact]
    public void AGroupWhoseAccountIsNoLongerOnThisPcFallsBackInsteadOfFailing()
    {
        var store = new ClassLmsAccounts(Path.Combine(_folder, "class-accounts.json"),
            new LmsAccountDirectory(Path.Combine(_folder, "accounts.json")));
        store.SetGroups("u-mona", "Mona", "removed-account", ["CAI5_AIS4_S7"]);

        // It still answers with a usable sign-in - this PC's own - rather than a target that is
        // not there. The class runs and says whose account it could not find.
        Assert.NotNull(store.StoreFor("CAI5_AIS4_S7"));
        Assert.Equal("Mona", store.Whose("CAI5_AIS4_S7"));
    }

    [Fact]
    public void ADamagedFileIsNotAnError()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "class-accounts.json"), "{ not json");
        Assert.Empty(Store().List());
        Assert.Null(Store().Find("CAI5_AIS4_S7"));
    }
}
