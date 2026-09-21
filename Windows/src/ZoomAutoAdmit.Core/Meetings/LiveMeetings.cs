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

    /// <summary>
    /// Sessions somebody has said are over. A meeting can end without the loop that beats for it
    /// knowing - closed from a phone, or the browser shut - and the watch that notices says so
    /// here. Without this the beat wrote the marker again every minute and the class stayed "live"
    /// for as long as the process ran, which left every step that waits for a meeting to end
    /// waiting for ever (seen on 2026-09-20: a class ended by 00:03 and its recording was still
    /// being refused at 00:21).
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> Finished = new();

    /// <summary>
    /// This meeting is over, whoever noticed. The marker goes, and a beat still in flight for it
    /// writes nothing more.
    /// </summary>
    public static void Finish(Guid sessionId)
    {
        Finished[sessionId] = 0;
        Clear(sessionId);
    }

    /// <summary>Whether <see cref="Finish"/> was called for this session in this process.</summary>
    public static bool IsFinished(Guid sessionId) => Finished.ContainsKey(sessionId);

    public static void Beat(Guid sessionId, string group, string engine, DateTimeOffset startedAt)
    {
        if (Finished.ContainsKey(sessionId)) return;
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

    /// <summary>Forgets what this process was told, for a test that reuses session ids.</summary>
    public static void ForgetFinished() => Finished.Clear();

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
