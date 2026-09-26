using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Meetings;
using ZoomAutoAdmit.WebAutomation.Lms;
using ZoomAutoAdmit.WebAutomation.Recordings;
using ZoomAutoAdmit.WindowsRuntime.Scheduling;
using ZoomAutoAdmit.WindowsUI.Infrastructure;
using ZoomAutoAdmit.WindowsUI.Services;

namespace ZoomAutoAdmit.WindowsUI.ViewModels;

/// <summary>One class (group, day, time) and where each part of its cycle stands.</summary>
public sealed record ClassRow(
    DateOnly Date, TimeOnly Start, string Group, string Title,
    string Zoom, string Lms, string RunSession, string Attendance, string Correction, string Complete, string Link,
    string NextStep, string Tone, string Details)
{
    public string When => $"{Date:ddd dd MMM} · {Start:HH\\:mm}";
    public string Key => $"{Group}|{Date:yyyy-MM-dd}|{Start:HH\\:mm}";
    /// <summary>The cycle as states the Sessions page draws: zoom, run, attendance, correct, complete, record.</summary>
    public IReadOnlyList<StepState> Steps { get; init; } = [];
    public string LmsStatus { get; init; } = "";
    public string? LmsReadAt { get; init; }
    public string? LmsUrl { get; init; }
    public string LinkKind { get; init; } = "";
    /// <summary>The Zoom account this class opens with, as this PC knows it.</summary>
    public string ZoomAccount { get; init; } = "";
    /// <summary>The LMS sign-in its steps go up under, and whose class it is when it is not this PC's own.</summary>
    public string LmsAccount { get; init; } = "";
    public string Coordinator { get; init; } = "";
    /// <summary>Its meeting is running on this PC right now.</summary>
    public bool Live { get; init; }
    /// <summary>"Physical", "Online", or empty when neither the schedule nor the LMS says.</summary>
    public string Mode { get; init; } = "";
    /// <summary>Held in a room: no Zoom meeting, and no attendance from Zoom.</summary>
    public bool Physical => Mode == ClassMode.Physical;
    /// <summary>What the LMS says the class is about: "Technical", "Freelancing", "Coaching"...</summary>
    public string Focus { get; init; } = "";
    /// <summary>Students the match is unsure about, for a person to say yes or no to.</summary>
    public IReadOnlyList<ExtensionAttendanceFeed.ReviewName> Attention { get; init; } = [];
    public string? RecordLink { get; init; }
    /// <summary>The class's material and assignment, as the Sessions page shows and asks about them.</summary>
    public MaterialInfo? Material { get; init; }
}

/// <summary>
/// A class's material for the page: its track and number, the files that go up, and its assignment
/// (title and deadline, "yyyy-MM-ddTHH:mm" local).
/// </summary>
public sealed record MaterialInfo(
    string Track, int? Number, string? Folder, IReadOnlyList<string> Files, IReadOnlyList<string> Skipped,
    bool Technical, bool Fixed, string Note, string? AssignmentTitle, string? Deadline, bool NoAssignment, bool Done)
{
    public string? Description { get; init; }
    /// <summary>The assignment's own file, when one was chosen for it.</summary>
    public string? AssignmentFile { get; init; }
    /// <summary>The folder or file chosen for this class by hand, if any.</summary>
    public string? Chosen { get; init; }
    /// <summary>The LMS showed some of it removed since the app put it there.</summary>
    public bool RemovedOnLms { get; init; }
    /// <summary>When the session's own page (its attachments and assignment) was last read; null: never.</summary>
    public string? SeenOnLms { get; init; }
}

/// <summary>
/// One part of a class's cycle. State is done, lms (seen done on the LMS), due, retry, failed,
/// future or none; Text is what the step shows, Detail the last message about it.
/// </summary>
public sealed record StepState(string Key, string Label, string State, string Text, string? Detail = null);

/// <summary>
/// The Sessions page: every class of the last two weeks and the next week, with the whole cycle on
/// one line - opened on Zoom, running on the LMS, attendance, the late-joiner pass, Complete, the
/// record link (Zoom, then Drive) - and what is still owed. Read from the schedules, the follow-up
/// queue and its history on this PC, and from the LMS itself (Check LMS). Also where the LMS
/// account the app signs in with is chosen.
/// </summary>
public sealed class LmsSessionsViewModel : ObservableObject
{
    private readonly WindowsMeetingScheduleStore _schedules;
    private readonly LmsFollowUpQueue _queue;
    private readonly LmsSessionCache _cache;
    private readonly LmsAccountDirectory _accounts;
    private readonly Func<LmsSessionRunner> _runner;
    /// <summary>The runner a group's classes are read with: signed in as that group's coordinator.</summary>
    private readonly Func<string?, LmsSessionRunner> _runnerFor;
    /// <summary>Which group belongs to which coordinator, and the LMS account its classes go up under.</summary>
    private readonly ClassLmsAccounts _classAccounts = new();
    /// <summary>When each class's meeting ended, and how - including one closed from a phone.</summary>
    private readonly ClassEndings _endings;
    private string _status = "";
    private bool _isBusy;
    private LmsAccountEntry? _selectedAccount;
    private string _newLabel = "", _newEmail = "", _newRole = "coordinator";
    private DateTimeOffset _lastAutoCheck = DateTimeOffset.MinValue;

    /// <summary>
    /// Opens a class's Zoom meeting now, the way its schedule would have: given the account, the
    /// link and what it opens with, it answers what happened. Set by the window; without it the
    /// card says so rather than pretending it pressed anything.
    /// </summary>
    public Func<string, string, ZoomAutoAdmit.Core.Sessions.SessionEngineType?, CancellationToken, Task<string>>? OpenMeeting { get; set; }

    /// <summary>
    /// Stops whatever this PC is running for a group, so the class can be opened afresh. Answers how
    /// many were stopped. Set by the window; without it a class that is already open is left alone.
    /// </summary>
    public Func<string, CancellationToken, Task<int>>? StopMeetingsOf { get; set; }

    public LmsSessionsViewModel(WindowsMeetingScheduleStore? schedules = null, LmsFollowUpQueue? queue = null,
        LmsSessionCache? cache = null, LmsAccountDirectory? accounts = null, Func<LmsSessionRunner>? runner = null,
        ClassEndings? endings = null)
    {
        _endings = endings ?? new ClassEndings();
        _schedules = schedules ?? new WindowsMeetingScheduleStore();
        _queue = queue ?? new LmsFollowUpQueue();
        _cache = cache ?? new LmsSessionCache();
        _accounts = accounts ?? new LmsAccountDirectory();
        _runner = runner ?? (() => new LmsSessionRunner(new LmsCredentialStore()));
        _runnerFor = runner != null ? _ => runner() : group => new LmsSessionRunner(_classAccounts.StoreFor(group));
        RefreshCommand = new AsyncRelayCommand(_ => ReloadAsync());
        CheckLmsCommand = new AsyncRelayCommand(_ => CheckLmsAsync(full: false));
        FullCheckCommand = new AsyncRelayCommand(_ => CheckLmsAsync(full: true));
        UseAccountCommand = new RelayCommand(_ => UseAccount());
        RemoveAccountCommand = new RelayCommand(_ => RemoveAccount());
        LoadAccounts();
    }

    public ObservableCollection<ClassRow> Rows { get; } = [];
    public ObservableCollection<LmsAccountEntry> Accounts { get; } = [];
    public IReadOnlyList<string> Roles { get; } = ["coordinator", "admin"];

    public int RunningNow { get; private set; }
    public int Today { get; private set; }
    public int NeedsAttention { get; private set; }
    public int WaitingForDrive { get; private set; }
    public string LastLmsCheck { get; private set; } = "never";
    public string ActiveAccount => _accounts.Active() is { Email.Length: > 0 } a ? $"{a.Label} — {a.Email} ({a.Role})" : "No LMS account saved";

    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public LmsAccountEntry? SelectedAccount { get => _selectedAccount; set => SetProperty(ref _selectedAccount, value); }
    public string NewLabel { get => _newLabel; set => SetProperty(ref _newLabel, value); }
    public string NewEmail { get => _newEmail; set => SetProperty(ref _newEmail, value); }
    public string NewRole { get => _newRole; set => SetProperty(ref _newRole, value); }

    public ICommand RefreshCommand { get; }
    public ICommand CheckLmsCommand { get; }
    public ICommand FullCheckCommand { get; }
    public ICommand UseAccountCommand { get; }
    public ICommand RemoveAccountCommand { get; }

    /// <summary>
    /// The LMS sign-in a class's steps go up under: its own coordinator's when this PC runs their
    /// classes, otherwise the account in use here.
    /// </summary>
    private string LmsEmailFor(string group)
    {
        try
        {
            if (_classAccounts.Find(group) is { } claim
                && _accounts.List().FirstOrDefault(a => a.Id.Equals(claim.AccountId, StringComparison.OrdinalIgnoreCase)) is { } theirs)
                return theirs.Email;
        }
        catch { }
        return _accounts.Active()?.Email ?? "";
    }

