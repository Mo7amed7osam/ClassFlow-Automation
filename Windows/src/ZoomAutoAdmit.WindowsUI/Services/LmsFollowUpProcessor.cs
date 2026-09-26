using ZoomAutoAdmit.AttendanceMatching;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Roster;
using ZoomAutoAdmit.WebAutomation.Lms;

namespace ZoomAutoAdmit.WindowsUI.Services;

/// <summary>
/// Consumes the durable LMS follow-up queue. Attendance names come from the existing snapshots
/// and matching engine; this class does not create a second roster or matching path.
/// </summary>
public sealed class LmsFollowUpProcessor
{
    public delegate Task<(bool IsSuccess, string Message)> RunAction(
        LmsFollowUp item, IReadOnlyCollection<string> present, bool dryRun, CancellationToken token);

    /// <summary>
    /// A step that has gone wrong often enough to be worth somebody's attention: the class, what
    /// failed, and how many tries it has had. Raised once per step per run of the app, so a class
    /// that keeps retrying says so once rather than every few minutes.
    /// </summary>
    public static event Action<LmsFollowUp, string, int>? Stuck;

    /// <summary>After this many failed tries a step is not going to fix itself.</summary>
    public const int TriesBeforeSaying = 3;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Said = new();

    private static void SayStuck(LmsFollowUp item, string why, int attempts)
    {
        if (attempts < TriesBeforeSaying || Stuck == null) return;
        if (!Said.TryAdd(item.Describe, 0)) return;
        try { Stuck(item, why, attempts); } catch { }
    }

    private readonly LmsFollowUpQueue _queue;
    private readonly Func<LmsFollowUp, CancellationToken, Task<IReadOnlyCollection<string>>> _presentNames;
    private readonly RunAction _runAction;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly Func<LmsFollowUp, CancellationToken, Task<ZoomAutoAdmit.WebAutomation.Zoom.ZoomParticipantsReport?>> _zoomReport;
    /// <summary>Less than this in the meeting (all joins added up) is warned about on the class.</summary>
    public static readonly TimeSpan ShortStay = TimeSpan.FromHours(1);
    /// <summary>The warning for each class, added to its step's outcome (shown on the Sessions card).</summary>
    private readonly Dictionary<string, string> _warnings = new(StringComparer.OrdinalIgnoreCase);

    private static string ClassKey(LmsFollowUp item) => $"{item.Group}|{item.SessionDate:yyyy-MM-dd}|{item.SessionStart:HH\\:mm}";

    /// <summary>
    /// What Zoom's own report said for each class, kept on the correction's outcome so the class card
    /// shows that the report was read - "Zoom report: 26 people, ended 21:14" - not only when
    /// somebody stayed under an hour.
    /// </summary>
    private readonly Dictionary<string, string> _reportNotes = new(StringComparer.OrdinalIgnoreCase);

    private string WithWarning(LmsFollowUp item, string message)
    {
        if (item.Step != LmsFollowUpStep.ZoomReportAttendance) return message;
        if (_reportNotes.TryGetValue(ClassKey(item), out var note)) message = $"{note} {message}";
        return _warnings.TryGetValue(ClassKey(item), out var warning) ? $"{message} {warning}" : message;
    }

    /// <summary>The steps that upload names, and so match them first.</summary>
    private static bool UploadsNames(LmsFollowUpStep step) =>
        step is LmsFollowUpStep.TakeAttendance or LmsFollowUpStep.CorrectAttendance or LmsFollowUpStep.ZoomReportAttendance;

    /// <summary>How long one LMS step may run before it is counted as failed and tried again.</summary>
    public static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(12);

    /// <summary>
    /// The names as they go to the LMS: one entry per student, whatever spelling each list used.
    /// Two lists put together - the Attendance page, the app's own match, Zoom's report - can hold
    /// the same person twice, and a class of 23 that reports 25 present is one nobody trusts again.
    /// </summary>
    private static IReadOnlyCollection<string> OnePerName(IEnumerable<string> names)
    {
        var kept = new List<string>();
        foreach (string name in names)
        {
            string clean = ZoomAutoAdmit.WebAutomation.Zoom.ZoomParticipantsReportReader.SameName(name);
            if (clean.Length == 0 || kept.Contains(clean, StringComparer.OrdinalIgnoreCase)) continue;
            kept.Add(clean);
        }
        return kept;
    }

