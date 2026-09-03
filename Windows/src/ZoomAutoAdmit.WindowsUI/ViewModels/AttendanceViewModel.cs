using System.Collections.ObjectModel;
using System.IO;
using ZoomAutoAdmit.Attendance;
using ZoomAutoAdmit.AttendanceMatching;
using ZoomAutoAdmit.Roster;
using System.ComponentModel;
using ZoomAutoAdmit.WindowsUI.Infrastructure;
using ZoomAutoAdmit.WindowsUI.Services;

namespace ZoomAutoAdmit.WindowsUI.ViewModels;

/// <summary>How a row is tinted. The view maps these to the theme's own brushes.</summary>
public enum AttendanceTint { Present, NeedsReview, NotSeen, Unmatched, Absent }

/// <summary>
/// One person in the reviewed list. Key identifies a student, or the observed Zoom name for an
/// unmatched row. Percent is empty when there is nothing to score.
/// </summary>
public sealed record AttendanceRow(string Key, string Name, string Detail, string Percent, AttendanceTint Tint)
{
    /// <summary>Rows that are already present can only be cleared; the rest can be matched or marked absent.</summary>
    public bool CanMatch => Tint is AttendanceTint.NeedsReview or AttendanceTint.NotSeen or AttendanceTint.Absent;
    public bool CanMarkAbsent => Tint is AttendanceTint.NeedsReview or AttendanceTint.NotSeen or AttendanceTint.Present;
    public bool CanClear => Tint is AttendanceTint.Present or AttendanceTint.Absent;
    public bool CanAssign => Tint == AttendanceTint.Unmatched;
}

/// <summary>A titled block of rows, e.g. "Needs review - 3".</summary>
public sealed record AttendanceGroupDisplay(string Title, int Count, AttendanceTint Tint, IReadOnlyList<AttendanceRow> Rows)
{
    public string Header => $"{Title} \u00b7 {Count}";
}

/// <summary>
/// One meeting, not one reading of it. The page is about a session that was run, so the picker
/// lists sessions and the readings behind each one are merged without being asked about.
/// </summary>
public sealed record AttendanceSessionDisplay(Guid SessionId, string Title, string Subtitle, SnapshotDisplay Latest);

public sealed class AttendanceViewModel : ObservableObject, IDisposable
{
    private readonly IAttendanceHistoryReader _reader;
    private readonly IAttendanceUiActions? _actions;
    private readonly IAttendanceHistoryEraser? _eraser;
    private readonly AiMatchingViewModel _matching;
    private readonly CancellationTokenSource _lifetime = new();
    private SnapshotDisplay? _selected;
    private AttendanceSessionDisplay? _session;
    private readonly Func<IReadOnlyList<RosterGroup>> _rosterGroups;
    private bool _selecting;
    private bool _idle = true;
    private bool _mergeWholeSession = true;
    private IReadOnlyList<AttendanceCaptureIssue> _issues = [];
    private string _status = "Loading the sessions that have been run.";
    public AttendanceViewModel(
        IAttendanceHistoryReader reader,
        AiMatchingViewModel matching,
        IAttendanceUiActions? actions = null,
        IAttendanceDialogs? dialogs = null,
        Func<IReadOnlyList<RosterGroup>>? rosterGroups = null)
    {
        _reader = reader; _matching = matching; _actions = actions; _dialogs = dialogs;
        _rosterGroups = rosterGroups ?? (() => []);
        _eraser = reader as IAttendanceHistoryEraser;
        _matching.PropertyChanged += OnMatchingChanged;
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
        MatchCommand = new AsyncRelayCommand(_ => MatchSelectedAsync());
        CaptureCommand = new AsyncRelayCommand(_ => CaptureAsync());
        DeleteSessionsCommand = new AsyncRelayCommand(_ => DeleteSessionsAsync());
        RowMatchCommand = new RelayCommand(row => MatchRow(row as AttendanceRow));
        RowAbsentCommand = new RelayCommand(row => MarkAbsent(row as AttendanceRow));
        RowClearCommand = new RelayCommand(row => ClearRow(row as AttendanceRow));
        RowAssignCommand = new RelayCommand(row => AssignObservedName(row as AttendanceRow));
        CopyListCommand = new RelayCommand(_ => CopyFullList());
        ExportCsvCommand = new RelayCommand(_ => ExportCsv());
        FinalizeCommand = new RelayCommand(_ => Finalize());
        ShowReviewCommand = new RelayCommand(_ => IsRosterOrder = false);
        ShowRosterOrderCommand = new RelayCommand(_ => IsRosterOrder = true);
    }

