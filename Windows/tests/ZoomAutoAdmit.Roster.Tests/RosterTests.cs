using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ZoomAutoAdmit.Roster.Tests;

public class RosterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roster-tests-" + Guid.NewGuid().ToString("N"));
    private string StorePath => Path.Combine(_root, "students.json");
    private StudentRosterStore Store() => new(StorePath);
    private static Student Student(string id = "S1", string name = "أحمد محمد") => new(id, name, ["Ahmed", "أحمد"], "student@example.com");

    [Fact]
    public async Task CrudRoundTripsAndKeepsBackupAndRequiredJsonShape()
    {
        var logs = new List<string>();
        var store = new StudentRosterStore(StorePath, logs.Add);
        Assert.Empty(await store.ListAsync());
        await store.AddAsync(Student());
        var original = Assert.Single(await Store().ListAsync());
        var updated = original with { FullName = "New name", Email = null };
        await store.UpdateAsync(updated, original);
        Assert.Equal("New name", Assert.Single(await store.ListAsync()).FullName);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(StorePath));
        var row = json.RootElement.GetProperty("students")[0];
        Assert.Equal("S1", row.GetProperty("studentId").GetString());
        Assert.Equal(2, row.GetProperty("aliases").GetArrayLength());
        Assert.True(File.Exists(StorePath + ".bak"));
        await store.DeleteAsync(updated);
        Assert.Empty(await store.ListAsync());
        Assert.Contains("[ROSTER] Student added", logs);
        Assert.Contains("[ROSTER] Student updated", logs);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task DuplicateIdsRejectedButSameNamesAndAliasesAllowed()
    {
        var store = Store();
        await store.AddAsync(Student());
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AddAsync(Student("s1")));
        await store.AddAsync(Student("S2"));
        Assert.Equal(2, (await store.ListAsync()).Count);
    }

    [Fact]
    public async Task ConcurrentStoresDoNotLoseWrites()
    {
        await Task.WhenAll(Enumerable.Range(1, 20).Select(i => Store().AddAsync(Student("S" + i))));
        Assert.Equal(20, (await Store().ListAsync()).Count);
    }

    [Fact]
    public async Task StaleEditAndDeleteAreRejectedAndIdsRemainStable()
    {
        var store = Store();
        await store.AddAsync(Student());
        var original = Assert.Single(await store.ListAsync());
        await store.UpdateAsync(original with { FullName = "Changed elsewhere" }, original);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(original with { FullName = "Stale" }, original));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteAsync(original));
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpdateAsync(original with { StudentId = "different" }, original));
        Assert.Equal("Changed elsewhere", Assert.Single(await store.ListAsync()).FullName);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"schemaVersion\":2,\"students\":[]}")]
    [InlineData("{}")]
    public async Task CorruptOrUnknownSchemaNeverOverwritten(string original)
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(StorePath, original);
        await Assert.ThrowsAnyAsync<Exception>(() => Store().AddAsync(Student()));
        Assert.Equal(original, await File.ReadAllTextAsync(StorePath));
    }

    [Fact]
    public async Task CancellationWhileLockedDoesNotChangeData()
    {
        await Store().AddAsync(Student());
        string before = await File.ReadAllTextAsync(StorePath);
        using var held = new FileStream(StorePath + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var cancellation = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store().AddAsync(Student("S2"), cancellation.Token));
        Assert.Equal(before, await File.ReadAllTextAsync(StorePath));
    }

    [Theory]
    [InlineData("", "Name", null)]
    [InlineData("S1", "", null)]
    [InlineData("S1", "Name", "not-an-email")]
    [InlineData("S1", "Name", "Display <student@example.com>")]
    public void InvalidStudentRejected(string id, string name, string? email) =>
        Assert.Throws<ArgumentException>(() => StudentValidation.Normalize(new(id, name, [], email)));

    [Fact]
    public async Task CsvHandlesBomUnicodeQuotedCommasAndAliases()
    {
        string path = Write("import.csv", "studentId,fullName,aliases,email\r\n001,\"محمد, أحمد\",Ahmed|أحمد,\r\n002,Same name,,person@example.com\r\n");
        var logs = new List<string>();
        var store = new StudentRosterStore(StorePath, logs.Add);
        Assert.Equal(2, await store.ImportFileAsync(path));
        var first = (await store.ListAsync())[0];
        Assert.Equal("001", first.StudentId);
        Assert.Equal("محمد, أحمد", first.FullName);
        Assert.Equal(new[] { "Ahmed", "أحمد" }, first.Aliases);
        Assert.Contains(logs, s => s.StartsWith("[ROSTER] Import completed"));
    }

    [Theory]
    [InlineData("studentId,fullName\nS2,Good\nS3,\n")]
    [InlineData("studentId,fullName\nS2,Good\nS1,Already exists\n")]
    [InlineData("studentId,fullName\nS2,Good\nS2,Duplicate\n")]
    [InlineData("wrong,headers\nS2,Good\n")]
    [InlineData("studentId,fullName\nS2,\"unterminated")]
    public async Task BadImportLeavesExistingRosterUnchanged(string csv)
    {
        var store = Store();
        await store.AddAsync(Student());
        var original = await File.ReadAllBytesAsync(StorePath);
        await Assert.ThrowsAnyAsync<Exception>(() => store.ImportFileAsync(Write("invalid.csv", csv)));
        Assert.Equal(original, await File.ReadAllBytesAsync(StorePath));
    }

    [Fact]
    public async Task XlsxSupportsSharedStringsInlineStringsAndSparseOptionalCells()
    {
        var path = Workbook(false);
        Assert.Equal(1, await Store().ImportFileAsync(path));
        var student = Assert.Single(await Store().ListAsync());
        Assert.Equal("0007", student.StudentId);
        Assert.Equal("أحمد", student.FullName);
        Assert.Empty(student.Aliases);
        Assert.Equal("student@example.com", student.Email);
    }

    [Fact]
    public async Task XlsxFormulaIsRejectedWithoutWritingRoster()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Store().ImportFileAsync(Workbook(true)));
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public async Task LoggingFailureDoesNotUndoSuccessfulSave()
    {
        var store = new StudentRosterStore(StorePath, _ => throw new Exception("log sink"));
        await store.AddAsync(Student());
        Assert.Single(await store.ListAsync());
    }

    private string Write(string name, string content)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content, new UTF8Encoding(true));
        return path;
    }

    private string Workbook(bool formula)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "students.xlsx");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Part(string name, string text)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(text);
        }
        Part("xl/workbook.xml", """
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="Students" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """);
        Part("xl/_rels/workbook.xml.rels", """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
            </Relationships>
            """);
        Part("xl/sharedStrings.xml", """
            <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>studentId</t></si><si><t>fullName</t></si><si><t>0007</t></si><si><t>أحمد</t></si></sst>
            """);
        Part("xl/worksheets/sheet1.xml", $$"""
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
            <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="D1" t="inlineStr"><is><t>email</t></is></c></row>
            <row r="2"><c r="A2" t="s"><v>2</v></c><c r="B2" t="s">{{(formula ? "<f>SUM(1,2)</f>" : "")}}<v>3</v></c><c r="D2" t="inlineStr"><is><t>student@example.com</t></is></c></row>
            </sheetData></worksheet>
            """);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
