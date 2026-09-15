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
    public async Task LmsRosterCreatesTheGroupInTheLmsOrder()
    {
        var result = await new LmsRosterImport(Store).SaveAsync("CAI5_AIS4_S9", ["Zulu Omar", "Alpha Ali", "Mona Samir"]);
        Assert.True(result.Ok);
        Assert.Equal(3, result.Added);
        var group = Assert.Single(await Store.ListAsync());
        Assert.Equal("CAI5_AIS4_S9", group.GroupId);
        Assert.Equal(new[] { "Zulu Omar", "Alpha Ali", "Mona Samir" }, group.Students.OrderBy(s => s.Order).Select(s => s.FullName));
    }

    [Fact]
    public async Task LmsRosterAddsOnlyNewNamesAndKeepsEveryoneElse()
    {
        await Store.CreateAsync("G", "Group");
        await Store.AddStudentsAsync(Assert.Single(await Store.ListAsync()),
        [
            new("S1", "G", 4, "Mona Samir Adel", []),
            new("S2", "G", 7, "Kept Here Only", []),
            new("S3", "G", 9, "Ahmed Alaa ElDin Jaber", []),
            new("S4", "G", 11, "Omar Tarek Fathy", []),
        ]);

        // Other case and spacing, a small typo in one word, a longer LMS name: none is added twice.
        var result = await new LmsRosterImport(Store).SaveAsync("g",
            ["mona  samir adel", "Ahmed Alaa IeDin Jaber", "Omar Tarek Fathy Hassan", "New Person Name"]);

        Assert.True(result.Ok);
        Assert.Equal(1, result.Added);
        Assert.Contains("2 spelled differently", result.Message);
        Assert.Contains("1 on the roster are not on the LMS list", result.Message);
        var students = Assert.Single(await Store.ListAsync()).Students.OrderBy(s => s.Order).ToArray();
        Assert.Equal(new[] { "Mona Samir Adel", "Kept Here Only", "Ahmed Alaa ElDin Jaber", "Omar Tarek Fathy", "New Person Name" }, students.Select(s => s.FullName));
        Assert.Equal(new[] { 4, 7, 9, 11, 12 }, students.Select(s => s.Order));
        Assert.Contains("Ahmed Alaa IeDin Jaber", students[2].Aliases);
        Assert.Empty(students[0].Aliases);
    }

    [Theory]
    [InlineData("Ahmed Ali Hassan Mahmoud", "Ahmed Ali Hassan Mostafa", false)]   // brothers: the last name differs entirely
    [InlineData("Ahmed Ali Hassan", "Ahmed Ali", false)]                          // two names are not enough
    [InlineData("Sara Adel Kamel", "Sarah Adel Kamel", true)]
    [InlineData("Mohammed Adel Kamel", "Mohamed Adel Kamel", true)]
    public void SameStudentIsDecidedCarefully(string roster, string lms, bool same)
    {
        Assert.Equal(same, LmsRosterImport.IsSameStudent(new("S", "G", 1, roster, []), lms));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
