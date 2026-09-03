using ZoomAutoAdmit.SessionRoles;
using ZoomAutoAdmit.WindowsRuntime.Scheduling;

namespace ZoomAutoAdmit.Inspector.Runtime;

/// <summary>
/// Reads the saved schedules to name a running meeting, so the session type can be recognised from
/// that name. Read-only: it never edits, triggers or reorders a schedule.
/// </summary>
public sealed class ScheduleNameSource(WindowsMeetingScheduleStore store) : ISessionNameSource
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(90);

    public string? Describe(string accountId, DateTimeOffset startTime)
    {
        try
        {
            var schedules = store.ListAsync().GetAwaiter().GetResult();
            var local = startTime.ToLocalTime();
            var date = DateOnly.FromDateTime(local.DateTime);
            var candidates = schedules
                .Where(schedule => schedule.AccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase))
                .Select(schedule => (schedule, when: Occurrence(schedule, date)))
                .Where(pair => pair.when.HasValue)
                .Select(pair => (pair.schedule, gap: (pair.when!.Value - local.DateTime).Duration()))
                .Where(pair => pair.gap <= Window)
                .OrderBy(pair => pair.gap)
                .ToArray();
            return candidates.Length == 0 ? null : candidates[0].schedule.Name;
        }
        catch { return null; }
    }

    private static DateTime? Occurrence(MeetingSchedule schedule, DateOnly date)
    {
        if (schedule.OccurrenceDate is { } exact) return exact == date ? exact.ToDateTime(schedule.Time) : null;
        return schedule.Days.Includes(date.DayOfWeek) ? date.ToDateTime(schedule.Time) : null;
    }
}
