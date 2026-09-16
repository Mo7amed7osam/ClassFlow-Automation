using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZoomAutoAdmit.Core.Meetings;

/// <summary>How a class's meeting came to an end.</summary>
public enum ClassEndedHow
{
    /// <summary>The program ended it for everyone once the class was over.</summary>
    Program,
    /// <summary>Someone answered the countdown with "I'll end it myself".</summary>
    ByHand,
    /// <summary>It was closed somewhere else - from the phone, or another device.</summary>
    Elsewhere,
}

public sealed record ClassEnding
{
    public required string Group { get; init; }
    public required DateOnly Date { get; init; }
    public required TimeOnly Start { get; init; }
    public required DateTimeOffset At { get; init; }
    public required ClassEndedHow How { get; init; }
    public string Message { get; init; } = "";

    [JsonIgnore]
    public string Key => $"{Group}|{Date:yyyy-MM-dd}|{Start:HH\\:mm}";
}

/// <summary>
/// When each class's meeting ended, and how.
///
/// The class card shows it as its own step, so "this class is over" is something the program knows
/// rather than guesses - including the ordinary case of the meeting being closed from a phone,
/// which the program sees but did not do.
/// </summary>
public sealed class ClassEndings
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private static readonly TimeSpan Keep = TimeSpan.FromDays(120);
    private readonly object _gate = new();

    public string Path { get; }

    public ClassEndings(string? path = null) =>
        Path = path ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZoomAutoAdmit", "Meetings", "endings.json");

    /// <summary>
    /// Writes down how a class ended. The first answer wins: a class the program ended is not
    /// overwritten a minute later by "the meeting is gone", which is the same event seen again.
    /// Never throws - a class must not fail because a note could not be written.
    /// </summary>
    public void Record(ClassEnding ending)
    {
        try
        {
            lock (_gate)
            {
                var all = Load();
                if (all.Any(e => e.Key == ending.Key)) return;
                all.Add(ending);
                all = [.. all.Where(e => e.At > DateTimeOffset.Now - Keep).OrderBy(e => e.At)];
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                string temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(all, Json));
                File.Move(temporary, Path, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
    }

    public IReadOnlyList<ClassEnding> All() { lock (_gate) return Load(); }

    public ClassEnding? For(string group, DateOnly date, TimeOnly start)
    {
        string key = $"{group}|{date:yyyy-MM-dd}|{start:HH\\:mm}";
        lock (_gate) return Load().FirstOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    private List<ClassEnding> Load()
    {
        try { return File.Exists(Path) ? JsonSerializer.Deserialize<List<ClassEnding>>(File.ReadAllText(Path), Json) ?? [] : []; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }
}
