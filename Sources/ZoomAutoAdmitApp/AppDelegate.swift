import AppKit
import OSLog
import ServiceManagement
import UserNotifications
import ZoomAutoAdmitCore

final class AppDelegate: NSObject, NSApplicationDelegate, UNUserNotificationCenterDelegate {
    private let logger = Logger(subsystem: "com.mohamedhosam.ZoomAutoAdmit", category: "app")
    private let defaults = UserDefaults.standard
    private var state: AppState!
    private var monitor: AutoAdmitMonitor!
    private var menuBarController: MenuBarController!
    private var schedulerCoordinator: SchedulerCoordinator!
    private var schedulerWindowController: SchedulerWindowController?
    private var settingsWindowController: SettingsWindowController?
    private var attendanceWindowController: AttendanceWindowController?
    private var automationWindowController: AutomationWindowController?
    private let automationCoordinator = AutomationCoordinator()
    private let operationsCenter = OperationsCenter()
    private var dashboardWindowController: DashboardWindowController?
    private var dashboardRefreshPending = false
    private let ignoreStore = AttendanceIgnoreStore()
    private var unknownParticipantsWindows: [UUID: UnknownParticipantsWindowController] = [:]
    private var automationRefreshPending = false
    private lazy var attendanceCoordinator = AttendanceCoordinator(
        configurationProvider: { [weak self] in
            self?.schedulerCoordinator.currentConfiguration ?? SchedulerConfiguration()
        },
        configurationWriter: { [weak self] configuration in
            self?.schedulerCoordinator.update(configuration: configuration)
        }
    )
    private var accessibilityCheckGeneration: UInt64 = 0

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
        // Every attendance path reads the global ignore list through this one provider.
        let ignoreStore = self.ignoreStore
        AttendanceIgnoreRules.currentProvider = { ignoreStore.rules }
        // Never displayed for an accessory app, but required for ⌘C/⌘V to reach
        // text fields in the app's own windows.
        MainMenu.install()

        let enabled: Bool
        if defaults.object(forKey: "monitoringEnabled") == nil {
            enabled = true
            defaults.set(true, forKey: "monitoringEnabled")
        } else {
            enabled = defaults.bool(forKey: "monitoringEnabled")
        }

        state = AppState(monitoringEnabled: enabled)
        // Hybrid monitoring: Accessibility notifications from Zoom shorten the
        // wait, the polling loop guarantees correctness on its own.
        monitor = AutoAdmitMonitor(activitySource: ZoomAXObserverActivitySource()) { [weak self] event in
            DispatchQueue.main.async {
                guard let self, self.state.monitoringEnabled else { return }
                self.state.apply(event)
                // An admission is worth a snapshot, but a burst of them is worth
                // only one; the coordinator coalesces them.
                if case .admitted = event {
                    self.attendanceCoordinator.noteAdmit()
                }
            }
        }
        menuBarController = MenuBarController(state: state)
        state.onChange = { [weak self] in self?.menuBarController.refresh() }

        // The scheduler drives the same monitor the menu commands drive, so a
        // scheduled start can never create a second Waiting Room loop.
        attendanceCoordinator.onChange = { [weak self] in self?.menuBarController.refresh() }
        attendanceCoordinator.onUnknownParticipants = { [weak self] session, unknown in
            self?.presentUnknownParticipants(session: session, unknown: unknown)
        }
        ignoreStore.onChange = { [weak self] in
            self?.attendanceCoordinator.ignoreListChanged()
        }
        automationCoordinator.configurationProvider = { [weak self] in
            self?.schedulerCoordinator?.currentConfiguration ?? SchedulerConfiguration()
        }
        automationCoordinator.liveAttendanceSession = { [weak self] in
            self?.attendanceCoordinator.currentSession
        }
        automationCoordinator.onWebAdmitted = { [weak self] in
            // A Web meeting's admission counts like a desktop one, and is worth a snapshot too.
            self?.state.apply(.admitted(participantName: nil, admitAll: false, at: Date()))
        }
        automationCoordinator.onChange = { [weak self] in self?.scheduleAutomationWindowRefresh() }
        schedulerCoordinator = SchedulerCoordinator(
            state: state,
            startAutoAdmit: { [weak self] in self?.setMonitoring(true) },
            stopAutoAdmit: { [weak self] in self?.setMonitoring(false) },
            startAttendance: { [weak self] group, schedule in
                self?.attendanceCoordinator.start(group: group, schedule: schedule)
            },
            stopAttendance: { [weak self] finalize in
                guard let self else { return }
                if finalize {
                    _ = self.attendanceCoordinator.finalize()
                } else {
                    self.attendanceCoordinator.stop()
                }
            },
            automationCoordinator: automationCoordinator
        )
        SchedulerLog.shared.write("[attendance] lifecycle-wired=true")
        operationsCenter.configurationProvider = { [weak self] in
            self?.schedulerCoordinator?.currentConfiguration ?? SchedulerConfiguration()
        }
        operationsCenter.liveAttendanceSession = { [weak self] in self?.attendanceCoordinator.currentSession }
        operationsCenter.workflowScheduleID = { [weak self] in self?.schedulerCoordinator?.activeWorkflowScheduleID }
        operationsCenter.automation = automationCoordinator
        operationsCenter.onChange = { [weak self] in self?.scheduleDashboardRefresh() }
        automationCoordinator.operations = operationsCenter
        schedulerCoordinator.operations = operationsCenter
        if Bundle.main.bundleIdentifier != nil {
            UNUserNotificationCenter.current().delegate = self
        }
        // A relaunch mid-class must not abandon the register: the session
        // is on disk, only the timer needs restarting.
        attendanceCoordinator.resumeOpenSession(
            configuration: schedulerCoordinator.currentConfiguration
        )
        attendanceCoordinator.finalizeAbandonedSessions(
            configuration: schedulerCoordinator.currentConfiguration
        )
        menuBarController.schedulerConfiguration = { [weak self] in
            self?.schedulerCoordinator.currentConfiguration ?? SchedulerConfiguration()
        }
        configureMenuActions()
        schedulerCoordinator.start()
        automationCoordinator.start()
        operationsCenter.start()

