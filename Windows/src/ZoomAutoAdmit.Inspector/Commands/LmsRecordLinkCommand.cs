using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Models;
using ZoomAutoAdmit.WebAutomation.Lms;
using ZoomAutoAdmit.WebAutomation.Zoom;

namespace ZoomAutoAdmit.Inspector.Commands;

/// <summary>
/// The step after a class is finished: take the cloud recording's shareable link and put it on
/// the session in the dashboard.
///
///   lms-record-link --group CAI5_AIS4_S7 [--profile s7] [--day 2026-09-01] [--dry-run] [--headed]
///
/// --dry-run does everything except press Save, which is how the whole path gets checked against
/// a real session without writing to it.
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

        // The recordings live under the account that ran the meeting, so its own browser profile
        // is the one signed in to Zoom. --profile overrides it for an account owned elsewhere.
        string profileName = string.Equals(options.WebProfile, "default", StringComparison.OrdinalIgnoreCase)
            ? group.Trim()
            : options.WebProfile;

        ConsoleLogger.Info($"Reading the recording link for {group} from the '{profileName}' profile...");
        var recording = await new ZoomRecordingLinkReader().ReadAsync(
            group.Trim(), profileName,
            // The same group records the same name every week, so the session's own day and time
            // are what say which recording this is.
            day: options.LmsDay ?? DateOnly.FromDateTime(DateTime.Now),
            startTime: options.LmsTime,
            headed: options.WebHeaded, keepBrowserOpen: false, cancellationToken: cancellationToken);
        if (!recording.IsSuccess || recording.ShareUrl == null)
        {
            ConsoleLogger.Error(recording.Message);
            return 2;
        }
        // Enough of the link to see it is the right one, without printing a link that opens the
        // recording to anyone who reads the log.
        ConsoleLogger.Success($"{recording.Message} ({Shorten(recording.ShareUrl)})");

        var runner = new LmsSessionRunner(new LmsCredentialStore());
        var attached = await runner.AttachRecordLinkAsync(
            group.Trim(),
            recording.ShareUrl,
            startTime: options.LmsTime,
            day: options.LmsDay,
            headed: options.WebHeaded,
            dryRun: options.DryRun,
            keepBrowserOpen: options.WebHeaded,
            replaceExisting: options.ReplaceExisting,
            cancellationToken: cancellationToken);
        if (attached.IsSuccess) ConsoleLogger.Success(attached.Message);
        else ConsoleLogger.Error(attached.Message);
        return attached.IsSuccess ? 0 : 3;
    }

    private static string Shorten(string url) => url.Length <= 48 ? url : url[..48] + "...";
}
