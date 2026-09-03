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

public class StudentsViewBindingTests
{
    [Fact]
    public async Task CompiledStudentsViewLoadsAndBindsControlsOnStaThread()
    {
        var model = new StudentsViewModel(new Source(), new Dialogs());
        await model.RefreshAsync();
        model.SelectedStudent = model.Items.Single();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var view = new StudentsView { DataContext = model, Width = 960, Height = 560 };
                view.Measure(new Size(960, 560));
                view.Arrange(new Rect(0, 0, 960, 560));
                view.UpdateLayout();
                var controls = Descendants(view).ToArray();
                var grid = Assert.Single(controls.OfType<DataGrid>());
                Assert.Same(model.Items, grid.ItemsSource);
                var name = controls.OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Student full name");
                Assert.Equal("Student Test", name.Text);
                Assert.False(name.GetBindingExpression(TextBox.TextProperty)!.HasError);
                var save = controls.OfType<Button>().Single(b => Equals(b.Content, "Save"));
                Assert.Same(model.SaveCommand, save.Command);
                var search = controls.OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Search students");
                search.Text = "no match";
                search.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                Assert.Empty(grid.Items);
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private sealed class Dialogs : IStudentDialogs
    {
        public string? SelectImportFile() => null;
        public bool ConfirmDelete(string id) => false;
    }

    private sealed class Source : IStudentRosterService
    {
        public Task<IReadOnlyList<Student>> ListAsync(CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<Student>>([new("S1", "Student Test", ["Alias"])]);
        public Task AddAsync(Student student, CancellationToken token = default) => throw new NotSupportedException();
        public Task UpdateAsync(Student student, Student expected, CancellationToken token = default) => throw new NotSupportedException();
        public Task DeleteAsync(Student expected, CancellationToken token = default) => throw new NotSupportedException();
        public Task<int> ImportFileAsync(string path, CancellationToken token = default) => throw new NotSupportedException();
    }
}
