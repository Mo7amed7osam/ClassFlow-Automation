using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Models;
using ZoomAutoAdmit.WebAutomation.Lms;

namespace ZoomAutoAdmit.Inspector.Commands;

/// <summary>
/// Takes a session's attendance on the dashboard, or corrects one that was already taken.
///
///   lms-attendance --group CAI5_AIS4_S7 [--day 2026-09-03] [--all-present | --present "A;B;C"]
///                  [--correct] [--dry-run] [--headed]
///
/// Without --dry-run it writes. --correct uses the "View details" list instead of taking a fresh
/// attendance, which is the late-joiner pass.
/// </summary>
public static class LmsAttendanceCommand
{
    public static async Task<int> ExecuteAsync(CliOptions options, CancellationToken cancellationToken = default)
    {
        string? group = options.LmsGroup;
        if (string.IsNullOrWhiteSpace(group))
        {
            ConsoleLogger.Error("lms-attendance requires --group <name as the dashboard shows it>.");
            return 1;
        }
        if (!options.AllPresent && string.IsNullOrWhiteSpace(options.PresentNames))
        {
            ConsoleLogger.Error("lms-attendance needs --all-present or --present \"Name One;Name Two\".");
            return 1;
        }

        var runner = new LmsSessionRunner(new LmsCredentialStore());
        string[] present = options.AllPresent
            ? []
            : options.PresentNames!.Split([';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        var result = options.CorrectAttendance
            ? await runner.CorrectAttendanceAsync(group.Trim(), present, options.LmsTime, options.LmsDay,
                options.WebHeaded, options.DryRun, options.WebHeaded, options.AllPresent, cancellationToken)
            : await runner.TakeAttendanceAsync(group.Trim(), present, options.LmsTime, options.LmsDay,
                options.WebHeaded, options.DryRun, options.WebHeaded, options.AllPresent, cancellationToken);

        if (result.Plan != null)
        {
            foreach (var mark in result.Plan.Marks)
                Console.WriteLine($"  {(mark.Joined ? "JOINED    " : "NOT-JOINED")}  {mark.StudentName}");
            foreach (var missing in result.Plan.NotOnTheDashboard)
                ConsoleLogger.Warn($"Seen in the meeting but not listed by the dashboard: {missing}");
        }
        if (result.IsSuccess) ConsoleLogger.Success(result.Message);
        else ConsoleLogger.Error(result.Message);
        return result.IsSuccess ? 0 : 2;
    }

}
