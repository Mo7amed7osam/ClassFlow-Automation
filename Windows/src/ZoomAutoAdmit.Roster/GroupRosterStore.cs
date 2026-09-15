using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZoomAutoAdmit.Roster;

public sealed class GroupRosterStore : IGroupRosterService
{
    private sealed class Document
    {
        public int SchemaVersion { get; init; } = 1;
        [JsonRequired] public List<RosterGroup> Groups { get; init; } = [];
    }
    private sealed record SeedDocument(IReadOnlyList<SeedGroup> Groups);
    private sealed record SeedGroup(string GroupId, string DisplayName, IReadOnlyList<SeedStudent> Students);
    private sealed record SeedStudent(int Order, string FullName);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Action<string> _log;
    private readonly bool _seedOnFirstUse;
    public string FilePath { get; }

    public GroupRosterStore(string? filePath = null, Action<string>? log = null, bool seedOnFirstUse = true)
    {
        FilePath = Path.GetFullPath(filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "Roster", "groups.json"));
        _log = log ?? Console.WriteLine;
        _seedOnFirstUse = seedOnFirstUse;
    }

    public async Task<IReadOnlyList<RosterGroup>> ListAsync(CancellationToken token = default)
    {
        using var held = await LockAsync(token);
        var groups = await ReadAsync(token);
        Log($"Group loaded; Groups: {groups.Count}");
        return groups.AsReadOnly();
    }

    public async Task CreateAsync(string groupId, string displayName, CancellationToken token = default)
    {
        var group = Normalize(new(groupId, displayName, DateTimeOffset.UtcNow, []));
        using var held = await LockAsync(token);
        var groups = await ReadAsync(token);
        if (groups.Any(g => g.GroupId.Equals(group.GroupId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Group ID already exists. Choose a unique ID.");
        groups.Add(group);
        await WriteAsync(groups, token);
        Log("Group created");
    }

    public Task RenameAsync(RosterGroup expected, string displayName, CancellationToken token = default) =>
        ChangeAsync(expected, group => group with { DisplayName = displayName }, "Group renamed", token);

    public async Task DeleteAsync(RosterGroup expected, CancellationToken token = default)
    {
        using var held = await LockAsync(token);
        var groups = await ReadAsync(token);
        groups.RemoveAt(FindExpected(groups, expected));
        await WriteAsync(groups, token);
        Log("Group deleted");
    }

    public Task AddStudentAsync(RosterGroup expected, GroupStudent student, CancellationToken token = default) =>
        ChangeAsync(expected, group => group with { Students = group.Students.Append(student).ToArray() }, "Student added", token);

    public Task AddStudentsAsync(RosterGroup expected, IReadOnlyList<GroupStudent> students, CancellationToken token = default) =>
        ChangeAsync(expected, group => group with { Students = group.Students.Concat(students).ToArray() }, $"Students added; Students: {students.Count}", token);

    public Task UpdateStudentAsync(RosterGroup expected, GroupStudent student, CancellationToken token = default) =>
        ChangeAsync(expected, group =>
        {
            if (!group.Students.Any(s => s.StudentId == student.StudentId))
                throw new InvalidOperationException("Student not found. Student IDs cannot be changed.");
            return group with { Students = group.Students.Select(s => s.StudentId == student.StudentId ? student : s).ToArray() };
        }, "Student updated", token);

    public Task DeleteStudentAsync(RosterGroup expected, string studentId, CancellationToken token = default) =>
        ChangeAsync(expected, group =>
        {
            if (!group.Students.Any(s => s.StudentId == studentId)) throw new InvalidOperationException("Student not found.");
            // Leave surviving order numbers intact; deletion never silently renumbers them.
            return group with { Students = group.Students.Where(s => s.StudentId != studentId).ToArray() };
        }, "Student deleted", token);

    public Task ReorderAsync(RosterGroup expected, IReadOnlyList<string> orderedStudentIds, CancellationToken token = default) =>
        ChangeAsync(expected, group =>
        {
            if (orderedStudentIds.Count != group.Students.Count ||
                orderedStudentIds.Distinct(StringComparer.Ordinal).Count() != orderedStudentIds.Count ||
                !orderedStudentIds.ToHashSet(StringComparer.Ordinal).SetEquals(group.Students.Select(s => s.StudentId)))
                throw new ArgumentException("Reorder must contain every student ID exactly once.");
            var students = group.Students.ToDictionary(s => s.StudentId, StringComparer.Ordinal);
            var slots = group.Students.Select(s => s.Order).Order().ToArray();
            return group with { Students = orderedStudentIds.Select((id, i) => students[id] with { Order = slots[i] }).ToArray() };
        }, "Students reordered", token);

    public async Task<int> ImportAsync(RosterGroup expected, string path, CancellationToken token = default)
    {
        var students = await Task.Run(() => RosterImportReader.ReadGroup(path, expected.GroupId, token), token);
        await ChangeAsync(expected, group => group with { Students = group.Students.Concat(students).ToArray() },
            $"Import completed; Students: {students.Count}", token);
        return students.Count;
    }

    private async Task ChangeAsync(RosterGroup expected, Func<RosterGroup, RosterGroup> change, string message, CancellationToken token)
    {
        using var held = await LockAsync(token);
        var groups = await ReadAsync(token);
        int index = FindExpected(groups, expected);
        groups[index] = Normalize(change(groups[index]) with { Revision = checked(groups[index].Revision + 1) });
        ValidateIds(groups);
        await WriteAsync(groups, token);
        Log(message);
    }

    private static int FindExpected(List<RosterGroup> groups, RosterGroup expected)
    {
        int index = groups.FindIndex(g => g.GroupId == expected.GroupId);
        if (index < 0 || groups[index].Revision != expected.Revision ||
            JsonSerializer.Serialize(groups[index], Json) != JsonSerializer.Serialize(expected, Json))
            throw new InvalidOperationException("The group changed or was deleted. Refresh before saving.");
        return index;
    }

    private static RosterGroup Normalize(RosterGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var labels = StudentValidation.Normalize(new(group.GroupId, group.DisplayName, []));
        if (group.CreatedAt == default || group.Revision < 0 || group.Students == null)
            throw new InvalidDataException("Group metadata or students are invalid.");
        var orders = new HashSet<int>();
        var students = group.Students.Select(student =>
        {
            if (student == null || student.Order <= 0 || !orders.Add(student.Order))
                throw new InvalidDataException("Every student needs a unique positive order number within the group.");
            if (student.GroupId != labels.StudentId) throw new InvalidDataException("Student belongs to a different group.");
            var clean = StudentValidation.Normalize(new(student.StudentId, student.FullName, student.Aliases, student.Email));
            return student with { StudentId = clean.StudentId, FullName = clean.FullName, Aliases = clean.Aliases, Email = clean.Email };
        }).OrderBy(s => s.Order).ToArray();
        return group with { GroupId = labels.StudentId, DisplayName = labels.FullName, Students = Array.AsReadOnly(students) };
    }

    private static void ValidateIds(List<RosterGroup> groups)
    {
        if (groups.Select(g => g.GroupId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != groups.Count)
            throw new InvalidDataException("Duplicate group IDs.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var student in groups.SelectMany(g => g.Students))
            if (!ids.Add(student.StudentId)) throw new InvalidDataException("Duplicate student ID. Nothing was saved.");
    }

    private async Task<FileStream> LockAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(50, token); }
        }
    }

    private async Task<List<RosterGroup>> ReadAsync(CancellationToken token)
    {
        if (!File.Exists(FilePath))
        {
            List<RosterGroup> initial = [];
            if (_seedOnFirstUse)
            {
                using var stream = typeof(GroupRosterStore).Assembly.GetManifestResourceStream("ZoomAutoAdmit.Roster.initial-groups.json")
                    ?? throw new InvalidDataException("Initial roster data is missing.");
                var seed = await JsonSerializer.DeserializeAsync<SeedDocument>(stream, Json, token)
                    ?? throw new InvalidDataException("Initial roster data is invalid.");
                initial = seed.Groups.Select(g => Normalize(new RosterGroup(g.GroupId, g.DisplayName, DateTimeOffset.UtcNow,
                    g.Students.Select(s => new GroupStudent(Guid.NewGuid().ToString("D"), g.GroupId, s.Order, s.FullName, [])).ToArray()))).ToList();
                ValidateIds(initial);
            }
            // Persist even an empty document so deleted groups are not recreated on restart.
            await WriteAsync(initial, token);
            return initial;
        }
        if (new FileInfo(FilePath).Length > 64L * 1024 * 1024) throw new InvalidDataException("Group roster exceeds the 64 MB safety limit.");
        await using var file = File.OpenRead(FilePath);
        var document = await JsonSerializer.DeserializeAsync<Document>(file, Json, token)
            ?? throw new InvalidDataException("Invalid group roster. Original preserved.");
        if (document.SchemaVersion != 1 || document.Groups == null) throw new InvalidDataException("Unsupported group roster schema.");
        var groups = document.Groups.Select(Normalize).ToList();
        ValidateIds(groups);
        return groups;
    }

    private async Task WriteAsync(List<RosterGroup> groups, CancellationToken token)
    {
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(file, new Document { Groups = groups }, Json, token);
                await file.FlushAsync(token);
                if (file.Length > 64L * 1024 * 1024) throw new InvalidDataException("Group roster exceeds the 64 MB safety limit.");
            }
            token.ThrowIfCancellationRequested();
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak");
            else File.Move(temporary, FilePath, overwrite: false);
            Log("Roster saved");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void Log(string message) { try { _log("[ROSTER] " + message); } catch { } }
}
