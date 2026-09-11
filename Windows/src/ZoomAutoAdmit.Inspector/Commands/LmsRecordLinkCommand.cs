using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Models;
using ZoomAutoAdmit.Inspector.Runtime;
using ZoomAutoAdmit.WebAutomation.Recordings;

namespace ZoomAutoAdmit.Inspector.Commands;

/// <summary>
/// The step after a class is finished: take the cloud recording's shareable link and put it on
/// the session in the dashboard.
///
///   lms-record-link --group CAI5_AIS4_S7 [--profile s7] [--day 2026-09-01] [--time 18:58]
///                   [--dry-run] [--headed] [--replace]
///
/// --dry-run does everything except press Save, which is how the whole path gets checked against
/// a real session without writing to it. Without --profile the account's own browser profile is
/// used - the one its meetings sign in with. Day and time are this computer's local time.
///
/// It runs through the same workflow as the HTTP API (serve-api), so both behave identically.
/// Exit codes: 0 done, 1 bad arguments, 2 the recording was not found or Zoom failed,
/// 3 the dashboard failed, 4 a browser profile was busy.
/// </summary>
public static class LmsRecordLinkCommand
{
    public static async Task<int> ExecuteAsync(CliOptions options, CancellationToken cancellationToken = default)
    {
        string? group = options.LmsGroup;
        if (string.IsNullOrWhiteSpace(group))
        {
            ConsoleLogger.Error("lms-record-link requires --group <name as the dashboard shows it>.");
            return 1;
        }

        var processor = RecordingWorkflow.Create(RecordingWorkflow.ToConsole);
        var outcome = await processor.ProcessAsync(new RecordingLinkRequest
        {
            Group = group.Trim(),
            // The same group records the same name every week, so the session's own day and time
            // are what say which recording this is.
            Date = options.LmsDay,
            StartTime = options.LmsTime,
            Profile = options.WebProfile,
            Headed = options.WebHeaded,
            DryRun = options.DryRun,
            ReplaceExisting = options.ReplaceExisting,
            // Watching it by hand: the dashboard stays open afterwards, as it always did.
            KeepBrowserOpen = options.WebHeaded,
        }, cancellationToken);

        if (outcome.IsSuccess) ConsoleLogger.Success(outcome.Message);
        else ConsoleLogger.Error(outcome.Message);
        return outcome.Status switch
        {
            RecordingLinkStatus.Attached or RecordingLinkStatus.AlreadyExists or RecordingLinkStatus.DryRun => 0,
            RecordingLinkStatus.RecordingNotFound or RecordingLinkStatus.ZoomFailed => 2,
            RecordingLinkStatus.Busy => 4,
            _ => 3,
        };
    }
}
