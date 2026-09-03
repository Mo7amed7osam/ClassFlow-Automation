using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZoomAutoAdmit.AttendanceMatching;

public sealed class JsonAliasMemory : IAliasMemory
{
    private sealed class Document
    {
        public int SchemaVersion { get; init; } = 1;
        [JsonRequired] public List<ApprovedAlias> Aliases { get; init; } = [];
    }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public string FilePath { get; }

    public JsonAliasMemory(string? filePath = null) => FilePath = Path.GetFullPath(filePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "Attendance", "aliases.json"));

    public async Task<IReadOnlyList<ApprovedAlias>> LoadAsync(CancellationToken token = default)
    {
        using var held = await LockAsync(token);
        return (await ReadAsync(token)).AsReadOnly();
    }

    public async Task SaveAsync(ApprovedAlias alias, CancellationToken token = default)
    {
        Validate(alias);
        using var held = await LockAsync(token);
        var aliases = await ReadAsync(token);
        string key = NameNormalizer.Normalize(alias.Alias);
        var existing = aliases.FindIndex(a => a.GroupId == alias.GroupId && NameNormalizer.Normalize(a.Alias) == key);
        if (existing >= 0 && aliases[existing].StudentId != alias.StudentId)
            throw new InvalidDataException("Alias is already assigned to another student in this group.");
        if (existing < 0) aliases.Add(alias);
        else aliases[existing] = alias;
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(file, new Document { Aliases = aliases }, Json, token);
                await file.FlushAsync(token);
                if (file.Length > 8 * 1024 * 1024) throw new InvalidDataException("Alias memory exceeds its 8 MB safety limit.");
            }
            token.ThrowIfCancellationRequested();
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak");
            else File.Move(temporary, FilePath, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<List<ApprovedAlias>> ReadAsync(CancellationToken token)
    {
        if (!File.Exists(FilePath)) return [];
        if (new FileInfo(FilePath).Length > 8 * 1024 * 1024) throw new InvalidDataException("Alias memory exceeds its safety limit.");
        await using var stream = File.OpenRead(FilePath);
        var document = await JsonSerializer.DeserializeAsync<Document>(stream, Json, token);
        if (document == null || document.SchemaVersion != 1 || document.Aliases == null)
            throw new InvalidDataException("Invalid alias memory. Original preserved.");
        var keys = new HashSet<(string, string)>();
        foreach (var alias in document.Aliases)
        {
            Validate(alias);
            if (!keys.Add((alias.GroupId, NameNormalizer.Normalize(alias.Alias))))
                throw new InvalidDataException("Conflicting alias memory. Original preserved.");
        }
        return document.Aliases;
    }

    private static void Validate(ApprovedAlias alias)
    {
        if (alias == null || string.IsNullOrWhiteSpace(alias.StudentId) || alias.StudentId.Length > 128 ||
            string.IsNullOrWhiteSpace(alias.GroupId) || alias.GroupId.Length > 128 ||
            string.IsNullOrWhiteSpace(alias.Alias) || alias.Alias.Length > 500 ||
            string.IsNullOrWhiteSpace(alias.RosterName) || alias.RosterName.Length > 500 ||
            NameNormalizer.Normalize(alias.Alias).Length == 0 ||
            alias.Confidence is < 0 or > 100 || alias.CreatedAt == default)
            throw new InvalidDataException("Invalid alias entry.");
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
}
