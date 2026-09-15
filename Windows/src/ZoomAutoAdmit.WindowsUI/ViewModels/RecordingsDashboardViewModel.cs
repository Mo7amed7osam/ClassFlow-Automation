using System.Diagnostics;
using System.Windows;
using ZoomAutoAdmit.WindowsUI.Infrastructure;
using ZoomAutoAdmit.WindowsUI.Services;

namespace ZoomAutoAdmit.WindowsUI.ViewModels;

/// <summary>
/// The Recordings page: the central backend's dashboard shown inside the app, and - on the PC that
/// is the server - the backend and agent this app runs for it.
///
/// The database password is never held here beyond the moment it is saved: it goes straight to
/// Windows Credential Manager, like the LMS sign-in.
/// </summary>
public sealed class RecordingsDashboardViewModel : ObservableObject, IDisposable
{
    private readonly IRecordingsDashboardSettingsStore _store;
    private readonly IDatabasePasswordStore _passwords;
    private readonly ICentralServerHost _host;
    private RecordingsDashboardSettings _settings;
    private ServerStatus _status;
    private bool _isServerMode;
    private string _serverUrl = "", _backendFolder = "", _databaseHost = "", _databaseUser = "", _databaseName = "";
    private int _databasePort, _port;
    private bool _startWithApp;
    private string _notice = "";
    private bool _showConnection;

    public RecordingsDashboardViewModel(IRecordingsDashboardSettingsStore? store = null, IDatabasePasswordStore? passwords = null, ICentralServerHost? host = null)
    {
        _store = store ?? new RecordingsDashboardSettingsStore();
        _passwords = passwords ?? new DatabasePasswordStore();
        _host = host ?? new CentralServerHost(_passwords);
        _settings = _store.Load();
        _status = _host.Status;
        _host.StatusChanged += OnStatusChanged;
        LoadFields(_settings);
        SaveCommand = new RelayCommand(_ => Save());
        StartCommand = new RelayCommand(async _ => await StartAsync(), _ => IsServerMode && _status.State != ServerState.Starting);
        StopCommand = new RelayCommand(_ => _host.Stop(), _ => IsServerMode && _status.State is ServerState.Running or ServerState.Starting or ServerState.Failed);
        ReloadCommand = new RelayCommand(_ => ReloadRequested?.Invoke());
        OpenInBrowserCommand = new RelayCommand(_ => OpenInBrowser(), _ => DashboardUri != null);
        ForgetPasswordCommand = new RelayCommand(_ => ForgetPassword(), _ => HasPassword);
        ToggleConnectionCommand = new RelayCommand(_ => ShowConnection = !ShowConnection);
        _showConnection = _settings.Problem() != null || (IsServerMode && !HasPassword);
    }

    /// <summary>The page should load (or reload) the dashboard.</summary>
    public event Action? ReloadRequested;

    public RelayCommand SaveCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ReloadCommand { get; }
    public RelayCommand OpenInBrowserCommand { get; }
    public RelayCommand ForgetPasswordCommand { get; }
    public RelayCommand ToggleConnectionCommand { get; }

    public RecordingsDashboardSettings Settings => _settings;
    public Uri? DashboardUri => _settings.DashboardUri;
    public bool IsInsideDashboard(Uri target) => _settings.IsInside(target);

    // ------------------------------------------------------------------ connection fields
    public bool IsServerMode { get => _isServerMode; set { if (SetProperty(ref _isServerMode, value)) { OnPropertyChanged(nameof(IsClientMode)); RaiseCommands(); } } }
    public bool IsClientMode { get => !_isServerMode; set => IsServerMode = !value; }
    public string ServerUrl { get => _serverUrl; set => SetProperty(ref _serverUrl, value); }
    public string BackendFolder { get => _backendFolder; set => SetProperty(ref _backendFolder, value); }
    public string DatabaseHost { get => _databaseHost; set => SetProperty(ref _databaseHost, value); }
    public int DatabasePort { get => _databasePort; set => SetProperty(ref _databasePort, value); }
    public string DatabaseUser { get => _databaseUser; set => SetProperty(ref _databaseUser, value); }
    public string DatabaseName { get => _databaseName; set => SetProperty(ref _databaseName, value); }
    public int Port { get => _port; set => SetProperty(ref _port, value); }
    public bool StartWithApp { get => _startWithApp; set => SetProperty(ref _startWithApp, value); }
    public bool ShowConnection { get => _showConnection; set => SetProperty(ref _showConnection, value); }
    public bool HasPassword => SafeHasPassword();
    public string PasswordState => HasPassword ? "Saved in Windows Credential Manager." : "Not saved yet.";
    public string Notice { get => _notice; private set => SetProperty(ref _notice, value); }

