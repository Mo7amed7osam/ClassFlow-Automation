using ZoomAutoAdmit.Roster;
using ZoomAutoAdmit.WindowsUI.Services;
using ZoomAutoAdmit.WindowsUI.ViewModels;
using Xunit;

namespace ZoomAutoAdmit.WindowsUI.Tests;

public class StudentsViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roster-vm-tests-" + Guid.NewGuid().ToString("N"));
    private StudentRosterStore Store => new(Path.Combine(_root, "students.json"));
    private sealed class Dialogs : IStudentDialogs
    {
        public bool Confirm = true;
        public string? SelectImportFile() => null;
        public bool ConfirmDelete(string id) => Confirm;
    }

    [Fact]
    public async Task AddEditSearchAndDeleteFlow()
    {
        var vm = new StudentsViewModel(Store, new Dialogs());
        vm.StudentId = "S1";
        vm.FullName = "Student One";
        vm.AliasesText = "أحمد\nAhmed";
        await vm.SaveAsync();
        Assert.Single(vm.Items);
        Assert.True(vm.IsEditing);
        vm.FullName = "Student Updated";
        await vm.SaveAsync();
        Assert.Equal("Student updated.", vm.StatusMessage);
        vm.SearchText = "أحمد";
        Assert.Single(vm.Items);
        vm.SearchText = "missing";
        Assert.Empty(vm.Items);
        vm.SearchText = "";
        vm.SelectedStudent = vm.Items.Single();
        await vm.DeleteAsync();
        Assert.Empty(vm.Items);
        Assert.True(vm.IsIdle);
    }

    [Fact]
    public async Task RefreshKeepsSelectedStudentInEditMode()
    {
        await Store.AddAsync(new("S1", "Student", []));
        var vm = new StudentsViewModel(Store, new Dialogs());
        await vm.RefreshAsync();
        vm.SelectedStudent = vm.Items.Single();
        await vm.RefreshAsync();
        Assert.True(vm.IsEditing);
        vm.FullName = "Edited after refresh";
        await vm.SaveAsync();
        Assert.Equal("Edited after refresh", Assert.Single(await Store.ListAsync()).FullName);
    }

    [Fact]
    public async Task DeleteRequiresConfirmation()
    {
        await Store.AddAsync(new("S1", "Student", []));
        var vm = new StudentsViewModel(Store, new Dialogs { Confirm = false });
        await vm.RefreshAsync();
        vm.SelectedStudent = vm.Items.Single();
        await vm.DeleteAsync();
        Assert.Single(await Store.ListAsync());
        Assert.Equal("Delete cancelled.", vm.StatusMessage);
    }

    [Fact]
    public async Task ValidationAndImportFailuresAreVisibleAndLeaveCommandsUsable()
    {
        var vm = new StudentsViewModel(Store, new Dialogs());
        await vm.SaveAsync();
        Assert.Contains("Full name", vm.StatusMessage);
        await vm.ImportAsync(Path.Combine(_root, "missing.csv"));
        Assert.Contains("failed", vm.StatusMessage);
        Assert.True(vm.IsIdle);
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task ImportUpdatesListAndStatus()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "import.csv");
        await File.WriteAllTextAsync(path, "studentId,fullName\nS1,Student One\n");
        var vm = new StudentsViewModel(Store, new Dialogs());
        await vm.ImportAsync(path);
        Assert.Single(vm.Items);
        Assert.Contains("Import completed: 1", vm.StatusMessage);
        vm.NewCommand.Execute(null);
        Assert.False(vm.IsEditing);
        Assert.Empty(vm.FullName);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
