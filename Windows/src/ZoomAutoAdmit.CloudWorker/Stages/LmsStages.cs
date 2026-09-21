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
/// lms.attendance: writes up who was in the meeting, an hour and a half in.
///
/// The names are the server's: the meeting lane sends what it sees while the class runs, the server
/// matches it against the group's roster, and this stage writes down the students it found. It never
/// writes an empty list - that would mark a whole class absent - and says why there was nothing
/// instead.
/// </summary>
public sealed class AttendanceStage(ILmsAccounts accounts, IAttendanceNames names, Action<string>? log = null,
                                      Func<DateTimeOffset>? now = null)
    : AttendanceWritingStage(accounts, names, log, now)
{
    public override string JobType => "lms.attendance";

    protected override Task<LmsAttendanceResult> WriteAsync(
        LmsSessionRunner runner, ClassStage stage, IReadOnlyCollection<string> present, CancellationToken cancellationToken) =>
        runner.TakeAttendanceAsync(stage.Group, present, stage.StartTime, stage.Date, headed: false,
                                   dryRun: stage.DryRun, cancellationToken: cancellationToken);

    protected override string Did(ClassStage stage) =>
        stage.DryRun ? "would have written the attendance" : "wrote the attendance";
}

/// <summary>
/// lms.late_joiners: three hours in, the attendance again from everything the meeting showed, so
/// whoever arrived after the first write-up is moved from Not-joined to Joined. The Windows app's
/// CorrectAttendance step, and the same runner method: only the rows that disagree are changed.
/// </summary>
public sealed class LateJoinersStage(ILmsAccounts accounts, IAttendanceNames names, Action<string>? log = null,
                                     Func<DateTimeOffset>? now = null)
    : AttendanceWritingStage(accounts, names, log, now)
{
    public override string JobType => "lms.late_joiners";

    protected override Task<LmsAttendanceResult> WriteAsync(
        LmsSessionRunner runner, ClassStage stage, IReadOnlyCollection<string> present, CancellationToken cancellationToken) =>
        runner.CorrectAttendanceAsync(stage.Group, present, stage.StartTime, stage.Date, headed: false,
                                      dryRun: stage.DryRun, cancellationToken: cancellationToken);

    protected override string Did(ClassStage stage) =>
        stage.DryRun ? "would have corrected the attendance for late joiners" : "corrected the attendance for late joiners";
}

/// <summary>What the two attendance stages share: the names come from the server, never empty.</summary>
public abstract class AttendanceWritingStage(ILmsAccounts accounts, IAttendanceNames names, Action<string>? log,
                                             Func<DateTimeOffset>? now)
    : LmsStageHandler(accounts, log, now)
{
    protected abstract Task<LmsAttendanceResult> WriteAsync(
        LmsSessionRunner runner, ClassStage stage, IReadOnlyCollection<string> present, CancellationToken cancellationToken);

    protected abstract string Did(ClassStage stage);

    protected override async Task<JobOutcome> RunOnLmsAsync(
        LmsSessionRunner runner, ClassStage stage, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<string> present;
        try
        {
            present = await names.PresentAsync(stage, cancellationToken);
        }
        catch (AttendanceUnavailableException problem)
        {
            // Not retried: nothing more will be collected from a meeting that is over, and a
            // missing roster needs a person. The reason goes on the class card as the server gave it.
            return JobOutcome.Failure("noAttendanceCollected", problem.Message);
        }
        if (present.Count == 0)
        {
            // Not a failure to retry: an empty list would mark a whole class absent, and that is
            // worse than leaving the attendance for a person to look at.
            return JobOutcome.Failure(
                "noAttendanceCollected",
                "Nobody on the roster was found in this class's meeting, so nothing was written: marking a whole "
                + "class absent is not something to do by accident. Check the matches on the attendance page.");
        }

        var result = await WriteAsync(runner, stage, present, cancellationToken);

        // The result's own kind, not a flat "it failed". A session the dashboard has not finished
        // yet is worth trying again in five minutes; one it does not list at all is not, and
        // flattening both to lmsFailed loses the difference the retry decision is made on.
        if (!result.IsSuccess) return Failed(result.FailureKind, result.Message);

        var answer = Answer(stage);
        answer["message"] = result.Message;
        answer["present"] = present.Count;
        answer["did"] = Did(stage);
        return JobOutcome.Success(answer);
    }
}

/// <summary>Who the worker believes was in a class, by the names its roster knows them by.</summary>
public interface IAttendanceNames
{
    Task<IReadOnlyCollection<string>> PresentAsync(ClassStage stage, CancellationToken cancellationToken);
}
