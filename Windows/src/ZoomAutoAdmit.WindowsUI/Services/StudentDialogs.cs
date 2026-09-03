using System.Windows;
using Microsoft.Win32;

namespace ZoomAutoAdmit.WindowsUI.Services;

public interface IStudentDialogs
{
    string? SelectImportFile();
    bool ConfirmDelete(string studentId);
}

public sealed class StudentDialogs : IStudentDialogs
{
    public string? SelectImportFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import student roster", Filter = "Student files (*.csv;*.xlsx)|*.csv;*.xlsx",
            CheckFileExists = true, Multiselect = false
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public bool ConfirmDelete(string studentId) => MessageBox.Show(Application.Current.MainWindow,
        $"Delete student '{studentId}' from the roster? This does not change attendance snapshots.",
        "Delete student", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
}
