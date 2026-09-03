using System.Windows;
using Microsoft.Win32;

namespace ZoomAutoAdmit.WindowsUI.Services;

/// <summary>One selectable option in a picker: what the person sees, and what the caller gets back.</summary>
public sealed record PickerOption(string Key, string Title, string Detail);

public interface IAttendanceDialogs
{
    /// <summary>Returns the chosen option's key, or null when the person cancels.</summary>
    string? Pick(string title, string prompt, IReadOnlyList<PickerOption> options);
    string? SaveCsvPath(string suggestedFileName);
    bool ConfirmFinalize(string summary);
    /// <summary>Deleting attendance cannot be undone, so it is always asked about first.</summary>
    bool ConfirmDelete(string summary);
    void Report(string title, string message);
}

public sealed class AttendanceDialogs : IAttendanceDialogs
{
    public string? Pick(string title, string prompt, IReadOnlyList<PickerOption> options)
    {
        if (options.Count == 0)
        {
            Report(title, "There is nothing to choose from here yet.");
            return null;
        }
        var dialog = new Views.PickerDialog(title, prompt, options) { Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true ? dialog.SelectedKey : null;
    }

    public string? SaveCsvPath(string suggestedFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export attendance",
            Filter = "CSV file (*.csv)|*.csv",
            FileName = suggestedFileName,
            AddExtension = true,
            DefaultExt = "csv"
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public bool ConfirmFinalize(string summary) => MessageBox.Show(
        Application.Current.MainWindow,
        summary + "\n\nFinalize writes this list to the attendance folder. Snapshots and roster are not changed.",
        "Finalize attendance",
        MessageBoxButton.OKCancel,
        MessageBoxImage.Question) == MessageBoxResult.OK;

    public bool ConfirmDelete(string summary) => MessageBox.Show(
        Application.Current.MainWindow,
        summary + "\n\nThe readings behind these sessions are deleted from this computer. This cannot be undone.",
        "Delete sessions",
        MessageBoxButton.OKCancel,
        MessageBoxImage.Warning) == MessageBoxResult.OK;

    public void Report(string title, string message) =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
}
