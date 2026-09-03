using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZoomAutoAdmit.Roster;

/// <summary>Standalone local source of truth; no account/meeting/attendance dependencies.</summary>
public sealed class StudentRosterStore : IStudentRosterService
{
    private sealed class Document
    {
        [JsonRequired] public int SchemaVersion { get; init; }
        [JsonRequired] public List<Student> Students { get; init; } = [];
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Action<string> _log;
    public string FilePath { get; }

    public StudentRosterStore(string? filePath = null, Action<string>? log = null)
    {
        FilePath = Path.GetFullPath(filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZoomAutoAdmit", "Roster", "students.json"));
        _log = log ?? Console.WriteLine;
    }

    public async Task<IReadOnlyList<Student>> ListAsync(CancellationToken token = default)
    {
        using var held = await LockAsync(token);
        return (await ReadAsync(token)).AsReadOnly();
    }

    public Task AddAsync(Student student, CancellationToken token = default) =>
        AddManyAsync([student], "Student added", token);

    public async Task UpdateAsync(Student student, Student expected, CancellationToken token = default)
    {
        student = StudentValidation.Normalize(student);
        expected = StudentValidation.Normalize(expected);
        if (student.StudentId != expected.StudentId)
            throw new ArgumentException("Student ID is stable. Create another student instead of changing it.");
        using var held = await LockAsync(token);
        var students = await ReadAsync(token);
        int index = FindExpected(students, expected);
        students[index] = student;
        await WriteAsync(students, token);
        Log("Student updated");
    }

    public async Task DeleteAsync(Student expected, CancellationToken token = default)
    {
        expected = StudentValidation.Normalize(expected);
        using var held = await LockAsync(token);
        var students = await ReadAsync(token);
        students.RemoveAt(FindExpected(students, expected));
        await WriteAsync(students, token);
        Log("Student deleted");
    }

    public async Task<int> ImportFileAsync(string path, CancellationToken token = default)
    {
        var imported = await Task.Run(() => RosterImportReader.Read(path, token), token);
        await AddManyAsync(imported, $"Import completed; Students: {imported.Count}", token);
        return imported.Count;
    }

    private async Task AddManyAsync(IReadOnlyList<Student> additions, string message, CancellationToken token)
    {
        var normalized = additions.Select(StudentValidation.Normalize).ToArray();
        if (normalized.Length == 0) throw new ArgumentException("No students to import.");
        using var held = await LockAsync(token);
        var students = await ReadAsync(token);
        var ids = students.Select(s => s.StudentId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var student in normalized)
            if (!ids.Add(student.StudentId)) throw new InvalidOperationException($"Duplicate Student ID '{student.StudentId}'. Nothing was imported.");
        students.AddRange(normalized);
        await WriteAsync(students, token);
        Log(message);
    }

    private static int FindExpected(List<Student> students, Student expected)
    {
        int index = students.FindIndex(s => s.StudentId.Equals(expected.StudentId, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || !StudentValidation.Same(students[index], expected))
            throw new InvalidOperationException("This student changed or was deleted in another window. Refresh before editing.");
        return index;
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

    private async Task<List<Student>> ReadAsync(CancellationToken token)
    {
        if (!File.Exists(FilePath)) return [];
        if (new FileInfo(FilePath).Length > 64 * 1024 * 1024) throw new InvalidDataException("Roster file exceeds the 64 MB safety limit.");
        await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var document = await JsonSerializer.DeserializeAsync<Document>(stream, JsonOptions, token)
            ?? throw new InvalidDataException("Roster file is empty or invalid. Original file preserved.");
        if (document.SchemaVersion != 1 || document.Students == null)
            throw new InvalidDataException("Unsupported roster schema. Original file preserved.");
        var students = document.Students.Select(StudentValidation.Normalize).ToList();
        if (students.Select(s => s.StudentId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != students.Count)
            throw new InvalidDataException("Stored roster has duplicate IDs. Original file preserved.");
        return students;
    }

    private async Task WriteAsync(List<Student> students, CancellationToken token)
    {
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, new Document { SchemaVersion = 1, Students = students }, JsonOptions, token);
                await stream.FlushAsync(token);
                if (stream.Length > 64L * 1024 * 1024)
                    throw new InvalidDataException("Updated roster exceeds the 64 MB safety limit. Existing roster preserved.");
            }
            token.ThrowIfCancellationRequested();
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak");
            else File.Move(temporary, FilePath, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void Log(string message)
    {
        try { _log("[ROSTER] " + message); }
        catch { /* A logging sink cannot turn a successful save into a reported failure. */ }
    }
}
