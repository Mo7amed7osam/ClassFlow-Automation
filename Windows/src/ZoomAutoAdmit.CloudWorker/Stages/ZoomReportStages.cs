using System.Text.Json.Nodes;
using ZoomAutoAdmit.CentralAgent;
using ZoomAutoAdmit.WebAutomation;
using ZoomAutoAdmit.WebAutomation.Zoom;

namespace ZoomAutoAdmit.CloudWorker.Stages;

/// <summary>
/// What the two after-the-class stages share: they read Zoom's own account pages, signed in as the
/// class's Zoom account, in that account's own browser profile.
///
/// Both only have an answer once the meeting has ended - Zoom does not list a meeting that is still
/// running, and it has not made a recording yet. A stage asked too early says "not yet" and asks to
/// be tried again, rather than reporting that a class had no participants or no recording, which
/// reads exactly like a class that went wrong.
/// </summary>
public abstract class ZoomAccountStage(IZoomAccounts accounts, Action<string>? log = null)
    : ClassStageHandler(log)
{
    protected override string? Requires(ClassStage stage) =>
        stage.ZoomAccountId is null
            ? $"'zoomAccountId' is required for {JobType}: these pages belong to one Zoom account, "
            + "and the wrong account's report is another class's."
            : null;

    protected override async Task<JobOutcome> RunAsync(ClassStage stage, CancellationToken cancellationToken)
    {
        var account = stage.ZoomAccountId!.Value;
        var credential = await accounts.ForAsync(account, cancellationToken);
        if (credential is null)
            return JobOutcome.Failure(
                "noZoomSignIn",
                "The server would not give this class's Zoom sign-in. Either the coordinator is not turned on, "
                + "or no Zoom password is saved for that account.");

        string reference = $"server:zoom/{account}";
        ZoomSignInCredential.Resolver = asked => asked == reference ? credential : null;
        try
        {
            // The same profile the meeting was opened in, so the account is already signed in and
            // the report pages open without a second sign-in.
            return await ReadAsync(stage, $"zoom-{account}", cancellationToken);
        }
        finally
        {
            ZoomSignInCredential.Resolver = null;
        }
    }

    /// <summary>
    /// No freshness limit. These pages only exist once the class is over, and Zoom takes its time
    /// publishing a recording - reading one the next morning is the point, not a mistake.
    /// </summary>
    protected override TimeSpan? Freshness => null;

    protected abstract Task<JobOutcome> ReadAsync(ClassStage stage, string profile, CancellationToken cancellationToken);

    /// <summary>Not an error, and worth trying again: Zoom has not published this yet.</summary>
    protected static JobOutcome NotYet(string what) =>
        JobOutcome.Failure("notPublishedYet", what, retryable: true, retryAfterSeconds: 900);
}

/// <summary>
/// zoom.report: Zoom's own participants report for the class's meeting, once it has ended.
///
/// This is the second and better answer about who attended. The snapshots taken while a meeting is
/// live miss whoever arrived after the last one; the report is Zoom's own record of every join,
/// which is why the class card corrects attendance from it after Ended.
/// </summary>
public sealed class ZoomReportStage(IZoomAccounts accounts, Action<string>? log = null)
    : ZoomAccountStage(accounts, log)
{
    public override string JobType => "zoom.report";

    protected override string? Requires(ClassStage stage) =>
        base.Requires(stage)
        ?? (stage.MeetingUrl is null
            ? "'meetingUrl' is required for zoom.report: the report is found by the meeting's number."
            : stage.StartTime is null
                ? "'startTime' is required for zoom.report: two classes of the same group on one day are "
                + "told apart by when they ran."
                : null);

    protected override async Task<JobOutcome> ReadAsync(ClassStage stage, string profile, CancellationToken cancellationToken)
    {
        var classStart = stage.Date.ToDateTime(stage.StartTime!.Value);
        var report = await new ZoomParticipantsReportReader()
            .ReadAsync(profile, stage.MeetingUrl!.ToString(), classStart, cancellationToken);

        if (report is null)
            return NotYet("Zoom lists no finished run of this meeting yet. A meeting appears in the report "
                          + "only once it has ended.");

        var answer = Answer(stage);
        answer["people"] = report.People.Count;
        answer["instances"] = report.Instances;
        answer["rows"] = report.Rows;
        if (report.EndedAt is { } ended) answer["endedAt"] = ended.ToString("O");
        answer["attended"] = new JsonArray([.. report.People.Select(p => (JsonNode)new JsonObject
        {
            ["name"] = p.Name,
            ["minutes"] = p.Minutes,
        })]);
        answer["did"] = "read Zoom's participants report";
        answer["message"] = $"{stage.Group}: {report.People.Count} people across {report.Instances} run(s) of the meeting.";
        return JobOutcome.Success(answer);
    }
}

/// <summary>
/// zoom.recording: the link to the class's cloud recording, once Zoom has finished making one.
///
/// The link is what the LMS wants; attaching it is recording.process's work, which already exists.
/// This stage only finds it, so a failure here never touches the LMS.
/// </summary>
public sealed class ZoomRecordingStage(IZoomAccounts accounts, Action<string>? log = null)
    : ZoomAccountStage(accounts, log)
{
    public override string JobType => "zoom.recording";

    protected override async Task<JobOutcome> ReadAsync(ClassStage stage, string profile, CancellationToken cancellationToken)
    {
        var result = await new ZoomRecordingLinkReader().ReadAsync(
            stage.Group, profile, stage.Date, stage.StartTime, headed: false,
            cancellationToken: cancellationToken);

        var answer = Answer(stage);
        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.ShareUrl))
        {
            // Zoom takes a while to process a recording after a class. "Not there yet" and "there
            // will never be one" look the same from here, so this waits rather than deciding.
            return NotYet(result.Message ?? "Zoom has no recording for this class yet.");
        }

        answer["shareUrl"] = result.ShareUrl;
        if (result.StartedAtUtc is { } began) answer["startedAt"] = began.ToString("O");
        answer["did"] = "found the recording's link";
        answer["message"] = $"{stage.Group}: {result.Message}";
        return JobOutcome.Success(answer);
    }
}
