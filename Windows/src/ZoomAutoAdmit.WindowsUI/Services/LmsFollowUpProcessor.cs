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

    public LmsFollowUpProcessor(
        LmsFollowUpQueue queue,
        Func<LmsFollowUp, CancellationToken, Task<IReadOnlyCollection<string>>>? presentNames = null,
        RunAction? runAction = null,
        IAttendanceHistoryReader? history = null,
        IGroupRosterService? roster = null,
        IAiMatchingService? matching = null,
        Func<LmsSessionRunner>? runner = null)
    {
        _queue = queue;
        var historyReader = history ?? new AttendanceHistoryReader();
        var rosterStore = roster ?? new GroupRosterStore(log: ConsoleLogger.Info);
        var matchingService = matching ?? new AiMatchingService();
        _presentNames = presentNames ?? (async (item, token) =>
        {
            // What the Attendance page (the extension's) shows is what goes to the LMS: its Present
            // list, with every confirmation and correction made there. Needs-review names are not
            // uploaded as present until someone confirms them on the page.
            var page = ExtensionAttendanceFeed.ResultsFor(item.Group, item.SessionDate, item.SessionStart);
            if (page != null && (page.Finalized || DateTimeOffset.Now - page.UpdatedAt < TimeSpan.FromHours(2)) && page.Present.Count > 0)
            {
                ConsoleLogger.Info($"[LMS] {item.Describe}: from the Attendance page - {page.Present.Count} present, {page.Review.Count} still to review, {page.Absent.Count} absent.");
                return page.Present;
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
                    try
                    {
                        IReadOnlyCollection<string> present = item.Step is LmsFollowUpStep.TakeAttendance or LmsFollowUpStep.CorrectAttendance
                            ? await _presentNames(item, token)
                            : [];
                        var outcome = await _runAction(item, present, dryRun, token);
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
            return DateOnly.FromDateTime(scheduled.Date) == item.SessionDate &&
                   Math.Abs((TimeOnly.FromDateTime(scheduled.DateTime).ToTimeSpan() - item.SessionStart.ToTimeSpan()).TotalMinutes) <= 5;
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
