using System.Collections.ObjectModel;
using System.Windows.Input;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.WindowsUI.Infrastructure;

namespace ZoomAutoAdmit.WindowsUI.ViewModels;

public sealed class LogsViewModel : ObservableObject, IDisposable
{
    private static readonly string[] RuntimeCategories =
        ["[BOOTSTRAP]", "[ACCOUNT]", "[ALLOCATOR]", "[MEETING]", "[AUTO_ADMIT]", "[SCHEDULER]", "[ZOOM]", "[MEETING_CHECK]", "[ACCOUNT_SWITCH]", "[AI_SETUP]", "[ATTENDANCE]", "[MATCHING]", "[ROSTER]"];
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;

    public LogsViewModel()
    {
        ClearCommand = new RelayCommand(_ => Entries.Clear());
        foreach (var entry in ConsoleLogger.GetRecentEntries()) OnEntryWritten(entry);
        ConsoleLogger.EntryWritten += OnEntryWritten;
    }

    public ObservableCollection<string> Entries { get; } = [];
    public ICommand ClearCommand { get; }

    private void OnEntryWritten(LogEntry entry)
    {
        if (!RuntimeCategories.Any(category => entry.Message.Contains(category, StringComparison.Ordinal)) && !WaitingRoomViewModel.IsAdmissionDiagnostic(entry)) return;
        string line = $"[{entry.Timestamp:HH:mm:ss.fff}] [{entry.Level}] {entry.Message}";
        if (_context == null) Add(line);
        else _context.Post(_ => Add(line), null);
    }

    private void Add(string line)
    {
        Entries.Add(line);
        while (Entries.Count > 1000) Entries.RemoveAt(0);
    }

    public void Dispose() => ConsoleLogger.EntryWritten -= OnEntryWritten;
}
