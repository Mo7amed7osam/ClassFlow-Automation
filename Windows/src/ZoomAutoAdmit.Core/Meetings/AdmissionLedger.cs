using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZoomAutoAdmit.Core.Meetings;

/// <summary>
/// Everyone let in from the waiting room, by name and time, whichever process admitted them (the
/// app, the standalone monitor, a scheduled meeting). One JSON line per admission in
/// %LOCALAPPDATA%\ZoomAutoAdmit\Admissions\admissions-yyyy-MM-dd.jsonl, appended by every process
/// that admits and read by the Waiting Room and Attendance pages.
/// </summary>
public static class AdmissionLedger
{
    public sealed record Entry(DateTimeOffset At, string? Name, string Source, int People);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly object Gate = new();
    private static readonly Regex ToastTail = new(
        @"\s+(has\s+)?(entered|joined)(\s+the)?(\s+waiting(\s+room)?)?.*$|\s+is\s+waiting.*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "Admissions");

    public static string PathFor(DateOnly day) => Path.Combine(Folder, $"admissions-{day:yyyy-MM-dd}.jsonl");

    // What Zoom's accessibility text adds to a waiting row: ", Press Space to admit" and "(Guest)".
    private static readonly Regex ScreenReaderTail = new(@",\s*press\s+\w+\s+to\s+.*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RoleTag = new(@"\s*\((guest|host|co-host|cohost|me|host,\s*me|co-host,\s*me)\)\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The person's name as Zoom shows it, without a notification's "entered the waiting room" or
    /// the list's "(Guest), Press Space to admit".
    /// </summary>
    public static string? CleanName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string name = ScreenReaderTail.Replace(raw.Trim(), "");
        name = RoleTag.Replace(name, " ");
        name = ToastTail.Replace(name.Trim().Trim('\'', '"', '“', '”'), "").Trim(' ', '.', ',', ':', '-', '\'', '"', '“', '”');
        name = Regex.Replace(name, @"\s{2,}", " ");
        return name.Length is 0 or > 120 ? null : name;
    }

    public static void Record(string? name, string source, int people = 1)
    {
        var entry = new Entry(DateTimeOffset.Now, CleanName(name), source, Math.Max(1, people));
        string line = JsonSerializer.Serialize(entry, Json) + Environment.NewLine;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Folder);
                    // Several processes append to the same file; each line is written in one go.
                    using var stream = new FileStream(PathFor(DateOnly.FromDateTime(DateTime.Now)), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    var bytes = System.Text.Encoding.UTF8.GetBytes(line);
                    stream.Write(bytes, 0, bytes.Length);
                }
                return;
            }
            catch (IOException) { Thread.Sleep(40); }
            catch (UnauthorizedAccessException) { return; }
        }
    }

    public static IReadOnlyList<Entry> Read(DateOnly day)
    {
        try
        {
            string path = PathFor(day);
            if (!File.Exists(path)) return [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var list = new List<Entry>();
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                // Lines written before the name was cleaned read clean too.
                try { if (JsonSerializer.Deserialize<Entry>(line, Json) is { } e) list.Add(e with { Name = CleanName(e.Name) }); } catch (JsonException) { }
            }
            return list;
        }
        catch (IOException) { return []; }
    }
}
