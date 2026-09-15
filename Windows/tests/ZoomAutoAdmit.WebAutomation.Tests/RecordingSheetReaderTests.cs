using ZoomAutoAdmit.WebAutomation.Recordings;
using Xunit;

namespace ZoomAutoAdmit.WebAutomation.Tests;

public sealed class RecordingSheetReaderTests
{
    // Fake Drive ids: the shape of a real link, pointing nowhere.
    private const string LinkA = "https://drive.google.com/file/d/FAKEaaaaaaaaaaaaaaaaaaaaaaaaaa/view?usp=drivesdk";
    private const string LinkB = "https://drive.google.com/file/d/FAKEbbbbbbbbbbbbbbbbbbbbbbbbbb/view";
    private const string LinkC = "https://drive.google.com/file/d/FAKEcccccccccccccccccccccccccc/view";

    [Fact]
    public void ReadsTheGroupsRowsWithTheirDayFromTheDateColumnOrTheFileName()
    {
        string csv = "\"File Name\",\"Type\",\"Date\",\"Shared Link\"\n" +
                     $"\"CAI5_AIS4_S8_ Video\",\"mp4\",\"2026-09-09\",\"{LinkA}\"\n" +
                     $"\"CAI5_AIS4_S8_2026-09-11_1901.mp4\",\"mp4\",\"\",\"{LinkB}\"\n" +
                     $"\"CAI5_AIS4_S7_ Video\",\"mp4\",\"2026-09-09\",\"{LinkC}\"\n" +
                     "\"CAI5_AIS4_S8_ Video\",\"mp4\",\"2026-09-12\",\"not a link\"\n";

        var cairo = TimeZoneInfo.CreateCustomTimeZone("Cairo+3", TimeSpan.FromHours(3), "Cairo+3", "Cairo+3");
        var rows = RecordingSheetReader.Parse(csv, "CAI5_AIS4_S8", cairo);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new DateOnly(2026, 9, 9), rows[0].Date);
        Assert.Equal(LinkA, rows[0].Link);
        // _1901 UTC is 22:01 in Cairo.
        Assert.Equal((new DateOnly(2026, 9, 11), new TimeOnly(22, 1)), (rows[1].Date, rows[1].Start!.Value));
    }

    [Fact]
    public void PicksThatDaysRecordingAndNeverAnotherDays()
    {
        var rows = new[]
        {
            new SheetRecording("CAI5_AIS4_S8_2026-09-09_1405.mp4", new(2026, 9, 9), new(14, 5), LinkA),
            new SheetRecording("CAI5_AIS4_S8_2026-09-09_1903.mp4", new(2026, 9, 9), new(19, 3), LinkB),
        };
        Assert.Equal(LinkB, RecordingSheetReader.Pick(rows, new(2026, 9, 9), new(19, 0))!.Link);
        Assert.Null(RecordingSheetReader.Pick(rows, new(2026, 9, 10), new(19, 0)));
    }

    [Fact]
    public void FindsTheSheetAndTabInAPastedLink()
    {
        const string url = "https://docs.google.com/spreadsheets/d/FAKEsheetidFAKEsheetidFAKE123/edit#gid=12345";
        Assert.Equal("FAKEsheetidFAKEsheetidFAKE123", RecordingSheetReader.SpreadsheetIdOf(url));
        Assert.Equal("12345", RecordingSheetReader.GidOf(url));
        Assert.Null(RecordingSheetReader.SpreadsheetIdOf("https://example.com/sheet"));
    }

    [Fact]
    public void QuotedCellsKeepTheirCommasAndLineBreaks()
    {
        var rows = RecordingSheetReader.ReadCsv("a,\"b, c\",\"d\"\"e\"\n\"x\ny\",z").ToList();
        Assert.Equal(["a", "b, c", "d\"e"], rows[0]);
        Assert.Equal(["x\ny", "z"], rows[1]);
    }
}
