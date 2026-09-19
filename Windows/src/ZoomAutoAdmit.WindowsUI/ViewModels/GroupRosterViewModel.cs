using System.Collections.ObjectModel;
using System.Windows.Input;
using ZoomAutoAdmit.Roster;
using ZoomAutoAdmit.WindowsUI.Infrastructure;
using ZoomAutoAdmit.WindowsUI.Services;

namespace ZoomAutoAdmit.WindowsUI.ViewModels;

public sealed class GroupRosterViewModel : ObservableObject
{
    private readonly IGroupRosterService _service;
    private readonly IGroupRosterDialogs _dialogs;
    private IReadOnlyList<RosterGroup> _all = [];
    private readonly Services.SignedInScope _scope;
    private RosterGroup? _group;
    private GroupStudent? _student;
    private bool _idle = true, _hasGroup;
    private string _groupId = "", _displayName = "", _groupSearch = "", _studentSearch = "", _status = "";
    private string _studentId = "", _order = "1", _fullName = "", _aliases = "", _email = "";

    public GroupRosterViewModel(IGroupRosterService service, IGroupRosterDialogs dialogs,
        Services.SignedInScope? scope = null)
    {
        _service = service;
        _dialogs = dialogs;
        // One PC holds everybody's rosters; a coordinator signed in here sees only their groups.
        _scope = scope ?? new Services.SignedInScope(() => null);
        _scope.Changed += () => { FilterGroups(); };
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
        NewGroupCommand = new RelayCommand(_ => { if (IsIdle) { SelectedGroup = null; GroupId = DisplayName = ""; } });
        SaveGroupCommand = new AsyncRelayCommand(_ => SaveGroupAsync());
        DeleteGroupCommand = new AsyncRelayCommand(_ => DeleteGroupAsync());
        NewStudentCommand = new RelayCommand(_ => { if (IsIdle) NewStudent(); });
        SaveStudentCommand = new AsyncRelayCommand(_ => SaveStudentAsync());
        DeleteStudentCommand = new AsyncRelayCommand(_ => DeleteStudentAsync());
        MoveUpCommand = new AsyncRelayCommand(_ => MoveAsync(-1));
        MoveDownCommand = new AsyncRelayCommand(_ => MoveAsync(1));
        ImportCommand = new AsyncRelayCommand(_ => PickImportAsync());
    }

