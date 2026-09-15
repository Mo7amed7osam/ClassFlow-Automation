using ZoomAutoAdmit.Core.Meetings;
using Xunit;

namespace ZoomAutoAdmit.Core.Tests;

[Collection("AdmissionLedger")]
public sealed class AdmissionLedgerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "zaa-ledger-" + Guid.NewGuid().ToString("N"));
    private readonly string _previous = AdmissionLedger.Folder;

    public AdmissionLedgerTests() => AdmissionLedger.Folder = _folder;

    public void Dispose()
    {
        AdmissionLedger.Folder = _previous;
        try { Directory.Delete(_folder, true); } catch { }
    }

    [Theory]
    [InlineData("Sara Ahmed entered the waiting room", "Sara Ahmed")]
    [InlineData("  \"Omar Khaled\" has joined the waiting room.", "Omar Khaled")]
    [InlineData("Mona Adel is waiting", "Mona Adel")]
    [InlineData("Youssef Ali", "Youssef Ali")]
    // What the Zoom app's waiting list reads out (seen on 2026-09-15).
    [InlineData("Haya Elnagdy(Guest), Press Space to admit", "Haya Elnagdy")]
    [InlineData("Ahmed Hammouda Korany Salama(Guest), Press Space to admit", "Ahmed Hammouda Korany Salama")]
    [InlineData("Sayed ayman Sayed ibrahim (Guest)", "Sayed ayman Sayed ibrahim")]
    public void CleanNameKeepsOnlyThePersonsName(string raw, string expected) =>
        Assert.Equal(expected, AdmissionLedger.CleanName(raw));

    [Fact]
    public void CleanNameRejectsEmptyOrAbsurdNames()
    {
        Assert.Null(AdmissionLedger.CleanName(null));
        Assert.Null(AdmissionLedger.CleanName("   "));
        Assert.Null(AdmissionLedger.CleanName(new string('x', 121)));
    }

    [Fact]
    public void EveryAdmitIsReadBackInOrderWithItsSource()
    {
        AdmissionLedger.Record("Sara Ahmed entered the waiting room", "desktop");
        AdmissionLedger.Record(null, "desktop", people: 3);
        AdmissionLedger.Record("Omar Khaled", "web");

        var entries = AdmissionLedger.Read(DateOnly.FromDateTime(DateTime.Now));

        Assert.Equal(3, entries.Count);
        Assert.Equal(("Sara Ahmed", "desktop", 1), (entries[0].Name, entries[0].Source, entries[0].People));
        Assert.Equal((null as string, 3), (entries[1].Name, entries[1].People));
        Assert.Equal("web", entries[2].Source);
        Assert.Empty(AdmissionLedger.Read(DateOnly.FromDateTime(DateTime.Now).AddDays(-1)));
    }
}
