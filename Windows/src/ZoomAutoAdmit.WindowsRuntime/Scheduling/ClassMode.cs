using ZoomAutoAdmit.WebAutomation.Lms;

namespace ZoomAutoAdmit.WindowsRuntime.Scheduling;

/// <summary>
/// Whether a class is held in a room or on Zoom. A physical class opens no Zoom meeting and takes no
/// attendance from Zoom: it is run on the LMS at its time, completed like any other, and its Zoom
/// recording and Drive link go up the same way.
///
/// The schedule decides first - what was uploaded here, or set on the class by hand - and the LMS's
/// own type ("Physical" in its session list) decides when the schedule does not say.
/// </summary>
public static class ClassMode
{
    public const string Physical = "Physical";
    public const string Online = "Online";

    /// <summary>This PC's last reading of the LMS, where its session types are.</summary>
    public static Func<IReadOnlyList<LmsSessionCache.Entry>> LmsSessions { get; set; } = () => new LmsSessionCache().Read();

    /// <summary>This PC's schedules, where a type set by upload or by hand is.</summary>
    public static Func<CancellationToken, Task<IReadOnlyList<MeetingSchedule>>> Schedules { get; set; } =
        token => new WindowsMeetingScheduleStore().ListAsync(token);

    /// <summary>The class's mode from what is known of it, or null when nothing says.</summary>
    public static string? Resolve(IEnumerable<MeetingSchedule> schedules, IEnumerable<LmsSessionCache.Entry> lms,
        string group, DateOnly day, TimeOnly start)
    {
        var fromSchedule = schedules
            .Where(s => Normalize(s.Mode) != null && IsClass(s, group, day, start))
            .Select(s => Normalize(s.Mode))
            .FirstOrDefault();
        if (fromSchedule != null) return fromSchedule;
        // The LMS's own times are not to be trusted (they can be hours off the timetable), so its
        // session is the group's session of that day - the nearest one when there are two.
        return lms
            .Where(c => c.Session.Group.Equals(group, StringComparison.OrdinalIgnoreCase) && c.Session.Date == day
                        && Normalize(c.Session.Mode) != null)
            .OrderBy(c => c.Session.Start is { } at ? Math.Abs((at.ToTimeSpan() - start.ToTimeSpan()).TotalMinutes) : 9999)
            .ThenByDescending(c => c.ReadAt)
            .Select(c => Normalize(c.Session.Mode))
            .FirstOrDefault();
    }

    public static bool IsPhysical(IEnumerable<MeetingSchedule> schedules, IEnumerable<LmsSessionCache.Entry> lms,
        string group, DateOnly day, TimeOnly start) =>
        Resolve(schedules, lms, group, day, start) == Physical;

    /// <summary>The same, read from this PC's schedules and its last reading of the LMS.</summary>
    public static async Task<bool> IsPhysicalAsync(string group, DateOnly day, TimeOnly start, CancellationToken token = default)
    {
        try
        {
            return IsPhysical(await Schedules(token), LmsSessions(), group, day, start);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return false; }       // unknown is online: the meeting still opens
    }

    /// <summary>"Physical", "Online", or null for anything else. "Offline" and "In person" are physical.</summary>
    public static string? Normalize(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "physical" or "physical session" or "offline" or "in person" or "in-person" or "onsite" or "on-site" => Physical,
        "online" or "online session" or "live" => Online,
        _ => null,
    };

    private static bool IsClass(MeetingSchedule s, string group, DateOnly day, TimeOnly start) =>
        group.Equals(string.IsNullOrWhiteSpace(s.GroupName) ? s.AccountId : s.GroupName, StringComparison.OrdinalIgnoreCase)
        && (s.OccurrenceDate is { } once ? once == day : s.Days.Includes(day.DayOfWeek))
        && s.Time.Hour == start.Hour && s.Time.Minute == start.Minute;
}
