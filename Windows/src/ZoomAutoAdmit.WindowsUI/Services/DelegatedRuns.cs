using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.WebAutomation.Lms;
using ZoomAutoAdmit.WindowsRuntime.Scheduling;

namespace ZoomAutoAdmit.WindowsUI.Services;

/// <summary>
/// Reads one coordinator's own LMS session list, signed in as them. The real one drives the
/// browser; a test hands back rows instead, so everything around it can be exercised without one.
/// </summary>
public delegate Task<IReadOnlyList<LmsSessionRunner.LmsSessionInfo>> ReadTimetable(
    LmsCredentialStore signIn, DateOnly from, DateOnly to, IReadOnlyCollection<string> groups, CancellationToken token);

/// <summary>What one pass over the coordinators the admin runs did, in terms a person can act on.</summary>
public sealed record DelegatedRunReport(
    int Coordinators, int Classes, int Scheduled, int Removed, IReadOnlyList<string> Problems)
{
    public bool HasProblems => Problems.Count > 0;

    public string Summary => Coordinators == 0
        ? "No coordinator is turned on, so this PC runs only its own classes."
        : $"{Coordinators} coordinator(s): {Scheduled} class(es) will open by themselves" +
          $"{(Removed > 0 ? $", {Removed} dropped" : "")}" +
          $"{(Problems.Count > 0 ? $". {Problems.Count} still need something." : ".")}";
}

/// <summary>
/// This PC runs other people's classes as well as its own.
///
/// The admin turns a coordinator on (on the Dashboard, or on the Coordinators page here). From
/// then on this PC holds two things of theirs: their LMS sign-in, kept like any other account in
/// Windows Credential Manager with its own browser profile, and their timetable, which is not typed
/// anywhere - it is read from that coordinator's own LMS session list, signed in as them. A class
/// only needs one thing a person still supplies: the Zoom link it opens, and the Zoom account on
/// this PC that opens it, because the LMS does not publish either.
///
/// Every class then carries whose it is, all the way through: the Zoom account that opens the
/// meeting, and the LMS account that presses Run Session, writes the attendance and puts the
/// recording on it. Two classes at 19:00 belonging to two coordinators do not collide - the first
/// takes the Zoom desktop app and the second the web engine (the allocation policy already does
/// this), each signs in to the LMS with its own browser profile, and each profile has its own lock.
/// Nothing waits on anything else.
///
/// A coordinator turned off loses all of it here at once: their groups stop being theirs, and
/// their classes are taken off this PC's schedule so nothing of theirs opens by accident.
/// </summary>
public sealed class DelegatedRuns
{
    /// <summary>How far back and forward a coordinator's timetable is read.</summary>
    public static readonly int DaysBack = 7, DaysAhead = 21;

    /// <summary>How often a coordinator's LMS session list is read again without being asked.</summary>
    public static readonly TimeSpan ReadTimetableEvery = TimeSpan.FromHours(6);

    /// <summary>How many coordinators' timetables are read at once. Each opens its own browser.</summary>
    public static readonly int AtOnce = 3;