    private readonly IAttendanceDialogs? _dialogs;
    // Manual decisions taken on this page. They never change the roster, the snapshots or the
    // matching engine; they only decide what this session's final list says.
    private readonly Dictionary<string, string?> _markedPresent = new(StringComparer.Ordinal);
    private readonly HashSet<string> _markedAbsent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _assignedNames = new(StringComparer.Ordinal);
    private bool _rosterOrder;
    private string _search = string.Empty;

    public RelayCommand RowMatchCommand { get; }
    public RelayCommand RowAbsentCommand { get; }
    public RelayCommand RowClearCommand { get; }
    public RelayCommand RowAssignCommand { get; }
    public RelayCommand CopyListCommand { get; }
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand FinalizeCommand { get; }
    public RelayCommand ShowReviewCommand { get; }
    public RelayCommand ShowRosterOrderCommand { get; }

    /// <summary>Review groups people by state; Roster order lists everyone in the roster's own order.</summary>
    public bool IsRosterOrder
    {
        get => _rosterOrder;
        set { if (SetProperty(ref _rosterOrder, value)) { OnPropertyChanged(nameof(IsReviewOrder)); RebuildSummary(); } }
    }
    public bool IsReviewOrder => !_rosterOrder;
    public string SearchText
    {
        get => _search;
        set { if (SetProperty(ref _search, value)) RebuildSummary(); }
    }
    public ObservableCollection<SnapshotDisplay> Snapshots { get; } = [];
    public ObservableCollection<ParticipantPresence> Participants { get; } = [];
    public ObservableCollection<StudentMatchResult> Results { get; } = [];
    public ObservableCollection<AttendanceReviewItem> Review { get; } = [];

    /// <summary>
    /// The reviewed session at a glance, built only from the match results already produced:
    /// no extra matching, no absence is inferred and no roster order is changed.
    /// </summary>
    public ObservableCollection<AttendanceGroupDisplay> Groups { get; } = [];
    public int PresentCount => EffectiveRows().Count(row => row.Tint == AttendanceTint.Present);
    public int NeedsReviewCount => EffectiveRows().Count(row => row.Tint == AttendanceTint.NeedsReview);
    public int NotSeenCount => EffectiveRows().Count(row => row.Tint == AttendanceTint.NotSeen);
    public int AbsentCount => _markedAbsent.Count;
    public int UnmatchedCount => UnmatchedRows().Count();
    public bool HasSummary => Results.Count > 0 || Review.Count > 0;
    public bool HasNoSummary => !HasSummary;

    /// <summary>Sessions that have been run, newest first. Every reading of one is merged into it.</summary>
    public ObservableCollection<AttendanceSessionDisplay> Sessions { get; } = [];

    public AttendanceSessionDisplay? SelectedSession
    {
        get => _session;
        set
        {
            if (!SetProperty(ref _session, value)) return;
            OnPropertyChanged(nameof(SessionTitle));
            OnPropertyChanged(nameof(SessionSubtitle));
            if (_selecting) return;          // The refresh below re-selects the same session itself.
            SelectedSnapshot = value?.Latest;
            _ = AutoMatchAsync();
        }
    }

    public string SessionTitle => SelectedSession?.Title ?? "No session yet";
    public string SessionSubtitle => SelectedSession?.Subtitle
        ?? "Run a session and who attended it is collected here.";

    /// <summary>
    /// A session already says which group it is - the meeting runs under that group's own account -
    /// so the roster group is looked up here rather than asked for, and matching starts by itself.
    /// </summary>
    private async Task AutoMatchAsync()
    {
        var session = SelectedSession;
        if (session == null) return;
        var meeting = session.Latest.Snapshot.Meeting;
        var group = FindGroup(meeting?.AccountId) ?? FindGroup(meeting?.AccountDisplayName);
        if (group == null)
        {
            Status = string.IsNullOrWhiteSpace(meeting?.AccountId)
                ? "This session did not record the account it ran under, so its group is unknown."
                : $"No group is named {meeting.AccountId}. Rename the group to match, and this session matches itself.";
            return;
        }
        _matching.SelectedGroup = group;
        await MatchSelectedAsync();
    }