    private async Task<IReadOnlyCollection<string>> ZoomReportNamesAsync(LmsFollowUp item, CancellationToken token)
    {
        if (item.Step != LmsFollowUpStep.ZoomReportAttendance || _isLive(item.Group)) return [];
        try
        {
            var report = await _zoomReport(item, token);
            if (report == null || report.People.Count == 0) { _reportNotes.Remove(ClassKey(item)); return []; }
            _reportNotes[ClassKey(item)] = $"Zoom report: {report.People.Count} people" +
                $"{(report.EndedAt is { } ended ? $", meeting ended {ended.LocalDateTime:HH:mm}" : "")}.";
            // A meeting in Zoom's usage report has ended: the class card's "Ended" step says so, even
            // when nothing on this PC saw it close (closed from a phone, or the app was not running).
            new ZoomAutoAdmit.Core.Meetings.ClassEndings().Record(new ZoomAutoAdmit.Core.Meetings.ClassEnding
            {
                Group = item.Group, Date = item.SessionDate, Start = item.SessionStart, At = report.EndedAt ?? DateTimeOffset.Now,
                How = ZoomAutoAdmit.Core.Meetings.ClassEndedHow.Elsewhere,
                Message = $"Zoom's usage report shows the meeting ended at {(report.EndedAt ?? DateTimeOffset.Now).LocalDateTime:HH:mm}.",
            });
            var shortStays = report.People.Where(person => person.Minutes < ShortStay.TotalMinutes).ToArray();
            if (shortStays.Length > 0)
                _warnings[ClassKey(item)] = "Warning - less than an hour in the meeting (Zoom report): " +
                    string.Join(", ", shortStays.OrderBy(person => person.Minutes).Select(person => $"{person.Name} ({person.Minutes} min)")) + ".";
            else _warnings.Remove(ClassKey(item));
            ConsoleLogger.Info($"[LMS] {item.Describe}: Zoom's participants report - {report.People.Count} people in {report.Instances} run(s)" +
                               $"{(shortStays.Length > 0 ? $", {shortStays.Length} under an hour" : "")}.");
            return [.. report.People.Select(person => person.Name)];
        }
        catch (OperationCanceledException) { throw; }
        catch (MeetingNotInReportException) when (DateTime.Now < item.SessionDate.ToDateTime(item.SessionStart) + WaitForReportUntil)
        {
            throw;       // the meeting is still going somewhere: the correction waits (see ProcessDueAsync)
        }
        catch (Exception ex) when (IsNetworkDown(ex) && DateTime.Now < item.SessionDate.ToDateTime(item.SessionStart) + WaitForReportUntil)
        {
            // The PC could not reach Zoom at all (2026-09-25: net::ERR_NAME_NOT_RESOLVED). That is
            // not a report with nobody in it: the step waits and reads it again once the network is back.
            string why = ex.Message.Split((char)10)[0].Trim();
            ConsoleLogger.Warn($"[LMS] {item.Describe}: Zoom could not be reached ({why}); the report is read again in a minute.");
            throw new MeetingNotInReportException($"Zoom could not be reached ({why}); the report is read again when the network is back.");
        }
        catch (Exception ex)
        {
            _reportNotes[ClassKey(item)] = $"Zoom report not read ({ex.Message}); the snapshots were used alone.";
            ConsoleLogger.Warn($"[LMS] {item.Describe}: Zoom's participants report could not be read ({ex.Message}); the snapshots are used alone.");
            return [];
        }
    }

    /// <summary>
    /// One step, given <see cref="StepTimeout"/>: a browser that never answers must not hold every
    /// later step of every class behind it. Past the time it is a failure like any other, and tried again.
    /// </summary>
    private async Task<(bool IsSuccess, string Message)> RunStepAsync(LmsFollowUp item, bool dryRun, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(StepTimeout);
        try
        {
            IReadOnlyCollection<string> present = UploadsNames(item.Step) ? await _presentNames(item, limit.Token) : [];
            var outcome = await _runAction(item, present, dryRun, limit.Token);
            return (outcome.IsSuccess, WithWarning(item, outcome.Message));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return (false, $"{item.Describe}: took longer than {StepTimeout.TotalMinutes:0} minutes and was stopped; it will be tried again.");
        }
    }

