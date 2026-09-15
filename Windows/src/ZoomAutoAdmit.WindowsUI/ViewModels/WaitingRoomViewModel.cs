using System.Collections.ObjectModel;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Meetings;
using ZoomAutoAdmit.WindowsUI.Infrastructure;
using ZoomAutoAdmit.WindowsUI.Services;

namespace ZoomAutoAdmit.WindowsUI.ViewModels;

/// <summary>One person let in, from the admission ledger.</summary>
public sealed record AdmittedPerson(DateTimeOffset At, string Name, string Source, int People)
{
    public string Time => At.LocalDateTime.ToString("HH:mm:ss");
    public string SourceText => Source switch { "desktop" => "Zoom app", "web" => "Zoom web", _ => Source };
}

public sealed class WaitingRoomViewModel(IMeetingActivitySource? source) : ObservableObject
{
    private string _status = "Connected to runtime diagnostics. Waiting for a meeting monitor.";
    private int _verified, _admittedToday, _distinctToday;
    public ObservableCollection<MeetingActivity> Activity { get; } = [];
    public ObservableCollection<LogEntry> Diagnostics { get; } = [];
    /// <summary>Everyone let in today, newest first, whichever process admitted them.</summary>
    public ObservableCollection<AdmittedPerson> Admitted { get; } = [];
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public int VerifiedAdmissions { get => _verified; private set => SetProperty(ref _verified, value); }
    public int AdmittedToday { get => _admittedToday; private set => SetProperty(ref _admittedToday, value); }
    public int DistinctToday { get => _distinctToday; private set => SetProperty(ref _distinctToday, value); }

    public void Refresh()
    {
        Activity.Clear();
        foreach (var item in source?.GetMeetingActivity() ?? []) Activity.Add(item);
        VerifiedAdmissions = Activity.Count(e => e.Event == "Admission verified");

        var ledger = AdmissionLedger.Read(DateOnly.FromDateTime(DateTime.Now));
        if (ledger.Count != Admitted.Count)
        {
            Admitted.Clear();
            foreach (var e in ledger.OrderByDescending(e => e.At))
                Admitted.Add(new AdmittedPerson(e.At, e.Name ?? (e.People > 1 ? $"{e.People} people (Admit all)" : "(name not read)"), e.Source, e.People));
        }
        AdmittedToday = ledger.Sum(e => e.People);
        DistinctToday = ledger.Where(e => e.Name != null).Select(e => e.Name!.ToLowerInvariant()).Distinct().Count();

        Diagnostics.Clear();
        foreach (var entry in ConsoleLogger.GetRecentEntries().Where(IsAdmissionDiagnostic).TakeLast(200).Reverse()) Diagnostics.Add(entry);
        Status = $"{AdmittedToday} let in today ({DistinctToday} different people), from every monitor - the app and the standalone one.";
    }

    public static bool IsAdmissionDiagnostic(LogEntry entry) =>
        entry.Message.Contains("WAITING_", StringComparison.Ordinal) ||
        entry.Message.Contains("ADMIT", StringComparison.Ordinal) ||
        entry.Message.Contains("ADMISSION", StringComparison.Ordinal) ||
        entry.Message.StartsWith("Waiting participants:", StringComparison.Ordinal) ||
        entry.Message.Contains("PARTICIPANT_HOVERED", StringComparison.Ordinal);
}
