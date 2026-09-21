using System.Text.Json.Nodes;
using ZoomAutoAdmit.CentralAgent;
using ZoomAutoAdmit.WebAutomation.Lms;

namespace ZoomAutoAdmit.CloudWorker.Stages;

/// <summary>
/// Which sign-in a class is written up under. The worker asks for one account at a time, and the
/// answer lives only as long as the stage that needed it.
///
/// This is the whole reason an LMS stage carries an lmsAccountId: a machine that fell back to
/// "whichever account was last used" would put one coordinator's class under another's name, and
/// nothing downstream could tell that it had.
/// </summary>
public interface ILmsAccounts
{
    /// <summary>
    /// The sign-in for that account, ready to use, or null when the server will not hand it over -
    /// the coordinator is not turned on, or no password is stored for them.
    /// </summary>
    Task<ILmsCredentialStore?> ForAsync(Guid lmsAccountId, CancellationToken cancellationToken);
}

/// <summary>
/// What the three LMS stages share: the account is fetched first, and a stage that cannot get one
/// says so instead of running under somebody else's.
/// </summary>
public abstract class LmsStageHandler(ILmsAccounts accounts, Action<string>? log = null,
                                      Func<DateTimeOffset>? now = null)
    : ClassStageHandler(log, now)
{
    protected override string? Requires(ClassStage stage) =>
        stage.LmsAccountId is null
            ? $"'lmsAccountId' is required for {JobType}: a class is written up under its own account."
            : null;

    protected override async Task<JobOutcome> RunAsync(ClassStage stage, CancellationToken cancellationToken)
    {
        var credentials = await accounts.ForAsync(stage.LmsAccountId!.Value, cancellationToken);
        if (credentials is null)
            return JobOutcome.Failure(
                "noLmsSignIn",
                "The server would not give this class's LMS sign-in. Either the coordinator is not turned on, " +
                "or no password is stored for that account.");

        // The session runner is the same one the Windows app drives. Nothing of the LMS automation
        // is written twice: this only says which account, which class, and whether to press.
        var runner = new LmsSessionRunner(credentials);
        return await RunOnLmsAsync(runner, stage, cancellationToken);
    }

    protected abstract Task<JobOutcome> RunOnLmsAsync(
        LmsSessionRunner runner, ClassStage stage, CancellationToken cancellationToken);

    /// <summary>
    /// An LmsRunResult as a job outcome, with its message kept as the LMS gave it.
    ///
    /// Which failures are worth trying again is the useful part. A session the dashboard has not
    /// finished yet will be finished later, so that one waits and comes back; a session it does
    /// not list at all will not appear by being asked twice, and retrying only buries the reason.
    /// </summary>
    protected static JobOutcome From(LmsRunResult result, ClassStage stage, string did)
    {
        var answer = Answer(stage);
        answer["message"] = result.Message;
        if (result.IsSuccess)
        {
            answer["did"] = stage.DryRun ? $"would have {did}" : did;
            return JobOutcome.Success(answer);
        }
        return Failed(result.FailureKind, result.Message);
    }

    protected static JobOutcome Failed(LmsFailure kind, string message) => kind switch
    {
        LmsFailure.SessionNotFinished =>
            JobOutcome.Failure("sessionNotFinished", message, retryable: true, retryAfterSeconds: 300),
        LmsFailure.NotSignedIn =>
            JobOutcome.Failure("noLmsSignIn", message),
        LmsFailure.SessionNotFound =>
            JobOutcome.Failure("sessionNotFound", message),
        LmsFailure.InvalidLink =>
            JobOutcome.Failure("invalidLink", message),
        _ => JobOutcome.Failure("lmsFailed", message),
    };
}

/// <summary>lms.run_session: presses Run Session on the class's own LMS session.</summary>
public sealed class RunSessionStage(ILmsAccounts accounts, Action<string>? log = null,
                                      Func<DateTimeOffset>? now = null)
    : LmsStageHandler(accounts, log, now)
{
    public override string JobType => "lms.run_session";

    protected override async Task<JobOutcome> RunOnLmsAsync(
        LmsSessionRunner runner, ClassStage stage, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            stage.Group, stage.StartTime, stage.Date, headed: false, dryRun: stage.DryRun,
            cancellationToken: cancellationToken);
        return From(result, stage, "started the session");
    }
}

/// <summary>lms.complete: closes the session once the class is over.</summary>
public sealed class CompleteSessionStage(ILmsAccounts accounts, Action<string>? log = null,
                                      Func<DateTimeOffset>? now = null)
    : LmsStageHandler(accounts, log, now)
{
    public override string JobType => "lms.complete";

    protected override async Task<JobOutcome> RunOnLmsAsync(
        LmsSessionRunner runner, ClassStage stage, CancellationToken cancellationToken)
    {
        var result = await runner.CompleteSessionAsync(
            stage.Group, stage.StartTime, stage.Date, headed: false, dryRun: stage.DryRun,
            cancellationToken: cancellationToken);
        return From(result, stage, "completed the session");
    }
}

/// <summary>
/// lms.attendance: writes up who was in the meeting.
///
/// The names come with the job. Collecting them is class.attendance's work and Zoom's report's
/// after that; this stage only writes down what it was given, so a correction is another job with
/// a better list rather than a rerun that hopes for a different answer.
/// </summary>
public sealed class AttendanceStage(ILmsAccounts accounts, IAttendanceNames names, Action<string>? log = null,
                                      Func<DateTimeOffset>? now = null)
    : LmsStageHandler(accounts, log, now)
{
    public override string JobType => "lms.attendance";

    protected override async Task<JobOutcome> RunOnLmsAsync(
        LmsSessionRunner runner, ClassStage stage, CancellationToken cancellationToken)
    {
        var present = await names.PresentAsync(stage, cancellationToken);
        if (present.Count == 0)
        {
            // Not a failure to retry: an empty list would mark a whole class absent, and that is
            // worse than leaving the attendance for a person to look at.
            return JobOutcome.Failure(
                "noAttendanceCollected",
                "Nobody was collected for this class, so nothing was written: marking a whole class absent " +
                "is not something to do by accident. Run class.attendance, or zoom.report once it has ended.");
        }

        var result = await runner.TakeAttendanceAsync(
            stage.Group, present, stage.StartTime, stage.Date, headed: false, dryRun: stage.DryRun,
            cancellationToken: cancellationToken);

        // The result's own kind, not a flat "it failed". A session the dashboard has not finished
        // yet is worth trying again in five minutes; one it does not list at all is not, and
        // flattening both to lmsFailed loses the difference the retry decision is made on.
        if (!result.IsSuccess) return Failed(result.FailureKind, result.Message);

        var answer = Answer(stage);
        answer["message"] = result.Message;
        answer["present"] = present.Count;
        answer["did"] = stage.DryRun ? "would have written the attendance" : "wrote the attendance";
        return JobOutcome.Success(answer);
    }
}

/// <summary>Who the worker believes was in a class, by the names its roster knows them by.</summary>
public interface IAttendanceNames
{
    Task<IReadOnlyCollection<string>> PresentAsync(ClassStage stage, CancellationToken cancellationToken);
}
