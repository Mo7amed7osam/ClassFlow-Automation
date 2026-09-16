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

    private string WithWarning(LmsFollowUp item, string message) =>
        item.Step is LmsFollowUpStep.TakeAttendance or LmsFollowUpStep.CorrectAttendance && _warnings.TryGetValue(ClassKey(item), out var warning)
            ? $"{message} {warning}"
            : message;

    private async Task<IReadOnlyCollection<string>> ZoomReportNamesAsync(LmsFollowUp item, CancellationToken token)
    {
        if (item.Step != LmsFollowUpStep.CorrectAttendance || _isLive(item.Group)) return [];
        try
        {
            var report = await _zoomReport(item, token);
            if (report == null || report.People.Count == 0) return [];
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
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"[LMS] {item.Describe}: Zoom's participants report could not be read ({ex.Message}); the snapshots are used alone.");
            return [];
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
        return await new ZoomAutoAdmit.WebAutomation.Zoom.ZoomParticipantsReportReader().ReadAsync(profile, url, token);
    }

    /// <summary>Whether a meeting of the group is running on this PC now.</summary>
    private readonly Func<string, bool> _isLive;
    /// <summary>How long a step waits before looking again while its meeting runs.</summary>
    public static readonly TimeSpan LiveWait = TimeSpan.FromMinutes(1);

    public LmsFollowUpProcessor(
        LmsFollowUpQueue queue,
        Func<LmsFollowUp, CancellationToken, Task<IReadOnlyCollection<string>>>? presentNames = null,
        RunAction? runAction = null,
        IAttendanceHistoryReader? history = null,
        IGroupRosterService? roster = null,
        IAiMatchingService? matching = null,
        Func<LmsSessionRunner>? runner = null,
        AppAttendanceMatcher? appMatcher = null,
        Func<string, bool>? isLive = null,
        Func<LmsFollowUp, CancellationToken, Task<ZoomAutoAdmit.WebAutomation.Zoom.ZoomParticipantsReport?>>? zoomReport = null)
    {
        var feed = new ExtensionAttendanceFeed();
        _zoomReport = zoomReport ?? ((item, token) => ReadZoomReportAsync(item, feed, token));
        _isLive = isLive ?? (group => ZoomAutoAdmit.Core.Meetings.LiveMeetings.IsLive(group));
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
                ConsoleLogger.Info($"[LMS] {item.Describe}: from the finalized Attendance page - {page.Present.Count} present.");
                return page.Present;
            }
            var app = ExtensionAttendanceFeed.ResultsNear(item.Group, item.SessionDate, item.SessionStart, window, app: true);
            // The late-joiner correction, once the meeting has ended: Zoom's own participants report
            // too - anyone the snapshots missed is added, and anyone there under an hour is warned about.
            var reportNames = await ZoomReportNamesAsync(item, token);
            if (reportNames.Count > 0 || app == null || DateTimeOffset.Now - app.UpdatedAt > TimeSpan.FromMinutes(15))
                app = await matcher.MatchClassAsync(item.Group, item.SessionDate.ToDateTime(item.SessionStart), token, reportNames) ?? app;
            var present = new List<string>(pageFresh ? page!.Present : []);
            foreach (var name in app?.Present ?? [])
                if (!present.Contains(name, StringComparer.OrdinalIgnoreCase)) present.Add(name);
            if (present.Count > 0)
            {
                ConsoleLogger.Info($"[LMS] {item.Describe}: {present.Count} present ({(pageFresh ? $"{page!.Present.Count} from the Attendance page, " : "")}" +
                                   $"{app?.Present.Count ?? 0} from the app's own match); {app?.Review.Count ?? 0} still to review.");
                return present;
            }
            return await FindPresentNamesAsync(item, historyReader, rosterStore, matchingService, token);
        });
        var createRunner = runner ?? (() => new LmsSessionRunner(new LmsCredentialStore()));
        _runAction = runAction ?? (async (item, present, dryRun, token) =>
        {
            var lms = createRunner();
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
            if (item.Step == LmsFollowUpStep.CorrectAttendance)
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
                        IReadOnlyCollection<string> present = item.Step is LmsFollowUpStep.TakeAttendance or LmsFollowUpStep.CorrectAttendance
                            ? await _presentNames(item, token)
                            : [];
                        var outcome = await _runAction(item, present, dryRun, token);
                        outcome = (outcome.IsSuccess, WithWarning(item, outcome.Message));
                        messages.Add(outcome.Message);
                        // Kept for the Sessions page: what each step did, or why it has not yet.
                        if (!dryRun) await _queue.RecordAsync(item, outcome.IsSuccess, outcome.Message, token);
                        if (!outcome.IsSuccess)
                        {
                            if (!dryRun) await _queue.RetryAsync(item, outcome.Message, now ?? DateTimeOffset.Now, token);
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
            try
            {
                IReadOnlyCollection<string> present = step is LmsFollowUpStep.TakeAttendance or LmsFollowUpStep.CorrectAttendance
                    ? await _presentNames(item, token)
                    : [];
                outcome = await _runAction(item, present, false, token);
                outcome = (outcome.IsSuccess, WithWarning(item, outcome.Message));
            }
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
