using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Models;
using ZoomAutoAdmit.WindowsRuntime.Scheduling;

namespace ZoomAutoAdmit.Inspector.Commands;

/// <summary>
/// Re-points every upcoming scheduled meeting whose task names a program that is not there.
///
///   repair-schedules [--dry-run]
///
/// The app does this by itself on every start; this is the same pass, for when the app is not the
/// thing being run, or to see what it would change first.
/// </summary>
public static class RepairSchedulesCommand
{
    public static async Task<int> ExecuteAsync(CliOptions options, CancellationToken cancellationToken = default)
    {
        var scheduler = new WindowsTaskSchedulerService();
        var store = new WindowsMeetingScheduleStore(taskScheduler: scheduler);
        ConsoleLogger.Info($"Tasks will be pointed at: {scheduler.ResolveInspectorExecutablePath()}");

        var result = await ScheduleTaskRepair.For(store, scheduler)
            .RepairAsync(DateOnly.FromDateTime(DateTime.Now), options.DryRun, cancellationToken);

        foreach (string line in result.Details) Console.WriteLine("  " + line);
        if (result.Failed > 0) ConsoleLogger.Error(result.Summary);
        else ConsoleLogger.Success(result.Summary + (options.DryRun ? " Nothing was changed." : string.Empty));
        return result.Failed > 0 ? 2 : 0;
    }
}