    private RosterGroup? FindGroup(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var groups = _rosterGroups();
        return groups.FirstOrDefault(item => string.Equals(item.GroupId, name, StringComparison.OrdinalIgnoreCase))
            ?? groups.FirstOrDefault(item => string.Equals(item.DisplayName, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Groups the loaded readings into sessions, keeping the one already being looked at, and says
    /// whether the session changed. Matching is left to the caller: a refresh holds IsIdle, and a
    /// match started underneath it would be dropped.
    /// </summary>
    private bool RebuildSessions()
    {
        var built = Snapshots
            .GroupBy(item => item.Snapshot.SessionId)
            .Select(group => Describe(group.OrderByDescending(item => item.Snapshot.Timestamp).ToArray()))
            .OrderByDescending(item => item.Latest.Snapshot.Timestamp)
            .ToArray();
        Sessions.Clear();
        foreach (var item in built) Sessions.Add(item);
        var keep = _session == null
            ? built.FirstOrDefault()
            : built.FirstOrDefault(item => item.SessionId == _session.SessionId) ?? built.FirstOrDefault();
        bool changed = keep?.SessionId != _session?.SessionId;
        _selecting = true;
        try { SelectedSession = keep; }
        finally { _selecting = false; }
        // Only a different session is worth matching again; a refresh of the same one is not.
        if (changed && keep != null) SelectedSnapshot = keep.Latest;
        return changed && keep != null;
    }

    private static AttendanceSessionDisplay Describe(IReadOnlyList<SnapshotDisplay> readings)
    {
        var latest = readings[0];
        var meeting = latest.Snapshot.Meeting;
        string group = string.IsNullOrWhiteSpace(meeting?.AccountDisplayName) ? "Unknown group" : meeting.AccountDisplayName;
        var start = meeting?.ScheduledStart.LocalDateTime ?? readings[^1].Snapshot.Timestamp.LocalDateTime;
        int seen = MergeParticipants(readings.Select(item => item.Snapshot)).Count;
        return new AttendanceSessionDisplay(
            latest.Snapshot.SessionId,
            $"{group} · {start:d MMM yyyy} at {start:h:mm tt}",
            $"{seen} name{(seen == 1 ? "" : "s")} seen · {readings.Count} reading{(readings.Count == 1 ? "" : "s")} · last at {latest.Snapshot.Timestamp.LocalDateTime:h:mm tt}",
            latest);
    }
    /// <summary>How many snapshots this reviewed session is based on.</summary>
    public string EvidenceText
    {
        get
        {
            int count = SessionSnapshots.Count;
            return count == 0 ? string.Empty : $"Evidence: {count} snapshot{(count == 1 ? "" : "s")}";
        }
    }
    public string SummaryHeadline => Results.Count == 0 && Review.Count == 0
        ? "No matched session yet"
        : $"{Results.Count} student{(Results.Count == 1 ? "" : "s")} in the roster \u00b7 {Participants.Count} observed name{(Participants.Count == 1 ? "" : "s")}";

    /// <summary>Every student with the manual decisions applied, in roster order.</summary>
    private IEnumerable<AttendanceRow> EffectiveRows() =>
        Results.OrderBy(item => item.Order).Select(ToRow);

    private AttendanceRow ToRow(StudentMatchResult item)
    {
        if (_markedAbsent.Contains(item.StudentId))
            return new AttendanceRow(item.StudentId, item.StudentName, "Marked absent here", string.Empty, AttendanceTint.Absent);

        if (_markedPresent.TryGetValue(item.StudentId, out var manualName))
            return new AttendanceRow(
                item.StudentId,
                item.StudentName,
                manualName == null ? "Confirmed present here" : $"Zoom: {manualName} \u00b7 matched here",
                "100%",
                AttendanceTint.Present);

        var tint = item.Status switch
        {
            AttendanceMatchStatus.Present => AttendanceTint.Present,
            AttendanceMatchStatus.NeedsReview => AttendanceTint.NeedsReview,
            _ => AttendanceTint.NotSeen
        };
        return new AttendanceRow(item.StudentId, item.StudentName, DescribeMatch(item), DescribePercent(item), tint);
    }

    private IEnumerable<AttendanceRow> UnmatchedRows() => Review
        .Where(item => !_assignedNames.ContainsKey(item.ObservedName))
        .Select(item => new AttendanceRow(item.ObservedName, item.ObservedName, item.Reason, string.Empty, AttendanceTint.Unmatched));

    private bool Matches(AttendanceRow row) =>
        string.IsNullOrWhiteSpace(_search) ||
        row.Name.Contains(_search.Trim(), StringComparison.OrdinalIgnoreCase) ||
        row.Detail.Contains(_search.Trim(), StringComparison.OrdinalIgnoreCase);

    private void RebuildSummary()
    {
        Groups.Clear();
        var rows = EffectiveRows().Where(Matches).ToArray();
        var unmatched = UnmatchedRows().Where(Matches).ToArray();

        if (IsRosterOrder)
        {
            if (rows.Length > 0) Groups.Add(new AttendanceGroupDisplay("Roster order", rows.Length, AttendanceTint.NotSeen, rows));
        }
        else
        {
            AddGroup("Needs review", AttendanceTint.NeedsReview, rows);
            AddGroup("Present", AttendanceTint.Present, rows);
            AddGroup("Marked absent", AttendanceTint.Absent, rows);
            AddGroup("Not seen yet", AttendanceTint.NotSeen, rows);
        }
        if (unmatched.Length > 0)
            Groups.Add(new AttendanceGroupDisplay("Unmatched Zoom names", unmatched.Length, AttendanceTint.Unmatched, unmatched));

        foreach (var name in new[]
        {
            nameof(PresentCount), nameof(NeedsReviewCount), nameof(NotSeenCount), nameof(AbsentCount),
            nameof(UnmatchedCount), nameof(HasSummary), nameof(HasNoSummary), nameof(SummaryHeadline), nameof(EvidenceText)
        }) OnPropertyChanged(name);
    }

    private void AddGroup(string title, AttendanceTint tint, IReadOnlyList<AttendanceRow> rows)
    {
        var selected = rows.Where(row => row.Tint == tint).ToArray();
        if (selected.Length > 0) Groups.Add(new AttendanceGroupDisplay(title, selected.Length, tint, selected));
    }

    private static string DescribeMatch(StudentMatchResult item) =>
        item.ObservedNames.Count == 0
            ? "No Zoom identity in any snapshot"
            : $"Zoom: {string.Join(", ", item.ObservedNames)}" +
              (item.MatchSource == MatchSource.None ? string.Empty : $" \u00b7 {DescribeSource(item.MatchSource)}");

    private static string DescribeSource(MatchSource source) => source switch
    {
        MatchSource.Rule => "name rule",
        MatchSource.Alias => "approved alias",
        MatchSource.AI => "AI, needs review",
        _ => "unmatched"
    };

    private static string DescribePercent(StudentMatchResult item) =>
        item.Status == AttendanceMatchStatus.NotObserved ? string.Empty : $"{item.Confidence}%";

    // ---------------------------------------------------------------- manual decisions

    private void MatchRow(AttendanceRow? row)
    {
        if (row == null || _dialogs == null) return;
        var taken = new HashSet<string>(_assignedNames.Keys, StringComparer.Ordinal);
        var options = Participants
            .Select(participant => participant.Name)
            .Distinct(StringComparer.Ordinal)
            .Where(name => !taken.Contains(name))
            .Select(name => new PickerOption(name, name, "Observed in this session"))
            .ToArray();
        string? chosen = _dialogs.Pick($"Match {row.Name}", "Pick the Zoom name this student joined with.", options);
        if (chosen == null) return;
        _markedAbsent.Remove(row.Key);
        _markedPresent[row.Key] = chosen;
        _assignedNames[chosen] = row.Key;
        Status = $"{row.Name} marked present from '{chosen}'. Nothing was written to the roster.";
        RebuildSummary();
    }

    private void MarkAbsent(AttendanceRow? row)
    {
        if (row == null) return;
        if (_markedPresent.Remove(row.Key, out var previous) && previous != null) _assignedNames.Remove(previous);
        _markedAbsent.Add(row.Key);
        Status = $"{row.Name} marked absent for this session only.";
        RebuildSummary();
    }

    private void ClearRow(AttendanceRow? row)
    {
        if (row == null) return;
        if (_markedPresent.Remove(row.Key, out var previous) && previous != null) _assignedNames.Remove(previous);
        _markedAbsent.Remove(row.Key);
        Status = $"{row.Name} returned to the matched result.";
        RebuildSummary();
    }

    private void AssignObservedName(AttendanceRow? row)
    {
        if (row == null || _dialogs == null) return;
        var options = Results
            .OrderBy(item => item.Order)
            .Select(item => new PickerOption(item.StudentId, item.StudentName, $"#{item.Order} \u00b7 {item.Status}"))
            .ToArray();
        string? studentId = _dialogs.Pick($"Assign '{row.Name}'", "Pick the student this Zoom name belongs to.", options);
        if (studentId == null) return;
        _markedAbsent.Remove(studentId);
        _markedPresent[studentId] = row.Key;
        _assignedNames[row.Key] = studentId;
        var student = Results.FirstOrDefault(item => item.StudentId == studentId);
        Status = $"'{row.Name}' assigned to {student?.StudentName ?? studentId} for this session.";
        RebuildSummary();
    }

    // ---------------------------------------------------------------- output

    /// <summary>Roster order, one line per student, with the state this page currently shows.</summary>
    public IReadOnlyList<(int Order, string Name, string State, string Detail)> FinalList() =>
        Results.OrderBy(item => item.Order).Select(item =>
        {
            var row = ToRow(item);
            string state = row.Tint switch
            {
                AttendanceTint.Present => "Present",
                AttendanceTint.NeedsReview => "Needs review",
                AttendanceTint.Absent => "Absent",
                _ => "Not seen"
            };
            return (item.Order, item.StudentName, state, row.Detail);
        }).ToArray();

    private void CopyFullList()
    {
        var list = FinalList();
        if (list.Count == 0) { Status = "Match a session first; there is no list to copy."; return; }
        string text = string.Join(Environment.NewLine,
            list.Select(row => $"{row.Order}. {row.Name} - {row.State}" + (row.Detail.Length == 0 ? "" : $" ({row.Detail})")));
        try
        {
            System.Windows.Clipboard.SetText(text);
            Status = $"Copied {list.Count} students in roster order.";
        }
        catch { Status = "The clipboard is not available right now."; }
    }

    private void ExportCsv()
    {
        var list = FinalList();
        if (list.Count == 0) { Status = "Match a session first; there is nothing to export."; return; }
        if (_dialogs == null) { Status = "Export is unavailable in this window."; return; }
        string suggested = $"attendance-{DateTime.Now:yyyyMMdd-HHmm}.csv";
        string? path = _dialogs.SaveCsvPath(suggested);
        if (path == null) return;
        try
        {
            var lines = new List<string> { "Order,Student,State,Detail" };
            lines.AddRange(list.Select(row => string.Join(",",
                row.Order, Csv(row.Name), Csv(row.State), Csv(row.Detail))));
            File.WriteAllLines(path, lines, System.Text.Encoding.UTF8);
            Status = $"Exported {list.Count} students to {Path.GetFileName(path)}.";
        }
        catch { Status = "The file could not be written. Check the folder and try again."; }
    }

    private static string Csv(string value) =>
        value.Contains(',') || value.Contains('"') ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    private void Finalize()
    {
        var list = FinalList();
        if (list.Count == 0) { Status = "Match a session first; there is nothing to finalize."; return; }
        if (_dialogs == null) { Status = "Finalizing is unavailable in this window."; return; }
        int present = list.Count(row => row.State == "Present");
        int review = list.Count(row => row.State == "Needs review");
        int absent = list.Count(row => row.State == "Absent");
        int unseen = list.Count(row => row.State == "Not seen");
        string summary = $"Present {present} \u00b7 Needs review {review} \u00b7 Absent {absent} \u00b7 Not seen {unseen}";
        if (review > 0) summary += $"{Environment.NewLine}{review} student(s) are still marked needs review.";
        if (!_dialogs.ConfirmFinalize(summary)) return;
        try
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ZoomAutoAdmit", "Attendance", "Finalized");
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, $"attendance-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            var lines = new List<string> { "Order,Student,State,Detail" };
            lines.AddRange(list.Select(row => string.Join(",", row.Order, Csv(row.Name), Csv(row.State), Csv(row.Detail))));
            File.WriteAllLines(file, lines, System.Text.Encoding.UTF8);
            Status = $"Finalized. {summary}. Saved as {Path.GetFileName(file)}.";
        }
        catch { Status = "The finalized list could not be saved. Check local file permissions."; }
    }
    public SnapshotDisplay? SelectedSnapshot
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            LoadParticipants();
            _matching.ClearResults();
            Results.Clear(); Review.Clear();
            ClearManualDecisions();
            RebuildSummary();
        }
    }
    /// <summary>On by default: one snapshot only ever shows who was on screen at that moment.</summary>
    public bool MergeWholeSession
    {
        get => _mergeWholeSession;
        set
        {
            if (!SetProperty(ref _mergeWholeSession, value)) return;
            LoadParticipants();
            _matching.ClearResults();
            Results.Clear(); Review.Clear();
            ClearManualDecisions();
            RebuildSummary();
        }
    }
    public ObservableCollection<AttendanceCaptureIssue> CaptureIssues { get; } = [];
    /// <summary>Empty unless captures failed for this session; a failed read is not an empty meeting.</summary>
    public string CaptureWarning => CaptureIssues.Count == 0 ? string.Empty
        : $"{CaptureIssues.Count} capture attempt{(CaptureIssues.Count == 1 ? "" : "s")} failed for this session — names may be missing. Last: {CaptureIssues[0].Timestamp.LocalDateTime:g} · {CaptureIssues[0].Trigger} · {CaptureIssues[0].Reason}";
    public bool HasCaptureIssues => CaptureIssues.Count > 0;

