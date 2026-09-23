namespace ZoomAutoAdmit.Core.Meetings;

/// <summary>
/// When a class may be marked complete on the LMS.
///
/// Completing a class cannot be undone there, and a meeting closing is not the same thing as a
/// class being over: a browser crashes, a host drops, the class carries on from somebody else's
/// Zoom. So a class is completed no earlier than <see cref="NoEarlierThan"/> after its own time,
/// however its meeting ended (2026-09-23: a 19:00 class was completed at 20:14 because the
/// browser crashed an hour in).
///
/// Coaching is the exception the user asks for: those are short, and they close with their meeting.
/// </summary>
public static class ClassCompletionRule
{
    public static TimeSpan NoEarlierThan { get; set; } = TimeSpan.FromHours(3);

    /// <summary>Whether a class's name says it is a Coaching session.</summary>
    public static bool IsCoaching(string? className) =>
        className?.Contains("coaching", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// The earliest moment this class may be completed, or null when it may be completed as soon as
    /// its meeting ends. <paramref name="className"/> is what this PC calls the class; not knowing
    /// it means the three hours apply, which is the careful way round.
    /// </summary>
    public static DateTimeOffset? NotBefore(string? className, DateOnly day, TimeOnly start, TimeSpan offset) =>
        IsCoaching(className) ? null : new DateTimeOffset(day.ToDateTime(start), offset) + NoEarlierThan;
}
