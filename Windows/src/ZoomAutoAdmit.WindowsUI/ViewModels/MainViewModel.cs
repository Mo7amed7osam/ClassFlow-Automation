using ZoomAutoAdmit.WindowsUI.Infrastructure;
using ZoomAutoAdmit.WindowsUI.Services;
using ZoomAutoAdmit.Roster;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Meetings;
using System.Windows.Threading;

namespace ZoomAutoAdmit.WindowsUI.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private int _selectedTabIndex;
    private string _navigationSearch = "";
    private string _pageTitle = "Overview";
    private string _pageSubtitle = "What this workspace is doing right now.";
    private NavigationItem? _selectedNavigation;
    private IReadOnlyList<NavigationItem> _filteredNavigation;
    private DispatcherTimer? _refreshTimer;
    private bool _refreshing, _disposed;
    private DateTimeOffset _lastLmsFollowUpCheck = DateTimeOffset.MinValue;

    /// <summary>Pages in MainWindow's TabControl. A page added there must be reachable from the sidebar.</summary>
    public const int TabCount = 18;
    public const int DashboardPage = 17;
    public const int SessionsPage = 15;
    public const int CentralRecordingsPage = 16;
    public const int CoordinatorsPage = 5;

    /// <summary>Subtitle is what this page is for, in one line, shown under its title.</summary>
    public sealed record NavigationItem(int Index, string Title, string Icon, string Section, string Subtitle = "");
    public IReadOnlyList<NavigationItem> Navigation { get; } = new NavigationItem[]
    {
        new(17, "Dashboard", "\uE80A", "MAIN", "Who is signed in (admin or coordinator), their LMS account, every session and the coordinators."),
        new(0, "Overview", "\uE80F", "MAIN", "What this workspace is doing right now."),
        new(15, "Sessions", "\uE8F9", "MAIN", "Every class and where it stands: Zoom, the LMS, attendance, Complete and its record link."),
        new(7, "Meetings", "\uE714", "MAIN", "Sessions running now, and what each one is using."),
        new(12, "AI Engine", "\uE950", "MAIN", "The model behind name matching, and whether it can be reached."),
        new(3, "Schedules", "\uE787", "AUTOMATION", "Meetings that open by themselves at a set date and time."),
        new(10, "Waiting Room", "\uE716", "AUTOMATION", "What the monitor is watching, and who it let in."),
        new(9, "AI Matching", "\uE713", "AUTOMATION", "Turn the names seen in Zoom into students on your roster."),
        new(13, "Session Roles", "\uE77B", "AUTOMATION", "Who is made co-host when a session of each type starts."),
        new(6, "Groups & Students", "\uE902", "RECORDS", "Your groups, and the students in each one, in roster order."),
        new(8, "Attendance", "\uE9D5", "RECORDS", "Who was seen in a session, checked against your roster."),
        new(16, "Recordings", "\uE8B2", "RECORDS", "Each class's recording link (Drive, Zoom or missing) and where it stands on the LMS."),
        new(5, "Coordinators & groups", "\uE7EF", "RECORDS", "Accounts, approvals and which groups each coordinator sees (admin)."),
        new(14, "Server", "\uE968", "SYSTEM", "The central server this PC runs (or connects to): its port and database."),
        new(2, "Accounts", "\uE77B", "SYSTEM", "The Zoom accounts this computer can host with."),
        new(4, "Logs", "\uE9D9", "SYSTEM", "What the app recorded while it worked, newest first."),
        new(11, "Settings", "\uE713", "SYSTEM", "Night or day, and where this PC keeps its data. Nothing here changes a meeting.")
    };

    private readonly IWindowsUiService _service;

    public MainViewModel(IWindowsUiService service, IStudentRosterService? roster = null, IStudentDialogs? studentDialogs = null,
        IGroupRosterService? groups = null, IGroupRosterDialogs? groupDialogs = null,
        IAiCredentialStore? aiCredentials = null, IAiMatchingService? aiService = null,
        IAttendanceHistoryReader? attendanceHistory = null, IAttendanceDialogs? attendanceDialogs = null,
        RecordingsDashboardViewModel? recordingsDashboard = null, LmsSessionsViewModel? lmsSessions = null)
    {
        _service = service;
        _filteredNavigation = Navigation;
        _selectedNavigation = Navigation.First(n => n.Index == 0);
        Dashboard = new DashboardViewModel(service);
        RecordingsDashboard = recordingsDashboard ?? new RecordingsDashboardViewModel();
        LmsSessions = lmsSessions ?? new LmsSessionsViewModel();
        LmsSessions.Processor = Lms.FollowUpProcessor;
        Central = new CentralViewModel();
        // "Check sheet" looks first at what n8n sent the central server from the recordings sheet.
        LmsSessions.CentralDriveLink = async (group, date, start) =>
        {
            if (await Central.Api.EnsureSignedInAsync() == null) throw new InvalidOperationException("not signed in to the dashboard");
            var page = await Central.Api.RecordingsAsync(group, null, "drive");
            var match = page.Items
                .Where(r => r.Date == date.ToString("yyyy-MM-dd") && !string.IsNullOrWhiteSpace(r.DriveLink))
                .OrderBy(r => TimeOnly.TryParse(r.StartTime, out var at) ? Math.Abs((at - start).TotalMinutes) : 9999)
                .FirstOrDefault();
            return (match?.DriveLink, match?.FileName);
        };
        StartMeeting = new StartMeetingViewModel(service);
        Accounts = new AccountsViewModel(service);
        Schedules = new SchedulesViewModel(service);
        Logs = new LogsViewModel();
        SessionRoles = new SessionRolesViewModel();
        AiMatching = new AiMatchingViewModel(aiCredentials ?? new AiCredentialStore(), aiService ?? new AiMatchingService());
        Roster = new GroupRosterViewModel(groups ?? new GroupRosterStore(log: ConsoleLogger.Info), groupDialogs ?? new GroupRosterDialogs());
        Attendance = new AttendanceViewModel(
            attendanceHistory ?? new AttendanceHistoryReader(),
            AiMatching,
            service as IAttendanceUiActions,
            attendanceDialogs ?? new AttendanceDialogs(),
            // A session names the account it ran under, which is the group; the page needs no picker.
            () => Roster.Groups);
        WaitingRoom = new WaitingRoomViewModel(service as IMeetingActivitySource);
        Students = new StudentsViewModel(roster ?? new StudentRosterStore(log: ConsoleLogger.Info), studentDialogs ?? new StudentDialogs());
        StartMeeting.MeetingStarted += OnMeetingStarted;
        service.MeetingBecameLive += OnMeetingBecameLive;
        StartMeeting.AccountsChanged += OnStartMeetingAccountsChanged;
        Accounts.AccountsChanged += OnAccountsChanged;
        ShowStartMeetingCommand = new RelayCommand(_ => SelectedTabIndex = 1);
        ToggleAdmittingCommand = new RelayCommand(_ => IsAdmitting = !IsAdmitting);
        ResetAdmittedTodayCommand = new RelayCommand(_ =>
        {
            AdmissionControl.ResetToday();
            RefreshAdmissionState();
        });
        RefreshAdmissionState();
        NavigateCommand = new RelayCommand(value =>
        {
            if (int.TryParse(value?.ToString(), out var index)) SelectedTabIndex = index;
        });
    }

    public SessionRolesViewModel SessionRoles { get; }
    public DashboardViewModel Dashboard { get; }
    /// <summary>The central backend's dashboard (recordings, groups, users), and the server this PC may run for it.</summary>
    public RecordingsDashboardViewModel RecordingsDashboard { get; }
    /// <summary>Every class and where its Zoom / LMS cycle stands; also the LMS account in use.</summary>
    public LmsSessionsViewModel LmsSessions { get; }
    /// <summary>The central server's recordings, accounts and groups, as pages of the app.</summary>
    public CentralViewModel Central { get; }
    public StartMeetingViewModel StartMeeting { get; }
    public AccountsViewModel Accounts { get; }
    public SchedulesViewModel Schedules { get; }
    public LogsViewModel Logs { get; }
    public AiMatchingViewModel AiMatching { get; }
    public AttendanceViewModel Attendance { get; }
    public WaitingRoomViewModel WaitingRoom { get; }
    /// <summary>The dashboard sign-in and the Run Session press that goes with a class.</summary>
    public LmsViewModel Lms { get; } = new();
    public StudentsViewModel Students { get; }
    public GroupRosterViewModel Roster { get; }
    public RelayCommand ShowStartMeetingCommand { get; }
    public RelayCommand NavigateCommand { get; }
    public string PageTitle => _pageTitle;

    /// <summary>
    /// The admitting switch, on unless somebody turns it off. It is kept in a file the engines
    /// read, so turning it off stops the engine in its own process too, not just this window.
    /// </summary>
    public bool IsAdmitting
    {
        get => AdmissionControl.IsAdmitting;
        set
        {
            if (AdmissionControl.IsAdmitting == value) return;
            AdmissionControl.SetPaused(!value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(AdmissionStateText));
        }
    }
    public string AdmissionStateText => IsAdmitting ? "Admitting" : "Paused";
    public string AdmissionBadgeText => IsAdmitting ? "WATCHING" : "PAUSED";
    /// <summary>Which engine the live meeting uses, or what the panel is waiting for.</summary>
    public string AdmissionEngineText => Dashboard.PrimarySession is { } session
        ? $"ZOOM {session.EngineType.ToString().ToUpperInvariant()} CLIENT"
        : "DESKTOP + WEB RUNTIME";

    /// <summary>One "Admit all" press instead of admitting people one at a time.</summary>
    public bool PrefersAdmitAll
    {
        get => AdmissionControl.PrefersAdmitAll;
        set
        {
            if (AdmissionControl.PrefersAdmitAll == value) return;
            AdmissionControl.SetPrefersAdmitAll(value);
            OnPropertyChanged();
        }
    }

    /// <summary>Whether a Web meeting opens a browser window you can watch.</summary>
    public bool ShowWebBrowser
    {
        get => AdmissionControl.ShowWebBrowser;
        set
        {
            if (AdmissionControl.ShowWebBrowser == value) return;
            AdmissionControl.SetShowWebBrowser(value);
            OnPropertyChanged();
        }
    }

    public RelayCommand ResetAdmittedTodayCommand { get; private set; } = new(_ => { });
    public int AdmittedToday => _admittedToday;
    public string AdmittedTodayText => _admittedToday == 1
        ? "1 person admitted today"
        : $"{_admittedToday} people admitted today";
    private int _admittedToday;

    public RelayCommand ToggleAdmittingCommand { get; private set; } = new(_ => { });

    private void RefreshAdmissionState()
    {
        var count = AdmissionControl.AdmittedToday();
        if (count != _admittedToday)
        {
            _admittedToday = count;
            OnPropertyChanged(nameof(AdmittedToday));
            OnPropertyChanged(nameof(AdmittedTodayText));
        }
        OnPropertyChanged(nameof(IsAdmitting));
        OnPropertyChanged(nameof(AdmissionStateText));
        OnPropertyChanged(nameof(AdmissionBadgeText));
        OnPropertyChanged(nameof(AdmissionEngineText));
        OnPropertyChanged(nameof(PrefersAdmitAll));
        OnPropertyChanged(nameof(ShowWebBrowser));
    }
    public string PageSubtitle => _pageSubtitle;
    public IReadOnlyList<NavigationItem> FilteredNavigation => _filteredNavigation;
    public NavigationItem? SelectedNavigation
    {
        get => _selectedNavigation;
        set { if (value != null) SelectedTabIndex = value.Index; }
    }
    public string NavigationSearch
    {
        get => _navigationSearch;
        set
        {
            if (SetProperty(ref _navigationSearch, value))
                SetProperty(ref _filteredNavigation, (IReadOnlyList<NavigationItem>)Navigation.Where(n =>
                    n.Title.Contains(value?.Trim() ?? "", StringComparison.OrdinalIgnoreCase)).ToArray(), nameof(FilteredNavigation));
        }
    }
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (value >= 0 && value < TabCount && SetProperty(ref _selectedTabIndex, value))
            {
                var item = Navigation.FirstOrDefault(n => n.Index == value);
                SetProperty(ref _selectedNavigation, item, nameof(SelectedNavigation));
                SetProperty(ref _pageTitle, item?.Title ?? "Start a meeting", nameof(PageTitle));
                SetProperty(
                    ref _pageSubtitle,
                    item?.Subtitle is { Length: > 0 } subtitle
                        ? subtitle
                        : "Pick the account and the link, and hand the meeting to Auto Admit.",
                    nameof(PageSubtitle));
                WindowsUiRuntimeLog.Write("NAVIGATION", $"Selected tab index: {value}.");
            }
        }
    }

    public async Task InitializeAsync()
    {
        List<Exception> failures = [];
        await TryInitializeAsync(Accounts.RefreshAsync, failures);
        await TryInitializeAsync(StartMeeting.RefreshAccountsAsync, failures);
        await TryInitializeAsync(Schedules.RefreshAsync, failures);
        await TryInitializeAsync(Dashboard.RefreshAsync, failures);
        await TryInitializeAsync(Students.RefreshAsync, failures);
        await TryInitializeAsync(Roster.RefreshAsync, failures);
        PublishAccountsToRoles();
        AiMatching.LoadSavedSettings();
        await Attendance.RefreshAsync();
        WaitingRoom.Refresh();
        if (!_disposed && _refreshTimer == null)
        {
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _refreshTimer.Tick += RefreshConnectedViews;
            _refreshTimer.Start();
        }
        if (failures.Count > 0)
            throw new AggregateException("One or more UI services could not be initialized.", failures);
    }

    private static async Task TryInitializeAsync(Func<Task> initialize, List<Exception> failures)
    {
        try { await initialize(); }
        catch (Exception ex) { failures.Add(ex); }
    }

    private async void OnMeetingStarted() => await Dashboard.RefreshAsync();

    /// <summary>
    /// The LMS half of a class (Run Session, and writing down its attendance steps) is done by the
    /// runtime's LmsMeetingBridge, so a meeting opened by a Windows task gets it too. The window only
    /// works through the follow-up queue (below) and shows what happened.
    /// </summary>
    private void OnMeetingBecameLive(LiveMeeting meeting) =>
        ConsoleLogger.Info($"[LMS] Meeting live for {meeting.GroupId} ({meeting.ScheduledStart.ToLocalTime():HH:mm}).");
    private async void RefreshConnectedViews(object? sender, EventArgs e)
    {
        if (_refreshing || _disposed) return;
        RefreshAdmissionState();
        _refreshing = true;
        try
        {
            await Dashboard.RefreshAsync();
            if (_disposed) return;
            WaitingRoom.Refresh();
            if (SelectedTabIndex == 9 && AiMatching.IsIdle) await Attendance.RefreshAsync();
            if (DateTimeOffset.Now - _lastLmsFollowUpCheck >= TimeSpan.FromSeconds(30))
            {
                _lastLmsFollowUpCheck = DateTimeOffset.Now;
                await Lms.ProcessDueFollowUpAsync();
                if (_disposed) return;
                await LmsSessions.TickAsync();
            }
        }
        finally { _refreshing = false; }
    }
    private async void OnAccountsChanged()
    {
        await StartMeeting.RefreshAccountsAsync();
        await Schedules.RefreshAsync();
        PublishAccountsToRoles();
    }

    private void PublishAccountsToRoles() =>
        SessionRoles.SetAvailableAccounts(Accounts.Items.Select(account =>
            string.IsNullOrWhiteSpace(account.GroupName) ? account.AccountId : account.GroupName));

    // An account added from Start Meeting is already selected there; the other pages just reload it.
    private async void OnStartMeetingAccountsChanged()
    {
        await Accounts.RefreshAsync();
        await Schedules.RefreshAsync();
        PublishAccountsToRoles();
    }

    public void Dispose()
    {
        _disposed = true;
        if (_refreshTimer != null) { _refreshTimer.Stop(); _refreshTimer.Tick -= RefreshConnectedViews; }
        StartMeeting.MeetingStarted -= OnMeetingStarted;
        _service.MeetingBecameLive -= OnMeetingBecameLive;
        StartMeeting.AccountsChanged -= OnStartMeetingAccountsChanged;
        Accounts.AccountsChanged -= OnAccountsChanged;
        Logs.Dispose();
        Dashboard.Dispose();
        Schedules.Dispose();
        AiMatching.Dispose();
        Attendance.Dispose();
        RecordingsDashboard.Dispose();
    }
}
