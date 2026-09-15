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
    string? GroupName = null);

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
