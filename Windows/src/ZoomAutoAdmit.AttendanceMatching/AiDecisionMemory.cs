using System.Text.Json;

namespace ZoomAutoAdmit.AttendanceMatching;

/// <summary>
/// Every answer the AI has given about one observed Zoom name and one roster name, kept so the same
/// question is never paid for twice. Matching runs every hour for each class and again before each LMS
/// upload, and the same students join every class with the same Zoom names: without this, the AI was
/// asked the same comparisons over and over, and every "no" or "not sure" was asked again the next time.
/// An answer is tied to the roster name as it was, so a corrected roster name is asked about afresh.
/// </summary>
public interface IAiDecisionMemory
{
    bool TryGet(string groupId, string studentId, string rosterName, string observedName, out AiNameMatch answer);
    void Save(string groupId, string studentId, string rosterName, string observedName, AiNameMatch answer);
}

public sealed class JsonAiDecisionMemory : IAiDecisionMemory
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly object _gate = new();
    private Dictionary<string, AiNameMatch>? _answers;

    public string FilePath { get; }

    public JsonAiDecisionMemory(string? filePath = null) => FilePath = Path.GetFullPath(filePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "Attendance", "ai-decisions.json"));

    private static string Key(string groupId, string studentId, string rosterName, string observedName) =>
        string.Join("|", groupId.Trim().ToUpperInvariant(), studentId.Trim(),
            NameNormalizer.Normalize(rosterName), NameNormalizer.Normalize(observedName));

    public bool TryGet(string groupId, string studentId, string rosterName, string observedName, out AiNameMatch answer)
    {
        lock (_gate)
        {
            if (Load().TryGetValue(Key(groupId, studentId, rosterName, observedName), out var found) && found.StudentId == studentId)
            {
                answer = found;
                return true;
            }
        }
        answer = null!;
        return false;
    }

    public void Save(string groupId, string studentId, string rosterName, string observedName, AiNameMatch answer)
    {
        lock (_gate)
        {
            var all = Load();
            all[Key(groupId, studentId, rosterName, observedName)] = answer;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(all, Json));
                File.Move(temporary, FilePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }     // only a saving, never load-bearing
        }
    }

    private Dictionary<string, AiNameMatch> Load()
    {
        if (_answers != null) return _answers;
        try
        {
            _answers = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, AiNameMatch>>(File.ReadAllText(FilePath), Json) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { _answers = []; }
        return _answers;
    }
}
