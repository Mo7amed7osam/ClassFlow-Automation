using Xunit;
using ZoomAutoAdmit.Core.Meetings;

namespace ZoomAutoAdmit.Core.Tests;

/// <summary>How each class's meeting ended, for the class card: by the program, by hand, or elsewhere.</summary>
public sealed class ClassEndingsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"endings-{Guid.NewGuid():N}.json");
    private static readonly DateOnly Day = new(2026, 9, 16);
    private static readonly TimeOnly Start = new(19, 0);

    public void Dispose()
    {
        try { File.Delete(_path); } catch (IOException) { }
    }

    private ClassEnding Ending(ClassEndedHow how, string group = "CAI5_AIS4_S7") => new()
    {
        Group = group,
        Date = Day,
        Start = Start,
        At = new DateTimeOffset(2026, 9, 16, 22, 1, 0, TimeSpan.FromHours(3)),
        How = how,
        Message = "only the host was left",
    };

    [Fact]
    public void HowAClassEndedIsReadBackForThatClassAlone()
    {
        var endings = new ClassEndings(_path);
        endings.Record(Ending(ClassEndedHow.Program));
        endings.Record(Ending(ClassEndedHow.Elsewhere, "CAI5_AIS4_S8"));

        var s7 = new ClassEndings(_path).For("CAI5_AIS4_S7", Day, Start);
        Assert.NotNull(s7);
        Assert.Equal(ClassEndedHow.Program, s7!.How);
        Assert.Equal("only the host was left", s7.Message);
        Assert.Equal(ClassEndedHow.Elsewhere, new ClassEndings(_path).For("CAI5_AIS4_S8", Day, Start)!.How);
        Assert.Null(new ClassEndings(_path).For("CAI5_AIS4_S7", Day, new TimeOnly(17, 0)));
    }

    [Fact]
    public void TheFirstAnswerWinsSoTheSameEndingIsNotRewritten()
    {
        var endings = new ClassEndings(_path);
        endings.Record(Ending(ClassEndedHow.Program));
        // A moment later the meeting is simply gone - the same ending seen again, not a new one.
        endings.Record(Ending(ClassEndedHow.Elsewhere));

        Assert.Equal(ClassEndedHow.Program, Assert.Single(endings.All()).How);
    }

    [Fact]
    public void NothingWrittenYetIsNotAnError() => Assert.Null(new ClassEndings(_path).For("CAI5_AIS4_S7", Day, Start));
}
