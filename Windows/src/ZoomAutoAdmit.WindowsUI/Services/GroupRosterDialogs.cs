using System.Windows;

namespace ZoomAutoAdmit.WindowsUI.Services;

public interface IGroupRosterDialogs
{
    string? SelectImportFile();
    bool ConfirmDeleteGroup(string groupId, int studentCount);
    bool ConfirmDeleteStudent(string studentId);
}

public sealed class GroupRosterDialogs : IGroupRosterDialogs
{
    private readonly StudentDialogs _students = new();
    public string? SelectImportFile() => _students.SelectImportFile();
    public bool ConfirmDeleteStudent(string studentId) => _students.ConfirmDelete(studentId);
    public bool ConfirmDeleteGroup(string groupId, int studentCount) => MessageBox.Show(Application.Current.MainWindow,
        $"Delete group '{groupId}' and its {studentCount} students? Attendance snapshots are not affected.",
        "Delete group", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
}