    private readonly IDelegatedRunsApi _api;
    private readonly IWindowsUiService _service;
    private readonly LmsAccountDirectory _directory;
    private readonly ClassLmsAccounts _classes;
    private readonly IZoomProfileCredentialStore _zoomCredentials;
    private readonly ReadTimetable _readTimetable;
    /// <summary>The groups each coordinator's own LMS listed, including ones nobody assigned them here.</summary>
    private readonly Dictionary<string, string[]> _seenOnTheirLms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> _log;
    private readonly Func<DateOnly> _today;
    private readonly Dictionary<string, DateTimeOffset> _lastRead = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DelegatedRuns(
        IDelegatedRunsApi api,
        IWindowsUiService service,
        LmsAccountDirectory? directory = null,
        ClassLmsAccounts? classes = null,
        IZoomProfileCredentialStore? zoomCredentials = null,
        ReadTimetable? readTimetable = null,
        Action<string>? log = null,
        Func<DateOnly>? today = null)
    {
        _api = api;
        _service = service;
        _directory = directory ?? new LmsAccountDirectory();
        _classes = classes ?? new ClassLmsAccounts();
        _zoomCredentials = zoomCredentials ?? new ZoomProfileCredentialStore();
        // The list only: opening every session would take minutes and nothing here needs what is
        // inside one. Each class reads its own session when its time comes.
        _readTimetable = readTimetable ?? ((signIn, from, to, groups, token) =>
            new LmsSessionRunner(signIn).SurveyAsync(from, to, groups, openEach: false, cancellationToken: token));
        _log = log ?? (message => ConsoleLogger.Info($"[RUNS] {message}"));
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.Now));
    }

    /// <summary>Whose classes this PC runs besides its own, as the last pass left it.</summary>
    public IReadOnlyList<ClassLmsAccount> Claimed => _classes.List();

    /// <summary>
    /// One pass: who is turned on, their sign-ins and groups, their timetables, and the classes
    /// that open by themselves. Only the admin's copy of the app has anything to do here.
    /// </summary>
    /// <param name="readTimetables">
    /// Read each coordinator's LMS session list again. Left alone it is read when what is held here
    /// is older than <see cref="ReadTimetableEvery"/>; a person pressing Refresh passes true.
    /// </param>
    public async Task<DelegatedRunReport> SyncAsync(bool readTimetables = false, CancellationToken token = default)
    {
        // One pass at a time: two overlapping passes would fight over the same schedules and
        // browser profiles, which is the very thing this class exists to prevent.
        await _gate.WaitAsync(token);
        try { return await RunOnceAsync(readTimetables, token); }
        finally { _gate.Release(); }
    }

    private async Task<DelegatedRunReport> RunOnceAsync(bool readTimetables, CancellationToken token)
    {
        if (!_api.IsAdmin) return new DelegatedRunReport(0, 0, 0, 0, []);

        var problems = new List<string>();
        var answer = await _api.DelegationsAsync(token);
        var running = answer.Delegations.Where(d => d.Enabled).ToArray();

        foreach (var stopped in answer.Delegations.Where(d => !d.Enabled))
            _classes.Forget(stopped.CoordinatorId);

        var ready = new List<(CentralDelegation Delegation, LmsAccountEntry Entry)>();
        foreach (var delegation in running)
        {
            if (delegation.LmsAccount == null)
            {
                problems.Add($"{delegation.DisplayName}: no LMS sign-in saved on their dashboard account, so their classes cannot go up as theirs.");
                _classes.Forget(delegation.CoordinatorId);
                continue;
            }
            try
            {
                var secret = await _api.CoordinatorLmsSecretAsync(delegation.CoordinatorId, delegation.LmsAccount.Id, token);
                // Never made the account in use: this PC's own sign-in stays the one it falls back to.
                var entry = _directory.Upsert(
                    string.IsNullOrWhiteSpace(secret.Label) ? delegation.DisplayName : secret.Label,
                    secret.Email, secret.Password, secret.Role, makeActive: false);
                // Every group of theirs the app knows of - given here, or seen on their own LMS -
                // so each class goes up under their sign-in and not this PC's.
                _classes.SetGroups(delegation.CoordinatorId, delegation.DisplayName, entry.Id,
                    delegation.GroupList.Where(g => !g.Archived).Select(g => g.Name)
                        .Concat(_seenOnTheirLms.TryGetValue(delegation.CoordinatorId, out var seen) ? seen : [])
                        .Distinct(StringComparer.OrdinalIgnoreCase),
                    delegation.ZoomAccount);
                await KeepTheirZoomSignInAsync(delegation, problems, token);
                ready.Add((delegation, entry));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                problems.Add($"{delegation.DisplayName}: their LMS sign-in could not be read ({CentralApiException.Explain(ex)}).");
            }
        }

        if (ready.Count > 0) await ReadTimetablesAsync(ready, readTimetables, problems, token);
        var (scheduled, removed) = await WriteSchedulesAsync(ready, problems, token);

        var report = new DelegatedRunReport(ready.Count, scheduled + problems.Count, scheduled, removed, problems);
        _log(report.Summary);
        foreach (var problem in problems) _log(problem);
        return report;
    }

    /// <summary>
    /// That coordinator's Zoom sign-in, kept on this PC under the name their account is known by.
    ///
    /// A browser profile nobody has signed in joins as a guest, and a guest cannot admit anybody -
    /// so without this, the first class of theirs opened on a fresh profile stops and waits for a
    /// person. An account they kept no password for is not a failure: the profile may well be
    /// signed in here already, and the class says so if it is not.
    /// </summary>
    private async Task KeepTheirZoomSignInAsync(CentralDelegation delegation, List<string> problems, CancellationToken token)
    {
        foreach (var account in delegation.ZoomAccountList.Where(a => a.HasPassword))
        {
            try
            {
                var secret = await _api.CoordinatorZoomSecretAsync(delegation.CoordinatorId, account.Id, token);
                if (string.IsNullOrEmpty(secret.Password)) continue;
                _zoomCredentials.Save(account.AccountId, secret.Email, secret.Password);
                _log($"{delegation.DisplayName}: their Zoom sign-in for {account.AccountId} is kept on this PC.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                problems.Add($"{delegation.DisplayName}: their Zoom sign-in for {account.AccountId} could not be read " +
                             $"({CentralApiException.Explain(ex)}); a fresh browser profile will wait for a person.");
            }
        }
    }

    // ------------------------------------------------------------------ their own timetables

    /// <summary>
    /// Each coordinator's own LMS session list, read signed in as them and sent to the server as
    /// their plan. They are read side by side: each has its own browser profile, so one person's
    /// read never waits for another's - only the number of browsers at once is held down.
    /// </summary>
    private async Task ReadTimetablesAsync(
        List<(CentralDelegation Delegation, LmsAccountEntry Entry)> ready, bool force, List<string> problems, CancellationToken token)
    {
        var from = _today().AddDays(-DaysBack);
        var to = _today().AddDays(DaysAhead);
        using var atOnce = new SemaphoreSlim(AtOnce, AtOnce);
        var said = new object();

        await Task.WhenAll(ready.Select(async pair =>
        {
            var (delegation, entry) = pair;
            if (!force && _lastRead.TryGetValue(delegation.CoordinatorId, out var last)
                && DateTimeOffset.Now - last < ReadTimetableEvery) return;
            var groups = delegation.GroupList.Where(g => !g.Archived).Select(g => g.Name).ToArray();
            if (groups.Length == 0)
            {
                lock (said) problems.Add($"{delegation.DisplayName}: no groups are assigned to them, so there is no timetable to read.");
                return;
            }

            await atOnce.WaitAsync(token);
            try
            {
                _log($"{delegation.DisplayName}: reading their LMS timetable ({from:yyyy-MM-dd} to {to:yyyy-MM-dd}).");
                // Their own account sees their own classes, so the whole list is read and narrowed
                // here. Narrowing inside the read threw away, without a word, every session of a
                // group the dashboard does not know they have - which is how a class on Hosam's
                // LMS was nowhere in the app (2026-09-23).
                var listed = await _readTimetable(new LmsCredentialStore(entry.Target, entry.Profile), from, to, [], token);
                // A class on their own LMS is theirs, whatever this PC's group list says, so it is
                // run: the dashboard's groups are how the app names things, not who owns a class.
                // A group nobody assigned them simply has no Zoom account of its own yet, and says
                // so until somebody chooses one (2026-09-23: a class of Hosam's was on his LMS and
                // nowhere here).
                var theirs = groups.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var alsoListed = listed
                    .Where(session => session.Date != null && !theirs.Contains(session.Group))
                    .GroupBy(session => session.Group, StringComparer.OrdinalIgnoreCase)
                    .Select(byGroup => $"{byGroup.Key} ({byGroup.Count()})")
                    .ToArray();
                if (alsoListed.Length > 0)
                {
                    string extra = $"{delegation.DisplayName}: their LMS also lists {string.Join(", ", alsoListed)}, " +
                                   "which is not among the groups they were given here. Those classes are run too; " +
                                   "choose the Zoom account that opens them on the Run classes page.";
                    lock (said) problems.Add(extra);
                    _log(extra);
                }
                var rows = listed
                    .Where(session => session.Date != null
                                      && !session.ListStatus.Equals("cancelled", StringComparison.OrdinalIgnoreCase))
                    .Select(session => (object)new
                    {
                        group = session.Group,
                        date = session.Date!.Value.ToString("yyyy-MM-dd"),
                        startTime = session.Start?.ToString("HH\\:mm"),
                        title = string.IsNullOrWhiteSpace(session.Title) ? null : session.Title,
                    })
                    .ToArray();
                if (rows.Length == 0)
                {
                    lock (said) problems.Add($"{delegation.DisplayName}: their LMS listed no session between {from:d MMM} and {to:d MMM}.");
                    return;
                }
                var seenNow = listed.Where(session => session.Date != null).Select(session => session.Group)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                lock (said) _seenOnTheirLms[delegation.CoordinatorId] = seenNow;
                // Written down now, not on the next pass: a class of a group only their LMS knows
                // about still goes up under their own sign-in the first time it runs.
                _classes.SetGroups(delegation.CoordinatorId, delegation.DisplayName, entry.Id,
                    theirs.Concat(seenNow).Distinct(StringComparer.OrdinalIgnoreCase), delegation.ZoomAccount);
                await _api.ImportRunPlanAsync(delegation.CoordinatorId, rows, token);
                _lastRead[delegation.CoordinatorId] = DateTimeOffset.Now;
                _log($"{delegation.DisplayName}: {rows.Length} class(es) read from their LMS.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (said) problems.Add($"{delegation.DisplayName}: their timetable could not be read ({ex.Message}).");
            }
            finally { atOnce.Release(); }
        }));
    }

    // ------------------------------------------------------------------ the classes themselves

    /// <summary>
    /// The plan becomes this PC's schedules: one entry per class, on its exact date, opening with
    /// that coordinator's own Zoom account and carrying their name. A class whose link or Zoom
    /// account is missing is reported instead of being scheduled - it would only fail at its time.
    /// </summary>
    private async Task<(int Scheduled, int Removed)> WriteSchedulesAsync(
        List<(CentralDelegation Delegation, LmsAccountEntry Entry)> ready, List<string> problems, CancellationToken token)
    {
        var all = await _service.GetSchedulesAsync(token);
        var existing = all.Where(s => s.CoordinatorId is { Length: > 0 }).ToList();
        // This PC's own classes. A coordinator's class that is already one of them - the same group
        // at the same time on that day - is not added a second time: two entries would both open,
        // and one class would be joined twice (2026-09-21, S8 at 19:00). Its LMS steps still go up
        // under that coordinator, because those follow the group, not the entry.
        var ownClasses = all.Where(s => s.CoordinatorId is not { Length: > 0 } && s.Enabled).ToList();
        if (ready.Count == 0)
        {
            foreach (var gone in existing) await _service.DeleteScheduleAsync(gone.Id, token);
            return (0, existing.Count);
        }

        // The Zoom accounts on this PC, by the name they are known by here and by the e-mail they
        // sign in with: a coordinator's account can have been added here under a different name, and
        // the e-mail is what actually says which Zoom account it is.
        var here = await _service.GetAccountsAsync(token);
        var byName = here.ToDictionary(a => a.AccountId, a => a.AccountId, StringComparer.OrdinalIgnoreCase);
        var byEmail = here.Where(a => !string.IsNullOrWhiteSpace(a.ZoomEmail))
            .GroupBy(a => a.ZoomEmail!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().AccountId, StringComparer.OrdinalIgnoreCase);
        var plan = await _api.RunPlanAsync(_today().AddDays(-DaysBack), _today().AddDays(DaysAhead),
            ready.Select(r => r.Delegation.CoordinatorId), token);
        var whose = ready.ToDictionary(r => r.Delegation.CoordinatorId, r => r.Delegation, StringComparer.OrdinalIgnoreCase);

        int scheduled = 0;
        var kept = new HashSet<Guid>();
        foreach (var item in plan.ClassList)
        {
            if (item.Status != "planned" || item.Day is not { } day || item.Start is not { } start) continue;
            if (!whose.TryGetValue(item.CoordinatorId, out var delegation)) continue;
            string who = delegation.DisplayName;
            if (ownClasses.Any(s => IsSameClass(s, item.Group, day, start))) { scheduled++; continue; }

            if (string.IsNullOrWhiteSpace(item.MeetingUrl))
            {
                problems.Add($"{who} · {item.Group} {day:ddd d MMM} {start:HH\\:mm}: no Zoom link yet. Add it on the Coordinators page and it carries on to that group's next classes.");
                continue;
            }
            string? wanted = item.ZoomAccount;
            if (string.IsNullOrWhiteSpace(wanted))
            {
                problems.Add($"{who} · {item.Group} {day:ddd d MMM} {start:HH\\:mm}: no Zoom account of theirs is chosen to open it with.");
                continue;
            }
            // Their account as it is known here: by its own name, or by the e-mail their own copy
            // of the app says it signs in to Zoom with.
            string? theirEmail = delegation.ZoomAccountList
                .FirstOrDefault(a => a.AccountId.Equals(wanted, StringComparison.OrdinalIgnoreCase))?.ZoomEmail;
            string? account = byName.GetValueOrDefault(wanted!)
                ?? (theirEmail is { Length: > 0 } mail ? byEmail.GetValueOrDefault(mail) : null);
            if (account == null)
            {
                problems.Add($"{who} · {item.Group}: this PC has no Zoom account called \"{wanted}\"" +
                             $"{(theirEmail is { Length: > 0 } e ? $" and none signed in as {e}" : "")}. " +
                             "Add it on the Accounts page, signed in to Zoom as them.");
                continue;
            }
            if (!Guid.TryParse(item.Id, out var id)) continue;

            var schedule = new MeetingSchedule(
                id,
                $"{item.Group}{(string.IsNullOrWhiteSpace(item.Title) ? "" : " • " + item.Title)}",
                item.MeetingUrl!,
                account!,
                start,
                ScheduleDays.None,
                Enabled: true,
                OccurrenceDate: day,
                GroupName: item.Group,
                PreferredEngine: EngineOf(item.PreferredEngine),
                Coordinator: who,
                CoordinatorId: item.CoordinatorId);

            var before = existing.FirstOrDefault(s => s.Id == id);
            kept.Add(id);
            // Only what changed: saving registers a Windows task again, and a class that already
            // opened today keeps that mark rather than being opened a second time.
            if (before != null) schedule = schedule with { LastTriggeredDate = before.LastTriggeredDate };
            if (before == schedule) { scheduled++; continue; }
            try
            {
                await _service.SaveScheduleAsync(schedule, token);
                scheduled++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                problems.Add($"{who} · {item.Group} {day:ddd d MMM}: could not be put on this PC's schedule ({ex.Message}).");
            }
        }

        int removed = 0;
        foreach (var gone in existing.Where(s => !kept.Contains(s.Id)))
        {
            if (await _service.DeleteScheduleAsync(gone.Id, token)) removed++;
        }
        return (scheduled, removed);
    }

    /// <summary>The same class: the same group, at the same time, on that day (once, or every week).</summary>
    internal static bool IsSameClass(MeetingSchedule schedule, string group, DateOnly day, TimeOnly start) =>
        string.Equals(schedule.GroupName ?? schedule.AccountId, group, StringComparison.OrdinalIgnoreCase)
        && schedule.Time.Hour == start.Hour && schedule.Time.Minute == start.Minute
        && (schedule.OccurrenceDate is { } once ? once == day : schedule.Days.Includes(day.DayOfWeek));

    private static ZoomAutoAdmit.Core.Sessions.SessionEngineType? EngineOf(string? preferred) => preferred switch
    {
        "desktop" => ZoomAutoAdmit.Core.Sessions.SessionEngineType.Desktop,
        "web" => ZoomAutoAdmit.Core.Sessions.SessionEngineType.Web,
        _ => null,
    };
}