    // ------------------------------------------------------------------ status
    public ServerStatus Status => _status;
    public bool IsServerUp => _status.IsUp;
    public string StatusText => _settings.Mode == DashboardMode.Client
        ? $"Connected to {_settings.BaseUri?.Host ?? "(no server set)"}"
        : _status.State switch
        {
            ServerState.Running => "Server running on this PC",
            ServerState.RunningElsewhere => "Server running (started outside the app)",
            ServerState.Starting => "Starting the server…",
            ServerState.Failed => "Server not running",
            _ => "Server stopped",
        };
    public string StatusDetail => _settings.Mode == DashboardMode.Client ? "Sign in with the account the admin gave you." : _status.Message;
    public string AgentText => _settings.Mode == DashboardMode.Client ? "" : _status.Agent switch
    {
        AgentState.Running => "Agent running",
        AgentState.RunningElsewhere => "Agent running (outside the app)",
        AgentState.NotRegistered => "Agent not registered on this PC",
        AgentState.Exited => "Agent stopped",
        _ => "Agent stopped",
    };
    /// <summary>good, busy or bad: the colour of the status dot.</summary>
    public string StatusTone => _settings.Mode == DashboardMode.Client ? "good" : _status.State switch
    {
        ServerState.Running or ServerState.RunningElsewhere => _status.Agent is AgentState.Running or AgentState.RunningElsewhere ? "good" : "busy",
        ServerState.Starting => "busy",
        _ => "bad",
    };
    /// <summary>Show the web page (a client always tries; a server once its backend answers).</summary>
    public bool CanShowDashboard => DashboardUri != null && (_settings.Mode == DashboardMode.Client || _status.IsUp);

    private void OnStatusChanged(ServerStatus status)
    {
        void Apply()
        {
            var wasUp = _status.IsUp;
            _status = status;
            RaiseStatus();
            if (!wasUp && status.IsUp) ReloadRequested?.Invoke();
        }
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Apply();
        else dispatcher.BeginInvoke(Apply);
    }

    private void RaiseStatus()
    {
        foreach (var name in new[] { nameof(Status), nameof(IsServerUp), nameof(StatusText), nameof(StatusDetail), nameof(AgentText), nameof(StatusTone), nameof(CanShowDashboard) })
            OnPropertyChanged(name);
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        StartCommand?.RaiseCanExecuteChanged();
        StopCommand?.RaiseCanExecuteChanged();
        OpenInBrowserCommand?.RaiseCanExecuteChanged();
        ForgetPasswordCommand?.RaiseCanExecuteChanged();
    }

    // ------------------------------------------------------------------ actions

    /// <summary>At app start: a server set to start with the app starts; a client just shows the page.</summary>
    public Task StartIfConfiguredAsync() =>
        _settings.Mode == DashboardMode.Server && _settings.StartWithApp && SafeHasPassword() ? _host.StartAsync(_settings) : Task.CompletedTask;

    public Task StartAsync()
    {
        if (!Save()) return Task.CompletedTask;
        return _host.StartAsync(_settings);
    }

    /// <summary>Checks and keeps the connection fields. False (with a notice) when they cannot be used.</summary>
    public bool Save()
    {
        var next = _settings with
        {
            Mode = IsServerMode ? DashboardMode.Server : DashboardMode.Client,
            ServerUrl = (ServerUrl ?? "").Trim(),
            BackendFolder = (BackendFolder ?? "").Trim(),
            DatabaseHost = string.IsNullOrWhiteSpace(DatabaseHost) ? "127.0.0.1" : DatabaseHost.Trim(),
            DatabasePort = DatabasePort,
            DatabaseUser = string.IsNullOrWhiteSpace(DatabaseUser) ? "postgres" : DatabaseUser.Trim(),
            DatabaseName = string.IsNullOrWhiteSpace(DatabaseName) ? "postgres" : DatabaseName.Trim(),
            Port = Port,
            StartWithApp = StartWithApp,
        };
        if (next.Problem() is { } problem) { Notice = problem; return false; }
        var modeChanged = next.Mode != _settings.Mode || next.DashboardUri != _settings.DashboardUri;
        if (modeChanged && _settings.Mode == DashboardMode.Server && next.Mode == DashboardMode.Client) _host.Stop();
        _settings = next;
        try { _store.Save(next); Notice = "Saved."; }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { Notice = "Could not save: " + ex.Message; return false; }
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(DashboardUri));
        RaiseStatus();
        if (modeChanged) ReloadRequested?.Invoke();
        return true;
    }

    /// <summary>Called once by the page with the PasswordBox's value, which it then clears.</summary>
    public void SavePassword(string? password)
    {
        if (string.IsNullOrEmpty(password)) { Notice = "Type the database password first."; return; }
        try { _passwords.Save(password); Notice = "Password saved in Windows Credential Manager."; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException) { Notice = ex.Message; }
        OnPropertyChanged(nameof(HasPassword));
        OnPropertyChanged(nameof(PasswordState));
        RaiseCommands();
    }

    private void ForgetPassword()
    {
        try { _passwords.Delete(); Notice = "The saved password was removed."; }
        catch (System.ComponentModel.Win32Exception ex) { Notice = ex.Message; }
        OnPropertyChanged(nameof(HasPassword));
        OnPropertyChanged(nameof(PasswordState));
        RaiseCommands();
    }

    private bool SafeHasPassword()
    {
        try { return _passwords.HasPassword; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private void OpenInBrowser()
    {
        if (DashboardUri is { } uri) OpenExternally(uri);
    }

    /// <summary>Opens an address in the normal browser (links to Drive, Zoom and the like).</summary>
    public static void OpenExternally(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { WindowsUiErrorLog.Write("Could not open a link in the browser.", ex); }
    }

    private void LoadFields(RecordingsDashboardSettings s)
    {
        _isServerMode = s.Mode == DashboardMode.Server;
        _serverUrl = s.ServerUrl;
        _backendFolder = s.BackendFolder;
        _databaseHost = s.DatabaseHost;
        _databasePort = s.DatabasePort;
        _databaseUser = s.DatabaseUser;
        _databaseName = s.DatabaseName;
        _port = s.Port;
        _startWithApp = s.StartWithApp;
    }

    public void Dispose()
    {
        _host.StatusChanged -= OnStatusChanged;
        _host.Dispose();
    }
}
