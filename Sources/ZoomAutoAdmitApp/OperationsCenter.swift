import AppKit
import Foundation
import UserNotifications
import ZoomAutoAdmitCore

/// The operations layer: the event log, the notifications drawn from it, and the health checks.
///
/// It owns no class state of its own. Cards are derived from the scheduler, the attendance
/// registers, the LMS queue and the recording sync records each time they are drawn; the event
/// log only adds the outcomes those stores do not keep.
final class OperationsCenter {
    let events: OperationsEventStore
    private let throttle = NotificationThrottle()
    private let healthQueue = DispatchQueue(label: "com.mohamedhosam.ZoomAutoAdmit.health", qos: .utility)
    private var networkTimer: DispatchSourceTimer?
    private let lock = NSLock()

    private var reports: [String: HealthReport] = [:]
    private var networkProbes: [String: (lms: HealthProbeResults.LmsProbe?, sheet: HealthProbeResults.SheetProbe?)] = [:]
    private var checking: Set<String> = []

    // Supplied by the app delegate.
    var configurationProvider: () -> SchedulerConfiguration = { SchedulerConfiguration() }
    var liveAttendanceSession: () -> AttendanceSession? = { nil }
    var workflowScheduleID: () -> UUID? = { nil }
    weak var automation: AutomationCoordinator?
    var onChange: (() -> Void)?

    private let attendanceStore: AttendanceStore
    private let zoom: ZoomAutomating

    static let networkCheckedKey = "operations.networkChecked"
    static let googleConnectedAtKey = "google.connectedAt"
    static let googleTestingModeKey = "google.testingMode"

    init(events: OperationsEventStore = OperationsEventStore(), attendanceStore: AttendanceStore = AttendanceStore(), zoom: ZoomAutomating = LiveZoomAutomation()) {
        self.events = events
        self.attendanceStore = attendanceStore
        self.zoom = zoom
    }

    func start() {
        healthQueue.async { [events] in events.prune() }
        let timer = DispatchSource.makeTimerSource(queue: healthQueue)
        timer.schedule(deadline: .now() + 60, repeating: 60, leeway: .seconds(10))
        timer.setEventHandler { [weak self] in self?.runDueNetworkChecks() }
        networkTimer = timer
        timer.resume()
    }

    func stop() {
        networkTimer?.cancel()
    }

    // MARK: - Events and notifications

    /// Writes the event and, when it is worth it, shows a notification for it.
    func record(_ event: OperationsEvent, notify: Bool, key: String? = nil) {
        var event = event
        if event.severity == .success, let sessionKey = event.sessionKey {
            throttle.clear(prefix: sessionKey)
        }
        if notify {
            let notice = OperationsNotice(
                key: key ?? "\(event.sessionKey ?? event.title)|\(event.kind.rawValue)",
                title: event.groupCode ?? event.title,
                body: event.groupCode == nil ? event.message : "\(event.title)\n\n\(event.message)",
                severity: event.severity,
                sessionKey: event.sessionKey,
                scheduleID: event.scheduleID
            )
            let decision = throttle.decide(notice)
            event.notified = decision.post
            if decision.post { post(decision, notice: notice) }
        }
        events.append(event)
        DispatchQueue.main.async { [weak self] in self?.onChange?() }
    }

