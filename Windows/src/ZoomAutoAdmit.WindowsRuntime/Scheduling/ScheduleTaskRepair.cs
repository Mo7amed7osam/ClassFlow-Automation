namespace ZoomAutoAdmit.WindowsRuntime.Scheduling;

/// <summary>What one repair pass found and did.</summary>
public sealed record ScheduleRepairResult(int Checked, int Repaired, int Failed, IReadOnlyList<string> Details)
{
    public string Summary => Repaired == 0 && Failed == 0
        ? $"All {Checked} upcoming schedule(s) point at a program that exists."
        : $"{Repaired} of {Checked} upcoming schedule(s) re-pointed at this copy of the app" +
          (Failed > 0 ? $"; {Failed} could not be re-registered." : ".");
}

/// <summary>
/// Keeps every upcoming schedule pointed at a program that exists.
///
/// A scheduled meeting's task names the program it starts by full path, fixed at the moment the
/// schedule was saved. Move the app - a new drive, a new folder, a rebuild somewhere else - and
/// every task still names the old place. Nothing fails when that happens; the meetings just never
/// open, which is the worst way for this to fail, because scheduled meetings are the ones nobody is
/// watching. So on start the app asks each upcoming task what it will run, and a task that names a
/// program which is not there is registered again against the program that is.
///
/// A task that works is left exactly as it is. Only a broken one is touched.
/// </summary>
public sealed class ScheduleTaskRepair(
    Func<CancellationToken, Task<IReadOnlyList<MeetingSchedule>>> listSchedules,
    Func<Guid, CancellationToken, Task<string?>> readTaskTarget,
    Func<MeetingSchedule, CancellationToken, Task> register,
    Func<string, bool>? programExists = null)
{
    private readonly Func<string, bool> _exists = programExists ?? File.Exists;

    /// <summary>The app's real scheduler and store, for the app and the terminal.</summary>
    public static ScheduleTaskRepair For(WindowsMeetingScheduleStore store, WindowsTaskSchedulerService scheduler) =>
        new((token) => store.ListAsync(token),
            (id, token) => scheduler.ReadTaskTargetAsync(id, token),
            (schedule, token) => scheduler.RegisterTaskAsync(schedule, token));

    /// <summary>
    /// Whether a schedule's task has to be registered again. Disabled schedules and dated ones
    /// already in the past are never touched: neither will run, and re-registering them would only
    /// bring back something that was meant to be finished.
    /// </summary>
    /// <param name="currentTarget">What the task runs now; null when there is no task at all.</param>
    public static bool NeedsRepair(MeetingSchedule schedule, DateOnly today, string? currentTarget, Func<string, bool> exists)
    {
        if (!schedule.Enabled) return false;
        if (schedule.OccurrenceDate is { } day && day < today) return false;
        if (string.IsNullOrWhiteSpace(currentTarget)) return true;
        return !exists(currentTarget);
    }

    /// <param name="dryRun">Report what would be re-registered without registering anything.</param>
    public async Task<ScheduleRepairResult> RepairAsync(
        DateOnly today, bool dryRun = false, CancellationToken cancellationToken = default)
    {
        var schedules = await listSchedules(cancellationToken);
        int checkedCount = 0, repaired = 0, failed = 0;
        var details = new List<string>();

        foreach (var schedule in schedules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!schedule.Enabled) continue;
            if (schedule.OccurrenceDate is { } day && day < today) continue;
            checkedCount++;

            string? target = await readTaskTarget(schedule.Id, cancellationToken);
            if (!NeedsRepair(schedule, today, target, _exists)) continue;

            string when = schedule.OccurrenceDate is { } date
                ? $"{date:yyyy-MM-dd} {schedule.Time:HH\\:mm}"
                : $"{schedule.Days} {schedule.Time:HH\\:mm}";
            string was = string.IsNullOrWhiteSpace(target) ? "no task" : target;
            if (dryRun)
            {
                repaired++;
                details.Add($"would re-point '{schedule.Name}' ({when}) - it runs {was}");
                continue;
            }

            try
            {
                await register(schedule, cancellationToken);
                // Registered is not the same as fixed: read it back and check the program is there.
                string? after = await readTaskTarget(schedule.Id, cancellationToken);
                if (NeedsRepair(schedule, today, after, _exists))
                {
                    failed++;
                    details.Add($"still broken after re-registering '{schedule.Name}' ({when}) - it runs {after ?? "no task"}");
                }
                else
                {
                    repaired++;
                    details.Add($"re-pointed '{schedule.Name}' ({when})");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                details.Add($"could not re-register '{schedule.Name}' ({when}): {ex.GetType().Name}");
            }
        }
        return new ScheduleRepairResult(checkedCount, repaired, failed, details);
    }
}
