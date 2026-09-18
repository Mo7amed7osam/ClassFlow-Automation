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
    /// <summary>A row still "Joining..." (a rejoin that has not arrived): nobody in the room yet.</summary>
    public bool IsJoining { get; init; }
    private static readonly Regex Joining = new(@"\bjoining\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
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
        return new(me, audio) { IsHostMe = HostMe.IsMatch(text), IsCoHost = !me && CoHost.IsMatch(text), IsJoining = Joining.IsMatch(text) };
    }
}

public enum RoomState { Unreadable, Busy, HostAlone, SmallAndSilent }
public enum AutoEndAction { Wait, EndForAll }
public sealed record AutoEndDecision(AutoEndAction Action, string Reason);

/// <summary>
/// When a class may be ended for everyone (the user's rule): from three hours after the class's time,
/// if nobody but the host is left, or if fewer than five students have been there for five minutes
/// with every mic off - the instructor being there, muted, does not make it a class.
///
/// Never while anyone is talking: a mic that comes on puts the room back to busy, so the five
/// minutes start again from the moment the last one goes quiet. Never on a read that could not see
/// the whole list, and never while breakout rooms are open (people may be inside them - the bridge
/// checks that before it ends anything).
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
        // A "Joining..." row has no mic to read and nobody behind it yet (the host's own rejoin left one
        // on S8, 2026-09-16, and it kept a silent room of three from ending).
        rows = [.. rows.Where(r => !r.IsJoining)];
        if (rows.Count == 0) return RoomState.Unreadable;
        // Anyone talking, or anyone whose mic cannot be read, keeps the class going (the host's own row
        // without audio does not: the app joins without it).
        foreach (var r in rows)
        {
            if (r.Audio == ParticipantAudio.Unmuted) return RoomState.Busy;
            if (r.Audio == ParticipantAudio.Unknown && !r.IsMe) return RoomState.Busy;
        }
        // "Fewer than five" is five of the people the class is for: the host's own row and the
        // instructor's (the co-host) are not students, so a co-host sitting there muted with three
        // students is still a room of three (the user's rule).
        int others = rows.Count(r => !r.IsMe);
        int students = rows.Count(r => !r.IsMe && !r.IsCoHost);
        if (others == 0) return RoomState.HostAlone;
        return students < SmallRoomBelow ? RoomState.SmallAndSilent : RoomState.Busy;
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
    /// The class is over when the instructor and the class walk out together: the room keeps at most
    /// this share of the people it had while the co-host was still there.
    /// </summary>
    public const double LeftShare = 0.5;

    /// <summary>Whether most of the class left with the co-host (half of them or more).</summary>
    public static bool MostLeft(int peopleWithCoHost, int peopleNow) =>
        peopleWithCoHost > 0 && peopleNow <= peopleWithCoHost * LeftShare;

    /// <summary>
    /// The user's second rule: from three hours after the class's time, the instructor (the co-host
    /// the app made) leaving and not coming back for five minutes ends the class - but only when the
    /// class left with them. A room that still holds its people without the co-host is not over: that
    /// is the co-host's connection, not the end of the lesson, and it is left alone. The five minutes
    /// are counted from the moment the room emptied as well as from the co-host's last sighting, so a
    /// half-read list that seems to show them again does not put the clock back.
    /// Not while anyone's mic is on, and only on a read that saw the whole list.
    /// </summary>
    public static AutoEndDecision Decide(TimeSpan sinceClassStart, bool sawCoHost, TimeSpan? coHostGoneFor,
        bool anyoneTalking, bool readComplete, int peopleWithCoHost = 0, int peopleNow = 0, TimeSpan? emptyFor = null)
    {
        if (sinceClassStart < AutoEndRule.EndAfter) return new(AutoEndAction.Wait, "less than three hours since the class's time");
        if (!sawCoHost || coHostGoneFor is not { } gone) return new(AutoEndAction.Wait, "the co-host is in the meeting (or never was)");
        if (!readComplete) return new(AutoEndAction.Wait, "the participants list could not be read in full");
        if (anyoneTalking) return new(AutoEndAction.Wait, "someone's mic is on");
        if (!MostLeft(peopleWithCoHost, peopleNow))
            return new(AutoEndAction.Wait, $"the co-host left {gone.TotalMinutes:0.#} minutes ago but the class is still here ({peopleNow} of {peopleWithCoHost}) - their connection may have dropped");
        if (emptyFor is { } emptied && emptied > gone) gone = emptied;
        var pastThree = sinceClassStart - AutoEndRule.EndAfter;
        if (gone > pastThree) gone = pastThree;
        return gone >= GoneFor
            ? new(AutoEndAction.EndForAll, $"the co-host and most of the class left {GoneFor.TotalMinutes:0} minutes ago ({peopleNow} of {peopleWithCoHost} left)")
            : new(AutoEndAction.Wait, $"the co-host and most of the class left {gone.TotalMinutes:0.#} minutes ago");
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
