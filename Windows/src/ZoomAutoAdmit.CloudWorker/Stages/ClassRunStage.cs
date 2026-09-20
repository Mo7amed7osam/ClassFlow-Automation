using System.Text.Json.Nodes;
using ZoomAutoAdmit.CentralAgent;
using ZoomAutoAdmit.Core.Models;
using ZoomAutoAdmit.WebAutomation;

namespace ZoomAutoAdmit.CloudWorker.Stages;

/// <summary>
/// Where a Zoom account's password comes from on a server. As with the LMS one, the reference is
/// the account and the answer lives only as long as the stage that asked.
/// </summary>
public interface IZoomAccounts
{
    /// <summary>The sign-in for that account, or null when the server will not hand it over.</summary>
    Task<ZoomSignInCredential?> ForAsync(Guid zoomAccountId, CancellationToken cancellationToken);
}

/// <summary>
/// class.run: opens the class's meeting and holds it for the class.
///
/// This is one long job, not four short ones, because a meeting is one long thing. It opens as the
/// host, admits the waiting room as people arrive, and ends when the class's time is up or when the
/// worker is asked to stop. Splitting it would mean a browser left alive between jobs, and a worker
/// restart would orphan it with nobody able to say whether the class was still running.
///
/// The worker takes one job at a time, so a worker running a class is busy for the class's length.
/// Several classes at once means several workers; that is what the compose file scales.
/// </summary>
public sealed class ClassRunStage(
    IZoomAccounts accounts,
    bool headless,
    Action<string>? log = null) : ClassStageHandler(log)
{
    /// <summary>How long after the class's own length to keep holding it. Nothing is admitted to a
    /// meeting nobody is in, and a worker that lost the backend must not hold its slot for ever.</summary>
    public static readonly TimeSpan DefaultLength = TimeSpan.FromMinutes(180);

    public override string JobType => "class.run";

    protected override string? Requires(ClassStage stage)
    {
        if (stage.MeetingUrl is null)
            return "'meetingUrl' is required for class.run: there is nothing to open without it.";
        if (stage.ZoomAccountId is null)
            return "'zoomAccountId' is required for class.run: a meeting is opened by a named account, "
                 + "never by whichever profile the machine happens to have.";
        return null;
    }

    protected override async Task<JobOutcome> RunAsync(ClassStage stage, CancellationToken cancellationToken)
    {
        var account = stage.ZoomAccountId!.Value;
        var credential = await accounts.ForAsync(account, cancellationToken);
        if (credential is null)
            return JobOutcome.Failure(
                "noZoomSignIn",
                "The server would not give this class's Zoom sign-in. Either the coordinator is not turned on, "
                + "or no Zoom password is saved for that account - a server cannot sign in by hand.");

        // The engine reads the sign-in through this reference. It is taken down again in the
        // finally that wraps everything below, including the dry run: a resolver left standing
        // makes one class's password readable while the next one runs.
        string reference = $"server:zoom/{account}";
        ZoomSignInCredential.Resolver = asked => asked == reference ? credential : null;
        try
        {
            return await HoldAsync(stage, account, credential, reference, cancellationToken);
        }
        finally
        {
            ZoomSignInCredential.Resolver = null;
        }
    }

    private async Task<JobOutcome> HoldAsync(
        ClassStage stage, Guid account, ZoomSignInCredential credential, string reference,
        CancellationToken cancellationToken)
    {

        var options = new CliOptions
        {
            Command = "auto-admit",
            Engine = "web",
            MeetingUrl = stage.MeetingUrl!.ToString(),
            // The account's own profile. Two coordinators' meetings therefore never share a Zoom
            // session, and neither signs the other out.
            WebProfile = $"zoom-{account}",
            WebHeaded = !headless,
            WebSignInCredential = reference,
            LmsGroup = stage.Group,
        };

        var length = stage.Duration ?? DefaultLength;
        using var classOver = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        classOver.CancelAfter(length);

        await using var engine = new WebAutoAdmitEngine();
        var answer = Answer(stage);
        answer["holdsFor"] = (int)length.TotalMinutes;

        if (stage.DryRun)
        {
            // Everything up to opening a meeting: the account is real, the link is valid, the
            // profile exists. Nothing is joined, so no class is disturbed by a rehearsal.
            answer["did"] = "would have opened the meeting and held it";
            answer["message"] = $"{stage.Group}: the Zoom sign-in and the meeting link are in place. Nothing was opened.";
            return JobOutcome.Success(answer);
        }

        try
        {
            Log($"opening {stage.Group} as {credential.Email}");
            await engine.StartAsync(options, classOver.Token);
        }
        catch (ZoomWebSignInRequiredException problem)
        {
            // A captcha or a one-time code. Nobody is watching a server's browser, so this is not
            // retried: it needs a person, and saying so is the only useful thing to do.
            return JobOutcome.Failure("needsVerification",
                $"Zoom asked for something only a person can answer: {problem.Message}");
        }

        Log($"{stage.Group} is live; admitting for {length.TotalMinutes:0} minutes");
        try
        {
            await engine.MonitorAsync(options, classOver.Token);
        }
        catch (OperationCanceledException) when (classOver.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The class's own length ran out, which is the ordinary way a class ends.
        }
        finally
        {
            try { await engine.StopAsync(); }
            catch (Exception problem) { Log($"the meeting did not close cleanly: {problem.GetType().Name}"); }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            // The worker is draining. The class was held for as long as the worker was allowed to,
            // and that is worth recording as what happened rather than as a failure.
            answer["did"] = "held the meeting until the worker was asked to stop";
            answer["message"] = $"{stage.Group}: the worker stopped before the class's time was up.";
            return JobOutcome.Success(answer);
        }

        answer["did"] = "held the meeting for the class";
        answer["message"] = $"{stage.Group}: the meeting was opened, admitted for {length.TotalMinutes:0} minutes, and closed.";
        return JobOutcome.Success(answer);
    }
}
