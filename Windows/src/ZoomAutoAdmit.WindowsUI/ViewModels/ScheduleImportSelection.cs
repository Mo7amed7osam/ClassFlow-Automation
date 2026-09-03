using ZoomAutoAdmit.WindowsUI.Infrastructure;
using ZoomAutoAdmit.WindowsUI.Services;

namespace ZoomAutoAdmit.WindowsUI.ViewModels;

/// <summary>One previewed timetable row plus the user's keep/skip choice. Importable rows start selected.</summary>
public sealed class ScheduleImportSelection : ObservableObject
{
    private bool _include;

    public ScheduleImportSelection(ScheduleImportRow row)
    {
        Row = row;
        _include = row.CanImport;
    }

    public ScheduleImportRow Row { get; }
    public bool CanImport => Row.CanImport;
    // An excluded row (physical session, missing date/time) can never be turned back on here.
    public bool Include { get => _include; set { if (CanImport) SetProperty(ref _include, value); } }
    public string SessionNumber => Row.SessionNumber;
    public DateOnly? Date => Row.Date;
    public string Type => Row.Type;
    public string Topic => Row.Topic;
    public string TimeRange => Row.TimeRange;
    public string Issue => Row.Issue;
}
