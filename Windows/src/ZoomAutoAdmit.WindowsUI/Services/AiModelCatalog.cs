using System.IO;
using System.Text.Json;

namespace ZoomAutoAdmit.WindowsUI.Services;

public interface IAiModelCatalog
{
    IReadOnlyList<string> Read(AiProvider provider);
    void Save(AiProvider provider, IReadOnlyList<string> models);
}

/// <summary>Model IDs the user added themselves. Plain JSON on purpose: model IDs are not secrets, keys never come near this file.</summary>
public sealed class AiModelCatalog : IAiModelCatalog
{
    public const int MaxModels = 25;
    private const long MaxFileBytes = 64 * 1024;

    private sealed class Document
    {
        public int SchemaVersion { get; init; } = 1;
        public Dictionary<string, List<string>> Models { get; init; } = new();
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public string FilePath { get; }

    public AiModelCatalog(string? filePath = null) => FilePath = Path.GetFullPath(filePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "AI", "models.json"));

    /// <summary>A model ID is a short opaque token: no whitespace, no control characters, bounded length.</summary>
    public static bool IsValidModel(string? model) =>
        !string.IsNullOrWhiteSpace(model) && model.Length <= 100 &&
        !model.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));

    public IReadOnlyList<string> Read(AiProvider provider)
    {
        var document = ReadDocument();
        return document.Models.TryGetValue(Key(provider), out var models) && models != null ? Clean(models) : [];
    }

    public void Save(AiProvider provider, IReadOnlyList<string> models)
    {
        if (!Enum.IsDefined(provider)) throw new ArgumentException("Invalid AI provider.");
        var document = ReadDocument();
        document.Models[Key(provider)] = [.. Clean(models)];
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(document, Json);
            if (payload.Length > MaxFileBytes) throw new InvalidDataException("The saved model list exceeds its size limit.");
            File.WriteAllBytes(temporary, payload);
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
            else File.Move(temporary, FilePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // An unreadable or foreign file is never overwritten silently: it is reported so the caller can keep the user informed.
    private Document ReadDocument()
    {
        if (!File.Exists(FilePath)) return new Document();
        if (new FileInfo(FilePath).Length > MaxFileBytes) throw new InvalidDataException("The saved model list exceeds its size limit.");
        using var stream = File.OpenRead(FilePath);
        var document = JsonSerializer.Deserialize<Document>(stream, Json);
        if (document == null || document.SchemaVersion != 1 || document.Models == null)
            throw new InvalidDataException("Invalid saved model list. Original preserved.");
        return document;
    }

    private static string Key(AiProvider provider) => provider.ToString();

    private static List<string> Clean(IEnumerable<string> models) =>
        [.. models.Where(IsValidModel).Select(model => model.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxModels)];
}
