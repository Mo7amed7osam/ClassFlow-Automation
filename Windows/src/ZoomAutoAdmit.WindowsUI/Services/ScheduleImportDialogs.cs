using Microsoft.Win32;

namespace ZoomAutoAdmit.WindowsUI.Services;

public interface IScheduleImportDialogs { string? PickWorkbook(); }
public sealed class ScheduleImportDialogs : IScheduleImportDialogs
{
    public string? PickWorkbook()
    {
        var dialog = new OpenFileDialog { Title = "Preview a DEPI meeting timetable", Filter = "Excel timetable (*.xlsx)|*.xlsx", CheckFileExists = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
