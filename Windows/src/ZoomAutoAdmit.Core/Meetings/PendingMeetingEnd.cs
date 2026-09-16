using System.Text.Json;

namespace ZoomAutoAdmit.Core.Meetings;

/// <summary>What someone watching decided about a class that is about to be ended for everyone.</summary>
public enum PendingEndAnswer
{
    /// <summary>Nobody said anything: the countdown runs out and the class is ended.</summary>
    NoAnswer,
    /// <summary>End it now, without waiting for the rest of the countdown.</summary>
    EndNow,
    /// <summary>Leave it alone - whoever is there will end it themselves.</summary>
    EndManually,
}

/// <summary>A class the program is about to end, and the moment it will be ended.</summary>
public sealed record PendingMeetingEndNotice
{
    public required Guid SessionId { get; init; }
    public required string Group { get; init; }
    public required DateTimeOffset ClassStart { get; init; }
    /// <summary>Why it is over, in the rule's own words ("only the host is left", …).</summary>
    public required string Reason { get; init; }
    public required DateTimeOffset EndsAt { get; init; }

    public TimeSpan Left(DateTimeOffset now) => EndsAt > now ? EndsAt - now : TimeSpan.Zero;
}

/// <summary>
/// The minute between deciding a class is over and ending it for everyone.
///
/// The class may be watched by a process with no window at all (a meeting a Windows task opened),
/// while the person who would want a say is in the app. So the decision is announced as a small
/// file and the answer comes back as another: whoever is watching sees a countdown and can end it
/// at once or keep it and end it themselves. Nobody answering is the normal case - the countdown
/// simply runs out, exactly as before this existed.
/// </summary>
public sealed class PendingMeetingEnds
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>How long the countdown runs before the class is ended by itself.</summary>
    public static readonly TimeSpan Warning = TimeSpan.FromMinutes(1);

    /// <summary>An announcement nobody withdrew (the watching process died) is ignored after this.</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromMinutes(10);

    public string Folder { get; }

    public PendingMeetingEnds(string? folder = null) =>
        Folder = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZoomAutoAdmit", "Meetings", "ending");

    private string NoticePath(Guid session) => Path.Combine(Folder, $"{session:N}.json");
    private string AnswerPath(Guid session) => Path.Combine(Folder, $"{session:N}.answer");

    /// <summary>Says a class is about to be ended. Never throws: the class is ended either way.</summary>
    public void Announce(PendingMeetingEndNotice notice)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.Delete(AnswerPath(notice.SessionId));          // a stale answer must not decide this one
            string path = NoticePath(notice.SessionId);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(notice, Json));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
    }

    /// <summary>The classes whose countdown is running now, for whoever shows it.</summary>
    public IReadOnlyList<PendingMeetingEndNotice> Waiting(DateTimeOffset now)
    {
        var waiting = new List<PendingMeetingEndNotice>();
        if (!Directory.Exists(Folder)) return waiting;
        foreach (string path in Directory.GetFiles(Folder, "*.json"))
        {
            PendingMeetingEndNotice? notice;
            try { notice = JsonSerializer.Deserialize<PendingMeetingEndNotice>(File.ReadAllText(path), Json); }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { continue; }
            if (notice is null) continue;
            // Left behind by a process that is gone: not a countdown anyone can still answer.
            if (notice.EndsAt + Stale < now) { try { File.Delete(path); } catch (IOException) { } continue; }
            waiting.Add(notice);
        }
        return [.. waiting.OrderBy(n => n.EndsAt)];
    }

    /// <summary>What the person said, if anything.</summary>
    public PendingEndAnswer Read(Guid session)
    {
        try
        {
            return File.ReadAllText(AnswerPath(session)).Trim() switch
            {
                "now" => PendingEndAnswer.EndNow,
                "manual" => PendingEndAnswer.EndManually,
                _ => PendingEndAnswer.NoAnswer,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return PendingEndAnswer.NoAnswer; }
    }

    public void Answer(Guid session, PendingEndAnswer answer)
    {
        if (answer == PendingEndAnswer.NoAnswer) return;
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(AnswerPath(session), answer == PendingEndAnswer.EndNow ? "now" : "manual");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The countdown is over (ended, or called off): nothing is left for anyone to answer.</summary>
    public void Withdraw(Guid session)
    {
        foreach (string path in new[] { NoticePath(session), AnswerPath(session) })
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
