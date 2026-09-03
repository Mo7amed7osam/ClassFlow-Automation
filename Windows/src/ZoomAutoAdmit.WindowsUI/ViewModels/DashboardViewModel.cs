using System.Collections.ObjectModel;
using System.Windows.Input;
using ZoomAutoAdmit.WindowsUI.Infrastructure;
using ZoomAutoAdmit.WindowsUI.Services;

namespace ZoomAutoAdmit.WindowsUI.ViewModels;

public sealed class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly IWindowsUiService _service;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private SessionDisplayInfo? _selectedSession;
    private string _statusMessage = string.Empty;
    private string _lastAction;
    private string _currentOperation;
    private string _errorMessage;
    private SessionDisplayInfo? _primarySession;
    private string _monitoringStatus = "Idle";

    public DashboardViewModel(IWindowsUiService service)
    {
        _service = service;
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
        StopCommand = new AsyncRelayCommand(StopAsync, parameter => parameter is SessionDisplayInfo);
        var status = service.CurrentStatus;
        _lastAction = status.LastAction;
        _currentOperation = status.CurrentOperation;
        _errorMessage = status.ErrorMessage;
        _service.StatusChanged += OnStatusChanged;
    }

    public ObservableCollection<SessionDisplayInfo> Sessions { get; } = [];
    public SessionDisplayInfo? SelectedSession { get => _selectedSession; set => SetProperty(ref _selectedSession, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string LastAction { get => _lastAction; private set => SetProperty(ref _lastAction, value); }
    public string CurrentOperation { get => _currentOperation; private set => SetProperty(ref _currentOperation, value); }
    public string ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }
    public SessionDisplayInfo? PrimarySession { get => _primarySession; private set => SetProperty(ref _primarySession, value); }
    public string MonitoringStatus { get => _monitoringStatus; private set => SetProperty(ref _monitoringStatus, value); }
    public ICommand RefreshCommand { get; }
    public ICommand StopCommand { get; }

    public async Task RefreshAsync()
    {
        try
        {
            var sessions = await _service.GetActiveSessionsAsync();
            Sessions.Clear();
            foreach (var session in sessions) Sessions.Add(session);
            PrimarySession = sessions.FirstOrDefault();
            MonitoringStatus = sessions.Any(s => s.State == "Monitoring") ? "Monitoring" : sessions.Count > 0 ? "Active" : "Idle";
            StatusMessage = sessions.Count == 0 ? "No active sessions." : $"{sessions.Count} active session(s).";
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task StopAsync(object? parameter)
    {
        if (parameter is not SessionDisplayInfo session) return;
        StatusMessage = $"Stopping {session.AccountName}...";
        try
        {
            StatusMessage = await _service.StopMeetingAsync(session.SessionId)
                ? $"{session.AccountName}: session stopped. Zoom itself keeps running."
                : $"{session.AccountName}: session could not be stopped.";
        }
        catch (Exception ex) { StatusMessage = $"{session.AccountName}: {ex.Message}"; }
        await RefreshAsync();
    }

    private void OnStatusChanged(UiActionStatus status)
    {
        if (_context == null || SynchronizationContext.Current == _context) ApplyStatus(status);
        else _context.Post(_ => ApplyStatus(status), null);
    }

    private void ApplyStatus(UiActionStatus status)
    {
        LastAction = status.LastAction;
        CurrentOperation = status.CurrentOperation;
        ErrorMessage = status.ErrorMessage;
    }

    public void Dispose() => _service.StatusChanged -= OnStatusChanged;
}