    /// <summary>The class's Zoom link (from its recordings) and the group's own web profile, then the report.</summary>
    private static async Task<ZoomAutoAdmit.WebAutomation.Zoom.ZoomParticipantsReport?> ReadZoomReportAsync(
        LmsFollowUp item, ExtensionAttendanceFeed feed, CancellationToken token)
    {
        var classStart = item.SessionDate.ToDateTime(item.SessionStart);
        // Zoom keeps the report, but only a class of the last day is read automatically.
        if (DateTime.Now - classStart > TimeSpan.FromHours(24)) return null;
        string? url = feed.Since(classStart.AddHours(-2))
            .Where(s => s.Group.Equals(item.Group, StringComparison.OrdinalIgnoreCase) && s.Start.Date == classStart.Date &&
                        ZoomAutoAdmit.WindowsRuntime.Scheduling.ScheduleTiming.IsSameClass(classStart.TimeOfDay, s.Start.TimeOfDay))
            .OrderByDescending(s => s.At)
            .Select(s => s.MeetingUrl)
            .FirstOrDefault(link => ZoomAutoAdmit.WebAutomation.Zoom.ZoomParticipantsReportReader.MeetingNumber(link) != null);
        if (url == null) return null;
        var accounts = await new ZoomAutoAdmit.WindowsRuntime.WindowsMeetingAccountManager().ListConfiguredAsync(token);
        string profile = accounts.FirstOrDefault(account => account.AccountId.Equals(item.Group, StringComparison.OrdinalIgnoreCase))?.WebProfileName
                         ?? item.Group;
        await using var held = await new ZoomAutoAdmit.WebAutomation.Recordings.ProfileOperationLock()
            .TryAcquireAsync(profile, TimeSpan.FromMinutes(2), token);
        if (held == null) throw new InvalidOperationException($"the '{profile}' browser profile is busy");
        var report = await new ZoomAutoAdmit.WebAutomation.Zoom.ZoomParticipantsReportReader().ReadAsync(profile, url, classStart, token, accountId: item.Group);
        // Zoom lists a meeting only once it has ended: nothing for the class yet means it is still going.
        return report ?? throw new MeetingNotInReportException();
    }

    /// <summary>How long after the class's time the correction waits for its meeting to appear in Zoom's report.</summary>
    public static readonly TimeSpan WaitForReportUntil = TimeSpan.FromHours(6);

    /// <summary>Zoom's report has no run of the class's meeting yet: it has not ended.</summary>
    public sealed class MeetingNotInReportException(string? message = null)
        : Exception(message ?? "Zoom's usage report does not list the meeting yet, so it has not ended.");

