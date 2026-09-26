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
    private readonly RecordingsDashboardSettingsStore _dashboardSettings = new();
    private readonly LocalAgentHost _agentHost = new();

    /// <summary>A newer version published on the central server, offered on the Dashboard.</summary>
    public AppUpdater Updater { get; } = new();

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
        RecordingsDashboardViewModel? recordingsDashboard = null, LmsSessionsViewModel? lmsSessions = null,
        CentralViewModel? central = null)
    {
        _service = service;
        _filteredNavigation = Navigation;
        _selectedNavigation = Navigation.First(n => n.Index == 0);
        Dashboard = new DashboardViewModel(service);
        RecordingsDashboard = recordingsDashboard ?? new RecordingsDashboardViewModel();
        LmsSessions = lmsSessions ?? new LmsSessionsViewModel();
        LmsSessions.Processor = Lms.FollowUpProcessor;
        Central = central ?? new CentralViewModel();
        // What the signed-in person may see on this shared PC. Every page that lists things keyed
        // by group narrows through it, so a coordinator never finds somebody else's work here. It
        // exists before anything can raise Central's PropertyChanged, which asks it to refresh.
        Scope = new SignedInScope(() => Central.Api.Me);
        Central.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(CentralViewModel.IsSignedIn) or nameof(CentralViewModel.IsAdmin))) return;
            RefreshNavigation();
            // Somebody else signed in: every page that shows things by group looks again, so the
            // list on screen is theirs and not the one before them.
            Scope.Refresh();
        };
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
        // The classes this PC runs for other coordinators, kept in step with the server: their
        // sign-ins, their groups, their timetables and the schedules that open by themselves.
        Runs = new DelegatedRuns(Central.Api, service);
        // This PC's own Zoom accounts go to the signed-in person's dashboard account: which group
        // each one hosts and the link its classes open. Whoever runs their classes picks from that
        // instead of being told a link twice.
        ZoomAccounts = new ZoomServerAccounts(Central.Api);
        // And the classes this PC opens by itself, so a new PC - a cloud one - finds them there
        // instead of being set up again.
        OwnSchedules = new ScheduleServerSync(Central.Api);
        StartMeeting = new StartMeetingViewModel(service);
        Accounts = new AccountsViewModel(service, scope: Scope);
        // A Zoom password typed here goes to the database at once, encrypted against the signed-in
        // dashboard account - not on the next pass, and not only into this PC's Credential Manager.
        // Every other PC, a cloud one included, takes it from there.
        // Saving an account signs its browser profile in to Zoom there and then, so nothing later
        // stops at Zoom's sign-in page.
        Accounts.SignInToZoom = (accountId, profile) => new ZoomProfileSignIn().SignInAsync(accountId, profile);
        Accounts.SaveToDatabase = async (accountId, password) =>
        {
            if (!Central.Api.IsSignedIn) return "nobody is signed in to the dashboard here, so it is only on this PC";
            var here = await _service.GetAccountsAsync();
            int? sent = await ZoomAccounts.PushAsync(here, (accountId, password));
            return sent is > 0
                ? "it is in the database too, encrypted"
                : "the database already has this PC's accounts; the password goes up on the next pass";
        };
        // Opening a class from its card goes the same way as the Start Meeting page: one account,
        // one link, one engine choice.
        LmsSessions.OpenMeeting ??= async (account, url, engine, token) =>
        {
            var started = await _service.StartMeetingAsync(account, url,
                engine switch
                {
                    ZoomAutoAdmit.Core.Sessions.SessionEngineType.Desktop => EnginePreference.Desktop,
                    ZoomAutoAdmit.Core.Sessions.SessionEngineType.Web => EnginePreference.Web,
                    _ => EnginePreference.Auto,
                }, token);
            return $"{account}: the meeting was opened with {started.EngineType}.";
        };
        // Starting a class again means letting go of what is running for it first: a meeting can be
        // gone from Zoom while this PC still believes it is hosting it.
        LmsSessions.StopMeetingsOf ??= async (group, token) =>
        {
            var running = await _service.GetActiveSessionsAsync(token);
            int stopped = 0;
            foreach (var session in running.Where(s => s.AccountId.Equals(group, StringComparison.OrdinalIgnoreCase)))
                if (await _service.StopMeetingAsync(session.SessionId, token)) stopped++;
            return stopped;
        };
        Schedules = new SchedulesViewModel(service, scope: Scope);
        Logs = new LogsViewModel();
        SessionRoles = new SessionRolesViewModel(scope: Scope)
        {
            // The cloud worker makes the instructor co-host from these, as this PC does.
            Publish = profiles => Central.Api.SaveSettingAsync("sessionRoles", new
            {
                profiles = System.Text.Json.JsonSerializer.SerializeToElement(profiles, RolesJson),
            }),
        };
        AiMatching = new AiMatchingViewModel(aiCredentials ?? new AiCredentialStore(), aiService ?? new AiMatchingService());
        Roster = new GroupRosterViewModel(groups ?? new GroupRosterStore(log: ConsoleLogger.Info),
            groupDialogs ?? new GroupRosterDialogs(), Scope);
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
        // Reads every turned-on coordinator's timetable again now, rather than waiting for the
        // next few hours to pass.
        SyncRunsCommand = new AsyncRelayCommand(async _ =>
        {
            RunsStatus = "Reading the coordinators' timetables from the LMS…";
            _lastRunsSync = DateTimeOffset.Now;
            try
            {
                var report = await Runs.SyncAsync(readTimetables: true);
                RunsStatus = report.Summary + string.Concat(report.Problems.Select(p => Environment.NewLine + p));
                await Schedules.RefreshAsync();
            }
            catch (Exception ex) { RunsStatus = "That did not work: " + CentralApiException.Explain(ex); }
        });
        NavigateCommand = new RelayCommand(value =>
        {
            if (int.TryParse(value?.ToString(), out var index)) SelectedTabIndex = index;
        });
    }

    /// <summary>Session roles as the server keeps them: names for the enums, as the file on disk has.</summary>
    private static readonly System.Text.Json.JsonSerializerOptions RolesJson =
        new(System.Text.Json.JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public SessionRolesViewModel SessionRoles { get; }
    /// <summary>Matches every recorded class to its roster by itself, hourly from the class's time.</summary>
    public AppAttendanceMatcher AttendanceMatcher { get; } = new();
    public DashboardViewModel Dashboard { get; }
    /// <summary>The central backend's dashboard (recordings, groups, users), and the server this PC may run for it.</summary>
    public RecordingsDashboardViewModel RecordingsDashboard { get; }
    /// <summary>Every class and where its Zoom / LMS cycle stands; also the LMS account in use.</summary>
    public LmsSessionsViewModel LmsSessions { get; }
    /// <summary>The central server's recordings, accounts and groups, as pages of the app.</summary>
    public CentralViewModel Central { get; }
    /// <summary>What the signed-in person may see of what this PC holds.</summary>
    public SignedInScope Scope { get; }
    public StartMeetingViewModel StartMeeting { get; }
    public AccountsViewModel Accounts { get; }
    public SchedulesViewModel Schedules { get; }
    /// <summary>The coordinators whose classes this PC opens and finishes as well as its own.</summary>
    public DelegatedRuns Runs { get; }
    /// <summary>This PC's Zoom accounts, kept against the signed-in person's dashboard account.</summary>
    public ZoomServerAccounts ZoomAccounts { get; }
    /// <summary>This PC's own classes, kept there too.</summary>
    public ScheduleServerSync OwnSchedules { get; }
    /// <summary>What the last pass brought here from the dashboard account, for the pages to show.</summary>
    public string RestoredStatus { get => _restoredStatus; private set => SetProperty(ref _restoredStatus, value); }
    private string _restoredStatus = "";
    public System.Windows.Input.ICommand SyncRunsCommand { get; }
    /// <summary>What the last pass over those coordinators did, for the Schedules page to show.</summary>
    public string RunsStatus { get => _runsStatus; private set => SetProperty(ref _runsStatus, value); }
    private string _runsStatus = "";
    private DateTimeOffset _lastRunsSync = DateTimeOffset.MinValue;
    /// <summary>How often the server is asked again who this PC runs and what their classes are.</summary>
    public static readonly TimeSpan SyncRunsEvery = TimeSpan.FromMinutes(5);
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

    /// <summary>
    /// Automatic co-host, beside the admit switch: off while the operator takes co-host away on
    /// purpose or tries something, so the app does not make anyone co-host (again) meanwhile.
    /// </summary>
    public bool IsAutoCoHost
    {
        get => AdmissionControl.IsAutoCoHost;
        set
        {
            if (AdmissionControl.IsAutoCoHost == value) return;
            AdmissionControl.SetAutoCoHost(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(CoHostStateText));
        }
    }
    public string CoHostStateText => IsAutoCoHost ? "Auto co-host" : "Co-host off";
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
        set { if (SetProperty(ref _navigationSearch, value)) RefreshNavigation(); }
    }

    /// <summary>The server and the coordinators are the admin's to run: a signed-in coordinator does not see those pages.</summary>
    private static readonly int[] AdminOnlyPages = [CoordinatorsPage, 14];
    public bool IsCoordinatorView => Central is { IsSignedIn: true, IsAdmin: false };

    private void RefreshNavigation()
    {
        string query = _navigationSearch?.Trim() ?? "";
        bool coordinator = IsCoordinatorView;
        SetProperty(ref _filteredNavigation, (IReadOnlyList<NavigationItem>)Navigation.Where(n =>
            n.Title.Contains(query, StringComparison.OrdinalIgnoreCase) && !(coordinator && AdminOnlyPages.Contains(n.Index))).ToArray(),
            nameof(FilteredNavigation));
        OnPropertyChanged(nameof(IsCoordinatorView));
        if (coordinator && AdminOnlyPages.Contains(SelectedTabIndex)) SelectedTabIndex = DashboardPage;
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
                // Each recorded class's names are matched to its roster once an hour from its time
                // (not awaited: an AI round can take a minute, and the page must keep refreshing).
                // Only in the app itself: a test's view model never sends real names to the AI.
                if (System.Windows.Application.Current is App) _ = AttendanceMatcher.TickAsync(DateTime.Now);
                // This PC's own agent carries its attendance and job results to the central server,
                // and joins the server by itself the first time it is signed in. Only in the app: a
                // test's view model never starts a process. It does nothing while all is well.
                if (System.Windows.Application.Current is App)
                {
                    var settings = _dashboardSettings.Load();
                    _ = _agentHost.EnsureRunningAsync(settings, Central.Api);
                    // Asks the server every few hours whether a newer version of the app is published.
                    _ = Updater.CheckAsync(settings, Central.Api);
                }
                // Whose classes this PC runs, and theirs for the days ahead. Only in the app
                // itself, and only the admin's copy has anybody else's to run.
                if (System.Windows.Application.Current is App && Central.IsSignedIn) _ = SyncThisPcAsync();
                if (System.Windows.Application.Current is App && Central.IsAdmin
                    && DateTimeOffset.Now - _lastRunsSync >= SyncRunsEvery)
                {
                    _lastRunsSync = DateTimeOffset.Now;
                    _ = SyncDelegatedRunsAsync();
                }
                await Lms.ProcessDueFollowUpAsync();
                if (_disposed) return;
                await LmsSessions.TickAsync();
            }
        }
        finally { _refreshing = false; }
    }
    /// <summary>
    /// One pass over the coordinators the admin runs. It is not awaited by the refresh: reading a
    /// timetable opens a browser and takes its time, and nothing on the page waits for it.
    /// </summary>
    private async Task SyncDelegatedRunsAsync()
    {
        try
        {
            var report = await Runs.SyncAsync();
            if (_disposed) return;
            RunsStatus = report.Summary;
            if (report.Scheduled > 0 || report.Removed > 0) await Schedules.RefreshAsync();
        }
        catch (Exception ex)
        {
            RunsStatus = "The coordinators' classes could not be brought here: " + CentralApiException.Explain(ex);
        }
    }

    /// <summary>
    /// What this PC and the dashboard account owe each other: its Zoom accounts and its own classes.
    /// A PC that has them sends them; a PC that has none - a new one - takes what was kept. Never
    /// load-bearing: the classes here run whether or not the server heard.
    /// </summary>
    private async Task SyncThisPcAsync()
    {
        var said = new List<string>();
        try
        {
            // Only what is theirs goes up as theirs. A coordinator signing in on the admin's PC sent
            // every account on it as their own (2026-09-21: Hosam "had" S7 and S8, Mohab G1 and G2).
            // Somebody with none of this PC's accounts neither sends nor takes: taking would write
            // their accounts over ones of the same name that belong to somebody else.
            var here = await _service.GetAccountsAsync();
            var theirs = here.Where(a => Scope.Owns(string.IsNullOrWhiteSpace(a.GroupName) ? a.AccountId : a.GroupName)).ToList();
            if ((here.Count == 0 || theirs.Count > 0)
                && await ZoomAccounts.SyncAsync(theirs, _service.SaveAccountAsync) is { } accounts)
                said.Add(accounts);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleLogger.Warn($"[ACCOUNTS] This PC's Zoom accounts and the server are not in step: {CentralApiException.Explain(ex)}");
        }
        try
        {
            var all = await _service.GetSchedulesAsync();
            var ownClasses = all.Where(s => Scope.Owns(string.IsNullOrWhiteSpace(s.GroupName) ? s.AccountId : s.GroupName)).ToList();
            if ((all.Count == 0 || ownClasses.Count > 0)
                && await OwnSchedules.SyncAsync(ownClasses, _service.SaveScheduleAsync) is { } classes)
                said.Add(classes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleLogger.Warn($"[SCHEDULES] This PC's classes and the server are not in step: {CentralApiException.Explain(ex)}");
        }
        if (_disposed || said.Count == 0) return;
        RestoredStatus = string.Join(" ", said);
        // Something arrived: the accounts and the timetable pages are showing the old, empty list.
        if (said.Any(line => line.Contains("came from", StringComparison.Ordinal)))
        {
            await Accounts.RefreshAsync();
            await Schedules.RefreshAsync();
            await StartMeeting.RefreshAccountsAsync();
            PublishAccountsToRoles();
        }
    }

    private async void OnAccountsChanged()
    {
        ZoomAccounts.Changed();
        OwnSchedules.Changed();
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
        ZoomAccounts.Changed();
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
        _agentHost.Dispose();
        Logs.Dispose();
        Dashboard.Dispose();
        Schedules.Dispose();
        AiMatching.Dispose();
        Attendance.Dispose();
        RecordingsDashboard.Dispose();
    }
}
