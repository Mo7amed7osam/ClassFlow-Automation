using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using ZoomAutoAdmit.Roster;
using ZoomAutoAdmit.WindowsUI.Services;
using ZoomAutoAdmit.WindowsUI.ViewModels;
using ZoomAutoAdmit.WindowsUI.Views;
using Xunit;

namespace ZoomAutoAdmit.WindowsUI.Tests;

public class GroupRosterViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "group-roster-ui-tests-" + Guid.NewGuid().ToString("N"));
    private GroupRosterStore Store => new(Path.Combine(_root, "groups.json"), seedOnFirstUse: false);
    private sealed class Dialogs : IGroupRosterDialogs
    {
        public bool Confirm = true;
        public string? SelectImportFile() => null;
        public bool ConfirmDeleteGroup(string id, int count) => Confirm;
        public bool ConfirmDeleteStudent(string id) => Confirm;
    }
    private GroupRosterViewModel Model(Dialogs? dialogs = null) => new(Store, dialogs ?? new Dialogs());

    [Fact]
    public async Task CreateRenameOpenAndDeleteGroupsWithConfirmation()
    {
        var dialogs = new Dialogs();
        var model = Model(dialogs);
        model.GroupId = "New-2026";
        model.DisplayName = "Dynamic";
        await model.SaveGroupAsync();
        Assert.True(model.HasGroup);
        model.DisplayName = "Renamed";
        await model.SaveGroupAsync();
        Assert.Equal("Renamed", Assert.Single(model.Groups).DisplayName);
        dialogs.Confirm = false;
        await model.DeleteGroupAsync();
        Assert.Single(model.Groups);
        dialogs.Confirm = true;
        await model.DeleteGroupAsync();
        Assert.Empty(model.Groups);
        Assert.False(model.HasGroup);
    }

    [Fact]
    public async Task StudentCrudSearchAndMoveKeepNumericRosterOrder()
    {
        var model = Model();
        model.GroupId = "G";
        model.DisplayName = "Group";
        await model.SaveGroupAsync();
        model.FullName = "Zulu";
        await model.SaveStudentAsync();
        model.NewStudent();
        model.FullName = "Alpha";
        model.AliasesText = "ألفا";
        await model.SaveStudentAsync();
        Assert.Equal(new[] { "Zulu", "Alpha" }, model.Students.Select(s => s.FullName));
        model.StudentSearch = "ألفا";
        Assert.Single(model.Students);
        await model.MoveAsync(-1);
        Assert.Contains("Clear student search", model.StatusMessage);
        model.StudentSearch = "";
        model.SelectedStudent = model.Students.Last();
        await model.MoveAsync(-1);
        Assert.Equal(new[] { "Alpha", "Zulu" }, model.Students.Select(s => s.FullName));
        model.FullName = "Edited";
        await model.SaveStudentAsync();
        Assert.Equal(1, model.SelectedStudent!.Order);
        await model.DeleteStudentAsync();
        Assert.Equal(2, Assert.Single(model.Students).Order);
    }

    [Fact]
    public async Task FilteringOutSelectionClearsEditorWithoutReusingExistingStudentId()
    {
        var model = Model();
        model.GroupId = "G";
        model.DisplayName = "Group";
        await model.SaveGroupAsync();
        model.FullName = "Zulu";
        await model.SaveStudentAsync();
        string originalId = model.StudentId;
        model.StudentSearch = "Other";
        Assert.Null(model.SelectedStudent);
        Assert.Empty(model.FullName);
        Assert.NotEqual(originalId, model.StudentId);
        Assert.Equal("2", model.Order);
        model.FullName = "Other";
        await model.SaveStudentAsync();
        Assert.Equal(2, Assert.Single(await Store.ListAsync()).Students.Count);
    }

    [Fact]
    public async Task SearchGroupsAndImportDoNotMixGroups()
    {
        await Store.CreateAsync("A", "First");
        await Store.CreateAsync("B", "Second");
        var model = Model();
        await model.RefreshAsync();
        model.GroupSearch = "Second";
        model.SelectedGroup = Assert.Single(model.Groups);
        string path = Path.Combine(_root, "students.csv");
        await File.WriteAllTextAsync(path, "Order,Name\n5,Zulu\n8,Alpha\n");
        await model.ImportAsync(path);
        Assert.Equal(new[] { 5, 8 }, model.Students.Select(s => s.Order));
        Assert.Contains("Import completed: 2", model.StatusMessage);
        Assert.Empty((await Store.ListAsync()).Single(g => g.GroupId == "A").Students);
    }

    [Fact]
    public async Task ErrorsAreVisibleAndDoNotDisableCommands()
    {
        var model = Model();
        await model.SaveStudentAsync();
        Assert.Contains("Select a group", model.StatusMessage);
        Assert.True(model.IsIdle);
        model.GroupId = "G";
        model.DisplayName = "Group";
        await model.SaveGroupAsync();
        model.Order = "";
        await model.SaveStudentAsync();
        Assert.Contains("Order", model.StatusMessage);
        Assert.True(model.SaveStudentCommand.CanExecute(null));
    }

    [Fact]
    public async Task CompiledRosterPageBindsAndDisallowsAlphabeticalGridSorting()
    {
        await Store.CreateAsync("G", "Group");
        var group = Assert.Single(await Store.ListAsync());
        await Store.AddStudentAsync(group, new("S1", "G", 4, "Zulu", []));
        var model = Model();
        await model.RefreshAsync();
        model.SelectedGroup = model.Groups.Single();
        model.SelectedStudent = model.Students.Single();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var view = new GroupRosterView { DataContext = model, Width = 1000, Height = 600 };
                view.Measure(new Size(1000, 600));
                view.Arrange(new Rect(0, 0, 1000, 600));
                view.UpdateLayout();
                var children = Descendants(view).ToArray();
                var grids = children.OfType<DataGrid>().ToArray();
                Assert.Equal(2, grids.Length);
                Assert.All(grids, grid => Assert.False(grid.CanUserSortColumns));
                var name = children.OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Group student full name");
                Assert.Equal("Zulu", name.Text);
                Assert.False(name.GetBindingExpression(TextBox.TextProperty)!.HasError);
                Assert.Same(model.MoveUpCommand, children.OfType<Button>().Single(b => Equals(b.Content, "Move up")).Command);
                completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