    /// <summary>The browser could not reach Zoom: no network, no DNS, the connection dropped.</summary>
    public static bool IsNetworkDown(Exception ex) =>
        ex.Message.Contains("net::ERR_", StringComparison.OrdinalIgnoreCase)
        && !ex.Message.Contains("ERR_ABORTED", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a meeting of the group is running on this PC now.</summary>
    private readonly Func<string, bool> _isLive;
    /// <summary>Whether a step's class is held in a room (the schedule first, then the LMS's type).</summary>
    private readonly Func<LmsFollowUp, CancellationToken, Task<bool>> _isPhysical;
    /// <summary>How long a step waits before looking again while its meeting runs.</summary>
    public static readonly TimeSpan LiveWait = TimeSpan.FromMinutes(1);

    public LmsFollowUpProcessor(
        LmsFollowUpQueue queue,
        Func<LmsFollowUp, CancellationToken, Task<IReadOnlyCollection<string>>>? presentNames = null,
        RunAction? runAction = null,
        IAttendanceHistoryReader? history = null,
        IGroupRosterService? roster = null,
        IAiMatchingService? matching = null,
        Func<string?, LmsSessionRunner>? runner = null,
        AppAttendanceMatcher? appMatcher = null,
        Func<string, bool>? isLive = null,
        Func<LmsFollowUp, CancellationToken, Task<ZoomAutoAdmit.WebAutomation.Zoom.ZoomParticipantsReport?>>? zoomReport = null,
        Func<LmsFollowUp, CancellationToken, Task<bool>>? isPhysical = null)
    {
        var feed = new ExtensionAttendanceFeed();
        _zoomReport = zoomReport ?? ((item, token) => ReadZoomReportAsync(item, feed, token));
        _isLive = isLive ?? (group => ZoomAutoAdmit.Core.Meetings.LiveMeetings.IsLive(group));
        _isPhysical = isPhysical ?? ((item, token) =>
            ZoomAutoAdmit.WindowsRuntime.Scheduling.ClassMode.IsPhysicalAsync(item.Group, item.SessionDate, item.SessionStart, token));
        _queue = queue;
        var historyReader = history ?? new AttendanceHistoryReader();
        var rosterStore = roster ?? new GroupRosterStore(log: ConsoleLogger.Info);
        var matchingService = matching ?? new AiMatchingService();
        var matcher = appMatcher ?? new AppAttendanceMatcher(rosters: rosterStore, matching: matchingService);
        _presentNames = presentNames ?? (async (item, token) =>
        {
            // A class reopened while it runs (20:32 for the 19:00 class) is still that class.
            var window = ZoomAutoAdmit.WindowsRuntime.Scheduling.ScheduleTiming.ClassRunsFor;
            // What the Attendance page shows (with every confirmation made there) and what the app
            // matched by itself (the name rules, then the AI). A page someone finalized is taken as
            // it is; otherwise both lists together. Needs-review names are never uploaded as present.
            var page = ExtensionAttendanceFeed.ResultsNear(item.Group, item.SessionDate, item.SessionStart, window, app: false);
            bool pageFresh = page != null && (page.Finalized || DateTimeOffset.Now - page.UpdatedAt < TimeSpan.FromHours(2)) && page.Present.Count > 0;
            if (pageFresh && page!.Finalized)
            {
                var finalized = OnePerName(page.Present);
                ConsoleLogger.Info($"[LMS] {item.Describe}: from the finalized Attendance page - {finalized.Count} present.");
                return finalized;
            }
            var app = ExtensionAttendanceFeed.ResultsNear(item.Group, item.SessionDate, item.SessionStart, window, app: true);
            // The late-joiner correction, once the meeting has ended: Zoom's own participants report
            // too - anyone the snapshots missed is added, and anyone there under an hour is warned about.
            var reportNames = await ZoomReportNamesAsync(item, token);
            // Matched again every single time, just before it goes up: names seen after the last
            // match (a late joiner, a renamed person, Zoom's own report) are in it, and the answers
            // given before cost nothing to use again.
            app = await matcher.MatchClassAsync(item.Group, item.SessionDate.ToDateTime(item.SessionStart), token, reportNames) ?? app;
            var present = OnePerName([.. pageFresh ? page!.Present : [], .. app?.Present ?? []]);
            if (present.Count > 0)
            {
                ConsoleLogger.Info($"[LMS] {item.Describe}: {present.Count} present ({(pageFresh ? $"{page!.Present.Count} from the Attendance page, " : "")}" +
                                   $"{app?.Present.Count ?? 0} from the app's own match); {app?.Review.Count ?? 0} still to review.");
                return present;
            }
            return OnePerName(await FindPresentNamesAsync(item, historyReader, rosterStore, matchingService, token));
        });
        // Each class signs in as its own group's coordinator, so two people's steps can run one
        // after the other (or at the same time, on their own browser profiles) and each lands
        // on the LMS under the right name.
        var classes = new ClassLmsAccounts();
        var createRunner = runner ?? (group => new LmsSessionRunner(classes.StoreFor(group)));
        _runAction = runAction ?? (async (item, present, dryRun, token) =>
        {
            var lms = createRunner(item.Group);
            if (item.Step == LmsFollowUpStep.TakeAttendance)
            {
                var result = await lms.TakeAttendanceAsync(item.Group, present, item.SessionStart,
                    item.SessionDate, dryRun: dryRun, cancellationToken: token);
                if (result.IsSuccess || !result.Message.Contains("offers no Take Session Attendance", StringComparison.OrdinalIgnoreCase))
                    return (result.IsSuccess, result.Message);
                // No button because the sheet is already there (taken by hand, or before a restart):
                // check that sheet against this class instead of failing on it forever.
                var check = await lms.CorrectAttendanceAsync(item.Group, present, item.SessionStart,
                    item.SessionDate, dryRun: dryRun, cancellationToken: token);
                return (check.IsSuccess, check.IsSuccess
                    ? $"Attendance was already on the LMS; checked it against this class. {check.Message}"
                    : result.Message);
            }
            if (item.Step is LmsFollowUpStep.CorrectAttendance or LmsFollowUpStep.ZoomReportAttendance)
            {
                var result = await lms.CorrectAttendanceAsync(item.Group, present, item.SessionStart,
                    item.SessionDate, dryRun: dryRun, cancellationToken: token);
                return (result.IsSuccess, result.Message);
            }
            if (item.Step == LmsFollowUpStep.AttachZoomRecording)
            {
                // My Recordings of the group's own Zoom profile, then Add Record Link. A session that
                // already has a link (the Drive one, arrived first) is left as it is.
                var recording = await ZoomAutoAdmit.Inspector.Runtime.RecordingWorkflow.Create(ConsoleLogger.Info)
                    .ProcessAsync(new ZoomAutoAdmit.WebAutomation.Recordings.RecordingLinkRequest
                    {
                        Group = item.Group, Date = item.SessionDate, StartTime = item.SessionStart, DryRun = dryRun, ReplaceExisting = false,
                    }, token);
                return (recording.IsSuccess, $"{item.Group}: {recording.Message}");
            }
            var complete = await lms.CompleteSessionAsync(item.Group, item.SessionStart,
                item.SessionDate, dryRun: dryRun, cancellationToken: token);
            return (complete.IsSuccess, complete.Message);
        });
    }

    public async Task<IReadOnlyList<string>> ProcessDueAsync(
        DateTimeOffset? now = null, bool dryRun = false, CancellationToken token = default)
    {
        if (!await _gate.WaitAsync(0, token)) return [];
        try
        {
            List<string> messages = [];
            var due = await _queue.DueAsync(now ?? DateTimeOffset.Now, token);
            foreach (var session in due.GroupBy(item => $"{item.Group}|{item.SessionDate}|{item.SessionStart}",
                         StringComparer.OrdinalIgnoreCase))
            {
                // Attendance, correction and Complete are a chain: a step waits for the one before.
                // The Zoom recording is not part of it - it only needs the session finished.
                bool chainBlocked = false;
                foreach (var item in session.OrderBy(item => item.Step))
                {
                    if (chainBlocked && item.Step != LmsFollowUpStep.AttachZoomRecording) continue;
                    // Written down under a whole LMS row read as one word, not a group: nothing to do.
                    if (!LmsSessionCache.IsGroupCode(item.Group))
                    {
                        if (!dryRun) await _queue.CompleteAsync(item, token);
                        continue;
                    }
                    // A physical class has no meeting to read attendance from: what was written
                    // down for it as an online class (a meeting opened by hand) is not owed.
                    if (LmsFollowUpQueue.IsFromZoom(item.Step) && await _isPhysical(item, token))
                    {
                        const string inRoom = "A physical session: attendance is taken in the room, not from Zoom.";
                        messages.Add($"{item.Describe}: {inRoom}");
                        if (!dryRun)
                        {
                            await _queue.RecordAsync(item, true, inRoom, token);
                            await _queue.CompleteAsync(item, token);
                        }
                        continue;
                    }
                    // The late-joiner correction, Complete and the recording wait while the class's
                    // meeting is still running on this PC: attendance is taken until the meeting closes.
                    if (item.Step != LmsFollowUpStep.TakeAttendance && _isLive(item.Group))
                    {
                        if (!dryRun) await _queue.PostponeAsync(item, (now ?? DateTimeOffset.Now) + LiveWait,
                            "The meeting is still running; this waits until it ends.", token);
                        chainBlocked = true;
                        continue;
                    }
                    try
                    {
                        (bool IsSuccess, string Message) outcome;
                        try { outcome = await RunStepAsync(item, dryRun, token); }
                        catch (MeetingNotInReportException ex)
                        {
                            if (!dryRun) await _queue.PostponeAsync(item, (now ?? DateTimeOffset.Now) + LiveWait, ex.Message, token);
                            chainBlocked = true;
                            continue;
                        }
                        messages.Add(outcome.Message);
                        // Kept for the Sessions page: what each step did, or why it has not yet.
                        if (!dryRun) await _queue.RecordAsync(item, outcome.IsSuccess, outcome.Message, token);
                        if (!outcome.IsSuccess)
                        {
                            if (!dryRun)
                            {
                                await _queue.RetryAsync(item, outcome.Message, now ?? DateTimeOffset.Now, token);
                                SayStuck(item, outcome.Message, item.Attempts + 1);
                            }
                            chainBlocked = true; // Correction must succeed before this session is completed.
                            continue;
                        }
                        if (!dryRun) await _queue.CompleteAsync(item, token);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        string message = $"{item.Describe}: {ex.Message}";
                        messages.Add(message);
                        if (!dryRun)
                        {
                            await _queue.RecordAsync(item, false, message, token);
                            await _queue.RetryAsync(item, message, now ?? DateTimeOffset.Now, token);
                            SayStuck(item, message, item.Attempts + 1);
                        }
                        chainBlocked = true;
                    }
                }
            }
            return messages;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// One step of one class, now, because someone pressed it on the Sessions page (the app was
    /// closed when it was due, say). It waits for any automatic work in progress, then does exactly
    /// what the queue would have done, writes the outcome down, and clears that step from the queue
    /// when it succeeded. Nothing else of the class is touched.
    /// </summary>
    public async Task<(bool IsSuccess, string Message)> RunNowAsync(string group, DateOnly date, TimeOnly start,
        LmsFollowUpStep step, CancellationToken token = default)
    {
        var item = new LmsFollowUp
        {
            Id = $"{group.Trim()}|{date:yyyy-MM-dd}|{start:HH\\:mm}|{step}",
            Group = group.Trim(), SessionDate = date, SessionStart = start, Step = step, DueAt = DateTimeOffset.Now,
        };
        if (!await _gate.WaitAsync(TimeSpan.FromMinutes(10), token))
            return (false, "The app is still busy with another LMS step; try again in a few minutes.");
        try
        {
            (bool IsSuccess, string Message) outcome;
            try { outcome = await RunStepAsync(item, false, token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { outcome = (false, $"{item.Describe}: {ex.Message}"); }
            var owed = (await _queue.ReadAsync(token)).FirstOrDefault(existing => existing.Id == item.Id);
            await _queue.RecordAsync(owed ?? item, outcome.IsSuccess, outcome.Message, token);
            if (outcome.IsSuccess && owed != null) await _queue.CompleteAsync(owed, token);
            ConsoleLogger.Info($"[LMS] By hand: {item.Describe}: {outcome.Message}");
            return outcome;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Writes a manual step's outcome (Run Session, a pasted link) into the same history.</summary>
    public Task RecordManualAsync(string group, DateOnly date, TimeOnly start, LmsFollowUpStep step, bool ok, string message,
        CancellationToken token = default) =>
        _queue.RecordAsync(new LmsFollowUp
        {
            Id = $"{group.Trim()}|{date:yyyy-MM-dd}|{start:HH\\:mm}|{step}",
            Group = group.Trim(), SessionDate = date, SessionStart = start, Step = step, DueAt = DateTimeOffset.Now,
        }, ok, message, token);

    /// <summary>Held while a manual step that is not in the queue (Run Session, a pasted link) runs.</summary>
    public async Task<T> ExclusiveAsync<T>(Func<Task<T>> work, CancellationToken token = default)
    {
        if (!await _gate.WaitAsync(TimeSpan.FromMinutes(10), token))
            throw new InvalidOperationException("The app is still busy with another LMS step; try again in a few minutes.");
        try { return await work(); }
        finally { _gate.Release(); }
    }

    private static async Task<IReadOnlyCollection<string>> FindPresentNamesAsync(
        LmsFollowUp item,
        IAttendanceHistoryReader history,
        IGroupRosterService roster,
        IAiMatchingService matching,
        CancellationToken token)
    {
        var attendance = await history.ReadAsync(token);
        var snapshots = attendance.Snapshots.Select(value => value.Snapshot).Where(snapshot =>
        {
            if (snapshot.Meeting is not { } meeting ||
                !meeting.AccountId.Equals(item.Group, StringComparison.OrdinalIgnoreCase)) return false;
            var scheduled = meeting.ScheduledStart.ToLocalTime();
            // A class opened by hand is recorded at the minute it went live (18:51 for the 19:00 class).
            return DateOnly.FromDateTime(scheduled.Date) == item.SessionDate &&
                   ZoomAutoAdmit.WindowsRuntime.Scheduling.ScheduleTiming.IsSameClass(
                       item.SessionStart.ToTimeSpan(), TimeOnly.FromDateTime(scheduled.DateTime).ToTimeSpan());
        }).ToArray();
        if (snapshots.Length == 0)
            throw new InvalidOperationException("No attendance snapshot exists for this group and scheduled session.");

        var groups = await roster.ListAsync(token);
        var group = groups.SingleOrDefault(value => value.GroupId.Equals(item.Group, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Roster group {item.Group} was not found.");
        var observed = snapshots.SelectMany(snapshot => snapshot.Participants)
            .Select(participant => participant.Name.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var result = await matching.MatchWithRulesOnlyAsync(group, observed, token);
        return result.Students.Where(student => student.Status == AttendanceMatchStatus.Present)
            .OrderBy(student => student.Order).Select(student => student.StudentName).ToArray();
    }
}
