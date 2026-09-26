using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Attendance;
using ZoomAutoAdmit.SessionRoles;
using ZoomAutoAdmit.Core.Meetings;
using ZoomAutoAdmit.Core.Sessions;
using ZoomAutoAdmit.Inspector.Engines;
using ZoomAutoAdmit.WebAutomation;
using ZoomAutoAdmit.WebAutomation.Browser;
using ZoomAutoAdmit.WindowsRuntime;
using ZoomAutoAdmit.WindowsRuntime.Scheduling;

namespace ZoomAutoAdmit.Inspector.Runtime;

public sealed class WindowsRuntimeBootstrapper : IAsyncDisposable
{
    private readonly WindowsDesktopMeetingLauncher _desktopLauncher;
    private readonly WindowsWebMeetingLauncher _webLauncher;

    public WindowsRuntimeBootstrapper(
        string? accountMetadataPath = null,
        string? profilesRoot = null,
        IWindowsCredentialReferenceResolver? credentialResolver = null,
        string? schedulesPath = null,
        IWindowsTaskScheduler? taskScheduler = null,
        Func<MeetingLaunchContext, IAttendanceParticipantSource>? attendanceSources = null,
        IAttendanceSnapshotStore? attendanceStore = null,
        Func<MeetingLifecycleEvents, LmsMeetingBridge>? createLmsBridge = null,
        Func<MeetingLifecycleEvents, AutoEndMeetingBridge?>? createAutoEnd = null)
    {
        ProfileMapper = new WindowsAccountWebProfileMapper(profilesRoot);
        AccountManager = new WindowsMeetingAccountManager(
            accountMetadataPath,
            credentialResolver,
            ProfileMapper);
        SessionCoordinator = new SessionCoordinator();

        _desktopLauncher = new WindowsDesktopMeetingLauncher(
            new WindowsAutoAdmitEngine(),
            new WindowsDesktopMeetingPlatform(),
            new WindowsDesktopAutoAdmitPreparation());
        var webEngine = new WebAutoAdmitEngine(
            profileManager: new ZoomProfileManager(profilesRoot));
        var webLifecycle = new WindowsWebAutoAdmitLifecycle(webEngine);
        _webLauncher = new WindowsWebMeetingLauncher(
            webLifecycle,
            new WindowsWebMeetingPreparation(webLifecycle));
        RuntimeFactory = new WindowsMeetingRuntimeFactory(_desktopLauncher, _webLauncher);
        LifecycleEvents = new MeetingLifecycleEvents();
        var sources = new RuntimeAttendanceSources(() => webEngine.ActiveMeetingPage);
        Attendance = new AttendanceLifecycleBridge(LifecycleEvents, attendanceSources ?? sources.Create, attendanceStore);
        Orchestrator = new MeetingOrchestrator(
            AccountManager,
            SessionCoordinator,
            RuntimeFactory,
            LifecycleEvents);
        TaskScheduler = taskScheduler ?? new WindowsTaskSchedulerService();
        ScheduleStore = new WindowsMeetingScheduleStore(schedulesPath, TaskScheduler);
        Scheduler = new WindowsMeetingScheduler(
            ScheduleStore,
            new OrchestratedScheduledMeetingRunner(Orchestrator));
        // Optional observer: grants co-host from the session role profiles. It owns no engine and
        // never touches admission, attendance or scheduling behaviour.
        SessionRoles = new SessionRoleBridge(
            LifecycleEvents,
            attendanceSources ?? (context => sources.Create(context, mayOpenPanel: false)),
            new ScheduleNameSource(ScheduleStore),
            assignerFor: WebOrDesktopCoHost(() => webEngine.ActiveMeetingPage));
        // The LMS half of every class: Run Session when the meeting goes live, and the attendance
        // steps written down. Here so it works however the meeting was started (app or Windows task).
        LmsMeetingBridge.ClassStart classStart = async (group, day, live, token) =>
            ScheduleTiming.ClassStartNear(await ScheduleStore.ListAsync(token), group, day, live);
        Lms = createLmsBridge?.Invoke(LifecycleEvents) ?? new LmsMeetingBridge(LifecycleEvents, classStart: classStart);
        // A physical class opens no meeting: at its time it is run on the LMS by the same bridge.
        ScheduledClassStarter.RunInRoom = Lms.RunInRoomAsync;
        // Ends a finished class for everyone (three hours on, and only when the room is empty or small
        // and silent). Reads the participants list only; never opens it.
        AutoEnd = createAutoEnd != null ? createAutoEnd(LifecycleEvents) : new AutoEndMeetingBridge(
            LifecycleEvents,
            attendanceSources ?? (context => sources.Create(context, mayOpenPanel: false)),
            classStart: classStart,
            webPage: () => webEngine.ActiveMeetingPage);
        ConsoleLogger.Success("[BOOTSTRAP] Services initialized");
    }

    /// <summary>A web class is made co-host through the web page; a desktop class through the Zoom app.</summary>
    private static Func<MeetingLaunchContext, ICoHostAssigner> WebOrDesktopCoHost(Func<Microsoft.Playwright.IPage?> webPage)
    {
        var desktop = new ZoomCoHostAssigner();
        var web = new WebCoHostAssigner(webPage);
        return context => context.EngineType == SessionEngineType.Web ? web : desktop;
    }

    public WindowsMeetingAccountManager AccountManager { get; }
    public WindowsAccountWebProfileMapper ProfileMapper { get; }
    public SessionCoordinator SessionCoordinator { get; }
    public WindowsMeetingRuntimeFactory RuntimeFactory { get; }
    public MeetingOrchestrator Orchestrator { get; }
    public MeetingLifecycleEvents LifecycleEvents { get; }
    public AttendanceLifecycleBridge Attendance { get; }
    public SessionRoleBridge SessionRoles { get; }
    public LmsMeetingBridge Lms { get; }
    public AutoEndMeetingBridge? AutoEnd { get; }
    public IWindowsTaskScheduler TaskScheduler { get; }
    public WindowsMeetingScheduleStore ScheduleStore { get; }
    public WindowsMeetingScheduler Scheduler { get; }

    public async ValueTask DisposeAsync()
    {
        await Scheduler.DisposeAsync();
        await SessionRoles.DisposeAsync();
        await Lms.DisposeAsync();
        if (AutoEnd != null) await AutoEnd.DisposeAsync();
        await Attendance.DisposeAsync();
        await _webLauncher.DisposeAsync();
        await _desktopLauncher.DisposeAsync();
    }
}
