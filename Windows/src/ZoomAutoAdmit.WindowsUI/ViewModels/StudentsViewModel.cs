using System.Collections.ObjectModel;
using System.Windows.Input;
using ZoomAutoAdmit.Roster;
using ZoomAutoAdmit.WindowsUI.Infrastructure;
using ZoomAutoAdmit.WindowsUI.Services;

namespace ZoomAutoAdmit.WindowsUI.ViewModels;

public sealed class StudentsViewModel : ObservableObject
{
    private readonly IStudentRosterService _service;
    private readonly IStudentDialogs _dialogs;
    private IReadOnlyList<Student> _all = [];
    private Student? _selected;
    private string _studentId = Guid.NewGuid().ToString("D"), _fullName = "", _aliases = "", _email = "", _search = "", _status = "";
    private bool _busy, _isIdle = true, _isEditing;

    public StudentsViewModel(IStudentRosterService service, IStudentDialogs dialogs)
    {
        _service = service;
        _dialogs = dialogs;
        NewCommand = new RelayCommand(_ => NewStudent());
        SaveCommand = new AsyncRelayCommand(_ => SaveAsync());
        DeleteCommand = new AsyncRelayCommand(_ => DeleteAsync());
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
        ImportCommand = new AsyncRelayCommand(_ => PickImportAsync());
    }

    public ObservableCollection<Student> Items { get; } = [];
    public Student? SelectedStudent
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            IsEditing = value != null;
            if (value == null)
            {
                StudentId = Guid.NewGuid().ToString("D");
                FullName = AliasesText = Email = "";
                return;
            }
            StudentId = value.StudentId;
            FullName = value.FullName;
            AliasesText = string.Join(Environment.NewLine, value.Aliases);
            Email = value.Email ?? "";
        }
    }
    public string StudentId { get => _studentId; set => SetProperty(ref _studentId, value); }
    public string FullName { get => _fullName; set => SetProperty(ref _fullName, value); }
    public string AliasesText { get => _aliases; set => SetProperty(ref _aliases, value); }
    public string Email { get => _email; set => SetProperty(ref _email, value); }
    public string SearchText { get => _search; set { if (SetProperty(ref _search, value)) Filter(); } }
    public string StatusMessage { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsIdle { get => _isIdle; private set => SetProperty(ref _isIdle, value); }
    public bool IsEditing { get => _isEditing; private set => SetProperty(ref _isEditing, value); }
    public ICommand NewCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ImportCommand { get; }

    public void NewStudent()
    {
        if (_busy) return;
        SelectedStudent = null;
        StudentId = Guid.NewGuid().ToString("D");
        FullName = AliasesText = Email = "";
        StatusMessage = "New student. Enter details and press Save.";
    }

    public Task RefreshAsync() => RunAsync("Loading roster...", async () =>
    {
        await ReloadAsync();
        StatusMessage = $"Loaded {_all.Count} students.";
    });

    public Task SaveAsync() => RunAsync("Saving student...", async () =>
    {
        var expected = SelectedStudent;
        var student = StudentValidation.Normalize(new(StudentId, FullName,
            AliasesText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), Email));
        if (expected == null) await _service.AddAsync(student);
        else await _service.UpdateAsync(student, expected);
        await ReloadAsync();
        SelectedStudent = _all.Single(s => s.StudentId == student.StudentId);
        StatusMessage = expected == null ? "Student added." : "Student updated.";
    });

    public Task DeleteAsync() => RunAsync("Delete student", async () =>
    {
        var expected = SelectedStudent;
        if (expected == null) { StatusMessage = "Select a saved student first."; return; }
        if (!_dialogs.ConfirmDelete(expected.StudentId)) { StatusMessage = "Delete cancelled."; return; }
        await _service.DeleteAsync(expected);
        await ReloadAsync();
        SelectedStudent = null;
        StudentId = Guid.NewGuid().ToString("D");
        FullName = AliasesText = Email = "";
        StatusMessage = "Student deleted. The previous roster is available in students.json.bak.";
    });

    private async Task PickImportAsync()
    {
        if (_busy) return;
        try
        {
            var path = _dialogs.SelectImportFile();
            if (path != null) await ImportAsync(path);
        }
        catch (Exception ex) { StatusMessage = "Import failed: " + ex.Message; }
    }

    public Task ImportAsync(string path) => RunAsync("Importing roster...", async () =>
    {
        int count = await _service.ImportFileAsync(path);
        await ReloadAsync();
        StatusMessage = $"Import completed: {count} students added. Existing students were not overwritten.";
    });

    private async Task ReloadAsync()
    {
        string? selectedId = SelectedStudent?.StudentId;
        _all = await _service.ListAsync();
        SelectedStudent = null;
        Filter();
        if (selectedId != null) SelectedStudent = Items.FirstOrDefault(s => s.StudentId == selectedId);
    }

    private void Filter()
    {
        string? selectedId = SelectedStudent?.StudentId;
        var query = SearchText?.Trim() ?? "";
        bool Has(string? value) => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
        Items.Clear();
        foreach (var student in _all.Where(s => query.Length == 0 || Has(s.StudentId) || Has(s.FullName) ||
                     Has(s.Email) || s.Aliases.Any(Has)))
            Items.Add(student);
        if (selectedId != null) SelectedStudent = Items.FirstOrDefault(s => s.StudentId == selectedId);
    }

    private async Task RunAsync(string message, Func<Task> operation)
    {
        if (_busy) return;
        _busy = true;
        IsIdle = false;
        StatusMessage = message;
        try { await operation(); }
        catch (Exception ex) { StatusMessage = "Roster action failed: " + ex.Message; }
        finally { _busy = false; IsIdle = true; }
    }
}
