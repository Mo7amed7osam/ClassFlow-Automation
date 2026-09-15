using System.IO.Compression;
using System.Text.Json;
using Xunit;

namespace ZoomAutoAdmit.Roster.Tests;

public class GroupRosterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "group-roster-tests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_root, "groups.json");
    private GroupRosterStore Store() => new(FilePath);
    private async Task<RosterGroup> Group(string id = "Any-New-Group")
    {
        await Store().CreateAsync(id, "Dynamic group");
        return (await Store().ListAsync()).Single(g => g.GroupId == id);
    }
    private static GroupStudent Student(RosterGroup group, string id, int order, string name = "Repeated Name") =>
        new(id, group.GroupId, order, name, ["Alias"]);

    [Fact]
    public async Task CreateRenameDeleteSaveLoadAreDynamicAndKeepStableId()
    {
        var group = await Group();
        Assert.NotEqual(default, group.CreatedAt);
        await Store().RenameAsync(group, "Renamed group");
        var renamed = Assert.Single(await Store().ListAsync());
        Assert.Equal(group.GroupId, renamed.GroupId);
        Assert.Equal(group.CreatedAt, renamed.CreatedAt);
        Assert.Equal("Renamed group", renamed.DisplayName);
        Assert.Equal(1, renamed.Revision);
        await Store().DeleteAsync(renamed);
        Assert.Empty(await Store().ListAsync());
        Assert.True(File.Exists(FilePath + ".bak"));
    }

    [Fact]
    public async Task AddEditDeletePreserveExplicitOrderNotAlphabetical()
    {
        var group = await Group();
        await Store().AddStudentAsync(group, Student(group, "S2", 8, "Alpha"));
        group = Assert.Single(await Store().ListAsync());
        await Store().AddStudentAsync(group, Student(group, "S1", 2, "Zulu"));
        group = Assert.Single(await Store().ListAsync());
        Assert.Equal(new[] { "Zulu", "Alpha" }, group.Students.Select(s => s.FullName));
        await Store().UpdateStudentAsync(group, group.Students[0] with { FullName = "Edited", Aliases = ["Changed alias"] });
        group = Assert.Single(await Store().ListAsync());
        Assert.Equal(new[] { 2, 8 }, group.Students.Select(s => s.Order));
        await Store().DeleteStudentAsync(group, "S1");
        var remaining = Assert.Single(Assert.Single(await Store().ListAsync()).Students);
        Assert.Equal(8, remaining.Order);
    }

    [Fact]
    public async Task ReorderIsPermutationOnlyAndPersistsAcrossReload()
    {
        var group = await Group();
        await Store().AddStudentAsync(group, Student(group, "first", 1));
        group = Assert.Single(await Store().ListAsync());
        await Store().AddStudentAsync(group, Student(group, "second", 7));
        group = Assert.Single(await Store().ListAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => Store().ReorderAsync(group, ["first", "first"]));
        await Store().ReorderAsync(group, ["second", "first"]);
        var read = Assert.Single(await Store().ListAsync());
        Assert.Equal(new[] { "second", "first" }, read.Students.Select(s => s.StudentId));
        Assert.Equal(new[] { 1, 7 }, read.Students.Select(s => s.Order));
    }

    [Fact]
    public async Task MultipleGroupsAllowSameNamesButRejectDuplicateIdsOrWrongGroup()
    {
        var a = await Group("A");
        var b = await Group("B");
        await Task.WhenAll(Store().AddStudentAsync(a, Student(a, "S1", 1)), Store().AddStudentAsync(b, Student(b, "S2", 1)));
        Assert.Equal(2, (await Store().ListAsync()).Sum(g => g.Students.Count));
        a = (await Store().ListAsync()).Single(g => g.GroupId == "A");
        await Assert.ThrowsAsync<InvalidDataException>(() => Store().AddStudentAsync(a, Student(a, "S2", 2)));
        await Assert.ThrowsAsync<InvalidDataException>(() => Store().AddStudentAsync(a, Student(b, "S3", 2)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().CreateAsync("a", "Duplicate"));
    }

    [Fact]
    public async Task ConcurrentCreatesDoNotLoseGroups()
    {
        await Task.WhenAll(Enumerable.Range(1, 12).Select(i => Store().CreateAsync("G" + i, "Group " + i)));
        Assert.Equal(12, (await Store().ListAsync()).Count);
    }

    [Fact]
    public async Task StaleGroupMutationIsRejected()
    {
        var group = await Group();
        await Store().RenameAsync(group, "Changed elsewhere");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().AddStudentAsync(group, Student(group, "S1", 1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().DeleteAsync(group));
        Assert.Equal("Changed elsewhere", Assert.Single(await Store().ListAsync()).DisplayName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task MissingInvalidOrDuplicateOrderCannotBeSaved(int order)
    {
        var group = await Group();
        await Store().AddStudentAsync(group, Student(group, "S1", 1));
        group = Assert.Single(await Store().ListAsync());
        string original = await File.ReadAllTextAsync(FilePath);
        await Assert.ThrowsAsync<InvalidDataException>(() => Store().AddStudentAsync(group, Student(group, "S2", order)));
        Assert.Equal(original, await File.ReadAllTextAsync(FilePath));
    }

    [Fact]
    public async Task CsvOrderNameImportGeneratesIdsAndPreservesNumericOrder()
    {
        var group = await Group();
        string path = WriteCsv("Order,Name,aliases\n9,Alpha,A|ألفا\n2,Zulu,\n");
        Assert.Equal(2, await Store().ImportAsync(group, path));
        var read = Assert.Single(await Store().ListAsync());
        Assert.Equal(new[] { "Zulu", "Alpha" }, read.Students.Select(s => s.FullName));
        Assert.Equal(new[] { 2, 9 }, read.Students.Select(s => s.Order));
        Assert.All(read.Students, s => { Assert.True(Guid.TryParse(s.StudentId, out _)); Assert.Equal(group.GroupId, s.GroupId); });
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(FilePath));
        Assert.True(json.RootElement.GetProperty("groups")[0].GetProperty("students")[0].TryGetProperty("order", out _));
    }

    [Theory]
    [InlineData("Name\nMissing Order\n")]
    [InlineData("Order,Name\n2,Good\n2,Duplicate\n")]
    [InlineData("Order,Name\n2,Good\n0,Invalid\n")]
    [InlineData("Order,Name\n2,Good\n1,Existing slot\n")]
    public async Task InvalidImportIsAtomic(string csv)
    {
        var group = await Group();
        await Store().AddStudentAsync(group, Student(group, "S1", 1));
        group = Assert.Single(await Store().ListAsync());
        var before = await File.ReadAllTextAsync(FilePath);
        await Assert.ThrowsAnyAsync<Exception>(() => Store().ImportAsync(group, WriteCsv(csv)));
        Assert.Equal(before, await File.ReadAllTextAsync(FilePath));
    }

    [Fact]
    public async Task ANewPcStartsWithNoGroupsAndDeletedGroupsStayDeleted()
    {
        // Nobody else's students ship with the app: the first read is an empty roster, kept on disk.
        Assert.Empty(await Store().ListAsync());
        Assert.True(File.Exists(FilePath));
        await Store().CreateAsync("G1", "Group");
        await Store().DeleteAsync(Assert.Single(await Store().ListAsync()));
        Assert.Empty(await Store().ListAsync());
    }

    [Fact]
    public async Task ExistingLegacyRosterAndCorruptGroupFileAreNotOverwritten()
    {
        Directory.CreateDirectory(_root);
        var legacyPath = Path.Combine(_root, "students.json");
        await File.WriteAllTextAsync(legacyPath, "legacy data");
        await Store().ListAsync();
        Assert.Equal("legacy data", await File.ReadAllTextAsync(legacyPath));
        await File.WriteAllTextAsync(FilePath, "{corrupt");
        await Assert.ThrowsAnyAsync<Exception>(() => Store().CreateAsync("New", "New"));
        Assert.Equal("{corrupt", await File.ReadAllTextAsync(FilePath));
    }

    [Fact]
    public async Task OrderedExcelImportReadsFirstWorksheet()
    {
        var group = await Group();
        string path = Path.Combine(_root, "group.xlsx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Part(string name, string content) { using var w = new StreamWriter(zip.CreateEntry(name).Open()); w.Write(content); }
            Part("xl/workbook.xml", """<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Roster" sheetId="1" r:id="r1"/></sheets></workbook>""");
            Part("xl/_rels/workbook.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="r1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>""");
            Part("xl/worksheets/sheet1.xml", """
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                <row><c r="A1" t="inlineStr"><is><t>Order</t></is></c><c r="B1" t="inlineStr"><is><t>Name</t></is></c></row>
                <row><c r="A2"><v>8</v></c><c r="B2" t="inlineStr"><is><t>Alpha</t></is></c></row>
                <row><c r="A3"><v>3</v></c><c r="B3" t="inlineStr"><is><t>Zulu</t></is></c></row>
                </sheetData></worksheet>
                """);
        }
        await Store().ImportAsync(group, path);
        Assert.Equal(new[] { 3, 8 }, Assert.Single(await Store().ListAsync()).Students.Select(s => s.Order));
    }

    [Fact]
    public async Task LogsDescribeGroupAndStudentChangesWithoutNames()
    {
        var logs = new List<string>();
        var store = new GroupRosterStore(FilePath, logs.Add);
        await store.CreateAsync("G", "Group");
        var g = Assert.Single(await store.ListAsync());
        await store.AddStudentAsync(g, Student(g, "S1", 1));
        g = Assert.Single(await store.ListAsync());
        await store.UpdateStudentAsync(g, g.Students[0] with { FullName = "Changed" });
        Assert.Contains("[ROSTER] Group created", logs);
        Assert.Contains(logs, s => s.StartsWith("[ROSTER] Group loaded"));
        Assert.Contains("[ROSTER] Student added", logs);
        Assert.Contains("[ROSTER] Student updated", logs);
        Assert.Contains("[ROSTER] Roster saved", logs);
    }

    private string WriteCsv(string contents)
    {
        var path = Path.Combine(_root, "import.csv");
        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
