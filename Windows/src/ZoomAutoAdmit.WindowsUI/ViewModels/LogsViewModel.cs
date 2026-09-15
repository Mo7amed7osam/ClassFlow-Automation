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
    // The thread that shows the list: lines written on any other thread are handed to it.
    private System.Windows.Threading.Dispatcher? _shownOn = System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread);

    /// <summary>The window that shows these lines; set when it is not the thread this was made on.</summary>
    public void ShowOn(System.Windows.Threading.Dispatcher dispatcher) => _shownOn = dispatcher;

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
        var dispatcher = _shownOn;
        if (dispatcher != null) { if (dispatcher.CheckAccess()) Add(line); else dispatcher.BeginInvoke(() => Add(line)); }
        else if (_context != null) _context.Post(_ => Add(line), null);
        else Add(line);
    }

    private void Add(string line)
    {
        Entries.Add(line);
        while (Entries.Count > 1000) Entries.RemoveAt(0);
    }

    public void Dispose() => ConsoleLogger.EntryWritten -= OnEntryWritten;
}