    private IReadOnlyList<SnapshotDisplay> SessionSnapshots => SelectedSnapshot == null
        ? []
        : Snapshots.Where(item => item.Snapshot.SessionId == SelectedSnapshot.Snapshot.SessionId).ToArray();

    private void LoadParticipants()
    {
        Participants.Clear();
        var sessionSnapshots = SessionSnapshots;
        IReadOnlyList<ParticipantPresence> names = SelectedSnapshot == null
            ? []
            : MergeWholeSession
                ? MergeParticipants(sessionSnapshots.Select(item => item.Snapshot))
                : SelectedSnapshot.Snapshot.Participants;
        foreach (var participant in names) Participants.Add(participant);
        CaptureIssues.Clear();
        if (SelectedSnapshot != null)
            foreach (var issue in _issues.Where(issue => issue.SessionId == SelectedSnapshot.Snapshot.SessionId))
                CaptureIssues.Add(issue);
        OnPropertyChanged(nameof(SnapshotDetails));
        OnPropertyChanged(nameof(CaptureWarning));
        OnPropertyChanged(nameof(HasCaptureIssues));
    }

    /// <summary>Union of every snapshot in the session, keeping the highest count seen for a repeated name.</summary>
    public static IReadOnlyList<ParticipantPresence> MergeParticipants(IEnumerable<AttendanceSnapshot> snapshots)
    {
        Dictionary<string, int> highest = new(StringComparer.Ordinal);
        List<string> order = [];
        foreach (var snapshot in snapshots)
        {
            Dictionary<string, int> counts = new(StringComparer.Ordinal);
            foreach (var participant in snapshot.Participants)
            {
                if (!highest.ContainsKey(participant.Name)) { highest[participant.Name] = 0; order.Add(participant.Name); }
                counts[participant.Name] = counts.GetValueOrDefault(participant.Name) + 1;
            }
            foreach (var pair in counts) if (pair.Value > highest[pair.Key]) highest[pair.Key] = pair.Value;
        }
        return order.SelectMany(name => Enumerable.Repeat(new ParticipantPresence(name), highest[name])).ToArray();
    }