    // ------------------------------------------------------------------ accounts

    /// <summary>After this PC's LMS sign-in changed elsewhere (the server's accounts were copied here).</summary>
    public void ReloadAccounts() => LoadAccounts();

    private void LoadAccounts()
    {
        Accounts.Clear();
        foreach (var a in _accounts.List()) Accounts.Add(a);
        var active = _accounts.Active();
        SelectedAccount = Accounts.FirstOrDefault(a => a.Id == active.Id);
        OnPropertyChanged(nameof(ActiveAccount));
    }

    private void UseAccount()
    {
        if (SelectedAccount == null) return;
        try
        {
            _accounts.SetActive(SelectedAccount.Id);
            Status = $"The app now signs in to the LMS as {SelectedAccount.Label} ({SelectedAccount.Email}).";
            ConsoleLogger.Info($"[LMS] Active account: {SelectedAccount.Label} ({SelectedAccount.Role}).");
        }
        catch (Exception ex) { Status = ex.Message; }
        LoadAccounts();
    }

    /// <summary>The password arrives from the PasswordBox (code-behind) and is not kept here.</summary>
    public void SaveAccount(string? password, bool makeActive)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(NewEmail) || string.IsNullOrWhiteSpace(password)) { Status = "Email and password are required."; return; }
            var entry = _accounts.Upsert(NewLabel, NewEmail, password, NewRole, makeActive);
            Status = $"Saved {entry.Label} ({entry.Email}).{(makeActive ? " The app uses it from now on." : "")}";
            NewLabel = NewEmail = "";
        }
        catch (Exception ex) { Status = ex.Message; }
        LoadAccounts();
    }

    private void RemoveAccount()
    {
        if (SelectedAccount == null) return;
        try { _accounts.Remove(SelectedAccount.Id); Status = $"Removed {SelectedAccount.Label}."; }
        catch (Exception ex) { Status = ex.Message; }
        LoadAccounts();
    }

    // ------------------------------------------------------------------ the LMS

    /// <summary>Called every 30 s by the window: reload local state; a quick LMS look every 15 minutes.</summary>
    public async Task TickAsync()
    {
        await ReloadAsync();
        if (!IsBusy && DateTimeOffset.Now - _lastAutoCheck > TimeSpan.FromMinutes(15) && _accounts.List().Count > 0)
            await CheckLmsAsync(full: false);
        await SweepSheetAsync();
        await SweepMaterialsAsync();
    }

    public Task CheckAsync(bool full) => CheckLmsAsync(full);
    public Task CheckAsync(bool full, DateOnly from, DateOnly to) => CheckLmsAsync(full, from, to);

    private Task CheckLmsAsync(bool full)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        // A week and a day ahead: next week's class of the same track is when an assignment is due.
        return CheckLmsAsync(full, full ? today.AddDays(-30) : today.AddDays(-3), full ? today.AddDays(1) : today.AddDays(8));
    }

    /// <summary>Reads the LMS for these days only: full opens every session (status, link, attendance).</summary>
    private async Task CheckLmsAsync(bool full, DateOnly from, DateOnly to)
    {
        if (IsBusy) return;
        IsBusy = true;
        _lastAutoCheck = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(DateTime.Now);
        Status = full ? $"Reading every session from {from:dd MMM} to {to:dd MMM} on the LMS (status, link, attendance)…" : "Reading the LMS session list…";
        try
        {
            var groups = (await _schedules.ListAsync()).Select(s => s.GroupName ?? s.AccountId)
                .Concat(_classAccounts.List().Select(c => c.Group))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            // Check LMS opens only the sessions this app put material or an assignment on: a file deleted
            // on the LMS shows on the card straight after, without a full check of every session.
            var withMaterial = MaterialSettings.Load().Done.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool HasMaterial(string group, DateOnly? date, TimeOnly? start) =>
                date is { } d && start is { } t && withMaterial.Contains(MaterialSettings.KeyOf(group, d, t));
            // Each coordinator's groups are read with their own LMS sign-in: the account in use sees
            // only its own groups, so a coordinator's class read with it stayed "Not read" (2026-09-26).
            var byAccount = groups.GroupBy(g => _classAccounts.Find(g)?.AccountId ?? "", StringComparer.OrdinalIgnoreCase).ToArray();
            var list = new List<LmsSessionRunner.LmsSessionInfo>();
            var readWith = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var problems = new List<string>();
            foreach (var account in byAccount)
            {
                string first = account.First();
                string email = LmsEmailFor(first);
                try
                {
                    var read = await Task.Run(() => _runnerFor(first).SurveyAsync(from, to, [.. account], openEach: full, openWhen: HasMaterial));
                    list.AddRange(read);
                    foreach (var s in read) readWith[s.Group] = email;
                }
                catch (Exception ex)
                {
                    problems.Add($"{(email.Length > 0 ? email : "the account in use")}: {ex.Message}");
                    ConsoleLogger.Warn($"[LMS] Sessions page, {string.Join(", ", account)}: {ex.GetType().Name}.");
                }
            }
            string fallback = _accounts.Active().Email;
            _cache.Merge(list, from, to, s => readWith.GetValueOrDefault(s.Group, fallback), listOnly: !full);
            Status = $"LMS read at {DateTime.Now:HH:mm}: {list.Count} session(s) from {from:dd MMM} to {to:dd MMM}, " +
                     $"with {byAccount.Length} LMS account(s)" + (problems.Count > 0 ? $". Not read: {string.Join("; ", problems)}" : ".");
        }
        catch (Exception ex)
        {
            Status = $"The LMS could not be read: {ex.Message}";
            ConsoleLogger.Warn($"[LMS] Sessions page: {ex.GetType().Name}.");
        }
        finally { IsBusy = false; }
        await ReloadAsync();
    }

    // ------------------------------------------------------------------ the rows

    /// <summary>Days chosen on the page (From - to); otherwise the last two weeks and the next one.</summary>
    public (DateOnly From, DateOnly To)? ViewRange { get; set; }

    public async Task ReloadAsync()
    {
        try
        {
            var now = DateTime.Now;
            var today = DateOnly.FromDateTime(now);
            var from = ViewRange?.From ?? today.AddDays(-14);
            var to = ViewRange?.To ?? today.AddDays(7);
            var schedules = await _schedules.ListAsync();
            var pending = await _queue.ReadAsync();
            var history = await _queue.ReadHistoryAsync();
            var cache = _cache.Read();
            var timetable = Timetable(schedules, cache);
            var materials = MaterialSettings.Load();

            static string Key(string g, DateOnly d, TimeOnly t) => $"{g.ToUpperInvariant()}|{d:yyyy-MM-dd}|{t:HH\\:mm}";
            var keys = new Dictionary<string, (string Group, DateOnly Date, TimeOnly Start)>();
            // Only a real group is a class: a step written down under a whole LMS row read as one word
            // (2026-09-26) is not shown as a class, or as a group to filter by.
            // Nor a class of a coordinator turned off here ("Run their classes" unticked).
            var notRunHere = new CoordinatorPause().Groups();
            void Add(string g, DateOnly d, TimeOnly t)
            {
                if (d >= from && d <= to && LmsSessionCache.IsGroupCode(g) && !notRunHere.Contains(g)) keys.TryAdd(Key(g, d, t), (g, d, t));
            }
            foreach (var s in schedules.Where(s => s.OccurrenceDate.HasValue))
                Add(s.GroupName ?? s.AccountId, s.OccurrenceDate!.Value, new TimeOnly(s.Time.Hour, s.Time.Minute));
            // An LMS session belongs to the class of its group that day, whatever time each side shows.
            foreach (var c in cache.Where(c => c.Session.Date.HasValue && c.Session.Start.HasValue))
                if (!keys.Values.Any(k => k.Group.Equals(c.Session.Group, StringComparison.OrdinalIgnoreCase) && k.Date == c.Session.Date))
                    Add(c.Session.Group, c.Session.Date!.Value, c.Session.Start!.Value);
            // A class opened by hand at 18:51 is the 19:00 class: its steps are shown on that one card.
            TimeOnly Snap(string g, DateOnly d, TimeOnly t) => ScheduleTiming.ClassStartNear(schedules, g, d, t) ?? t;
            var historyAt = new Dictionary<object, TimeOnly>(ReferenceEqualityComparer.Instance);
            var pendingAt = new Dictionary<object, TimeOnly>(ReferenceEqualityComparer.Instance);
            foreach (var h in history) { historyAt[h] = Snap(h.Group, h.SessionDate, h.SessionStart); Add(h.Group, h.SessionDate, historyAt[h]); }
            foreach (var p in pending) { pendingAt[p] = Snap(p.Group, p.SessionDate, p.SessionStart); Add(p.Group, p.SessionDate, pendingAt[p]); }

            var rows = new List<ClassRow>();
            foreach (var (group, date, start) in keys.Values)
            {
                var schedule = schedules.FirstOrDefault(s => s.OccurrenceDate == date && new TimeOnly(s.Time.Hour, s.Time.Minute) == start &&
                                                             (s.GroupName ?? s.AccountId).Equals(group, StringComparison.OrdinalIgnoreCase));
                var lms = cache.Where(c => c.Session.Group.Equals(group, StringComparison.OrdinalIgnoreCase) && c.Session.Date == date)
                               .OrderBy(c => c.Session.Start is { } t ? Math.Abs((t.ToTimeSpan() - start.ToTimeSpan()).TotalMinutes) : 9999)
                               .ThenByDescending(c => c.ReadAt).FirstOrDefault();
                LmsFollowUpQueue.Outcome? Done(LmsFollowUpStep step) => history.FirstOrDefault(h =>
                    h.Step == step && h.SessionDate == date && historyAt[h] == start && h.Group.Equals(group, StringComparison.OrdinalIgnoreCase));
                LmsFollowUp? Owed(LmsFollowUpStep step) => pending.FirstOrDefault(p =>
                    p.Step == step && p.SessionDate == date && pendingAt[p] == start && p.Group.Equals(group, StringComparison.OrdinalIgnoreCase));

                var classStart = date.ToDateTime(start);
                string lmsStatus = lms is null ? "" : (lms.Session.PageStatus.Length > 0 && !lms.Session.PageStatus.StartsWith('(') ? lms.Session.PageStatus : lms.Session.ListStatus);
                bool finished = lmsStatus is "finished" or "completed";
                bool running = lmsStatus == "running";
                // Held in a room or on Zoom: the schedule says first, then the LMS's own type.
                string mode = ClassMode.Resolve(schedules, cache, group, date, start) ?? "";
                bool physical = mode == ClassMode.Physical;

                string zoom = schedule == null ? "—"
                    : schedule.LastTriggeredDate == date ? "Opened"
                    : !schedule.Enabled ? "Disabled"
                    : classStart - ScheduleTiming.StartLead > now ? $"Opens {classStart - ScheduleTiming.StartLead:HH:mm}"
                    : "Not opened";
                string lmsText = lms is null ? "Not checked" : $"{(lmsStatus.Length > 0 ? lmsStatus : "?")} · {lms.ReadAt:HH:mm}";
                string Step(LmsFollowUpStep step, bool doneOnLms)
                {
                    var owed = Owed(step);
                    var done = Done(step);
                    if (done is { Succeeded: true }) return $"✓ {done.At:HH:mm}";
                    if (owed != null)
                        return owed.LastError != null ? $"Retry {owed.DueAt.LocalDateTime:HH:mm} ({owed.Attempts})" : $"Due {owed.DueAt.LocalDateTime:HH:mm}";
                    if (done is { Succeeded: false }) return "✗ failed";
                    return doneOnLms ? "✓ (LMS)" : "—";
                }
                string run = Done(LmsFollowUpStep.RunSession) is { } r ? (r.Succeeded ? $"✓ {r.At:HH:mm}" : "✗ failed") : (running || finished ? "✓ (LMS)" : "—");
                string attendance = Step(LmsFollowUpStep.TakeAttendance, lms?.Session.AttendanceTaken == true);
                string correction = Step(LmsFollowUpStep.CorrectAttendance, false);
                string complete = Step(LmsFollowUpStep.CompleteSession, finished);
                string link = lms?.Session.LinkKind switch
                {
                    "drive" => "Drive ✓",
                    "zoom" => "Zoom (Drive pending)",
                    "none" => "No link",
                    "other" => "Other link",
                    _ => "—",
                };

                string next; string tone;
                bool past = classStart.AddHours(3.5) < now;
                if (classStart > now.AddMinutes(20)) { next = zoom.StartsWith("Opens") ? zoom : "Scheduled"; tone = "future"; }
                else if (!physical && zoom == "Not opened" && !running && !finished) { next = "Meeting did not open"; tone = "bad"; }
                else if (!running && !finished) { next = run.StartsWith('✗') ? "Run Session failed — press it on the LMS" : "Run Session"; tone = run.StartsWith('✗') || past ? "bad" : "live"; }
                else if (!physical && !attendance.StartsWith('✓')) { next = attendance == "—" ? (past ? "Attendance not taken" : "Attendance at 1.5 h") : $"Attendance {attendance}"; tone = attendance.Contains("Retry") || (past && attendance == "—") ? "bad" : "live"; }
                else if (!finished && !complete.StartsWith('✓')) { next = complete == "—" ? "Correction + Complete at 3 h" : $"Complete {complete}"; tone = complete.Contains("Retry") ? "bad" : "live"; }
                else if (link is "No link" or "—") { next = finished ? "Add the record link" : "Record link"; tone = lms?.Session.LinkKind == "none" ? "warn" : "live"; }
                else if (link.StartsWith("Zoom")) { next = "Waiting for the Drive link"; tone = "warn"; }
                else { next = "Done"; tone = "done"; }

                string details = string.Join("\n", new[]
                {
                    lms?.Session.Title, lms?.Session.RecordLink is { Length: > 0 } l ? $"Link: {l}" : null,
                    Done(LmsFollowUpStep.RunSession)?.Message, Done(LmsFollowUpStep.TakeAttendance)?.Message,
                    Owed(LmsFollowUpStep.TakeAttendance)?.LastError, Done(LmsFollowUpStep.CorrectAttendance)?.Message,
                    Done(LmsFollowUpStep.ZoomReportAttendance)?.Message, Done(LmsFollowUpStep.CompleteSession)?.Message,
                }.Where(x => !string.IsNullOrWhiteSpace(x)));
                StepState State(string key, string label, LmsFollowUpStep step, bool doneOnLms, bool dueLater)
                {
                    var owed = Owed(step);
                    var done = Done(step);
                    if (done is { Succeeded: true }) return new(key, label, "done", $"Done {done.At.LocalDateTime:HH:mm}", done.Message);
                    if (owed != null)
                        return owed.LastError != null
                            ? new(key, label, "retry", $"Retry {owed.DueAt.LocalDateTime:HH:mm} · {owed.Attempts}×", owed.LastError)
                            : new(key, label, "due", $"Due {owed.DueAt.LocalDateTime:HH:mm}");
                    if (done is { Succeeded: false }) return new(key, label, "failed", "Failed", done.Message);
                    if (doneOnLms) return new(key, label, "lms", "On the LMS");
                    return new(key, label, dueLater ? "future" : "none", dueLater ? "Later" : "Not done");
                }
                bool upcoming = classStart > now;
                var steps = new List<StepState>
                {
                    zoom switch
                    {
                        "Opened" => new StepState("zoom", "Zoom", "done", "Opened"),
                        "—" => new StepState("zoom", "Zoom", "none", "No schedule"),
                        "Disabled" => new StepState("zoom", "Zoom", "none", "Disabled"),
                        "Not opened" => new StepState("zoom", "Zoom", running || finished ? "lms" : "failed", running || finished ? "Opened elsewhere" : "Did not open"),
                        _ => new StepState("zoom", "Zoom", "future", zoom),
                    },
                    Done(LmsFollowUpStep.RunSession) is { } ran
                        ? new StepState("run", "Run", ran.Succeeded ? "done" : (running || finished ? "lms" : "failed"), ran.Succeeded ? $"Done {ran.At.LocalDateTime:HH:mm}" : (running || finished ? "On the LMS" : "Failed"), ran.Message)
                        : new StepState("run", "Run", running || finished ? "lms" : upcoming ? "future" : "none", running || finished ? "On the LMS" : upcoming ? "At start" : "Not run"),
                    State("attendance", "Attendance", LmsFollowUpStep.TakeAttendance, lms?.Session.AttendanceTaken == true, classStart + LmsFollowUpQueue.TakeAttendanceAfter > now),
                    State("correct", "Late joiners", LmsFollowUpStep.CorrectAttendance, false, classStart + LmsFollowUpQueue.CorrectAttendanceAfter > now),
                    State("complete", "Complete", LmsFollowUpStep.CompleteSession, finished, classStart + LmsFollowUpQueue.CorrectAttendanceAfter > now),
                };
                // The second half of the late-joiner correction, from Zoom's own report (shown after
                // "Ended", where it belongs): it looks different while Zoom has not published the
                // report yet, and like every other step once it has gone up.
                StepState ReportStep()
                {
                    var step = State("report", "Zoom report", LmsFollowUpStep.ZoomReportAttendance, false, classStart + LmsFollowUpQueue.CorrectAttendanceAfter > now);
                    var owed = Owed(LmsFollowUpStep.ZoomReportAttendance);
                    bool waitingForZoom = owed?.LastError != null && owed.Attempts == 0 &&
                        (owed.LastError.Contains("report", StringComparison.OrdinalIgnoreCase) || owed.LastError.Contains("still running", StringComparison.OrdinalIgnoreCase));
                    return waitingForZoom ? step with { State = "waiting", Text = "Waiting for Zoom's report" } : step;
                }
                // How the meeting ended. The program ends a finished class itself, but it is just as
                // often closed from a phone - which it notices rather than guesses at.
                var ending = _endings.For(group, date, start);
                steps.Add(ending switch
                {
                    { How: ClassEndedHow.Program } e => new StepState("ended", "Ended", "done", $"Ended {e.At.LocalDateTime:HH:mm}", e.Message),
                    { How: ClassEndedHow.Elsewhere } e => new StepState("ended", "Ended", "done", $"Closed {e.At.LocalDateTime:HH:mm}", e.Message),
                    { How: ClassEndedHow.ByHand } e => new StepState("ended", "Ended", "due", "Yours to end", e.Message),
                    _ => new StepState("ended", "Ended", past ? "none" : "future", past ? "Still open" : "After class"),
                });
                // Zoom's report exists only once the meeting has ended, so its step comes after "Ended".
                steps.Add(ReportStep());
                // The record link, in two steps: the Zoom recording soon after class, then the Drive
                // copy that replaces it. A link the app wrote counts even when the last LMS read was a
                // quick list read, which does not look at links.
                bool linkDue = classStart + LmsFollowUpQueue.CorrectAttendanceAfter <= now || finished;
                var recordStep = State("record", "Zoom recording", LmsFollowUpStep.AttachZoomRecording, false, !linkDue);
                var drive = Done(LmsFollowUpStep.AttachDriveLink);
                string lmsKind = lms?.Session.LinkKind ?? "";
                bool driveOnLms = lmsKind == "drive" || drive is { Succeeded: true };
                bool zoomOnLms = recordStep.State == "done" || lmsKind == "zoom";
                string linkKind = driveOnLms ? "drive" : zoomOnLms ? "zoom" : lmsKind;
                link = driveOnLms ? "Drive ✓" : zoomOnLms ? "Zoom (Drive pending)" : link;
                steps.Add(
                    recordStep.State == "done" ? recordStep
                    : lmsKind == "zoom" ? recordStep with { State = "lms", Text = "On the LMS" }
                    : driveOnLms ? recordStep with { State = "lms", Text = "Drive instead" }
                    : lmsKind == "other" ? recordStep with { State = "lms", Text = "Other link" }
                    : recordStep.State is "none" && linkDue ? recordStep with { State = finished ? "failed" : "due", Text = finished ? "No link" : "After class" }
                    : recordStep);
                steps.Add(
                    drive is { Succeeded: true } ? new StepState("drive", "Drive", "done", $"Done {drive.At.LocalDateTime:HH:mm}", drive.Message)
                    : lmsKind == "drive" ? new StepState("drive", "Drive", "lms", "On the LMS")
                    : !linkDue ? new StepState("drive", "Drive", "future", "After class")
                    : drive is { Succeeded: false } ? new StepState("drive", "Drive", "due", "Not in sheet yet", drive.Message)
                    : new StepState("drive", "Drive", "due", "Waiting for Drive"));
                // The material and the assignment: fixed material goes up by itself at class time; a
                // technical class waits for the folder chosen for it.
                // Freelancing and Soft Skills are numbered by the LMS week in the session's title.
                var plan = MaterialPlanner.Plan(timetable, group, date, start, materials, lms?.Session.Title);
                string materialKey = MaterialSettings.KeyOf(group, date, start);
                materials.Done.TryGetValue(materialKey, out var materialDone);
                materials.Errors.TryGetValue(materialKey, out var materialError);
                materials.Assignments.TryGetValue(materialKey, out var choice);
                var files = MaterialPlanner.FilesFor(plan, choice);
                string fileList = string.Join("\n", files.Select(f => f.Title));
                // What the LMS showed at its last full read of the session: material or an assignment
                // removed there shows as missing here, and material put there by hand shows as there.
                var seenAt = lms?.Session.DetailsReadAt;
                var shownOnLms = seenAt != null ? lms!.Session.Attachments : null;
                bool readSinceDone = materialDone != null && seenAt > materialDone.At;
                var removedFiles = readSinceDone && shownOnLms != null
                    ? materialDone!.Files.Where(f => !shownOnLms.Any(a => LmsSessionRunner.Shows(a, f))).ToArray() : [];
                bool removed = materialDone != null && removedFiles.Length > 0 && removedFiles.Length == materialDone.Files.Length;
                bool alreadyOnLms = materialDone == null && shownOnLms != null && files.Count > 0 && files.All(f => shownOnLms.Any(a => LmsSessionRunner.Shows(a, f.Title)));
                bool assignmentRemoved = materialDone?.Assignment != null && readSinceDone && lms!.Session.HasAssignment == false;
                steps.Add(
                    removed ? new StepState("material", "Material", "due", "Removed on the LMS", $"The LMS showed none of it at {seenAt!.Value.LocalDateTime:ddd dd MMM HH:mm}. Upload again to put it back.\n{fileList}")
                    : removedFiles.Length > 0 ? new StepState("material", "Material", "partial", $"{materialDone!.Files.Length - removedFiles.Length}/{materialDone.Files.Length} on the LMS", $"Removed on the LMS: {string.Join(", ", removedFiles)}")
                    : materialDone != null ? new StepState("material", "Material", "done", $"Done {materialDone.At.LocalDateTime:HH:mm}", $"{plan.Label}\n{string.Join("\n", materialDone.Files)}")
                    : alreadyOnLms ? new StepState("material", "Material", "lms", "On the LMS", $"{plan.Label}\n{fileList}")
                    : files.Count == 0 ? new StepState("material", "Material", "none", plan.IsTechnical ? "Choose folder" : plan.Track.Length == 0 ? "None" : "No files", plan.Note)
                    : materialError != null ? new StepState("material", "Material", "retry", "Retry", materialError)
                    : plan.IsFixed && classStart > now ? new StepState("material", "Material", "future", $"{plan.Label} · {files.Count} files at {start:HH\\:mm}", $"{plan.Note}\n{fileList}")
                    : new StepState("material", "Material", "due", $"{(plan.Label.Length > 0 ? plan.Label + " · " : "")}{files.Count} files", $"{plan.Note}\n{fileList}"));
                var assignment = MaterialPlanner.AssignmentFor(plan, choice, date, start);
                string? assignmentTitle = assignment?.Title ?? choice?.Title ?? plan.AssignmentFile?.Title;
                DateTime? deadline = assignment?.Deadline ?? choice?.Deadline;
                steps.Add(
                    choice?.None == true ? new StepState("assignment", "Assignment", "none", "None", "Marked as having no assignment.")
                    : assignmentRemoved ? new StepState("assignment", "Assignment", "due", "Removed on the LMS", $"The LMS offered Add Assignment again at {seenAt!.Value.LocalDateTime:ddd dd MMM HH:mm}.\n{materialDone!.Assignment}")
                    : materialDone?.Assignment != null ? new StepState("assignment", "Assignment", "done", $"Due {materialDone.Deadline:dd MMM HH\\:mm}", materialDone.Assignment)
                    : lms?.Session.HasAssignment == true && seenAt != null ? new StepState("assignment", "Assignment", "lms", "On the LMS", "The session has its assignment (Edit Assignment).")
                    : assignmentTitle == null ? new StepState("assignment", "Assignment", "none", plan.IsTechnical ? "Add" : "None", plan.IsTechnical ? "Add one if this session has an assignment: title, deadline and its file." : "No assignment file in its folder.")
                    : deadline == null ? new StepState("assignment", "Assignment", "due", "Set deadline", assignmentTitle)
                    : new StepState("assignment", "Assignment", classStart > now ? "future" : "due", $"Due {deadline:dd MMM HH\\:mm}", $"{assignmentTitle}\n{assignment?.Description}"));
                var material = new MaterialInfo(plan.Track, plan.Number, plan.Folder, [.. files.Select(f => f.Title)], plan.Skipped,
                    plan.IsTechnical, plan.IsFixed, plan.Note, assignmentTitle, deadline?.ToString("yyyy-MM-dd'T'HH:mm"), choice?.None == true,
                    (materialDone != null && !removed) || alreadyOnLms)
                {
                    Description = choice?.Description ?? assignment?.Description,
                    AssignmentFile = choice?.File,
                    Chosen = materials.Folders.GetValueOrDefault(materialKey),
                    // Removed on the LMS on purpose: shown as empty, never put back by itself.
                    RemovedOnLms = removed || removedFiles.Length > 0 || assignmentRemoved,
                    SeenOnLms = seenAt?.LocalDateTime.ToString("ddd dd MMM HH:mm"),
                };

                // A physical class: no meeting and no attendance from Zoom. Run, Complete and the
                // recording are the same as any class's; the Zoom half says it was held in the room.
                if (physical)
                {
                    // Held in a room with nothing recorded on Zoom: no recording and no Drive copy
                    // is owed, and neither is shown as failed or waited for.
                    var noRecording = Done(LmsFollowUpStep.AttachZoomRecording) is { Succeeded: true } rec
                                      && rec.Message.StartsWith(LmsFollowUpProcessor.NoZoomRecording, StringComparison.Ordinal) ? rec : null;
                    if (noRecording != null)
                        for (int i = 0; i < steps.Count; i++)
                            if (steps[i].Key is "record" || (steps[i].Key is "drive" && steps[i].State is not ("done" or "lms")))
                                steps[i] = steps[i] with { State = "none", Text = "None", Detail = noRecording.Message };
                    for (int i = 0; i < steps.Count; i++)
                    {
                        steps[i] = steps[i].Key switch
                        {
                            "zoom" => steps[i] with { State = "none", Text = "In the room",
                                Detail = "A physical session: no Zoom meeting is opened for it. It is run and completed on the LMS, and its recording goes up as usual." },
                            "attendance" when steps[i].State is "done" or "lms" => steps[i],
                            "attendance" or "correct" or "ended" or "report" => steps[i] with { State = "none", Text = "In the room",
                                Detail = "A physical session: attendance is taken in the room, not from Zoom." },
                            _ => steps[i],
                        };
                    }
                }

                bool linkNext = next is "Waiting for the Drive link" or "Add the record link" or "Record link";
                if (driveOnLms && linkNext) { next = "Done"; tone = "done"; }
                else if (zoomOnLms && !driveOnLms && linkNext) { next = "Waiting for the Drive link"; tone = "warn"; }
                // A class whose day has passed is over (Completed): nothing is owed on the LMS any more
                // except its recording - the Drive link when there is one, else the Zoom recording.
                if (date < today)
                {
                    for (int i = 0; i < steps.Count; i++)
                        if (steps[i].Key is "zoom" or "run" or "attendance" or "correct" or "complete" && steps[i].State is not ("done" or "lms")
                            && !(physical && steps[i].Text == "In the room"))
                            steps[i] = steps[i] with { State = "lms", Text = "Past" };
                    (next, tone) = driveOnLms ? ("Done", "done") : zoomOnLms ? ("Waiting for the Drive link", "warn") : ("Add the recording", "warn");
                }
                rows.Add(new ClassRow(date, start, group, lms?.Session.Title ?? schedule?.Name ?? "", zoom, lmsText, run, attendance,
                    correction, complete, link, next, tone, details)
                {
                    Steps = steps,
                    LmsStatus = lmsStatus,
                    LmsReadAt = lms is null ? null : $"{lms.ReadAt.LocalDateTime:ddd HH:mm}",
                    LmsUrl = lms?.Session.PageUrl,
                    LinkKind = linkKind,
                    // Whose accounts this class runs under. On a PC that runs several people's
                    // classes, that is the first thing to look at when one of them goes wrong.
                    ZoomAccount = schedule?.AccountId ?? "",
                    Live = LiveMeetings.IsLive(group),
                    Mode = mode,
                    Focus = lms?.Session.Focus ?? "",
                    Attention = ExtensionAttendanceFeed.ResultsNear(group, date, start, TimeSpan.FromHours(2), app: true)?.Attention ?? [],
                    Coordinator = _classAccounts.Whose(group) is { Length: > 0 } whose ? whose : schedule?.Coordinator ?? "",
                    LmsAccount = LmsEmailFor(group),
                    RecordLink = lms?.Session.RecordLink is { Length: > 0 } rl ? rl : null,
                    Material = material,
                });
            }

            var ordered = rows.OrderBy(r => Math.Abs((r.Date.ToDateTime(r.Start) - now).TotalHours) > 12 ? 1 : 0)
                .ThenByDescending(r => r.Date).ThenBy(r => r.Start).ToList();
            Rows.Clear();
            foreach (var row in ordered) Rows.Add(row);
            RunningNow = rows.Count(r => r.Lms.StartsWith("running"));
            Today = rows.Count(r => r.Date == today);
            NeedsAttention = rows.Count(r => r.Tone == "bad");
            WaitingForDrive = rows.Count(r => r.Link.StartsWith("Zoom"));
            var newest = cache.Count > 0 ? cache.Max(c => c.ReadAt) : (DateTimeOffset?)null;
            LastLmsCheck = newest is { } n ? $"{n.LocalDateTime:ddd HH:mm}" : "never";
            foreach (var name in new[] { nameof(RunningNow), nameof(Today), nameof(NeedsAttention), nameof(WaitingForDrive), nameof(LastLmsCheck), nameof(ActiveAccount) })
                OnPropertyChanged(name);
        }
        catch (Exception ex) { Status = $"The sessions could not be loaded: {ex.Message}"; }
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ by hand (Sessions page buttons)

    /// <summary>Raised whenever what the page shows may have changed.</summary>
    public event Action? Changed;

    /// <summary>The app's one follow-up processor (set by the window), so buttons never run beside the queue.</summary>
    public ZoomAutoAdmit.WindowsUI.Services.LmsFollowUpProcessor? Processor { get; set; }

    private readonly HashSet<string> _working = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<string> Working { get { lock (_working) return [.. _working]; } }
    private readonly Dictionary<string, DateTimeOffset> _sheetTried = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastSheetSweep = DateTimeOffset.MinValue;

    public RecordingSheetSettings SheetSettings => RecordingSheetSettings.Load();

    public void SaveSheetSettings(string? url, IDictionary<string, string>? tabs)
    {
        var settings = new RecordingSheetSettings { Url = string.IsNullOrWhiteSpace(url) ? null : url.Trim() };
        foreach (var (group, tab) in tabs ?? new Dictionary<string, string>())
            if (!string.IsNullOrWhiteSpace(tab)) settings.Tabs[group] = tab.Trim();
        if (settings.Url != null && RecordingSheetReader.SpreadsheetIdOf(settings.Url) == null)
            throw new InvalidOperationException("That is not a Google Sheets link.");
        settings.Save();
        Status = settings.Url == null ? "The recordings sheet link was removed." : "The recordings sheet link is saved.";
        Changed?.Invoke();
    }

    /// <summary>
    /// Says by hand whether a class is held in the room or on Zoom. It is written on the class's own
    /// schedule entry, which the LMS's type never overrides: a physical class opens no meeting and
    /// takes no attendance from Zoom; it is still run, completed and given its recording.
    /// </summary>
    /// <summary>
    /// A physical class that was not recorded on Zoom, said by hand: its recording step is done with
    /// "none", so it is neither tried again nor shown as failed. Only a class held in the room.
    /// </summary>
    private async Task<(bool Ok, string Message)> MarkNoRecordingAsync(string group, DateOnly date, TimeOnly start)
    {
        if (!ClassMode.IsPhysical(await _schedules.ListAsync(), _cache.Read(), group, date, start))
            return (false, $"{group} {date:ddd d MMM} is an online class: its Zoom recording is owed. Mark it held in the room first if it was.");
        var owed = (await _queue.ReadAsync()).FirstOrDefault(p => p.Step == LmsFollowUpStep.AttachZoomRecording && p.SessionDate == date
            && p.SessionStart == start && p.Group.Equals(group, StringComparison.OrdinalIgnoreCase));
        var item = owed ?? new LmsFollowUp
        {
            Id = $"{group}|{date:yyyy-MM-dd}|{start:HH:mm}|{LmsFollowUpStep.AttachZoomRecording}",
            Group = group, SessionDate = date, SessionStart = start, Step = LmsFollowUpStep.AttachZoomRecording, DueAt = DateTimeOffset.Now,
        };
        await _queue.RecordAsync(item, true, $"{LmsFollowUpProcessor.NoZoomRecording}: marked by hand - this physical session was not recorded on Zoom.");
        if (owed != null) await _queue.CompleteAsync(owed);
        ConsoleLogger.Info($"[SESSIONS] {group} {date:yyyy-MM-dd} {start:HH:mm}: marked as having no Zoom recording.");
        return (true, $"{group} {date:ddd d MMM}: no Zoom recording - nothing more is looked for.");
    }

    private async Task<(bool Ok, string Message)> SetModeAsync(string group, DateOnly date, TimeOnly start, string mode)
    {
        var schedules = await _schedules.ListAsync();
        var entries = schedules.Where(s => (string.IsNullOrWhiteSpace(s.GroupName) ? s.AccountId : s.GroupName).Equals(group, StringComparison.OrdinalIgnoreCase)
                                           && s.OccurrenceDate == date && s.Time.Hour == start.Hour && s.Time.Minute == start.Minute).ToArray();
        if (entries.Length == 0)
            return (false, $"{group} {date:ddd d MMM} {start:HH:mm} is not on this PC's schedule, so the LMS's own type decides it.");
        foreach (var entry in entries) await _schedules.UpsertAsync(entry with { Mode = mode });
        if (mode == ClassMode.Physical)
            await _queue.SchedulePhysicalAsync(group, date, start);
        ConsoleLogger.Info($"[SESSIONS] {group} {date:yyyy-MM-dd} {start:HH:mm}: marked {(mode == ClassMode.Physical ? "held in the room" : "on Zoom")} by hand.");
        return (true, mode == ClassMode.Physical
            ? $"{group} is held in the room: no Zoom meeting and no attendance from Zoom. Run, Complete and the recording go on as usual."
            : $"{group} is on Zoom: its meeting opens at its time and attendance is taken from it.");
    }

    /// <summary>
    /// A person's answer about one student the match was unsure of. It changes this class's list
    /// only; the answer goes to the LMS with the next upload, which the card offers right after.
    /// </summary>
    private (bool Ok, string Message) Answer(string group, DateOnly date, TimeOnly start, string? student, bool present)
    {
        if (string.IsNullOrWhiteSpace(student)) return (false, "Which student?");
        var answered = ExtensionAttendanceFeed.AnswerAttention(group, date, start, student.Trim(), present);
        if (answered == null) return (false, $"{group} has no matched list for {date:ddd d MMM} {start:HH\\:mm} to answer about.");
        ConsoleLogger.Info($"[ATTENDANCE] {group} {start:HH\\:mm}: {student} marked {(present ? "present" : "absent")} by hand.");
        return (true, $"{student} is {(present ? "present" : "not in this class")}. " +
                      $"{answered.Attention.Count} still to answer; press \"Take attendance\" to send the list.");
    }

    /// <summary>
    /// Opens this class's meeting, for when its own time came and it did not open - a browser
    /// profile that was busy, Zoom asking for a person, a PC that was asleep.
    ///
    /// A class already live is never opened a second time: joining the same meeting twice is what
    /// leaves two sessions in the list, each admitting and counting the same people.
    /// </summary>
    private async Task<(bool Ok, string Message)> OpenZoomAsync(string group, DateOnly date, TimeOnly start, bool again = false)
    {
        if (OpenMeeting == null) return (false, "This window cannot open meetings.");
        string stopped = "";
        if (ZoomAutoAdmit.Core.Meetings.LiveMeetings.IsLive(group))
        {
            // Asked for plainly ("start it again"), a class that looks open is stopped first: a
            // meeting can be gone from Zoom while this PC still thinks it is running it, and that
            // is exactly when somebody presses this.
            if (!again) return (false, $"{group} is already open on this PC. Use \"Start the meeting again\" if it dropped.");
            int count = StopMeetingsOf == null ? 0 : await StopMeetingsOf(group, CancellationToken.None);
            ZoomAutoAdmit.Core.Meetings.LiveMeetings.ClearGroup(group);
            stopped = count > 0 ? $"The {count} meeting(s) running for it were stopped first. " : "";
        }

        var schedules = await _schedules.ListAsync();
        var schedule = schedules.FirstOrDefault(s =>
            string.Equals(string.IsNullOrWhiteSpace(s.GroupName) ? s.AccountId : s.GroupName, group, StringComparison.OrdinalIgnoreCase)
            && s.Time.Hour == start.Hour && s.Time.Minute == start.Minute
            && (s.OccurrenceDate is { } once ? once == date : s.Days.Includes(date.DayOfWeek)));
        if (schedule == null)
            return (false, $"{group} has no class on this PC at {start:HH\\:mm} on {date:ddd d MMM}, so there is no link to open.");

        try
        {
            string said = await OpenMeeting(schedule.AccountId, schedule.MeetingUrl, schedule.PreferredEngine, CancellationToken.None);
            // It opened: the class counts as opened today, so its own schedule does not open it again.
            await _schedules.MarkOpenedAsync(schedule.Id, date, CancellationToken.None);
            return (true, stopped + said);
        }
        catch (Exception ex) { return (false, $"{group}: {ex.Message}"); }
    }

    /// <summary>
    /// One step of one class, now. step: zoom, zoomAgain, run, attendance, correct, complete, zoomRecording, sheet, link.
    /// These are real: they press the LMS's own buttons, exactly as the automatic cycle does.
    /// </summary>
    public async Task<(bool Ok, string Message)> RunStepAsync(string group, DateOnly date, TimeOnly start, string step, string? link = null)
    {
        string key = $"{group}|{date:yyyy-MM-dd}|{start:HH\\:mm}|{step}";
        lock (_working) if (!_working.Add(key)) return (false, "That step is already running.");
        Changed?.Invoke();
        (bool Ok, string Message) result;
        try
        {
            var processor = Processor ?? throw new InvalidOperationException("The LMS is not ready yet.");
            result = step switch
            {
                "run" => await processor.ExclusiveAsync(async () =>
                {
                    var run = await Task.Run(() => _runner().RunAsync(group, start, date, dryRun: false));
                    await processor.RecordManualAsync(group, date, start, LmsFollowUpStep.RunSession, run.IsSuccess, run.Message);
                    return (run.IsSuccess, run.Message);
                }),
                "yesThem" => Answer(group, date, start, link, present: true),
                "notThem" => Answer(group, date, start, link, present: false),
                "noRecording" => await MarkNoRecordingAsync(group, date, start),
                "inRoom" => await SetModeAsync(group, date, start, ClassMode.Physical),
                "onZoom" => await SetModeAsync(group, date, start, ClassMode.Online),
                "zoom" => await OpenZoomAsync(group, date, start),
                "zoomAgain" => await OpenZoomAsync(group, date, start, again: true),
                "attendance" => await processor.RunNowAsync(group, date, start, LmsFollowUpStep.TakeAttendance),
                "correct" => await processor.RunNowAsync(group, date, start, LmsFollowUpStep.CorrectAttendance),
                "report" => await processor.RunNowAsync(group, date, start, LmsFollowUpStep.ZoomReportAttendance),
                "complete" => await processor.RunNowAsync(group, date, start, LmsFollowUpStep.CompleteSession),
                "zoomRecording" => await processor.RunNowAsync(group, date, start, LmsFollowUpStep.AttachZoomRecording),
                "recording" => await RecordingAsync(processor, group, date, start),
                "sheet" => await CheckSheetAsync(processor, group, date, start),
                "link" => await AttachGivenLinkAsync(processor, group, date, start, link),
                // A folder or file picked in the upload box is kept for the class only now, on Upload.
                "material" or "assignment" => await MaterialAsync(processor, group, date, start, string.IsNullOrWhiteSpace(link) ? null : link),
                _ => (false, $"Unknown step '{step}'."),
            };
        }
        catch (Exception ex) { result = (false, ex.Message); }
        finally { lock (_working) _working.Remove(key); }
        Status = result.Message;
        ConsoleLogger.Info($"[LMS] Sessions page, {step} for {group} {date:yyyy-MM-dd}: {result.Message}");
        await ReloadAsync();
        return result;
    }

    /// <summary>
    /// The class's recording on the LMS, the best one there is: its Drive link (n8n / the sheet) when
    /// there is one, and otherwise its Zoom recording - which the Drive link replaces later.
    /// </summary>
    private async Task<(bool Ok, string Message)> RecordingAsync(ZoomAutoAdmit.WindowsUI.Services.LmsFollowUpProcessor processor,
        string group, DateOnly date, TimeOnly start)
    {
        var drive = await CheckSheetAsync(processor, group, date, start);
        if (drive.Ok) return drive;
        var row = Rows.FirstOrDefault(r => r.Group.Equals(group, StringComparison.OrdinalIgnoreCase) && r.Date == date && r.Start == start);
        if (row?.LinkKind == "zoom") return (false, $"{drive.Message} The Zoom recording is already on the LMS.");
        var zoom = await processor.RunNowAsync(group, date, start, LmsFollowUpStep.AttachZoomRecording);
        return (zoom.IsSuccess, zoom.IsSuccess ? $"No Drive link yet, so the Zoom recording was put on the LMS. {zoom.Message}" : $"{drive.Message} Zoom: {zoom.Message}");
    }

    /// <summary>The recordings sheet's Drive link for the class, put on its LMS session (replacing a Zoom link).</summary>
    private async Task<(bool Ok, string Message)> CheckSheetAsync(ZoomAutoAdmit.WindowsUI.Services.LmsFollowUpProcessor processor,
        string group, DateOnly date, TimeOnly start)
    {
        _sheetTried[$"{group}|{date:yyyy-MM-dd}"] = DateTimeOffset.Now;
        SheetRecording? found = null;
        var tried = new List<string>();
        // The recordings sheet itself first (the Drive copies are listed there), then what the
        // central server was sent from it.
        var settings = RecordingSheetSettings.Load();
        if (RecordingSheetReader.SpreadsheetIdOf(settings.Url) != null)
        {
            try
            {
                var rows = await new RecordingSheetReader().ReadGroupAsync(settings, group);
                found = RecordingSheetReader.Pick(rows, date, start);
                if (found == null) tried.Add("the recordings sheet has no row of that day yet");
            }
            catch (Exception ex) { tried.Add(ex.Message); }
        }
        if (found == null && CentralDriveLink != null)
        {
            try
            {
                var (centralLink, file) = await CentralDriveLink(group, date, start);
                if (RecordingLinks.IsGoogleDriveFileLink(centralLink)) found = new SheetRecording(file ?? "the central server", date, null, centralLink!);
            }
            catch { }
        }
        if (found == null)
        {
            string none = $"No Drive link yet for {group} {date:ddd dd MMM}: {string.Join("; ", tried.DefaultIfEmpty("the recordings sheet's link is not set"))}.";
            await processor.RecordManualAsync(group, date, start, LmsFollowUpStep.AttachDriveLink, false, none);
            return (false, none);
        }        var cached = _cache.Read().FirstOrDefault(c => c.Session.Group.Equals(group, StringComparison.OrdinalIgnoreCase) && c.Session.Date == date);
        if (cached != null && RecordingLinks.DriveFileIdOf(cached.Session.RecordLink) is { } onLms && onLms == RecordingLinks.DriveFileIdOf(found.Link))
        {
            string same = $"{group}: the sheet's Drive link is already the one on the LMS.";
            await processor.RecordManualAsync(group, date, start, LmsFollowUpStep.AttachDriveLink, true, same);
            return (true, same);
        }
        return await WriteLinkAsync(processor, group, date, start, found.Link, $"from {found.FileName}");
    }

    /// <summary>The Drive link n8n reported to the central server for a class (set by the window).</summary>
    public Func<string, DateOnly, TimeOnly, Task<(string? Link, string? File)>>? CentralDriveLink { get; set; }

    private bool HasDriveSource => CentralDriveLink != null || RecordingSheetReader.SpreadsheetIdOf(RecordingSheetSettings.Load().Url) != null;

    private Task<(bool Ok, string Message)> AttachGivenLinkAsync(ZoomAutoAdmit.WindowsUI.Services.LmsFollowUpProcessor processor,
        string group, DateOnly date, TimeOnly start, string? link)
    {
        link = link?.Trim();
        if (!RecordingLinks.IsAttachable(link))
            return Task.FromResult((false, "Paste a Google Drive file link or a Zoom recording link."));
        return WriteLinkAsync(processor, group, date, start, link!, "pasted on the Sessions page");
    }

    private async Task<(bool Ok, string Message)> WriteLinkAsync(ZoomAutoAdmit.WindowsUI.Services.LmsFollowUpProcessor processor,
        string group, DateOnly date, TimeOnly start, string link, string where)
    {
        var kind = RecordingLinks.Classify(link);
        return await processor.ExclusiveAsync(async () =>
        {
            var outcome = await ZoomAutoAdmit.Inspector.Runtime.RecordingWorkflow.CreateForApi(ConsoleLogger.Info)
                .AttachProvidedLinkAsync(new ProvidedRecordLinkRequest
                {
                    Group = group, RecordLink = link, Date = date, StartTime = start,
                    // Drive always replaces what is there; a Zoom link never replaces a Drive one.
                    ReplaceExisting = kind == RecordingLinkKind.GoogleDrive,
                }, CancellationToken.None);
            bool ok = outcome.Status is RecordingLinkStatus.Attached or RecordingLinkStatus.AlreadyExists;
            string message = $"{group}: {(kind == RecordingLinkKind.GoogleDrive ? "Drive" : "Zoom")} link {where} - {outcome.Message}";
            await processor.RecordManualAsync(group, date, start,
                kind == RecordingLinkKind.GoogleDrive ? LmsFollowUpStep.AttachDriveLink : LmsFollowUpStep.AttachZoomRecording, ok, message);
            return (ok, message);
        });
    }

    /// <summary>
    /// Every finished class of the last two weeks without its Drive link: looked up in the sheet and
    /// put on the LMS. By the button, or by itself every 30 minutes (each class at most every 2 hours).
    /// </summary>
    public async Task<(bool Ok, string Message)> CheckSheetForAllAsync(bool automatic = false)
    {
        if (!HasDriveSource)
            return (false, "Paste the recordings sheet's link first (Sessions page, settings).");
        var now = DateTime.Now;
        var wanting = Rows.Where(r => r.Date.ToDateTime(r.Start) < now.AddHours(-2) && r.Date >= DateOnly.FromDateTime(now).AddDays(-14))
            .Where(r => r.LinkKind is not "drive" and not "other")
            .Where(r => !automatic || !_sheetTried.TryGetValue($"{r.Group}|{r.Date:yyyy-MM-dd}", out var at) || DateTimeOffset.Now - at > TimeSpan.FromHours(2))
            .ToList();
        if (wanting.Count == 0) return (true, "Every finished class already has its Drive link.");
        int attached = 0; var notes = new List<string>();
        foreach (var row in wanting)
        {
            // Drive first; a class with no link at all also gets its Zoom recording when Drive has none yet.
            var (ok, message) = await RunStepAsync(row.Group, row.Date, row.Start, row.LinkKind == "zoom" ? "sheet" : "recording");
            if (ok) attached++; else notes.Add(message);
        }
        string summary = $"Sheet check: {attached} of {wanting.Count} class(es) now carry their Drive link." + (notes.Count > 0 ? " " + notes[0] : "");
        Status = summary;
        return (attached > 0 || notes.Count == 0, summary);
    }

    /// <summary>Called from TickAsync: the sheet, every 30 minutes, when its link is known.</summary>
    private async Task SweepSheetAsync()
    {
        if (DateTimeOffset.Now - _lastSheetSweep < TimeSpan.FromMinutes(30) || Processor == null) return;
        _lastSheetSweep = DateTimeOffset.Now;
        if (!HasDriveSource) return;
        try { var (_, message) = await CheckSheetForAllAsync(automatic: true); ConsoleLogger.Info($"[LMS] {message}"); }
        catch (Exception ex) { ConsoleLogger.Warn($"[LMS] Sheet check failed: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ material and assignments

    /// <summary>
    /// The classes and what each is about: the schedules here, and the LMS's own list for what they
    /// do not say. A class the schedules do not have (a physical one dropped by an older import) is
    /// taken from the LMS, and a schedule entry whose name does not say its kind ("Week 10 -
    /// Session 1") takes the LMS's focus - so a Freelancing class's material and its next Freelancing
    /// class are known without anyone typing them (2026-09-26, S7 on Fri 25 Sep).
    /// </summary>
    private List<TimetableEntry> Timetable(IEnumerable<MeetingSchedule> schedules) => Timetable(schedules, _cache.Read());

    private static List<TimetableEntry> Timetable(IEnumerable<MeetingSchedule> schedules, IReadOnlyList<LmsSessionCache.Entry> lms)
    {
        LmsSessionRunner.LmsSessionInfo? Listed(string group, DateOnly day, TimeOnly start) => lms
            .Where(c => c.Session.Group.Equals(group, StringComparison.OrdinalIgnoreCase) && c.Session.Date == day && c.Session.Start is { } t
                        && Math.Abs((t.ToTimeSpan() - start.ToTimeSpan()).TotalMinutes) <= 90)
            .OrderByDescending(c => c.ReadAt).Select(c => c.Session).FirstOrDefault();
        var entries = schedules.Where(s => s.OccurrenceDate.HasValue).Select(s =>
        {
            string group = s.GroupName ?? s.AccountId;
            var start = new TimeOnly(s.Time.Hour, s.Time.Minute);
            string name = s.Name;
            if (MaterialPlanner.TrackOf(name).Length == 0 && Listed(group, s.OccurrenceDate!.Value, start) is { Focus.Length: > 0 } session)
                name = $"{name} • {session.Focus}";
            return new TimetableEntry(group, s.OccurrenceDate!.Value, start, name);
        }).ToList();
        foreach (var session in lms.Select(c => c.Session).Where(s => s.Date != null && s.Start != null && s.Focus.Length > 0))
        {
            if (entries.Any(e => e.Group.Equals(session.Group, StringComparison.OrdinalIgnoreCase) && e.Date == session.Date
                                 && Math.Abs((e.Start.ToTimeSpan() - session.Start!.Value.ToTimeSpan()).TotalMinutes) <= 90)) continue;
            entries.Add(new TimetableEntry(session.Group, session.Date!.Value, session.Start!.Value, $"{session.Group} • {session.Title} • {session.Focus}"));
        }
        return entries;
    }

    /// <summary>
    /// Puts the class's material on its session - each file as an attachment - and creates its
    /// assignment when it has one with a deadline. What is already there is left alone, and what was
    /// done is kept so it is never done twice.
    /// </summary>
    private async Task<(bool Ok, string Message)> MaterialAsync(ZoomAutoAdmit.WindowsUI.Services.LmsFollowUpProcessor processor,
        string group, DateOnly date, TimeOnly start, string? chosen = null)
    {
        if (chosen != null)
        {
            if (!Directory.Exists(chosen) && !File.Exists(chosen)) return (false, $"{chosen} is not on this PC any more.");
            ChooseMaterialFolder(group, date, start, chosen);
        }
        var timetable = Timetable(await _schedules.ListAsync());
        var settings = MaterialSettings.Load();
        // The session's LMS title carries its week, which numbers Freelancing and Soft Skills.
        string? lmsTitle = _cache.Read().Where(c => c.Session.Group.Equals(group, StringComparison.OrdinalIgnoreCase) && c.Session.Date == date)
            .OrderByDescending(c => c.ReadAt).Select(c => c.Session.Title).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        var plan = MaterialPlanner.Plan(timetable, group, date, start, settings, lmsTitle);
        string key = MaterialSettings.KeyOf(group, date, start);
        // Without the week the number would come from the timetable, which starts part-way through.
        if (plan.IsFixed && plan.Track != MaterialPlanner.English && MaterialPlanner.WeekOf(lmsTitle) == null && !settings.Folders.ContainsKey(key))
            return (false, $"{group}: the session's week is not known yet (Check LMS reads it), so its {plan.Track} number would be a guess. Nothing was uploaded.");
        settings.Assignments.TryGetValue(key, out var choice);
        var assignment = MaterialPlanner.AssignmentFor(plan, choice, date, start);
        var files = MaterialPlanner.FilesFor(plan, choice);
        if (files.Count == 0 && assignment == null) return (false, $"{group}: {plan.Note}");

        var result = await processor.ExclusiveAsync(() => Task.Run(() => _runner().AddMaterialsAsync(group, date, start, files, assignment)));
        settings = MaterialSettings.Load();                                    // read again: the page may have changed it meanwhile
        if (result.IsSuccess)
        {
            settings.Done[key] = new MaterialRecord(DateTimeOffset.Now, [.. files.Select(f => f.Title)],
                result.AssignmentCreated || result.AssignmentAlreadyThere ? assignment?.Title : null, assignment?.Deadline);
            settings.Errors.Remove(key);
        }
        else settings.Errors[key] = result.Message;
        settings.Save();
        return (result.IsSuccess, result.Message);
    }

    /// <summary>
    /// What a folder or file would put on a class's session, without keeping the choice: the upload
    /// box shows it, and only Upload keeps it.
    /// </summary>
    public async Task<(MaterialPlan Plan, IReadOnlyList<LmsMaterialFile> Files, LmsAssignment? Assignment)> PreviewMaterialAsync(
        string group, DateOnly date, TimeOnly start, string? path)
    {
        var timetable = Timetable(await _schedules.ListAsync());
        var settings = MaterialSettings.Load();
        string key = MaterialSettings.KeyOf(group, date, start);
        if (!string.IsNullOrWhiteSpace(path)) settings.Folders[key] = path;              // this copy only; nothing is saved
        string? lmsTitle = _cache.Read().Where(c => c.Session.Group.Equals(group, StringComparison.OrdinalIgnoreCase) && c.Session.Date == date)
            .OrderByDescending(c => c.ReadAt).Select(c => c.Session.Title).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        var plan = MaterialPlanner.Plan(timetable, group, date, start, settings, lmsTitle);
        settings.Assignments.TryGetValue(key, out var choice);
        return (plan, MaterialPlanner.FilesFor(plan, choice), MaterialPlanner.AssignmentFor(plan, choice, date, start));
    }

    /// <summary>
    /// Forgets a class on this PC (the admin clearing a test session): what it still owed and what
    /// was done for it, including a meeting opened by hand near its time. Nothing on the LMS or in
    /// Zoom is touched. A class with a schedule keeps its card until the schedule is deleted.
    /// </summary>
    public async Task<string> DeleteClassAsync(string group, DateOnly date, TimeOnly start)
    {
        var schedules = await _schedules.ListAsync();
        int removed = await _queue.ForgetClassAsync(group, date,
            t => (ScheduleTiming.ClassStartNear(schedules, group, date, t) ?? t) == start);
        bool scheduled = schedules.Any(s => s.OccurrenceDate == date && new TimeOnly(s.Time.Hour, s.Time.Minute) == start &&
                                            (s.GroupName ?? s.AccountId).Equals(group, StringComparison.OrdinalIgnoreCase));
        ConsoleLogger.Info($"[LMS] Deleted {group} {date:yyyy-MM-dd} {start:HH\\:mm} from this PC ({removed} entries).");
        await ReloadAsync();
        return scheduled
            ? $"{group} {date:dd MMM} {start:HH\\:mm}: its steps were forgotten; it is still on the Schedules page, so the card stays until that schedule is deleted."
            : $"{group} {date:dd MMM} {start:HH\\:mm} was deleted from this PC.";
    }

    /// <summary>The folder chosen for one class (a technical class's material), or none.</summary>
    public void ChooseMaterialFolder(string group, DateOnly date, TimeOnly start, string? folder)
    {
        var settings = MaterialSettings.Load();
        string key = MaterialSettings.KeyOf(group, date, start);
        if (string.IsNullOrWhiteSpace(folder)) settings.Folders.Remove(key); else settings.Folders[key] = folder;
        settings.Done.Remove(key);                     // a new folder is new material
        settings.Errors.Remove(key);
        settings.Save();
    }

    /// <summary>The assignment chosen for one class: title, description, deadline and its own file - or that it has none.</summary>
    public void ChooseAssignment(string group, DateOnly date, TimeOnly start, string? title, DateTime? deadline, bool none,
        string? description = null, string? file = null)
    {
        var settings = MaterialSettings.Load();
        string key = MaterialSettings.KeyOf(group, date, start);
        settings.Assignments[key] = new AssignmentChoice(string.IsNullOrWhiteSpace(title) ? null : title.Trim(), deadline, none,
            string.IsNullOrWhiteSpace(description) ? null : description.Trim(), string.IsNullOrWhiteSpace(file) ? null : file);
        if (settings.Done.TryGetValue(key, out var done) && done.Assignment == null) settings.Done.Remove(key);   // so the assignment still goes up
        settings.Save();
    }

    /// <summary>A track's folder (Freelancing, Soft Skills, English).</summary>
    public void ChooseTrackFolder(string track, string folder)
    {
        var settings = MaterialSettings.Load();
        settings.Tracks[track] = folder;
        settings.Save();
    }

    public MaterialSettings Materials => MaterialSettings.Load();

    private readonly Dictionary<string, DateTimeOffset> _materialTried = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastMaterialSweep = DateTimeOffset.MinValue;

    /// <summary>
    /// Called from TickAsync: today's Freelancing, Soft Skills and English classes get their material
    /// once they have started (ten minutes in, clear of the Run Session at the start), each at most
    /// every 30 minutes until it is up.
    /// </summary>
    private async Task SweepMaterialsAsync()
    {
        if (Processor == null || DateTimeOffset.Now - _lastMaterialSweep < TimeSpan.FromMinutes(5)) return;
        _lastMaterialSweep = DateTimeOffset.Now;
        var now = DateTime.Now;
        // Today's classes, and one of the last two days whose material never went up (its kind was
        // not known then, or the app was closed): it is put on its session late rather than never.
        var due = Rows.Where(r => r.Material is { Fixed: true, Done: false, RemovedOnLms: false, Files.Count: > 0 })
            .Where(r => now >= r.Date.ToDateTime(r.Start).AddMinutes(10) && now <= r.Date.ToDateTime(r.Start).AddHours(48))
            .Where(r => !_materialTried.TryGetValue(r.Key, out var at) || DateTimeOffset.Now - at > TimeSpan.FromMinutes(30))
            .ToList();
        foreach (var row in due)
        {
            _materialTried[row.Key] = DateTimeOffset.Now;
            try
            {
                var (_, message) = await RunStepAsync(row.Group, row.Date, row.Start, "material");
                ConsoleLogger.Info($"[LMS] Material for {row.Group} {row.Date:yyyy-MM-dd}: {message}");
            }
            catch (Exception ex) { ConsoleLogger.Warn($"[LMS] Material for {row.Group} failed: {ex.Message}"); }
        }
    }

    public void UseAccount(string id)
    {
        SelectedAccount = Accounts.FirstOrDefault(a => a.Id == id);
        UseAccount();
    }

    public void RemoveAccount(string id)
    {
        SelectedAccount = Accounts.FirstOrDefault(a => a.Id == id);
        RemoveAccount();
    }

    public void SaveAccount(string label, string email, string role, string? password, bool makeActive)
    {
        NewLabel = label; NewEmail = email; NewRole = role is "admin" ? "admin" : "coordinator";
        SaveAccount(password, makeActive);
    }
}