    private func post(_ decision: NoticeDecision, notice: OperationsNotice) {
        guard Bundle.main.bundleIdentifier != nil else { return }
        let content = UNMutableNotificationContent()
        content.title = decision.title
        content.body = decision.body
        content.threadIdentifier = notice.sessionKey ?? "operations"
        var info: [String: String] = [:]
        if let sessionKey = notice.sessionKey { info["sessionKey"] = sessionKey }
        if let scheduleID = notice.scheduleID { info["scheduleID"] = scheduleID.uuidString }
        content.userInfo = info
        if notice.severity == .failure { content.sound = .default }
        UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: decision.identifier, content: content, trigger: nil))
    }

    /// Notification history: events that were meant for a notification, newest first.
    func history(limit: Int = 300) -> [OperationsEvent] {
        Array(events.load(since: Date().addingTimeInterval(-OperationsEventStore.retention)).filter { $0.notified != nil }.reversed().prefix(limit))
    }

    // MARK: - Cards

    func inputs() -> OperationsInputs {
        var sessions = attendanceStore.loadAll()
        if let live = liveAttendanceSession() {
            sessions.removeAll { $0.id == live.id }
            sessions.append(live)
        }
        let sync = automation?.recordingSync
        return OperationsInputs(
            configuration: configurationProvider(),
            attendanceSessions: sessions,
            followUps: automation?.followUps.read() ?? [],
            recordings: sync?.records ?? [],
            events: events.load(since: Calendar.current.startOfDay(for: Date()).addingTimeInterval(-3 * 24 * 60 * 60)),
            lmsSettings: LmsSettings.load(),
            workflowScheduleID: workflowScheduleID(),
            webMeetingScheduleIDs: automation?.webMeetingScheduleIDs ?? [],
            recordingSyncEnabled: RecordingSyncSettings.load().enabled,
            nextRecordingSync: sync?.nextRun
        )
    }

    func overviews() -> [SessionOverview] {
        SessionOverviewBuilder.build(inputs())
    }

    func report(for overview: SessionOverview) -> HealthReport? {
        lock.withLock { reports[overview.id] }
    }

    func isChecking(_ overview: SessionOverview) -> Bool {
        lock.withLock { checking.contains(overview.id) }
    }

    // MARK: - Health checks

    private static func occurrenceID(_ schedule: ZoomSchedule, _ startsAt: Date) -> String {
        "\(schedule.id.uuidString)|\(Int(startsAt.timeIntervalSince1970))"
    }

    /// Runs a health check for one class. `includeNetwork` signs in to the LMS and reads the sheet.
    func runHealthCheck(schedule: ZoomSchedule, startsAt: Date, includeNetwork: Bool, reason: String, completion: ((HealthReport) -> Void)? = nil) {
        let id = Self.occurrenceID(schedule, startsAt)
        lock.withLock { _ = checking.insert(id) }
        DispatchQueue.main.async { [weak self] in self?.onChange?() }
        healthQueue.async { [weak self] in
            guard let self else { return }
            let configuration = self.configurationProvider()
            let preflight = PreflightChecker.check(
                schedule: schedule,
                profile: configuration.profile(for: schedule),
                group: configuration.group(for: schedule),
                startsAt: startsAt,
                automation: self.zoom
            )
            let report = self.buildReport(schedule: schedule, startsAt: startsAt, preflight: preflight, includeNetwork: includeNetwork)
            self.finish(report, reason: reason)
            DispatchQueue.main.async { completion?(report) }
        }
    }

    /// The scheduler's own check five minutes before the start, extended with everything else.
    /// Reuses the LMS and Google results of the 30-minute check instead of signing in again.
    func preflightCompleted(schedule: ZoomSchedule, startsAt: Date, preflight: PreflightReport) {
        healthQueue.async { [weak self] in
            guard let self else { return }
            let report = self.buildReport(schedule: schedule, startsAt: startsAt, preflight: preflight, includeNetwork: false)
            self.finish(report, reason: "5 min before start")
        }
    }

    private func buildReport(schedule: ZoomSchedule, startsAt: Date, preflight: PreflightReport, includeNetwork: Bool) -> HealthReport {
        let id = Self.occurrenceID(schedule, startsAt)
        let configuration = configurationProvider()
        let group = configuration.group(for: schedule)
        let lmsSettings = LmsSettings.load()
        let syncSettings = RecordingSyncSettings.load()
        let (date, _) = LmsFollowUpQueue.dashboardDateAndTime(startsAt)
        let sync = automation?.recordingSync

        var lmsProbe: HealthProbeResults.LmsProbe?
        var sheetProbe: HealthProbeResults.SheetProbe?
        if includeNetwork {
            if lmsSettings.isAnythingEnabled, let group, automation?.hasLmsSignIn == true {
                lmsProbe = automation?.checkLmsSession(group: group.dashboardGroupName, date: date)
            }
            if syncSettings.enabled, let sync, sync.isGoogleConnected, !syncSettings.spreadsheetID.isEmpty {
                sheetProbe = sync.probeSheet()
            }
            lock.withLock { networkProbes[id] = (lmsProbe, sheetProbe) }
        } else if let cached = lock.withLock({ networkProbes[id] }) {
            (lmsProbe, sheetProbe) = cached
        }

        let probes = HealthProbeResults(
            zoomInstalled: NSWorkspace.shared.urlForApplication(withBundleIdentifier: "us.zoom.xos") != nil,
            preflight: preflight,
            lmsCredentialsSaved: automation?.hasLmsSignIn ?? false,
            lms: lmsProbe,
            recordingSyncEnabled: syncSettings.enabled,
            googleConnected: sync?.isGoogleConnected ?? false,
            spreadsheetConfigured: !syncSettings.spreadsheetID.isEmpty,
            sheet: sheetProbe,
            googleConnectedAt: UserDefaults.standard.object(forKey: Self.googleConnectedAtKey) as? Date,
            googleTestingMode: UserDefaults.standard.bool(forKey: Self.googleTestingModeKey),
            helperProblem: Self.helperProblem(),
            freeDiskBytes: Self.freeDiskBytes()
        )
        var report = HealthChecker.report(schedule: schedule, startsAt: startsAt, configuration: configuration, lmsSettings: lmsSettings, probes: probes)
        report.includesNetwork = lmsProbe != nil || sheetProbe != nil
        return report
    }

    private func finish(_ report: HealthReport, reason: String) {
        let id = "\(report.scheduleID.uuidString)|\(Int(report.startsAt.timeIntervalSince1970))"
        lock.withLock {
            reports[id] = report
            checking.remove(id)
        }
        let severity: OperationsSeverity = !report.isReady ? .failure : (report.warnings.isEmpty ? .success : .warning)
        let summary = report.failures.first.map { "\($0.name): \($0.detail ?? "failed")\($0.fix.map { "\n\nFix: \($0)" } ?? "")" }
            ?? report.warnings.first.map { "\($0.name): \($0.detail ?? "warning")\(report.warnings.count > 1 ? " (+\(report.warnings.count - 1) more)" : "")" }
            ?? "Ready to start session"
        SchedulerLog.shared.write("[HEALTH] \(report.headline) (\(reason)): " + report.items.filter { $0.state != .pass }.map { "\($0.state.symbol) \($0.name)" }.joined(separator: ", "))
        record(
            OperationsEvent(
                kind: .healthCheck,
                severity: severity,
                groupCode: report.groupCode,
                sessionDate: report.sessionDate,
                scheduleID: report.scheduleID,
                title: report.isReady ? (report.warnings.isEmpty ? "Pre-flight passed ✓" : "Pre-flight: ready with warnings ⚠️") : "Session will not work as planned ❌",
                message: summary
            ),
            // Quiet when everything is fine: a notification before every class trains people to ignore them.
            notify: severity != .success,
            key: "\(report.groupCode ?? report.scheduleName)|\(report.sessionDate)|health"
        )
    }

    /// Every minute: the LMS + Google check for classes starting within the next 30 minutes, once each.
    private func runDueNetworkChecks(now: Date = Date()) {
        let configuration = configurationProvider()
        var checked = Set(UserDefaults.standard.stringArray(forKey: Self.networkCheckedKey) ?? [])
        var changed = false
        for schedule in configuration.schedules where schedule.isEnabled {
            let starts = ScheduleTimeline.occurrences(of: schedule, after: now, through: now.addingTimeInterval(OperationsTiming.networkCheckLead))
            for start in starts {
                let id = Self.occurrenceID(schedule, start)
                guard !checked.contains(id) else { continue }
                checked.insert(id)
                changed = true
                runHealthCheck(schedule: schedule, startsAt: start, includeNetwork: true, reason: "30 min before start")
            }
        }
        if changed {
            // Keep the list short: only the last few days of occurrences matter.
            let cutoff = now.addingTimeInterval(-3 * 24 * 60 * 60).timeIntervalSince1970
            let kept = checked.filter { ($0.split(separator: "|").last.flatMap { Double($0) } ?? 0) >= cutoff }
            UserDefaults.standard.set(Array(kept), forKey: Self.networkCheckedKey)
        }
    }

    // MARK: - Recording warning

    /// After a sheet sync: classes that ran yesterday and still have no recording on the LMS.
    /// Warned once per class.
    func recordingSyncFinished(now: Date = Date(), calendar: Calendar = .current) {
        healthQueue.async { [weak self] in
            guard let self else { return }
            let inputs = self.inputs()
            guard inputs.recordingSyncEnabled, let yesterday = calendar.date(byAdding: .day, value: -1, to: now) else { return }
            let (date, _) = LmsFollowUpQueue.dashboardDateAndTime(yesterday, calendar: calendar)
            let warned = Set(self.events.load(since: now.addingTimeInterval(-10 * 24 * 60 * 60)).filter { $0.kind == .recordingWaiting }.compactMap(\.sessionKey))
            for group in inputs.configuration.studentGroups {
                let key = OperationsSessionKey.make(groupCode: group.dashboardGroupName, sessionDate: date)
                guard !warned.contains(key) else { continue }
                let ran = inputs.attendanceSessions.contains { $0.groupID == group.id && LmsFollowUpQueue.dashboardDateAndTime($0.startedAt, calendar: calendar).0 == date }
                guard ran else { continue }
                let record = inputs.recordings.first { $0.id == key }
                guard record?.state != .attached else { continue }
                let reason: String
                switch record?.state {
                case .none: reason = "No Drive link for this date in the sheet yet."
                case .some(let state): reason = "\(state.rawValue.capitalized): \(record?.error ?? "not attached yet")"
                }
                self.record(
                    OperationsEvent(kind: .recordingWaiting, severity: .warning, groupCode: group.dashboardGroupName, sessionDate: date,
                                    title: "Recording waiting ⚠️", message: "Drive link not attached after the morning sync.\n\(reason)"),
                    notify: true
                )
            }
        }
    }

    // MARK: - System probes

    static func helperProblem() -> String? {
        if case .failure(let error) = AutomationEnvironment.locate() { return error.localizedDescription }
        return nil
    }

    static func freeDiskBytes() -> Int64? {
        let home = URL(fileURLWithPath: NSHomeDirectory())
        return (try? home.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey]))?.volumeAvailableCapacityForImportantUsage
    }
}