    public string SnapshotDetails
    {
        get
        {
            if (SelectedSnapshot == null) return "No snapshot selected. No absence is inferred.";
            var snapshot = SelectedSnapshot.Snapshot;
            var sessionSnapshots = SessionSnapshots;
            string scope = MergeWholeSession
                ? $"Whole session: {sessionSnapshots.Count} snapshot{(sessionSnapshots.Count == 1 ? "" : "s")} merged" +
                  (sessionSnapshots.Count > 1
                      ? $" ({sessionSnapshots.Min(item => item.Snapshot.Timestamp).LocalDateTime:HH:mm}–{sessionSnapshots.Max(item => item.Snapshot.Timestamp).LocalDateTime:HH:mm})"
                      : "")
                : $"This snapshot only · Reason: {snapshot.Reason}";
            return $"Session: {snapshot.SessionId} · Account: {snapshot.Meeting?.AccountId ?? "unknown"} · {scope} · " +
                   $"{Participants.Count} raw names · Missing names are never absences.";
        }
    }
    public bool IsIdle { get => _idle; private set => SetProperty(ref _idle, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand MatchCommand { get; }
    public AsyncRelayCommand CaptureCommand { get; }
    public AsyncRelayCommand DeleteSessionsCommand { get; }

    /// <summary>
    /// Clears sessions out of the store: this one, everything older than it, or all of them. A
    /// session is deleted whole - its readings go with it - and nothing goes without being asked.
    /// </summary>
    public async Task DeleteSessionsAsync()
    {
        if (!IsIdle) return;
        if (_eraser == null || _dialogs == null) { Status = "Deleting sessions is unavailable in this window."; return; }
        var session = SelectedSession;
        if (session == null) { Status = "There is no session to delete."; return; }

        var older = Sessions.Where(item => item.Latest.Snapshot.Timestamp < session.Latest.Snapshot.Timestamp).ToArray();
        List<PickerOption> options =
        [
            new("one", "This session only", session.Title),
            new("all", $"Every session \u00b7 {Sessions.Count}", "Clears the whole list and starts again."),
        ];
        if (older.Length > 0)
            options.Insert(1, new("older", $"Every session older than this one \u00b7 {older.Length}", "Keeps this one and anything newer."));

        string? choice = _dialogs.Pick("Delete sessions", "Old sessions and the readings behind them are removed from this computer.", options);
        if (choice == null) return;
        Guid[] targets = choice switch
        {
            "one" => [session.SessionId],
            "older" => older.Select(item => item.SessionId).ToArray(),
            "all" => Sessions.Select(item => item.SessionId).ToArray(),
            _ => [],
        };
        if (targets.Length == 0) return;
        if (!_dialogs.ConfirmDelete($"{targets.Length} session{(targets.Length == 1 ? "" : "s")} will be deleted.")) return;

        bool droppedSelection = targets.Contains(session.SessionId);
        int deleted;
        IsIdle = false;
        try { deleted = await _eraser.DeleteSessionsAsync(targets, _lifetime.Token); }
        catch (OperationCanceledException) { return; }
        catch { Status = "The sessions could not be deleted. Check local file permissions; nothing else was changed."; return; }
        finally { IsIdle = true; }

        // The list on screen came from a session that may no longer exist.
        if (droppedSelection) SelectedSession = null;
        await RefreshAsync();
        Status = deleted == targets.Length
            ? $"Deleted {deleted} session{(deleted == 1 ? "" : "s")}. {Status}"
            : $"Deleted {deleted} of {targets.Length} sessions; the rest could not be removed. {Status}";
    }

    public async Task RefreshAsync()
    {
        if (!IsIdle) return;
        IsIdle = false;
        bool sessionChanged = false;
        try
        {
            var history = await _reader.ReadAsync(_lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            // Don't silently switch the selected session when another session writes a snapshot.
            var id = SelectedSnapshot?.Id;
            var keep = history.Snapshots.Select(s => s.Id).ToHashSet();
            foreach (var item in Snapshots.Where(s => !keep.Contains(s.Id)).ToArray()) Snapshots.Remove(item);
            for (var index = 0; index < history.Snapshots.Count; index++)
            {
                var item = history.Snapshots[index];
                var previous = Snapshots.FirstOrDefault(s => s.Id == item.Id);
                if (previous == null) Snapshots.Insert(index, item);
                else
                {
                    var currentIndex = Snapshots.IndexOf(previous);
                    if (currentIndex != index) Snapshots.Move(currentIndex, index);
                }
            }
            _issues = history.CaptureIssues;
            var restored = id == null ? Snapshots.FirstOrDefault() : Snapshots.FirstOrDefault(s => s.Id == id);
            if (ReferenceEquals(restored, SelectedSnapshot)) LoadParticipants();   // Same session, new snapshots: re-merge.
            else SelectedSnapshot = restored;
            sessionChanged = RebuildSessions();
            Status = Sessions.Count == 0
                ? "No session has been recorded yet. Run one and its attendance appears here."
                : $"{Sessions.Count} session{(Sessions.Count == 1 ? "" : "s")} recorded.";
            if (history.Unreadable > 0) Status += $" {history.Unreadable} unreadable readings skipped.";
            if (CaptureIssues.Count > 0) Status += $" {CaptureIssues.Count} reading{(CaptureIssues.Count == 1 ? "" : "s")} of this session failed, so a name may be missing.";
            if (history.Limited) Status += " Showing the most recent 2,000 readings.";
        }
        catch (OperationCanceledException) { }
        catch { Status = "Attendance storage could not be read. Check local file permissions; no data was changed."; }
        finally { IsIdle = true; }
        if (sessionChanged) await AutoMatchAsync();
    }

    public async Task MatchSelectedAsync()
    {
        if (!IsIdle || !_matching.IsIdle) return;
        if (SelectedSnapshot == null || Participants.Count == 0) { Status = "No session with observed names is selected, so nothing was matched."; return; }
        IsIdle = false;
        try
        {
            Results.Clear(); Review.Clear();
            ClearManualDecisions();
            RebuildSummary();
            var snapshot = SelectedSnapshot;
            var group = _matching.SelectedGroup;
            _matching.ObservedNames = string.Join(Environment.NewLine, Participants.Select(p => p.Name));
            await _matching.MatchAsync();
            if (SelectedSnapshot != snapshot || _matching.SelectedGroup != group) return;
            foreach (var row in _matching.Results) Results.Add(row);
            foreach (var item in _matching.Review) Review.Add(item);
            RebuildSummary();
            Status = _matching.Status;
        }
        finally { IsIdle = true; }
    }

    public async Task CaptureAsync()
    {
        if (!IsIdle) return;
        if (_actions == null) { Status = "Capture is unavailable in this window."; return; }
        IsIdle = false;
        string message;
        try
        {
            // With no snapshot yet (every earlier read failed) the live meetings are still capturable.
            IReadOnlyList<Guid> targets = SelectedSnapshot != null
                ? [SelectedSnapshot.Snapshot.SessionId]
                : await _actions.GetActiveSessionIdsAsync(_lifetime.Token);
            if (targets.Count == 0)
            {
                message = "No meeting is running. Start a meeting, then capture.";
            }
            else
            {
                var replies = new List<string>();
                foreach (var sessionId in targets)
                {
                    _lifetime.Token.ThrowIfCancellationRequested();
                    replies.Add((await _actions.CaptureAttendanceAsync(sessionId, _lifetime.Token)).Message);
                }
                message = targets.Count == 1
                    ? replies[0]
                    : $"Capture requested for {targets.Count} running meetings. {replies.Distinct().First()}";
            }
        }
        catch (OperationCanceledException) { message = "Capture cancelled."; }
        catch { message = "Capture failed. See attendance runtime logs."; }
        finally { IsIdle = true; }
        await RefreshAsync();
        Status = message;
    }
    /// <summary>A different snapshot, group or match run invalidates decisions taken on the old one.</summary>
    private void ClearManualDecisions()
    {
        _markedPresent.Clear();
        _markedAbsent.Clear();
        _assignedNames.Clear();
    }

    private void OnMatchingChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(AiMatchingViewModel.SelectedGroup)) { Results.Clear(); Review.Clear(); ClearManualDecisions(); RebuildSummary(); } }
    public void Dispose() { _matching.PropertyChanged -= OnMatchingChanged; _lifetime.Cancel(); _lifetime.Dispose(); }
}
