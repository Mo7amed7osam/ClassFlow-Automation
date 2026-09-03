using System.Collections.ObjectModel;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.WindowsUI.Infrastructure;
using ZoomAutoAdmit.WindowsUI.Services;

namespace ZoomAutoAdmit.WindowsUI.ViewModels;

public sealed class WaitingRoomViewModel(IMeetingActivitySource? source) : ObservableObject
{
    private string _status = "Connected to runtime diagnostics. Waiting for a meeting monitor.";
    private int _verified;
    public ObservableCollection<MeetingActivity> Activity { get; } = [];
    public ObservableCollection<LogEntry> Diagnostics { get; } = [];
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public int VerifiedAdmissions { get => _verified; private set => SetProperty(ref _verified, value); }
    public void Refresh()
    {
        Activity.Clear();
        foreach (var item in source?.GetMeetingActivity() ?? []) Activity.Add(item);
        VerifiedAdmissions = Activity.Count(e => e.Event == "Admission verified");
        Diagnostics.Clear();
        foreach (var entry in ConsoleLogger.GetRecentEntries().Where(IsAdmissionDiagnostic).TakeLast(200).Reverse()) Diagnostics.Add(entry);
        Status = $"Connected — {VerifiedAdmissions} verified admission events in recent session history. " +
            "Diagnostics below are shared across monitors; no participant count is inferred from log lines.";
    }
    public static bool IsAdmissionDiagnostic(LogEntry entry) =>
        entry.Message.Contains("WAITING_", StringComparison.Ordinal) ||
        entry.Message.Contains("ADMIT", StringComparison.Ordinal) ||
        entry.Message.Contains("ADMISSION", StringComparison.Ordinal) ||
        entry.Message.StartsWith("Waiting participants:", StringComparison.Ordinal) ||
        entry.Message.Contains("PARTICIPANT_HOVERED", StringComparison.Ordinal);
}