        if enabled {
            checkAccessibilityFromScratch(source: "launch")
        } else {
            state.setMonitoringEnabled(false)
        }
        if CommandLine.arguments.contains("--run-first-schedule") {
            // Testing aid: runs the first configured schedule immediately.
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { [weak self] in
                guard let self,
                      let schedule = self.schedulerCoordinator.currentConfiguration.schedules.first else {
                    return
                }
                self.schedulerCoordinator.runNow(schedule)
            }
        }
        if CommandLine.arguments.contains(ParticipantsCapture.launchArgument) {
            DispatchQueue.global(qos: .userInitiated).async { ParticipantsCapture.run() }
        }
        if CommandLine.arguments.contains(ParticipantsCheck.launchArgument) {
            DispatchQueue.global(qos: .userInitiated).async { ParticipantsCheck.run() }
        }
        if CommandLine.arguments.contains(PreJoinCapture.launchArgument) {
            let opensPreview = !CommandLine.arguments.contains("--no-open")
            DispatchQueue.global(qos: .userInitiated).async {
                PreJoinCapture.run(openPreview: opensPreview)
            }
        }
        if CommandLine.arguments.contains("--open-dashboard") {
            DispatchQueue.main.async { [weak self] in self?.openDashboard(focus: nil) }
        }
        if CommandLine.arguments.contains("--open-schedules") {
            // Convenience for opening the editor without going through the menu.
            DispatchQueue.main.async { [weak self] in self?.openSchedulerWindow() }
        }
        if CommandLine.arguments.contains(ZoomUISnapshot.launchArgument) {
            // Read-only diagnostic capture requested at launch. Runs off the main
            // thread and does not touch monitoring.
            DispatchQueue.global(qos: .utility).async {
                ZoomUISnapshot.capture(reason: "launch argument")
            }
        }
        logger.info("Menu bar application launched")
    }

    func applicationWillTerminate(_ notification: Notification) {
        operationsCenter.stop()
        automationCoordinator.stop()
        schedulerCoordinator?.stop()
        monitor?.stop()
        logger.info("Application terminating; monitor stopped")
    }

    private func configureMenuActions() {
        menuBarController.onSetMonitoring = { [weak self] enabled in
            self?.setMonitoring(enabled)
        }
        menuBarController.onOpenAccessibilitySettings = { [weak self] in
            self?.openAccessibilitySettings()
        }
        menuBarController.onCheckAgain = { [weak self] in
            self?.checkAccessibilityFromScratch(source: "Check Again")
        }
        menuBarController.onOpenSchedules = { [weak self] in
            self?.openSchedulerWindow()
        }
        menuBarController.onOpenSettings = { [weak self] in
            self?.openSettingsWindow()
        }
        menuBarController.onOpenAttendance = { [weak self] in
            self?.openAttendanceWindow()
        }
        menuBarController.onOpenDashboard = { [weak self] in
            self?.openDashboard(focus: nil)
        }
        menuBarController.onOpenAutomation = { [weak self] in
            self?.openAutomationWindow()
        }
        menuBarController.onFinalizeAttendance = { [weak self] in
            _ = self?.attendanceCoordinator.finalize()
            self?.menuBarController.refresh()
        }
        menuBarController.attendanceSummary = { [weak self] in
            self?.attendanceCoordinator.liveSummary?.lines
        }
        menuBarController.onSnapshotNow = { [weak self] in
            self?.attendanceCoordinator.snapshotNow()
        }
        menuBarController.onRunSchedule = { [weak self] schedule in
            self?.schedulerCoordinator.runNow(schedule)
        }
        menuBarController.onShowRunDetails = { [weak self] in
            self?.showRunDetails()
        }
        menuBarController.schedulerConfiguration = { [weak self] in
            self?.schedulerCoordinator.currentConfiguration ?? SchedulerConfiguration()
        }
        menuBarController.nextScheduled = { [weak self] in
            self?.schedulerCoordinator.nextScheduled()
        }
        menuBarController.isWorkflowRunning = { [weak self] in
            self?.schedulerCoordinator.isWorkflowActive ?? false
        }
        menuBarController.onQuit = { [weak self] in
            self?.automationCoordinator.stop()
            self?.schedulerCoordinator.stop()
            self?.monitor.stop()
            NSApp.terminate(nil)
        }
    }

    private func presentUnknownParticipants(session: AttendanceSession, unknown: [UnknownParticipant]) {
        guard unknownParticipantsWindows[session.id] == nil else { return }
        let controller = UnknownParticipantsWindowController(session: session, unknown: unknown)
        controller.onApply = { [weak self] permanent, thisMeeting, students in
            guard let self else { return }
            if !permanent.isEmpty { self.ignoreStore.add(permanent, source: .detected) }
            if !thisMeeting.isEmpty { self.attendanceCoordinator.ignoreForThisMeeting(thisMeeting, sessionID: session.id) }
            if !students.isEmpty { self.attendanceCoordinator.addStudents(students, sessionID: session.id) }
            SchedulerLog.shared.write("[attendance] unknown-participants decided permanent=\(permanent.count) meeting=\(thisMeeting.count) students=\(students.count)")
            self.unknownParticipantsWindows[session.id] = nil
        }
        unknownParticipantsWindows[session.id] = controller
        controller.present()
    }

    private func openAutomationWindow() {
        if automationWindowController == nil {
            automationWindowController = AutomationWindowController(
                coordinator: automationCoordinator,
                configurationProvider: { [weak self] in
                    self?.schedulerCoordinator.currentConfiguration ?? SchedulerConfiguration()
                }
            )
        }
        automationWindowController?.present()
    }

    private func openDashboard(focus sessionKey: String?) {
        if dashboardWindowController == nil {
            let controller = DashboardWindowController(
                center: operationsCenter,
                configurationProvider: { [weak self] in
                    self?.schedulerCoordinator.currentConfiguration ?? SchedulerConfiguration()
                }
            )
            controller.onOpenAutomation = { [weak self] in self?.openAutomationWindow() }
            controller.onOpenAttendance = { [weak self] in self?.openAttendanceWindow() }
            controller.onRunFollowUp = { [weak self] id in self?.automationCoordinator.runFollowUpNow(id: id) }
            dashboardWindowController = controller
        }
        dashboardWindowController?.present(focus: sessionKey)
    }

    /// Events arrive in bursts; the dashboard redraws at most once every two seconds.
    private func scheduleDashboardRefresh() {
        guard dashboardWindowController?.isVisible == true, !dashboardRefreshPending else { return }
        dashboardRefreshPending = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 2) { [weak self] in
            self?.dashboardRefreshPending = false
            self?.dashboardWindowController?.refresh()
        }
    }

    // MARK: Notifications

    /// Shown even while the app is frontmost: the operator may be looking at the dashboard.
    func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification, withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .list, .sound])
    }

    /// Clicking a notification opens its class on the dashboard.
    func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse, withCompletionHandler completionHandler: @escaping () -> Void) {
        let sessionKey = response.notification.request.content.userInfo["sessionKey"] as? String
        DispatchQueue.main.async { [weak self] in self?.openDashboard(focus: sessionKey) }
        completionHandler()
    }

    /// Log lines arrive in bursts; the window redraws at most twice a second.
    private func scheduleAutomationWindowRefresh() {
        scheduleDashboardRefresh()
        guard automationWindowController?.isVisible == true, !automationRefreshPending else { return }
        automationRefreshPending = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { [weak self] in
            self?.automationRefreshPending = false
            self?.automationWindowController?.refresh()
        }
    }

    private func openAttendanceWindow() {
        if attendanceWindowController == nil {
            attendanceWindowController = AttendanceWindowController(
                store: AttendanceStore(),
                configurationProvider: { [weak self] in
                    self?.schedulerCoordinator.currentConfiguration ?? SchedulerConfiguration()
                },
                configurationWriter: { [weak self] configuration in
                    self?.schedulerCoordinator.update(configuration: configuration)
                },
                liveSessionProvider: { [weak self] in
                    self?.attendanceCoordinator.currentSession
                },
                finalizeHandler: { [weak self] in
                    self?.attendanceCoordinator.finalize()
                }
            )
        }
        attendanceWindowController?.present()
    }

    private func openSettingsWindow() {
        if settingsWindowController == nil {
            let controller = SettingsWindowController()
            controller.ignoreStore = ignoreStore
            controller.onOpenAccessibilitySettings = { [weak self] in self?.openAccessibilitySettings() }
            controller.onCheckAccessibility = { [weak self] in
                self?.checkAccessibilityFromScratch(source: "Check Again")
            }
            controller.onCaptureZoomUI = { [weak self] in self?.captureZoomUISnapshot() }
            controller.onOpenAccessibilityLog = {
                NSWorkspace.shared.open(AccessibilityDiagnosticLog.shared.fileURL)
            }
            controller.onOpenSchedulerLog = { [weak self] in
                guard let self else { return }
                NSWorkspace.shared.open(self.schedulerCoordinator.logFileURL)
            }
            settingsWindowController = controller
        }
        settingsWindowController?.present()
    }

    /// Full failure text plus the log, for when the menu summary isn't enough.
    private func showRunDetails() {
        guard case .failed(let title, let detail) = state.runOutcome else { return }
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = title
        alert.informativeText = detail
        alert.addButton(withTitle: "Close")
        alert.addButton(withTitle: "Open Log")
        if alert.runModal() == .alertSecondButtonReturn {
            NSWorkspace.shared.open(schedulerCoordinator.logFileURL)
        }
    }

    private func setMonitoring(_ enabled: Bool) {
        defaults.set(enabled, forKey: "monitoringEnabled")
        state.setMonitoringEnabled(enabled)
        if enabled {
            checkAccessibilityFromScratch(source: "Start Monitoring")
        } else {
            monitor.stop()
        }
    }

    private func checkAccessibilityFromScratch(source: String) {
        accessibilityCheckGeneration &+= 1
        let generation = accessibilityCheckGeneration
        let previousRuntimeCDHash = defaults.string(forKey: "lastRuntimeCDHash")

        DispatchQueue.global(qos: .utility).async { [weak self] in
            let snapshot = AccessibilityDiagnosticsSnapshot.capture(
                previousRuntimeCDHash: previousRuntimeCDHash,
                userInitiatedRecheck: source == "Check Again"
            )
            AccessibilityDiagnosticLog.shared.write(snapshot.logLines(source: source))

            DispatchQueue.main.async {
                guard let self, generation == self.accessibilityCheckGeneration else { return }
                self.defaults.set(snapshot.runtimeSignature.cdHash, forKey: "lastRuntimeCDHash")
                self.applyAccessibilityDiagnostics(snapshot)
            }
        }
    }

    private func applyAccessibilityDiagnostics(_ snapshot: AccessibilityDiagnosticsSnapshot) {
        switch snapshot.classification {
        case .trusted:
            guard state.monitoringEnabled else {
                state.setMonitoringEnabled(false)
                return
            }
            if monitor.isRunning {
                monitor.checkNow(forceDelivery: true)
            } else {
                monitor.start()
            }
        case .relaunchRequired:
            monitor.stop()
            state.apply(.accessibilityPermissionGrantedRelaunchRequired)
        case .appBundleMismatch:
            monitor.stop()
            state.apply(.accessibilityAppBundleMismatch)
        case .possibleStaleTCCEntry:
            monitor.stop()
            state.apply(.accessibilityPossibleStaleTCCEntry)
        case .notTrusted:
            monitor.stop()
            state.apply(.accessibilityPermissionRequired)
        }
    }

    private func openSchedulerWindow() {
        if schedulerWindowController == nil {
            schedulerWindowController = SchedulerWindowController(
                coordinator: schedulerCoordinator,
                state: state
            )
            schedulerWindowController?.automation = automationCoordinator
        }
        schedulerWindowController?.present()
    }

    private func captureZoomUISnapshot() {
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            let url = ZoomUISnapshot.capture(reason: "menu command")
            DispatchQueue.main.async {
                guard let url else {
                    self?.state.apply(.error("Could not capture the Zoom UI snapshot"))
                    return
                }
                NSWorkspace.shared.open(url)
            }
        }
    }

    private func openAccessibilitySettings() {
        guard let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") else {
            state.apply(.error("Could not create Accessibility Settings URL"))
            return
        }
        NSWorkspace.shared.open(url)
    }

    private func toggleLaunchAtLogin() {
        do {
            if SMAppService.mainApp.status == .enabled {
                try SMAppService.mainApp.unregister()
            } else {
                try SMAppService.mainApp.register()
            }
            menuBarController.refresh()
        } catch {
            logger.error("Launch at Login update failed: \(error.localizedDescription, privacy: .public)")
            state.apply(.error("Launch at Login requires the app to be installed in Applications"))
        }
    }
}
