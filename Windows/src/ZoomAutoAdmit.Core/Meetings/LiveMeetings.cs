using System.Text.Json;

namespace ZoomAutoAdmit.Core.Meetings;

/// <summary>
/// Which meetings are running on this PC right now, whichever process runs them (the app, or a
/// class started by a Windows task in its own process). Each running meeting keeps a small file
/// fresh; a file not refreshed for a few minutes belongs to a process that is gone.
/// </summary>
public static class LiveMeetings
{
    public sealed record Entry(Guid SessionId, string Group, string Engine, DateTimeOffset StartedAt, DateTimeOffset SeenAt, int ProcessId);

    /// <summary>How often a running meeting refreshes its file, and how old a file may be and still count.</summary>
    public static readonly TimeSpan BeatEvery = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(3);

    public static string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "Meetings", "live");

    public static void Beat(Guid sessionId, string group, string engine, DateTimeOffset startedAt)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var entry = new Entry(sessionId, group, engine, startedAt, DateTimeOffset.Now, Environment.ProcessId);
            string path = Path.Combine(Folder, sessionId.ToString("N") + ".json");
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(entry));
            File.Move(temporary, path, overwrite: true);
        }
        catch { /* Never load-bearing for the meeting itself. */ }
    }

    public static void Clear(Guid sessionId)
    {
        try { File.Delete(Path.Combine(Folder, sessionId.ToString("N") + ".json")); } catch { }
    }

    /// <summary>Every meeting whose file is fresh.</summary>
    public static IReadOnlyList<Entry> List(DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.Now;
        var list = new List<Entry>();
        try
        {
            if (!Directory.Exists(Folder)) return list;
            foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(file));
                    if (entry == null) continue;
                    if (at - entry.SeenAt <= FreshFor) list.Add(entry);
                    else if (at - entry.SeenAt > TimeSpan.FromHours(12)) File.Delete(file);
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    public static bool IsLive(string group, DateTimeOffset? now = null) =>
        List(now).Any(entry => entry.Group.Equals(group, StringComparison.OrdinalIgnoreCase));
}
