import AppKit
import Foundation
import OSLog
import UserNotifications
import ZoomAutoAdmitCore

/// Wires persisted schedules to the startup workflow and to the *existing* Auto
/// Admit monitor.
///
/// It deliberately owns no polling of its own: when a meeting is verified it
/// calls the same `AutoAdmitMonitor` the menu commands use, so there is exactly
/// one Waiting Room loop in the process no matter how many schedules fire.
final class SchedulerCoordinator {
    private let logger = Logger(subsystem: "com.mohamedhosam.ZoomAutoAdmit", category: "scheduler-coordinator")
    private let store: ScheduleStore
    private let schedulerLog: SchedulerLog
    private let state: AppState
    private let workflowQueue = DispatchQueue(label: "com.mohamedhosam.ZoomAutoAdmit.workflow", qos: .userInitiated)
    private let automation: ZoomAutomating

    /// Supplied by the app delegate so the coordinator drives the one shared monitor.
    private let startAutoAdmit: () -> Void
    private let stopAutoAdmit: () -> Void
    /// Required lifecycle hooks. Attendance remains failure-isolated at runtime,
    /// but making the wiring non-optional prevents a scheduled workflow from
    /// silently doing nothing because AppDelegate forgot to connect it.
    private let startAttendance: (StudentGroup, ZoomSchedule) -> Void
    private let stopAttendance: (_ finalize: Bool) -> Void
    /// Dashboard steps, Web meetings and the launch agent. Optional so tests can leave it out.
    private let automationCoordinator: AutomationCoordinator?

    private var scheduler: SchedulerService!
    private var configuration: SchedulerConfiguration
    private var isWorkflowRunning = false
    private let workflowLock = NSLock()
    /// Schedules whose Auto Admit run was started by the scheduler, so an end
    /// time only ever stops monitoring the scheduler itself turned on.
    private var schedulerStartedMonitoring = Set<UUID>()
    private var runningScheduleID: UUID?
    /// The event log and notifications. Nil in tests, where notifications go out directly.
    weak var operations: OperationsCenter?

    init(
        state: AppState,
        store: ScheduleStore = ScheduleStore(),
        schedulerLog: SchedulerLog = .shared,
        automation: ZoomAutomating = LiveZoomAutomation(),
        startAutoAdmit: @escaping () -> Void,
        stopAutoAdmit: @escaping () -> Void,
        startAttendance: @escaping (StudentGroup, ZoomSchedule) -> Void,
        stopAttendance: @escaping (_ finalize: Bool) -> Void,
        automationCoordinator: AutomationCoordinator? = nil
    ) {
        self.automationCoordinator = automationCoordinator
        self.state = state
        self.store = store
        self.schedulerLog = schedulerLog
        self.automation = automation
        self.startAutoAdmit = startAutoAdmit
        self.stopAutoAdmit = stopAutoAdmit
        self.startAttendance = startAttendance
        self.stopAttendance = stopAttendance
        self.configuration = store.load()

        scheduler = SchedulerService(
            configuration: configuration,
            onFire: { [weak self] schedule, profile, date in
                self?.handleFire(schedule: schedule, profile: profile, occurrence: date)
            },
            onMonitoringEnd: { [weak self] schedule in
                self?.handleMonitoringEnd(schedule: schedule)
            },
            onPreflight: { [weak self] schedule, profile, startsAt in
                self?.runPreflight(schedule: schedule, profile: profile, startsAt: startsAt)
            }
        )
    }

    var currentConfiguration: SchedulerConfiguration { configuration }
    var scheduleFileURL: URL { store.location }
    var logFileURL: URL { schedulerLog.fileURL }

