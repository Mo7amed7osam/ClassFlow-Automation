using System.Text.RegularExpressions;

namespace ZoomAutoAdmit.Core.Meetings;

public enum ParticipantAudio { Muted, Unmuted, NoAudio, Unknown }

/// <summary>One row of Zoom's participants list, as far as ending the meeting cares.</summary>
public sealed record ParticipantRow(bool IsMe, ParticipantAudio Audio)
{
    /// <summary>This app's own row, and it is the meeting's host (not a co-host).</summary>
    public bool IsHostMe { get; init; }
    /// <summary>Someone else who is a co-host (the instructor the app made co-host, usually).</summary>
    public bool IsCoHost { get; init; }
    private static readonly Regex HostMe = new(@"\(\s*host\s*,\s*me\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CoHost = new(@"\(\s*co-?host\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // "Unmuted" contains "muted", so it is looked for first.
    private static readonly Regex Unmuted = new(@"\baudio\s+unmuted\b|\b(speaking|talking)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Muted = new(@"\baudio\s+muted\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Me = new(@"\((?:co-?host|host)?\s*,?\s*me\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Status = new(@"\((guest|host|co-?host)|\bvideo\s+(on|off)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// "Mostafa Badr,(Guest), Computer audio unmuted,Video on, …" -> unmuted;
    /// "eyouth coordinator,(Host, me), Computer audio muted, …" -> me, muted. A row with Zoom's usual
    /// tags but no audio at all has not joined the audio (silent); anything else is Unknown, which
    /// never lets the meeting end.
    /// </summary>
    public static ParticipantRow Parse(string? label)
    {
        string text = label ?? "";
        bool me = Me.IsMatch(text);
        var audio = Unmuted.IsMatch(text) ? ParticipantAudio.Unmuted
            : Muted.IsMatch(text) ? ParticipantAudio.Muted
            : Status.IsMatch(text) && !text.Contains("audio", StringComparison.OrdinalIgnoreCase) ? ParticipantAudio.NoAudio
            : ParticipantAudio.Unknown;
        return new(me, audio) { IsHostMe = HostMe.IsMatch(text), IsCoHost = !me && CoHost.IsMatch(text) };
    }
}

public enum RoomState { Unreadable, Busy, HostAlone, SmallAndSilent }
public enum AutoEndAction { Wait, EndForAll }
public sealed record AutoEndDecision(AutoEndAction Action, string Reason);

/// <summary>
/// When a class may be ended for everyone (the user's rule): from three hours after the class's time,
/// if nobody but the host is left, or if fewer than five people have been there for five minutes
/// with every mic off. Never while anyone is talking, and never on a read that could not see the
/// whole list.
/// </summary>
public static class AutoEndRule
{
    public static readonly TimeSpan EndAfter = TimeSpan.FromHours(3);
    public static readonly TimeSpan SmallRoomFor = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan AloneFor = TimeSpan.FromSeconds(50);
    public const int SmallRoomBelow = 5;

    public static RoomState Classify(IReadOnlyList<ParticipantRow> rows, bool readComplete)
    {
        if (!readComplete || rows.Count == 0) return RoomState.Unreadable;
        // Anyone talking, or anyone whose mic cannot be read, keeps the class going (the host's own row
        // without audio does not: the app joins without it).
        foreach (var r in rows)
        {
            if (r.Audio == ParticipantAudio.Unmuted) return RoomState.Busy;
            if (r.Audio == ParticipantAudio.Unknown && !r.IsMe) return RoomState.Busy;
        }
        int others = rows.Count(r => !r.IsMe);
        if (others == 0) return RoomState.HostAlone;
        return others < SmallRoomBelow ? RoomState.SmallAndSilent : RoomState.Busy;
    }

    public static AutoEndDecision Decide(TimeSpan sinceClassStart, RoomState state, TimeSpan heldFor)
    {
        if (sinceClassStart < EndAfter) return new(AutoEndAction.Wait, "less than three hours since the class's time");
        // The five minutes (or the host's minute alone) count from the three hours at the earliest.
        var pastThree = sinceClassStart - EndAfter;
        if (heldFor > pastThree) heldFor = pastThree;
        return state switch
        {
            RoomState.HostAlone when heldFor >= AloneFor => new(AutoEndAction.EndForAll, "nobody but the host is left"),
            RoomState.SmallAndSilent when heldFor >= SmallRoomFor => new(AutoEndAction.EndForAll, $"fewer than {SmallRoomBelow} people, all muted, for {SmallRoomFor.TotalMinutes:0} minutes"),
            RoomState.Busy => new(AutoEndAction.Wait, "people are there (or someone's mic is on)"),
            RoomState.Unreadable => new(AutoEndAction.Wait, "the participants list could not be read in full"),
            _ => new(AutoEndAction.Wait, $"waiting for the state to hold ({heldFor.TotalSeconds:0} s so far)"),
        };
    }
}

public static class CoHostAbsenceRule
{
    public static readonly TimeSpan GoneFor = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The user's second rule: from three hours after the class's time, a co-host who was in the
    /// meeting (the instructor the app made co-host) and has been gone five minutes ends it - the
    /// five minutes counted from the three hours at the earliest. Not while anyone's mic is on, and
    /// only on a read that saw the whole list.
    /// </summary>
    public static AutoEndDecision Decide(TimeSpan sinceClassStart, bool sawCoHost, TimeSpan? coHostGoneFor, bool anyoneTalking, bool readComplete)
    {
        if (sinceClassStart < AutoEndRule.EndAfter) return new(AutoEndAction.Wait, "less than three hours since the class's time");
        if (!sawCoHost || coHostGoneFor is not { } gone) return new(AutoEndAction.Wait, "the co-host is in the meeting (or never was)");
        if (!readComplete) return new(AutoEndAction.Wait, "the participants list could not be read in full");
        if (anyoneTalking) return new(AutoEndAction.Wait, "someone's mic is on");
        var pastThree = sinceClassStart - AutoEndRule.EndAfter;
        if (gone > pastThree) gone = pastThree;
        return gone >= GoneFor
            ? new(AutoEndAction.EndForAll, $"the co-host left and has not come back for {GoneFor.TotalMinutes:0} minutes")
            : new(AutoEndAction.Wait, $"the co-host left {gone.TotalMinutes:0.#} minutes ago");
    }
}

/// <summary>How long the room has been in the same state (an unreadable pass starts over).</summary>
public sealed class AutoEndTracker
{
    private RoomState? _state;
    private DateTimeOffset _since;

    public TimeSpan Observe(DateTimeOffset at, RoomState state)
    {
        if (state == RoomState.Unreadable || _state != state) { _state = state; _since = at; }
        return at - _since;
    }
}