    /// <summary>The store itself, for the Groups & Students page (React) that edits it directly.</summary>
    public IGroupRosterService Service => _service;
    public IGroupRosterDialogs Dialogs => _dialogs;
    public ObservableCollection<RosterGroup> Groups { get; } = [];
    /// <summary>What this PC is showing less of, and why. Empty when every group is shown.</summary>
    public string ScopeNote { get => _scopeNote; private set => SetProperty(ref _scopeNote, value); }
    private string _scopeNote = string.Empty;
    public ObservableCollection<GroupStudent> Students { get; } = [];
    public RosterGroup? SelectedGroup
    {
        get => _group;
        set
        {
            if (!SetProperty(ref _group, value)) return;
            HasGroup = value != null;
            GroupId = value?.GroupId ?? "";
            DisplayName = value?.DisplayName ?? "";
            NewStudent();
            FilterStudents();
        }
    }
    public GroupStudent? SelectedStudent
    {
        get => _student;
        set
        {
            if (!SetProperty(ref _student, value)) return;
            if (value == null) { ResetStudentDraft(); return; }
            StudentId = value.StudentId;
            Order = value.Order.ToString();
            FullName = value.FullName;
            AliasesText = string.Join(Environment.NewLine, value.Aliases);
            Email = value.Email ?? "";
        }
    }
    public bool IsIdle { get => _idle; private set => SetProperty(ref _idle, value); }
    public bool HasGroup { get => _hasGroup; private set => SetProperty(ref _hasGroup, value); }
    public string GroupId { get => _groupId; set => SetProperty(ref _groupId, value); }
    public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value); }
    public string GroupSearch { get => _groupSearch; set { if (SetProperty(ref _groupSearch, value)) FilterGroups(); } }
    public string StudentSearch { get => _studentSearch; set { if (SetProperty(ref _studentSearch, value)) FilterStudents(); } }
    public string StudentId { get => _studentId; private set => SetProperty(ref _studentId, value); }
    public string Order { get => _order; set => SetProperty(ref _order, value); }
    public string FullName { get => _fullName; set => SetProperty(ref _fullName, value); }
    public string AliasesText { get => _aliases; set => SetProperty(ref _aliases, value); }
    public string Email { get => _email; set => SetProperty(ref _email, value); }
    public string StatusMessage { get => _status; private set => SetProperty(ref _status, value); }
    public ICommand RefreshCommand { get; }
    public ICommand NewGroupCommand { get; }
    public ICommand SaveGroupCommand { get; }
    public ICommand DeleteGroupCommand { get; }
    public ICommand NewStudentCommand { get; }
    public ICommand SaveStudentCommand { get; }
    public ICommand DeleteStudentCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand ImportCommand { get; }

    public Task RefreshAsync() => RunAsync("Loading groups...", async () =>
    {
        await ReloadAsync(SelectedGroup?.GroupId, SelectedStudent?.StudentId);
        StatusMessage = $"Group loaded. {Groups.Count} groups shown. Select a group to open its roster.";
    });

    public Task SaveGroupAsync() => RunAsync("Saving group...", async () =>
    {
        var group = SelectedGroup;
        string id = GroupId.Trim();
        if (group == null) await _service.CreateAsync(id, DisplayName);
        else
        {
            if (id != group.GroupId) throw new ArgumentException("Group ID cannot be changed. Rename changes the display name only.");
            await _service.RenameAsync(group, DisplayName);
        }
        GroupSearch = "";
        await ReloadAsync(id);
        StatusMessage = group == null ? "Group created." : "Group renamed.";
    });

    public Task DeleteGroupAsync() => RunAsync("Delete group", async () =>
    {
        var group = RequireGroup();
        if (!_dialogs.ConfirmDeleteGroup(group.GroupId, group.Students.Count)) { StatusMessage = "Delete cancelled."; return; }
        await _service.DeleteAsync(group);
        await ReloadAsync();
        StatusMessage = "Group deleted. Previous version is in groups.json.bak.";
    });

    public void NewStudent()
    {
        SelectedStudent = null;
        ResetStudentDraft();
    }

    private void ResetStudentDraft()
    {
        StudentId = Guid.NewGuid().ToString("D");
        int maximum = SelectedGroup?.Students.Select(s => s.Order).DefaultIfEmpty(0).Max() ?? 0;
        Order = maximum == int.MaxValue ? "" : (maximum + 1).ToString();
        FullName = AliasesText = Email = "";
    }

    public Task SaveStudentAsync() => RunAsync("Saving student...", async () =>
    {
        var group = RequireGroup();
        var selected = SelectedStudent;
        if (!int.TryParse(Order, out int order) || order <= 0) throw new ArgumentException("Order must be a positive integer.");
        var student = new GroupStudent(StudentId, group.GroupId, order, FullName,
            AliasesText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), Email);
        if (selected == null) await _service.AddStudentAsync(group, student);
        else await _service.UpdateStudentAsync(group, student);
        await ReloadAsync(group.GroupId, student.StudentId);
        StatusMessage = selected == null ? "Student added." : "Student updated. Order preserved unless explicitly edited.";
    });

    public Task DeleteStudentAsync() => RunAsync("Delete student", async () =>
    {
        var group = RequireGroup();
        var student = SelectedStudent ?? throw new InvalidOperationException("Select a student first.");
        if (!_dialogs.ConfirmDeleteStudent(student.StudentId)) { StatusMessage = "Delete cancelled."; return; }
        await _service.DeleteStudentAsync(group, student.StudentId);
        await ReloadAsync(group.GroupId);
        StatusMessage = "Student deleted. Other order numbers are unchanged.";
    });

    public Task MoveAsync(int direction) => RunAsync("Reordering students...", async () =>
    {
        if (direction is not (-1 or 1)) throw new ArgumentException("Choose move up or down.");
        var group = RequireGroup();
        var student = SelectedStudent ?? throw new InvalidOperationException("Select a student first.");
        if (!string.IsNullOrWhiteSpace(StudentSearch)) throw new InvalidOperationException("Clear student search before reordering.");
        var ids = group.Students.OrderBy(s => s.Order).Select(s => s.StudentId).ToList();
        int current = ids.IndexOf(student.StudentId), target = current + direction;
        if (current < 0 || target < 0 || target >= ids.Count) { StatusMessage = "Student is already at the boundary."; return; }
        (ids[current], ids[target]) = (ids[target], ids[current]);
        await _service.ReorderAsync(group, ids);
        await ReloadAsync(group.GroupId, student.StudentId);
        StatusMessage = "Roster saved with the new manual order.";
    });

    private async Task PickImportAsync()
    {
        if (!IsIdle) return;
        try
        {
            RequireGroup();
            var path = _dialogs.SelectImportFile();
            if (path != null) await ImportAsync(path);
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    public Task ImportAsync(string path) => RunAsync("Importing into selected group...", async () =>
    {
        var group = RequireGroup();
        int count = await _service.ImportAsync(group, path);
        await ReloadAsync(group.GroupId);
        StatusMessage = $"Import completed: {count} students; supplied order numbers preserved.";
    });

    private RosterGroup RequireGroup() => SelectedGroup
        ?? throw new InvalidOperationException("Select a group on the left first — students are imported into a group.");

    private async Task ReloadAsync(string? groupId = null, string? studentId = null)
    {
        _all = await _service.ListAsync();
        SelectedGroup = null;
        FilterGroups();
        SelectedGroup = Groups.FirstOrDefault(g => g.GroupId == groupId);
        if (studentId != null) SelectedStudent = Students.FirstOrDefault(s => s.StudentId == studentId);
    }

    private void FilterGroups()
    {
        string? id = SelectedGroup?.GroupId;
        string query = GroupSearch?.Trim() ?? "";
        // Whose they are first, then what was typed in the box.
        var mine = _all.Where(g => _scope.Owns(g.GroupId)).ToArray();
        Groups.Clear();
        foreach (var group in mine.Where(g => g.GroupId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                     g.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))) Groups.Add(group);
        ScopeNote = _scope.Narrowed(mine.Length, _all.Count, "group(s)");
        // A group that is no longer theirs must not stay open with its students on screen.
        SelectedGroup = id == null ? SelectedGroup : Groups.FirstOrDefault(g => g.GroupId == id);
    }

    private void FilterStudents()
    {
        var group = SelectedGroup;
        string? id = SelectedStudent?.StudentId;
        string query = StudentSearch?.Trim() ?? "";
        Students.Clear();
        if (group != null)
            foreach (var student in group.Students.OrderBy(s => s.Order).Where(s =>
                         s.FullName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                         s.StudentId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                         s.Aliases.Any(a => a.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                         (s.Email?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))) Students.Add(student);
        if (id != null) SelectedStudent = Students.FirstOrDefault(s => s.StudentId == id);
    }

    private async Task RunAsync(string message, Func<Task> action)
    {
        if (!IsIdle) return;
        IsIdle = false;
        StatusMessage = message;
        try { await action(); }
        catch (Exception ex) { StatusMessage = "Roster action failed: " + ex.Message; }
        finally { IsIdle = true; }
    }
}
