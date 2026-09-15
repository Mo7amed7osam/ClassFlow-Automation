using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Models;
using ZoomAutoAdmit.WebAutomation.Lms;

namespace ZoomAutoAdmit.Inspector.Commands;

/// <summary>
/// Reads a group's students from the LMS without saving them anywhere - the check behind the app's
/// "Get from LMS" button.
///
///   lms-roster --group CAI5_AIS4_S8 [--day 2026-09-15] [--headed]
///
/// It opens one of the group's sessions (this month's first, then last month's), reads its
/// attendance list and closes it with Escape. Nothing is ticked or saved on the LMS.
/// </summary>
public static class LmsRosterCommand
{
    /// <summary>lms-describe --group X --day D: a session page's attachments and its two boxes' fields.</summary>
    public static async Task<int> DescribeAsync(CliOptions options, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.LmsGroup) || options.LmsDay == null)
        {
            ConsoleLogger.Error("lms-describe requires --group and --day.");
            return 1;
        }
        Console.WriteLine(await new LmsSessionRunner(new LmsCredentialStore())
            .DescribeSessionAsync(options.LmsGroup.Trim(), options.LmsDay.Value, options.LmsTime, cancellationToken));
        return 0;
    }

    /// <summary>
    /// lms-material --group X --day D [--time HH:mm] [--dry-run]: the class's material (from the
    /// timetable and the material folders) put on its session. Without --dry-run it uploads.
    /// </summary>
    public static async Task<int> MaterialAsync(CliOptions options, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.LmsGroup) || options.LmsDay == null)
        {
            ConsoleLogger.Error("lms-material requires --group and --day.");
            return 1;
        }
        string group = options.LmsGroup.Trim();
        DateOnly day = options.LmsDay.Value;
        var schedules = await new ZoomAutoAdmit.WindowsRuntime.Scheduling.WindowsMeetingScheduleStore().ListAsync();
        var timetable = schedules.Where(s => s.OccurrenceDate.HasValue)
            .Select(s => new TimetableEntry(s.GroupName ?? s.AccountId, s.OccurrenceDate!.Value, new TimeOnly(s.Time.Hour, s.Time.Minute), s.Name)).ToList();
        TimeOnly? start = options.LmsTime ?? timetable.Where(e => e.Group.Equals(group, StringComparison.OrdinalIgnoreCase) && e.Date == day).Select(e => (TimeOnly?)e.Start).FirstOrDefault();
        if (start == null) { ConsoleLogger.Error($"{group} has no class on {day:yyyy-MM-dd} in the timetable; give --time."); return 1; }
        var settings = MaterialSettings.Load();
        string? lmsTitle = new LmsSessionCache().Read().Where(c => c.Session.Group.Equals(group, StringComparison.OrdinalIgnoreCase) && c.Session.Date == day)
            .OrderByDescending(c => c.ReadAt).Select(c => c.Session.Title).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        var plan = MaterialPlanner.Plan(timetable, group, day, start.Value, settings, lmsTitle);
        Console.WriteLine($"  LMS title: {lmsTitle ?? "(not read yet)"} · {plan.Label} · {plan.Note}");
        settings.Assignments.TryGetValue(MaterialSettings.KeyOf(group, day, start.Value), out var choice);
        var files = MaterialPlanner.FilesFor(plan, choice);
        foreach (var file in files) Console.WriteLine($"    file: {file.Title}");
        foreach (var skipped in plan.Skipped) Console.WriteLine($"    skipped (not PDF/ZIP/PowerPoint): {skipped}");
        var assignment = MaterialPlanner.AssignmentFor(plan, choice, day, start.Value);
        if (assignment != null) Console.WriteLine($"    assignment: {assignment.Title}, due {assignment.Deadline:ddd dd MMM HH:mm} - \"{assignment.Description}\"");
        if (files.Count == 0 && assignment == null) return 0;
        var result = await new LmsSessionRunner(new LmsCredentialStore())
            .AddMaterialsAsync(group, day, start, files, assignment, dryRun: options.DryRun, cancellationToken: cancellationToken);
        if (!options.DryRun)
        {
            // Kept where the app keeps it, so the Sessions page shows it done and nothing goes up twice.
            string key = MaterialSettings.KeyOf(group, day, start.Value);
            var saved = MaterialSettings.Load();
            if (result.IsSuccess)
            {
                saved.Done[key] = new MaterialRecord(DateTimeOffset.Now, [.. files.Select(f => f.Title)],
                    result.AssignmentCreated || result.AssignmentAlreadyThere ? assignment?.Title : null, assignment?.Deadline);
                saved.Errors.Remove(key);
            }
            else saved.Errors[key] = result.Message;
            saved.Save();
        }
        if (result.IsSuccess) ConsoleLogger.Success(result.Message); else ConsoleLogger.Error(result.Message);
        return result.IsSuccess ? 0 : 2;
    }

    /// <summary>
    /// lms-check --day D: what the Sessions page's full check does for one day - every session of the
    /// timetable's groups read (status, link, attendance, attachments, assignment) and kept where the
    /// app reads it. Nothing is changed on the LMS.
    /// </summary>
    public static async Task<int> CheckAsync(CliOptions options, CancellationToken cancellationToken = default)
    {
        if (options.LmsDay == null) { ConsoleLogger.Error("lms-check requires --day."); return 1; }
        DateOnly day = options.LmsDay.Value;
        var schedules = await new ZoomAutoAdmit.WindowsRuntime.Scheduling.WindowsMeetingScheduleStore().ListAsync();
        var groups = schedules.Select(s => s.GroupName ?? s.AccountId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var store = new LmsCredentialStore();
        var list = await new LmsSessionRunner(store).SurveyAsync(day, day, groups, openEach: true, cancellationToken: cancellationToken);
        new LmsSessionCache().Merge(list, day, day, store.Read()?.Email ?? "", listOnly: false);
        foreach (var s in list)
            Console.WriteLine($"  {s.Group} {s.Start:HH\\:mm} {s.Title} · {s.PageStatus} · attachments: {(s.Attachments == null ? "?" : s.Attachments.Count.ToString())} · assignment: {s.HasAssignment?.ToString() ?? "?"}");
        ConsoleLogger.Success($"{list.Count} session(s) of {day:yyyy-MM-dd} read and kept for the app.");
        return 0;
    }

    public static async Task<int> ExecuteAsync(CliOptions options, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.LmsGroup))
        {
            ConsoleLogger.Error("lms-roster requires --group <name as the dashboard shows it>.");
            return 1;
        }
        var result = await new LmsSessionRunner(new LmsCredentialStore())
            .ReadRosterAsync(options.LmsGroup.Trim(), options.LmsDay, options.WebHeaded, cancellationToken);
        if (!result.IsSuccess) { ConsoleLogger.Error(result.Message); return 2; }
        Console.WriteLine($"  read from the {result.ReadFrom:yyyy-MM-dd} session: {result.Names.Count} students");
        ConsoleLogger.Success(result.Message);
        return 0;
    }
}