    func start() {
        requestNotificationAuthorization()
        scheduler.start()
        refreshNextScheduleSummary()
        // A wake from sleep can skip many ticks; re-evaluate immediately so a
        // start time that passed while the Mac slept is still caught.
        NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didWakeNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            self?.scheduler.checkNow()
            self?.refreshNextScheduleSummary()
        }
        schedulerLog.write("Scheduler started with \(configuration.schedules.count) schedule(s)")
    }

    func stop() {
        scheduler.stop()
    }

    func update(configuration newConfiguration: SchedulerConfiguration) {
        configuration = newConfiguration
        store.save(newConfiguration)
        scheduler.update(configuration: newConfiguration)
        refreshNextScheduleSummary()
        automationCoordinator?.syncLaunchAgent()
        schedulerLog.write("Schedules saved (\(newConfiguration.schedules.count) schedule(s))")
    }

    func reload() {
        configuration = store.load()
        scheduler.update(configuration: configuration)
        refreshNextScheduleSummary()
    }

    /// Runs a schedule immediately, for testing a configuration without waiting.
    /// Returns false when a workflow is already running, so the caller can keep
    /// its Run Now control disabled instead of queueing a second startup.
    @discardableResult
    func runNow(_ schedule: ZoomSchedule) -> Bool {
        guard let profile = configuration.profile(for: schedule) else {
            DispatchQueue.main.async { [state] in
                state.setRunOutcome(.failed(
                    title: "Schedule has no account profile",
                    detail: "Choose a Zoom account for “\(schedule.name)” first."
                ))
            }
            return false
        }
        guard !isWorkflowActive else {
            schedulerLog.write("Run Now ignored for \(schedule.name): a workflow is already running")
            return false
        }
        handleFire(schedule: schedule, profile: profile, occurrence: Date())
        return true
    }

    /// The schedule whose workflow is running, for the dashboard.
    var activeWorkflowScheduleID: UUID? {
        workflowLock.lock()
        defer { workflowLock.unlock() }
        return isWorkflowRunning ? runningScheduleID : nil
    }

    /// True from the moment a workflow is queued until it finishes.
    var isWorkflowActive: Bool {
        workflowLock.lock()
        defer { workflowLock.unlock() }
        return isWorkflowRunning
    }

    /// The soonest upcoming schedule, for the menu's Next meeting section.
    func nextScheduled() -> (schedule: ZoomSchedule, date: Date)? {
        scheduler.nextScheduled()
    }

    func refreshNextScheduleSummary() {
        guard let next = scheduler.nextScheduled() else {
            DispatchQueue.main.async { [state] in state.setNextScheduleSummary([]) }
            return
        }
        let profileName = configuration.profile(for: next.schedule)?.name ?? "No account"
        let formatter = DateFormatter()
        formatter.dateStyle = .medium
        formatter.timeStyle = .short
        formatter.doesRelativeDateFormatting = true
        let lines = [
            next.schedule.name,
            formatter.string(from: next.date),
            "\(profileName) · \(next.schedule.meeting.displayText)"
        ]
        DispatchQueue.main.async { [state] in state.setNextScheduleSummary(lines) }
    }

    /// Checks a schedule ahead of time and reports anything that would stop it.
    ///
    /// Read-only, and deliberately quiet when everything is fine: a
    /// notification five minutes before every class would train the user to
    /// ignore them.
    private func runPreflight(schedule: ZoomSchedule, profile: ZoomAccountProfile?, startsAt: Date) {
        workflowQueue.async { [weak self] in
            guard let self else { return }
            let report = PreflightChecker.check(
                schedule: schedule,
                profile: profile,
                group: self.configuration.group(for: schedule),
                startsAt: startsAt,
                automation: self.automation
            )

            self.schedulerLog.write("──────── Pre-flight: \(report.scheduleName) ────────")
            if report.issues.isEmpty {
                self.schedulerLog.write("Pre-flight: ready")
            } else {
                for issue in report.issues {
                    self.schedulerLog.write("Pre-flight [\(issue.severity.rawValue)] \(issue.kind.rawValue): \(issue.message)")
                }
            }

            if let operations = self.operations {
                // The operations layer extends this check, reports it and decides whether to notify.
                operations.preflightCompleted(schedule: schedule, startsAt: startsAt, preflight: report)
                DispatchQueue.main.async { [state] in state.setPreflightReport(report.issues.isEmpty ? nil : report) }
                return
            }
            guard !report.issues.isEmpty else { return }
            self.notify(
                title: report.isReady
                    ? "\(report.scheduleName) starts soon"
                    : "\(report.scheduleName) needs attention",
                body: report.detail
            )
            DispatchQueue.main.async { [state] in
                state.setPreflightReport(report)
            }
        }
    }

    // MARK: Workflow

    private func handleFire(schedule: ZoomSchedule, profile: ZoomAccountProfile, occurrence: Date) {
        // The desktop app holds one meeting. An account set to Web, or Auto while the desktop
        // app is busy, goes to the Zoom Web Client instead - which needs no workflow lock, so two
        // classes can overlap.
        if let automationCoordinator, let link = ZoomEngineAllocator.webLink(for: schedule.meeting), profile.preferredEngine != .desktop {
            let desktopBusy = isWorkflowActive || automationCoordinator.allocator.isDesktopReserved || desktopHasActiveMeeting()
            let engine = automationCoordinator.allocator.allocate(
                scheduleID: schedule.id,
                preference: profile.preferredEngine,
                hasWebLink: true,
                desktopHasActiveMeeting: desktopBusy
            )
            schedulerLog.write("[ALLOCATOR] \(schedule.name): \(profile.preferredEngine.rawValue) preference, desktop busy=\(desktopBusy) → \(engine.rawValue)")
            if engine == .web {
                runWebMeeting(schedule: schedule, profile: profile, link: link, occurrence: occurrence, automation: automationCoordinator)
                return
            }
            automationCoordinator.allocator.release(scheduleID: schedule.id)
        }

        workflowLock.lock()
        guard !isWorkflowRunning else {
            workflowLock.unlock()
            // Two schedules landing together must not race two startups.
            schedulerLog.write("Skipped \(schedule.name): another workflow is already running")
            return
        }
        isWorkflowRunning = true
        runningScheduleID = schedule.id
        workflowLock.unlock()
        report(schedule, kind: .zoomStarting, severity: .info, title: "Zoom starting", message: "Opening \(schedule.meeting.name) with \(profile.name)", notify: false)

        DispatchQueue.main.async { [state] in
            state.setRunOutcome(.running(scheduleName: schedule.name))
        }

        workflowQueue.async { [weak self] in
            guard let self else { return }
            defer {
                self.workflowLock.lock()
                self.isWorkflowRunning = false
                self.workflowLock.unlock()
            }

            self.schedulerLog.write("──────── Schedule triggered: \(schedule.name) ────────")
            self.schedulerLog.write("Occurrence: \(occurrence)")
            self.schedulerLog.write("Required account profile: \(profile.name) <\(profile.accountIdentifier)>")
            self.schedulerLog.write("Meeting: \(schedule.meeting.displayText)")
            let groupID = schedule.attendanceGroupID?.uuidString ?? "none"
            if let group = self.configuration.group(for: schedule) {
                self.schedulerLog.write(
                    "[attendance] schedule=\(schedule.id.uuidString)/\(schedule.name) "
                    + "attendanceGroupID=\(groupID) group=\(group.id.uuidString)/\(group.name) "
                    + "roster=\(group.students.count)"
                )
            } else {
                self.schedulerLog.write(
                    "[attendance] schedule=\(schedule.id.uuidString)/\(schedule.name) "
                    + "attendanceGroupID=\(groupID) group=unresolved roster=0"
                )
            }

            let runner = ZoomWorkflowRunner(
                automation: self.automation,
                timeouts: ZoomWorkflowTimeouts()
            ) { [weak self] state, detail in
                guard let self else { return }
                self.schedulerLog.write(state: state, detail: detail)
                self.publish(state: state, detail: detail, schedule: schedule, profile: profile)
            }

            let result = runner.run(schedule: schedule, profile: profile)
            self.finish(result: result, schedule: schedule)
        }
    }

    private func publish(
        state workflowState: ZoomWorkflowState,
        detail: String?,
        schedule: ZoomSchedule,
        profile: ZoomAccountProfile
    ) {
        // Internal state names stay in the log; the menu gets plain language.
        let text = WorkflowPresentation.progressText(
            for: workflowState,
            schedule: schedule,
            profile: profile
        )
        DispatchQueue.main.async { [state] in state.setWorkflowStatus(text) }
    }

    private func finish(result: ZoomWorkflowResult, schedule: ZoomSchedule) {
        switch result {
        case .completed(let autoAdmitStarted):
            schedulerLog.write("Workflow completed for \(schedule.name); autoAdmit=\(autoAdmitStarted)")
            report(schedule, kind: .zoomStarted, severity: .success, title: "Zoom started", message: autoAdmitStarted ? "Meeting verified; Auto Admit is active" : "Meeting verified; Auto Admit is off", notify: false)
            // Attendance recording begins only once the meeting is verified.
            dispatchAttendanceStart(for: schedule)
            automationCoordinator?.meetingWentLive(
                schedule: schedule,
                profile: configuration.profile(for: schedule),
                group: configuration.group(for: schedule),
                startedAt: Date(),
                engine: .desktop
            )

            if autoAdmitStarted {
                schedulerStartedMonitoring.insert(schedule.id)
                scheduler.registerMonitoring(for: schedule, startedAt: Date())
                DispatchQueue.main.async { [startAutoAdmit] in startAutoAdmit() }
                schedulerLog.write("Auto Admit started via the existing monitor")
            } else if configuration.group(for: schedule) != nil {
                // No Auto Admit, but the register still has to close at the end time.
                scheduler.registerMonitoring(for: schedule, startedAt: Date())
            }
            DispatchQueue.main.async { [state] in
                state.setRunOutcome(.succeeded(
                    meetingName: schedule.meeting.name,
                    autoAdmitActive: autoAdmitStarted
                ))
            }
            notify(
                title: "\(schedule.meeting.name) started",
                body: autoAdmitStarted
                    ? "Auto Admit is active."
                    : "Auto Admit is off for this schedule."
            )

        case .failed(let failure):
            let copy = WorkflowPresentation.copy(for: failure)
            schedulerLog.write("Workflow FAILED for \(schedule.name): \(failure.message)")
            report(schedule, kind: .zoomFailed, severity: .failure, title: "Zoom did not start ❌", message: "Reason:\n\(copy.title)\n\n\(copy.detail)", notify: true)
            DispatchQueue.main.async { [state] in
                state.setRunOutcome(.failed(title: copy.title, detail: copy.detail))
            }
            notify(title: copy.title, body: copy.detail)
        }

        DispatchQueue.main.asyncAfter(deadline: .now() + 6) { [state] in
            state.clearTransientRunState()
        }
        refreshNextScheduleSummary()
    }

    /// Isolated so the schedule→group→lifecycle wiring has a direct regression
    /// test. The callback is non-optional and is dispatched on the main thread,
    /// where AppDelegate owns the AttendanceCoordinator.
    func dispatchAttendanceStart(for schedule: ZoomSchedule) {
        if let group = configuration.group(for: schedule) {
            schedulerLog.write(
                "[attendance] start-dispatch schedule=\(schedule.id.uuidString) "
                + "group=\(group.id.uuidString)/\(group.name) roster=\(group.students.count)"
            )
            DispatchQueue.main.async { [startAttendance] in
                startAttendance(group, schedule)
            }
        } else if let groupID = schedule.attendanceGroupID {
            schedulerLog.write(
                "[attendance] start-blocked schedule=\(schedule.id.uuidString) "
                + "group=\(groupID.uuidString) reason=group-not-found"
            )
        } else {
            schedulerLog.write(
                "[attendance] start-skipped schedule=\(schedule.id.uuidString) reason=no-linked-group"
            )
        }
    }

    private func handleMonitoringEnd(schedule: ZoomSchedule) {
        if let automationCoordinator, automationCoordinator.isWebMeetingRunning(for: schedule.id) {
            schedulerLog.write("End time reached for \(schedule.name); closing its Web meeting")
            automationCoordinator.stopWebMeeting(scheduleID: schedule.id)
            report(schedule, kind: .zoomEnded, severity: .info, title: "Web meeting closed", message: "End time reached", notify: false)
            notify(title: "Web meeting closed", body: "End time reached for \(schedule.name)")
            return
        }
        guard schedulerStartedMonitoring.remove(schedule.id) != nil else {
            // After a relaunch mid-class, or for a class run without Auto Admit, the register is
            // live but Auto Admit was not started here. Auto Admit is left alone; the register
            // still closes at the end time.
            if automationCoordinator?.liveAttendanceSession()?.scheduleID == schedule.id {
                schedulerLog.write("End time reached for \(schedule.name); closing its attendance register only")
                report(schedule, kind: .zoomEnded, severity: .info, title: "Class ended", message: "End time reached; register closed", notify: false)
                DispatchQueue.main.async { [stopAttendance] in stopAttendance(true) }
                return
            }
            schedulerLog.write("End time for \(schedule.name) ignored: monitoring was not started by the scheduler")
            return
        }
        // Only monitoring stops. The Zoom meeting itself is never ended.
        schedulerLog.write("End time reached for \(schedule.name); stopping Auto Admit only")
        DispatchQueue.main.async { [stopAutoAdmit, stopAttendance] in
            stopAutoAdmit()
            // The register is closed at the configured end time, which is the
            // point at which "not seen yet" honestly becomes "absent".
            stopAttendance(true)
        }
        report(schedule, kind: .zoomEnded, severity: .info, title: "Class ended", message: "End time reached; Auto Admit stopped and the register closed", notify: false)
        notify(title: "Auto Admit stopped", body: "End time reached for \(schedule.name)")
    }

    // MARK: Web Client meetings

    private func runWebMeeting(schedule: ZoomSchedule, profile: ZoomAccountProfile, link: URL, occurrence: Date, automation: AutomationCoordinator) {
        schedulerLog.write("──────── Schedule triggered (Web): \(schedule.name) ────────")
        let group = configuration.group(for: schedule)
        switch automation.startWebMeeting(schedule: schedule, profile: profile, group: group, link: link) {
        case .success(let detail):
            schedulerLog.write("[WEB] \(schedule.name): \(detail)")
            if schedule.endTime != nil {
                scheduler.registerMonitoring(for: schedule, startedAt: Date())
            }
            automation.meetingWentLive(schedule: schedule, profile: profile, group: group, startedAt: Date(), engine: .web)
            DispatchQueue.main.async { [state] in
                state.setRunOutcome(.succeeded(meetingName: schedule.meeting.name, autoAdmitActive: schedule.enablesAutoAdmit))
            }
            report(schedule, kind: .zoomStarted, severity: .success, title: "Zoom started in Zoom Web", message: detail, notify: false)
            notify(title: "\(schedule.meeting.name) started in Zoom Web", body: detail)
        case .failure(let error):
            schedulerLog.write("[WEB] FAILED for \(schedule.name): \(error.localizedDescription)")
            DispatchQueue.main.async { [state] in
                state.setRunOutcome(.failed(title: "Web meeting not started", detail: error.localizedDescription))
            }
            report(schedule, kind: .zoomFailed, severity: .failure, title: "Web meeting not started ❌", message: "Reason:\n\(error.localizedDescription)", notify: true)
            notify(title: "Web meeting not started", body: error.localizedDescription)
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 6) { [state] in state.clearTransientRunState() }
    }

    private func desktopHasActiveMeeting() -> Bool {
        guard let process = automation.zoomProcess() else { return false }
        return automation.meetingPresence(for: process).state == .active
    }

    // MARK: Notifications

    private func requestNotificationAuthorization() {
        // Unavailable when the app runs from an unsigned or non-bundled copy;
        // the scheduler must keep working regardless.
        guard Bundle.main.bundleIdentifier != nil else { return }
        UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]) { [logger] granted, error in
            if let error {
                logger.notice("Notification authorization unavailable: \(error.localizedDescription, privacy: .public)")
            } else {
                logger.info("Notification authorization granted=\(granted)")
            }
        }
    }

    /// Into the event log when there is one; the notification is then its decision.
    private func report(_ schedule: ZoomSchedule, kind: OperationsEventKind, severity: OperationsSeverity, title: String, message: String, notify: Bool) {
        guard let operations else { return }
        let group = configuration.group(for: schedule)
        operations.record(
            OperationsEvent(kind: kind, severity: severity, groupCode: group?.dashboardGroupName, sessionDate: LmsFollowUpQueue.dashboardDateAndTime(AutomationCoordinator.scheduledStart(of: schedule, around: Date())).0,
                            scheduleID: schedule.id, title: title, message: message),
            notify: notify
        )
    }

    /// The plain notifications, used only without the operations layer (tests, older wiring).
    private func notify(title: String, body: String) {
        guard operations == nil, Bundle.main.bundleIdentifier != nil else { return }
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        let request = UNNotificationRequest(
            identifier: UUID().uuidString,
            content: content,
            trigger: nil
        )
        UNUserNotificationCenter.current().add(request)
    }
}
