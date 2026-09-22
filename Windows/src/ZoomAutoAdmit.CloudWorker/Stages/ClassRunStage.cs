using System.Text.Json.Nodes;
using ZoomAutoAdmit.CentralAgent;
using ZoomAutoAdmit.Core.Meetings;
using ZoomAutoAdmit.Core.Models;
using ZoomAutoAdmit.Core.Sessions;
using ZoomAutoAdmit.SessionRoles;
using ZoomAutoAdmit.WebAutomation;
using ZoomAutoAdmit.WebAutomation.Browser;

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
/// class.run: opens the class's meeting and holds it for the class, as the Windows app does.
///
/// This is one long job, not four short ones, because a meeting is one long thing. It opens as the
/// host, admits the waiting room as people arrive, makes the instructor co-host, writes down who
/// was there, and ends the class for everyone by the Windows app's own rule - from three hours after
/// its time, once the room is empty or the instructor and the class have left. Splitting it would
/// mean a browser left alive between jobs, and a worker restart would orphan it.
///
/// The meeting lane takes one job at a time, so it is busy for the class's length; the LMS steps run
/// in the worker's other lane beside it.
/// </summary>
public sealed class ClassRunStage(
    IZoomAccounts accounts,
    bool headless,
    Action<string>? log = null,
    Func<DateTimeOffset>? now = null,
    IAttendanceSnapshots? attendance = null,
    ServerSessionRoles? roles = null) : ClassStageHandler(log, now)
{
    /// <summary>The class's length when it names none: the three hours the Windows rule waits for.</summary>
    public static readonly TimeSpan DefaultLength = AutoEndRule.EndAfter;

    /// <summary>
    /// How long past the rule's three hours a meeting is held at the most. The rule never ends a class
    /// while anyone is talking, and on Windows a person is there to notice one that runs on for ever.
    /// Nobody watches a server, and the meeting lane's only slot must come back, so after this the
    /// class is ended regardless - and the class card says so.
    /// </summary>
    public static readonly TimeSpan Overrun = TimeSpan.FromHours(2);

    public override string JobType => "class.run";

    /// <summary>
    /// A class is still worth opening up to the latest it could be held to. A deployment that stops
    /// the worker mid-class puts the job back, and the worker that starts again rejoins the class as
    /// its host - which is what keeps a redeploy from costing the class its waiting room.
    /// </summary>
    protected override TimeSpan? Freshness => Overrun;

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

        var classStart = StartOf(stage);
        var length = stage.Duration is { } given && given > DefaultLength ? given : DefaultLength;
        var latest = classStart + length + Overrun;

        var answer = Answer(stage);
        answer["holdsFor"] = (int)(latest - classStart).TotalMinutes;

        if (stage.DryRun)
        {
            // Everything up to opening a meeting: the account is real, the link is valid, the
            // profile exists. Nothing is joined, so no class is disturbed by a rehearsal.
            answer["did"] = "would have opened the meeting and held it";
            answer["message"] = $"{stage.Group}: the Zoom sign-in and the meeting link are in place. Nothing was opened.";
            return JobOutcome.Success(answer);
        }

        // neverHeaded: a profile that has never signed in is shown to a person by default, and on
        // a server there is neither a person nor a display. Without this the first class on a
        // fresh container could not open at all.
        await using var engine = new WebAutoAdmitEngine(
            profileManager: new ZoomProfileManager(neverHeaded: headless));

        try
        {
            Log($"opening {stage.Group} as {credential.Email}");
            await engine.StartAsync(options, cancellationToken);
        }
        catch (ZoomWebSignInRequiredException problem)
        {
            // A captcha or a one-time code. Nobody is watching a server's browser, so this is not
            // retried: it needs a person, and saying so is the only useful thing to do.
            return JobOutcome.Failure("needsVerification",
                $"Zoom asked for something only a person can answer: {problem.Message}");
        }

        // A host that joins unmuted puts a server's silence - or its noise - into every class.
        try
        {
            await engine.DisableMicrophoneAsync(cancellationToken);
            await engine.DisableCameraAsync(cancellationToken);
        }
        catch (Exception problem) when (problem is not OperationCanceledException)
        {
            Log($"could not mute or turn the camera off: {problem.GetType().Name}");
        }

        // The meeting as the Windows app's own observers know one. The class's id is the session's,
        // so a worker that restarts mid-class picks up the same co-host and the same class.
        var sessionId = stage.ClassPlanId;
        var context = new MeetingLaunchContext(
            new MeetingSession(sessionId,
                new ScheduledMeeting(stage.MeetingUrl!, stage.Group, classStart, sessionId, SessionEngineType.Web,
                                     stage.Group, classStart),
                Now),
            new MeetingAccount(stage.Group, stage.Group, reference, SessionEngineType.Web),
            SessionEngineType.Web,
            options.WebProfile);
        var events = new MeetingLifecycleEvents();
        var joined = new WebJoinedList(() => engine.ActiveMeetingPage);

        // The instructor made co-host by the Windows app's own bridge, woken by every admission.
        SessionRoleBridge? bridge = null;
        if (roles is not null)
        {
            await roles.RefreshAsync(cancellationToken);
            bridge = new SessionRoleBridge(events, _ => joined, new ClassTitle(stage.Title), roles,
                new WebCoHostAssigner(() => engine.ActiveMeetingPage), Log,
                presenters: NoPresenterSource.Instance, autoCoHostOn: () => true);
        }
        await events.PublishAsync(context, MeetingLifecycleEventKind.Active);

        Log($"{stage.Group} is live; admitting until the class is over (at the latest {latest.ToOffset(classStart.Offset):HH:mm})");

        using var holding = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var untilLatest = latest - Now;
        holding.CancelAfter(untilLatest > TimeSpan.FromMinutes(1) ? untilLatest : TimeSpan.FromMinutes(1));

        // Who is there, read beside the admitting and sent to the server, which is where the LMS
        // stages get the names from.
        var reader = attendance is null ? null
            : new MeetingAttendance(token => ReadNamesAsync(joined, token), attendance, stage, Log);
        var collector = reader?.RunAsync(holding.Token) ?? Task.CompletedTask;
        bool lastReadTaken = false;
        async Task LastReadAsync(CancellationToken token)
        {
            if (reader is null || lastReadTaken) return;
            lastReadTaken = true;
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await reader.FinalAsync(limit.Token);
        }

        var ending = new MeetingEnd(
            joined,
            meetingOpen: () => engine.ActiveMeetingPage is { IsClosed: false },
            breakoutRoomsOpen: _ => ZoomWebBreakoutRooms.AreOpenAsync(engine.ActiveMeetingPage),
            endForAll: token => ZoomWebMeetingEnder.EndForAllAsync(engine.ActiveMeetingPage, token),
            classStart, sessionId, Log, now: () => Now,
            beforeEnding: LastReadAsync);

        // The admitting runs inside the session's scope, so every admission wakes the co-host bridge
        // the way it does on Windows.
        var monitor = Task.Run(async () =>
        {
            using var scope = MeetingAdmissionScope.Begin(sessionId, events, context);
            await engine.MonitorAsync(options, holding.Token);
        }, CancellationToken.None);
        var watch = ending.WatchAsync(holding.Token);

        MeetingEndOutcome? outcome = null;
        string? admittingStopped = null;
        try
        {
            var first = await Task.WhenAny(monitor, watch);
            if (first == watch && watch.IsCompletedSuccessfully) outcome = watch.Result;
            else if (first == monitor && monitor.IsFaulted)
                admittingStopped = monitor.Exception!.GetBaseException().Message;
        }
        finally
        {
            await holding.CancelAsync();
            try { await monitor; } catch (Exception) { /* its end is reported below */ }
            try { outcome ??= await watch; } catch (Exception) { }
            try { await collector; } catch (Exception problem) { Log($"attendance stopped badly: {problem.GetType().Name}"); }
            await events.PublishAsync(context, MeetingLifecycleEventKind.Ending);
            if (bridge is not null) await bridge.DisposeAsync();
        }

        bool draining = cancellationToken.IsCancellationRequested;
        try
        {
            if (outcome is null && !draining)
            {
                // The latest the class is held to, or the admitting stopped on its own. Either way the
                // lane's slot has to come back, so the class is ended - once the last read is taken.
                if (engine.ActiveMeetingPage is { IsClosed: false })
                {
                    try { await LastReadAsync(CancellationToken.None); } catch (Exception) { }
                    var (ended, message) = await ZoomWebMeetingEnder.EndForAllAsync(engine.ActiveMeetingPage, CancellationToken.None);
                    string why = admittingStopped is null
                        ? $"it was still going at {latest.ToOffset(classStart.Offset):HH:mm}, {Overrun.TotalHours:0} hours past the rule's three"
                        : $"the admitting stopped ({admittingStopped})";
                    outcome = new(ended ? EndedHow.ByRule : EndedHow.EndFailed,
                        ended ? $"Ended because {why}. {message}" : $"Could not be ended after {why}: {message}");
                    if (ended) LiveMeetings.Finish(sessionId);
                }
                else
                {
                    outcome = new(EndedHow.Elsewhere, "The meeting was closed somewhere else (by the instructor, or from a phone).");
                }
            }
            else if (outcome is { How: EndedHow.Elsewhere })
            {
                // Nothing left to read, but the server is told the class is over.
                lastReadTaken = true;
            }
        }
        finally
        {
            try { await engine.StopAsync(); }
            catch (Exception problem) { Log($"the browser did not close cleanly: {problem.GetType().Name}"); }
        }

        if (reader is not null)
        {
            answer["attended"] = reader.MostSeen;
            answer["rows"] = reader.Sent;
        }

        if (draining)
        {
            // Not ended: a deployment must not close a class people are in. The browser has left and
            // the meeting carries on with its co-host; the job goes back to the queue, and a worker
            // that starts again inside the class's time joins it again as the host.
            throw new OperationCanceledException(cancellationToken);
        }

        answer["endedTheMeeting"] = outcome!.Closed;
        answer["did"] = outcome.How switch
        {
            EndedHow.ByRule => "held the meeting and ended it when the class was over",
            EndedHow.Elsewhere => "held the meeting until it was closed",
            _ => "held the meeting but could not close it",
        };
        answer["message"] = $"{stage.Group}: {outcome.Reason}";
        if (!outcome.Closed)
            // Reported, not only logged: a meeting left open blocks this account's next class, and a
            // class that was never the host's was never admitted from its waiting room either.
            answer["warning"] = outcome.Reason;
        else if (admittingStopped is not null || outcome.Reason.StartsWith("Ended because it was still going", StringComparison.Ordinal))
            answer["warning"] = outcome.Reason;
        return JobOutcome.Success(answer);
    }

    private static async Task<MeetingRead> ReadNamesAsync(WebJoinedList joined, CancellationToken cancellationToken)
    {
        var read = await joined.ReadAsync(cancellationToken);
        return new MeetingRead(read.Participants.Select(p => p.Name).ToList(), read.IsComplete);
    }
}
