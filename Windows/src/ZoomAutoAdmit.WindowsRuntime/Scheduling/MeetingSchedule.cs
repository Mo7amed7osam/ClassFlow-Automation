namespace ZoomAutoAdmit.WindowsRuntime.Scheduling;

[Flags]
public enum ScheduleDays
{
    None = 0,
    Monday = 1 << 0,
    Tuesday = 1 << 1,
    Wednesday = 1 << 2,
    Thursday = 1 << 3,
    Friday = 1 << 4,
    Saturday = 1 << 5,
    Sunday = 1 << 6,
    EveryDay = Monday | Tuesday | Wednesday | Thursday | Friday | Saturday | Sunday
}

public static class ScheduleTiming
{
    /// <summary>
    /// How early a scheduled meeting opens. The room is up and Auto Admit is already watching
    /// before the time written on the schedule, so the first person to arrive is let in at once.
    /// </summary>
    public static readonly TimeSpan StartLead = TimeSpan.FromMinutes(15);

    /// <summary>The moment this schedule should be launched for a given day.</summary>
    public static DateTime LaunchMoment(this MeetingSchedule schedule, DateOnly date) =>
        date.ToDateTime(schedule.Time) - StartLead;

    /// <summary>How far a meeting that went live can be from its class and still be that class.</summary>
    public static readonly TimeSpan SameClassWindow = TimeSpan.FromMinutes(90);

    /// <summary>
    /// How long after its start a class is still going on. A meeting opened again while the class runs
    /// (the app restarted, the meeting dropped and Start was pressed at 20:32 for the 19:00 class) is
    /// that class, not a new one.
    /// </summary>
    public static readonly TimeSpan ClassRunsFor = TimeSpan.FromHours(3) + TimeSpan.FromMinutes(15);

    /// <summary>A meeting live at <paramref name="moment"/> belongs to the class starting at <paramref name="classStart"/>:
    /// up to 90 minutes early, or any time while the class is still going on.</summary>
    public static bool IsSameClass(TimeSpan classStart, TimeSpan moment)
    {
        var gap = moment - classStart;
        return gap >= -SameClassWindow && gap <= ClassRunsFor;
    }

    /// <summary>
    /// The scheduled start of the group's class that day nearest a moment (a meeting opened by hand at
    /// 18:51 is the 19:00 class), or null when no class of the group is that close.
    /// </summary>
    public static TimeOnly? ClassStartNear(IEnumerable<MeetingSchedule> schedules, string group, DateOnly day, TimeOnly moment) =>
        schedules
            .Where(s => group.Equals(s.GroupName, StringComparison.OrdinalIgnoreCase) || group.Equals(s.AccountId, StringComparison.OrdinalIgnoreCase))
            .Where(s => s.OccurrenceDate == day || (s.OccurrenceDate == null && s.Days.Includes(day.DayOfWeek)))
            .Select(s => new TimeOnly(s.Time.Hour, s.Time.Minute))
            .Where(t => IsSameClass(t.ToTimeSpan(), moment.ToTimeSpan()))
            .OrderBy(t => Math.Abs((t.ToTimeSpan() - moment.ToTimeSpan()).TotalMinutes))
            .Select(t => (TimeOnly?)t)
            .FirstOrDefault();
}

public sealed record MeetingSchedule(
    Guid Id,
    string Name,
    string MeetingUrl,
    string AccountId,
    TimeOnly Time,
    ScheduleDays Days,
    bool Enabled,
    DateOnly? LastTriggeredDate = null,
    DateOnly? OccurrenceDate = null,
    string? GroupName = null,
    // The engine this class opens with first (null: the account's choice, normally the Zoom app).
    // When it fails, the other one is tried.
    ZoomAutoAdmit.Core.Sessions.SessionEngineType? PreferredEngine = null,
    // Whose class this is, when this PC runs other people's as well as its own. Coordinator is the
    // name shown in the list; CoordinatorId is their account on the central server, and the class
    // is dropped from here when they are turned off there. Null on this PC's own classes.
    string? Coordinator = null,
    string? CoordinatorId = null,
    // "Physical" (held in a room: no Zoom meeting is opened, and attendance is not taken from Zoom)
    // or "Online"; null when this entry does not say, and then the LMS's own type decides.
    string? Mode = null);

public static class ScheduleDaysExtensions
{
    public static bool Includes(this ScheduleDays days, DayOfWeek day) =>
        days.HasFlag(day switch
        {
            DayOfWeek.Monday => ScheduleDays.Monday,
            DayOfWeek.Tuesday => ScheduleDays.Tuesday,
            DayOfWeek.Wednesday => ScheduleDays.Wednesday,
            DayOfWeek.Thursday => ScheduleDays.Thursday,
            DayOfWeek.Friday => ScheduleDays.Friday,
            DayOfWeek.Saturday => ScheduleDays.Saturday,
            DayOfWeek.Sunday => ScheduleDays.Sunday,
            _ => ScheduleDays.None
        });
}
