import AppKit
import Foundation
import OSLog
import UserNotifications
import ZoomAXSupport
import ZoomAutoAdmitCore

/// Everything the app does around a class beyond Zoom itself, ported from the Windows build:
///
/// - the DEPI dashboard: Run Session when a meeting goes live, the attendance upload and the
///   late-joiner correction later, the recording link at the end;
/// - the recording API n8n calls;
/// - meetings held in the Zoom Web Client when an account prefers it or the desktop app is busy;
/// - co-host for a group's instructors once they join.
///
/// The browser work is done by the Node helper. Every call here runs off the main thread, and a
/// failure in any of it never touches Auto Admit or the attendance register.
final class AutomationCoordinator {
    private let logger = Logger(subsystem: "com.mohamedhosam.ZoomAutoAdmit", category: "automation")
    private let schedulerLog: SchedulerLog
    private let helper: AutomationHelper
    private let lms: LmsClient
    private let credentials: LmsCredentialStoring
    let followUps: LmsFollowUpQueue
    private let attendanceStore: AttendanceStore
    private let coHostHistory = CoHostHistoryStore()
    let allocator = ZoomEngineAllocator()
    /// Google Sheet → LMS recording links.
    let recordingSync: RecordingSyncCoordinator

    private let dashboardQueue = DispatchQueue(label: "com.mohamedhosam.ZoomAutoAdmit.lms", qos: .utility)
    private let rolesQueue = DispatchQueue(label: "com.mohamedhosam.ZoomAutoAdmit.roles", qos: .utility)
    private var followUpTimer: DispatchSourceTimer?
    private var coHostTimer: DispatchSourceTimer?
    private var processingFollowUps = false

    /// The live desktop register, so a follow-up during class reads what is known right now.
    var liveAttendanceSession: () -> AttendanceSession? = { nil }
    var configurationProvider: () -> SchedulerConfiguration = { SchedulerConfiguration() }
    /// Web meetings report admissions and attendance through these.
    var onWebAdmitted: (() -> Void)?
    var onChange: (() -> Void)?
    /// The event log and notifications. Nil in tests, where notifications go out directly.
    weak var operations: OperationsCenter? {
        didSet { recordingSync.operations = operations }
    }

    private(set) var recentLog: [String] = []
    private let logLock = NSLock()


    // Web meetings, by schedule
    private struct WebMeeting {
        let process: RunningAutomation
        let schedule: ZoomSchedule
        let recorder: WebAttendanceRecorder?
        let startedAt: Date
    }
    private var webMeetings: [UUID: WebMeeting] = [:]
    private let webLock = NSLock()

    // Co-host: who was already granted in this meeting, so each person is handled once.
    private var coHostGranted: Set<String> = []
    /// Failed tries per person this meeting. Each try opens a menu in the live meeting, so it
    /// stops after a few and asks the host to do it by hand once.
    private var coHostFailures: [String: Int] = [:]
    static let coHostMaximumTries = 3
    private var coHostSessionKey: UUID?

    init(
        schedulerLog: SchedulerLog = .shared,
        helper: AutomationHelper = AutomationHelper(),
        credentials: LmsCredentialStoring = LmsCredentialStore(),
        followUps: LmsFollowUpQueue = LmsFollowUpQueue(),
        attendanceStore: AttendanceStore = AttendanceStore()
    ) {
        self.schedulerLog = schedulerLog
        self.helper = helper
        self.credentials = credentials
        self.lms = LmsClient(helper: helper, credentials: credentials)
        self.followUps = followUps
        self.attendanceStore = attendanceStore
        self.recordingSync = RecordingSyncCoordinator(lms: LmsClient(helper: helper, credentials: credentials), followUps: followUps, attendanceStore: attendanceStore)
        recordingSync.log = { [weak self] in self?.log($0) }
        recordingSync.helperLine = { [weak self] in self?.helperLine($0) }
        recordingSync.notify = { [weak self] in self?.notify(title: $0, body: $1) }
        recordingSync.configurationProvider = { [weak self] in self?.configurationProvider() ?? SchedulerConfiguration() }
        recordingSync.liveAttendanceSession = { [weak self] in self?.liveAttendanceSession() }
        recordingSync.onChange = { [weak self] in self?.onChange?() }
    }

    var settings: LmsSettings { LmsSettings.load() }
    var hasLmsSignIn: Bool { credentials.read() != nil }
    var savedLmsEmail: String? { credentials.read()?.email }

    func start() {
        let timer = DispatchSource.makeTimerSource(queue: dashboardQueue)
        timer.schedule(deadline: .now() + 20, repeating: 60, leeway: .seconds(5))
        timer.setEventHandler { [weak self] in self?.processDueFollowUps() }
        followUpTimer = timer
        timer.resume()

        let roles = DispatchSource.makeTimerSource(queue: rolesQueue)
        roles.schedule(deadline: .now() + 30, repeating: 20, leeway: .seconds(3))
        roles.setEventHandler { [weak self] in self?.coHostPass() }
        coHostTimer = roles
        roles.resume()

        recordingSync.start()

        syncLaunchAgent()
    }

    func stop() {
        followUpTimer?.cancel()
        coHostTimer?.cancel()
        recordingSync.stop()
        webLock.lock()
        let meetings = webMeetings.values
        webLock.unlock()
        meetings.forEach { $0.process.stop(grace: 5) }
    }

    // MARK: - Logging

    func log(_ message: String) {
        schedulerLog.write(message)
        let stamp = DateFormatter.localizedString(from: Date(), dateStyle: .none, timeStyle: .medium)
        logLock.lock()
        recentLog.append("\(stamp)  \(message)")
        if recentLog.count > 400 { recentLog.removeFirst(recentLog.count - 400) }
        logLock.unlock()
        DispatchQueue.main.async { [weak self] in self?.onChange?() }
    }

    var logSnapshot: [String] {
        logLock.lock()
        defer { logLock.unlock() }
        return recentLog
    }

    private func helperLine(_ message: AutomationMessage) {
        switch message {
        case .log(let level, let text):
            log(level == "info" ? text : "[\(level)] \(text)")
        case .event, .result:
            break
        }
    }

    // MARK: - Dashboard: when a meeting goes live

    /// Called once a scheduled meeting is verified live, on either engine.
    func meetingWentLive(schedule: ZoomSchedule, profile: ZoomAccountProfile?, group: StudentGroup?, startedAt: Date, engine: ZoomEngine) {
        let settings = self.settings
        guard settings.isAnythingEnabled else { return }
        guard let group else {
            log("[LMS] \(schedule.name) has no attendance group, so the dashboard steps were skipped.")
            return
        }
        guard hasLmsSignIn else {
            log("[LMS] No dashboard sign-in is saved; the dashboard steps for \(group.dashboardGroupName) were skipped.")
            report(OperationsEvent(kind: .lmsSessionFailed, severity: .failure, groupCode: group.dashboardGroupName, sessionDate: LmsFollowUpQueue.dashboardDateAndTime(startedAt).0, scheduleID: schedule.id,
                                   title: "LMS sign-in missing ❌", message: "No dashboard steps will run for this class.\n\nFix: add the sign-in in Automation → LMS."), notify: true)
            return
        }

        if let problem = LmsGroupMapping.problem(for: group, in: configurationProvider()) {
            log("[LMS] ✗ Dashboard steps for \(schedule.name) refused: \(problem.message)")
            report(OperationsEvent(kind: .lmsSessionFailed, severity: .failure, groupCode: group.dashboardGroupName, sessionDate: LmsFollowUpQueue.dashboardDateAndTime(startedAt).0, scheduleID: schedule.id,
                                   title: "Dashboard steps refused ❌", message: problem.message), notify: true)
            return
        }

        let dashboardGroup = group.dashboardGroupName
        let sessionStart = Self.scheduledStart(of: schedule, around: startedAt)
        let (date, time) = LmsFollowUpQueue.dashboardDateAndTime(sessionStart)

        let steps = settings.followUpSteps
        if !steps.isEmpty {
            let written = followUps.schedule(
                group: dashboardGroup,
                sessionStartedAt: sessionStart,
                steps: steps,
                attendanceGroupID: group.id,
                scheduleID: schedule.id,
                recordingProfile: profile?.resolvedWebProfileName
            )
            for item in written {
                log("[LMS] Due \(DateFormatter.localizedString(from: item.dueAt, dateStyle: .short, timeStyle: .short)): \(item.describe)")
            }
        }

        guard settings.runSessionOnMeetingStart else { return }
        dashboardQueue.async { [weak self] in
            guard let self else { return }
            self.log("[LMS] Meeting live for \(dashboardGroup) (\(engine.rawValue)); starting its dashboard session.")
            let result = self.lms.runSession(group: dashboardGroup, date: date, startTime: time, settings: settings, onMessage: self.helperLine)
            self.log("[LMS] \(result.success ? "✓" : "✗") \(result.message)")
            if result.success {
                self.report(OperationsEvent(kind: .lmsSessionStarted, severity: .success, groupCode: dashboardGroup, sessionDate: date, scheduleID: schedule.id,
                                            title: "LMS session started\(settings.dryRun ? " (rehearse)" : "")", message: result.message), notify: false)
            } else {
                self.report(OperationsEvent(kind: .lmsSessionFailed, severity: .failure, groupCode: dashboardGroup, sessionDate: date, scheduleID: schedule.id,
                                            title: "Run Session did not complete ❌", message: "Reason:\n\(result.message)"), notify: true)
            }
        }
    }

    /// The register closed at the class's end time: one last late-joiner correction, so the LMS
    /// ends with the final attendance. When the timed correction is still queued it is that
    /// step (nothing is added); when it already ran, the same step is queued again, due now.
    func registerClosed(schedule: ZoomSchedule, at now: Date = Date()) {
        let settings = self.settings
        let configuration = configurationProvider()
        guard hasLmsSignIn, let group = configuration.group(for: schedule) else { return }
        let start = Self.scheduledStart(of: schedule, around: now)
        var queuedAnything = false
        if settings.correctAttendance {
            let written = followUps.schedule(
                group: group.dashboardGroupName,
                sessionStartedAt: start,
                steps: [.correctAttendance],
                attendanceGroupID: group.id,
                scheduleID: schedule.id
            )
            if written.isEmpty {
                log("[LMS] Register closed for \(group.dashboardGroupName); its late-joiner correction is still queued and will send the final attendance.")
            } else {
                log("[LMS] Register closed for \(group.dashboardGroupName); final attendance correction queued, due now.")
                queuedAnything = true
            }
        }
        // Then End on the dashboard and the Zoom recording, in that order (perform keeps the order).
        let endSteps = settings.endOfClassSteps
        if !endSteps.isEmpty {
            let written = followUps.schedule(
                group: group.dashboardGroupName,
                sessionStartedAt: start,
                steps: endSteps,
                attendanceGroupID: group.id,
                scheduleID: schedule.id,
                recordingProfile: configuration.profile(for: schedule)?.resolvedWebProfileName,
                dueAt: now
            )
            for item in written { log("[LMS] Queued at the end of class: \(item.describe)") }
            queuedAnything = queuedAnything || !written.isEmpty
        }
        if queuedAnything { processDueFollowUps() }
    }

    /// The occurrence's own start time, not the moment the workflow finished; the dashboard
    /// lists sessions by the scheduled time.
    static func scheduledStart(of schedule: ZoomSchedule, around moment: Date, calendar: Calendar = .current) -> Date {
        var parts = calendar.dateComponents([.year, .month, .day], from: moment)
        parts.hour = schedule.startTime.hour
        parts.minute = schedule.startTime.minute
        guard let candidate = calendar.date(from: parts) else { return moment }
        // A meeting started a little after midnight for a late class belongs to the previous day.
        if candidate.timeIntervalSince(moment) > 6 * 60 * 60 {
            return calendar.date(byAdding: .day, value: -1, to: candidate) ?? candidate
        }
        return candidate
    }

    // MARK: - Dashboard: follow-ups

    func processDueFollowUps(force: Bool = false) {
        dashboardQueue.async { [weak self] in
            guard let self, !self.processingFollowUps else { return }
            self.processingFollowUps = true
            defer { self.processingFollowUps = false }
            let now = Date()
            self.followUps.prune(at: now)
            let due = force ? self.followUps.read().filter { $0.attempts < LmsFollowUpQueue.maximumAttempts } : self.followUps.due(at: now)
            for item in due { self.perform(item, now: now) }
            DispatchQueue.main.async { self.onChange?() }
        }
    }

    /// Runs one outstanding step now, whatever its due time.
    func runFollowUpNow(id: String) {
        dashboardQueue.async { [weak self] in
            guard let self, let item = self.followUps.read().first(where: { $0.id == id }) else { return }
            self.perform(item, now: Date())
            DispatchQueue.main.async { self.onChange?() }
        }
    }

    private func perform(_ item: LmsFollowUp, now: Date) {
        let settings = self.settings
        guard hasLmsSignIn else {
            followUps.fail(item, reason: "No LMS sign-in is saved.", at: now)
            return
        }
        if let problem = LmsGroupMapping.problem(for: item, in: configurationProvider()) {
            // Not an attempt: nothing was sent, and retrying cannot fix a mapping.
            followUps.postpone(item, until: now.addingTimeInterval(LmsFollowUpQueue.retryAfter), reason: problem.message)
            log("[LMS] ✗ \(item.describe) refused: \(problem.message)")
            report(event(for: item, kind: .lmsStepFailed, severity: .failure, title: "\(item.step.displayName) refused ❌",
                         message: "Reason:\n\(problem.message)\n\nRetry scheduled in 15 minutes"), notify: true)
            return
        }
        log("[LMS] Running \(item.describe)")

        switch item.step {
        case .takeAttendance, .correctAttendance:
            guard let session = register(for: item) else {
                followUps.fail(item, reason: "No attendance register was found for this class.", at: now)
                log("[LMS] ✗ \(item.describe): no attendance register was found.")
                reportFailure(item, reason: "No attendance register was found for this class.")
                return
            }
            let present = LmsPresentNames.present(in: session)
            let review = LmsPresentNames.needsReview(in: session)
            if !review.isEmpty {
                log("[LMS] \(review.count) student(s) still need review and are sent as Not-joined: \(review.prefix(5).joined(separator: ", "))")
            }
            let result = item.step == .takeAttendance
                ? lms.takeAttendance(group: item.group, date: item.sessionDate, startTime: item.sessionStart, present: present, settings: settings, onMessage: helperLine)
                : lms.correctAttendance(group: item.group, date: item.sessionDate, startTime: item.sessionStart, present: present, settings: settings, onMessage: helperLine)
            logPlan(result)

            if result.success {
                followUps.complete(item)
                log("[LMS] ✓ \(result.message)")
                let plan = result.body["plan"]
                let joined = plan?["joinedCount"]?.number.map { Int($0) }
                let notJoined = plan?["notJoinedCount"]?.number.map { Int($0) }
                let rehearse = settings.dryRun ? " (rehearse)" : ""
                if item.step == .takeAttendance {
                    let counts = joined.map { "\($0) Joined\n\(notJoined ?? 0) Not Joined" } ?? result.message
                    report(event(for: item, kind: .attendanceUploaded, severity: .success, title: "Attendance uploaded successfully ✅\(rehearse)", message: counts),
                           notify: !settings.dryRun)
                } else {
                    let changes = result.body["changes"]?.array?.count ?? 0
                    report(event(for: item, kind: .attendanceCorrected, severity: .success, title: "Late joiners corrected ✅\(rehearse)",
                                 message: changes == 0 ? "The dashboard already matched" : "\(changes) row(s) changed"), notify: false)
                }
            } else if item.step == .takeAttendance, result.body["alreadyTaken"]?.bool == true {
                // Taken by hand already: the late-joiner pass will reconcile it.
                followUps.complete(item)
                log("[LMS] Attendance for \(item.group) was already taken on the dashboard; leaving it to the correction pass.")
                report(event(for: item, kind: .attendanceUploaded, severity: .success, title: "Attendance already taken", message: "It was taken on the dashboard already; the late-joiner pass reconciles it"), notify: false)
            } else if item.step == .correctAttendance, result.body["notTakenYet"]?.bool == true,
                      followUps.read().contains(where: { $0.step == .takeAttendance && $0.group == item.group && $0.sessionDate == item.sessionDate }) {
                followUps.postpone(item, until: now.addingTimeInterval(LmsFollowUpQueue.retryAfter), reason: "Waiting for the attendance upload first.")
            } else {
                followUps.fail(item, reason: result.message, at: now)
                log("[LMS] ✗ \(result.message)")
                reportFailure(item, reason: result.message)
            }

        case .endSession:
            // The final attendance goes first: a session is never ended with a correction still owed.
            if let waiting = pendingBefore(item, steps: [.takeAttendance, .correctAttendance]) {
                followUps.postpone(item, until: now.addingTimeInterval(5 * 60), reason: "Waiting for \(waiting.step.displayName) first.")
                log("[LMS] \(item.describe) waits for \(waiting.step.displayName).")
                return
            }
            let result = lms.endSession(group: item.group, date: item.sessionDate, startTime: item.sessionStart, settings: settings, onMessage: helperLine)
            if result.success {
                followUps.complete(item)
                log("[LMS] ✓ \(result.message)")
                let outcome = result.body["outcome"]?.string
                report(event(for: item, kind: .lmsSessionEnded, severity: .success,
                             title: outcome == "alreadyEnded" ? "LMS session already ended" : "LMS session ended ✅\(settings.dryRun ? " (rehearse)" : "")",
                             message: result.message), notify: false)
            } else {
                followUps.fail(item, reason: result.message, at: now)
                log("[LMS] ✗ \(result.message)")
                reportFailure(item, reason: result.message)
            }

        case .attachRecording:
            if let waiting = pendingBefore(item, steps: [.takeAttendance, .correctAttendance, .endSession]) {
                followUps.postpone(item, until: now.addingTimeInterval(5 * 60), reason: "Waiting for \(waiting.step.displayName) first.")
                return
            }
            let configuration = configurationProvider()
            let profile = item.recordingProfile
                ?? item.scheduleID.flatMap { id in configuration.schedules.first { $0.id == id } }.flatMap { configuration.profile(for: $0)?.resolvedWebProfileName }
            guard let profile else {
                followUps.fail(item, reason: "This class's schedule has no Zoom account profile.", at: now)
                reportFailure(item, reason: "This class's schedule has no Zoom account profile.")
                return
            }
            let zoomZone = UserDefaults.standard.string(forKey: "lms.zoomAccountTimeZone").flatMap(TimeZone.init(identifier:))
            let result = lms.attachZoomRecording(group: item.group, date: item.sessionDate, startTime: item.sessionStart, profile: profile,
                                                 zoomUtcOffsetMinutes: zoomZone.map { $0.secondsFromGMT() / 60 }, settings: settings, onMessage: helperLine)
            let outcome = result.body["outcome"]?.string
            let issue = result.body["issue"]?.string
            if result.success {
                followUps.complete(item)
                log("[RECORDINGS] ✓ \(result.message)")
                report(event(for: item, kind: .recordingZoomAttached, severity: .success,
                             title: outcome == "keptExisting" ? "Record link already set" : "Zoom recording attached ✅\(settings.dryRun ? " (rehearse)" : "")",
                             message: result.message), notify: false)
            } else if issue == "recordingNotFound" || issue == "busy" || issue == "sessionNotEnded" {
                // Zoom lists a recording only once the meeting has ended and it is processing: try again soon.
                followUps.fail(item, reason: result.message, at: now, retryAfter: 15 * 60)
                log("[RECORDINGS] … \(result.message) Trying again in 15 minutes.")
                if item.attempts + 1 >= LmsFollowUpQueue.maximumAttempts { reportFailure(item, reason: result.message) }
            } else {
                followUps.fail(item, reason: result.message, at: now)
                log("[RECORDINGS] ✗ \(result.message)")
                if issue == "zoomNotSignedIn" {
                    report(event(for: item, kind: .lmsStepFailed, severity: .failure, title: "Zoom recording: sign in needed ❌",
                                 message: "\(result.message)\n\nFix: Schedules → Accounts → select the account → Sign in to Zoom Web."),
                           notify: true, key: "zoom-signin|\(profile)")
                } else {
                    reportFailure(item, reason: result.message)
                }
            }
        }
    }

    /// An earlier step of the same class that is still owed (and not given up on).
    private func pendingBefore(_ item: LmsFollowUp, steps: [LmsFollowUpStep]) -> LmsFollowUp? {
        followUps.read().first {
            $0.id != item.id && steps.contains($0.step)
                && LmsGroupMapping.key($0.group) == LmsGroupMapping.key(item.group)
                && $0.sessionDate == item.sessionDate
                && $0.attempts < LmsFollowUpQueue.maximumAttempts
        }
    }

    private func event(for item: LmsFollowUp, kind: OperationsEventKind, severity: OperationsSeverity, title: String, message: String) -> OperationsEvent {
        OperationsEvent(kind: kind, severity: severity, groupCode: item.group, sessionDate: item.sessionDate, scheduleID: item.scheduleID, title: title, message: message)
    }

    /// A failed attempt: grouped while it is retried, and said plainly once it has given up.
    private func reportFailure(_ item: LmsFollowUp, reason: String) {
        let gaveUp = item.attempts + 1 >= LmsFollowUpQueue.maximumAttempts
        let verb = item.step == .takeAttendance ? "Attendance upload" : item.step.displayName
        report(
            event(for: item, kind: gaveUp ? .lmsStepGaveUp : .lmsStepFailed, severity: .failure,
                  title: gaveUp ? "\(verb) gave up ❌" : "\(verb) failed ❌",
                  message: "Reason:\n\(reason)\n\n" + (gaveUp ? "No more automatic retries; run it from the dashboard." : "Retry scheduled in 15 minutes")),
            notify: true,
            key: "\(OperationsSessionKey.make(groupCode: item.group, sessionDate: item.sessionDate))|\(item.step.rawValue)|\(gaveUp ? "gaveUp" : "failed")"
        )
    }

    /// Into the event log when there is one; otherwise the plain notification it always was.
    func report(_ event: OperationsEvent, notify: Bool, key: String? = nil) {
        if let operations {
            operations.record(event, notify: notify, key: key)
        } else if notify {
            self.notify(title: event.groupCode.map { "\($0): \(event.title)" } ?? event.title, body: event.message)
        }
    }

    private func logPlan(_ result: AutomationResult) {
        guard let marks = result.body["plan"]?["marks"]?.array else { return }
        for mark in marks {
            guard let name = mark["studentName"]?.string else { continue }
            log("   \(mark["joined"]?.bool == true ? "JOINED    " : "NOT-JOINED")  \(name)")
        }
        for missing in result.body["plan"]?["notOnTheDashboard"]?.array?.compactMap(\.string) ?? [] {
            log("   Seen in the meeting but not listed by the dashboard: \(missing)")
        }
    }

    private func register(for item: LmsFollowUp) -> AttendanceSession? {
        var sessions = attendanceStore.loadAll()
        if let live = liveAttendanceSession() {
            sessions.removeAll { $0.id == live.id }
            sessions.append(live)
        }
        webLock.lock()
        for meeting in webMeetings.values {
            if let current = meeting.recorder?.current {
                sessions.removeAll { $0.id == current.id }
                sessions.append(current)
            }
        }
        webLock.unlock()
        return LmsPresentNames.session(for: item, in: sessions).map { AttendanceIgnoring.apply(.current, to: $0) }
    }

    // MARK: - Dashboard: manual actions from the Automation window

    enum ManualAction {
        case runSession
        case takeAttendance(present: [String])
        case correctAttendance(present: [String])
    }

    func perform(_ action: ManualAction, group: String, date: String, time: String?, completion: @escaping (AutomationResult) -> Void) {
        let settings = self.settings
        let configuration = configurationProvider()
        if let problem = LmsGroupMapping.sharedDashboardGroups(in: configuration).first(where: {
            if case .sharedDashboardGroup(let code, _) = $0 { return LmsGroupMapping.key(code) == LmsGroupMapping.key(group) }
            return false
        }) {
            let refused = AutomationResult(success: false, message: problem.message, failure: "ambiguousGroup")
            log("[LMS] ✗ \(refused.message)")
            completion(refused)
            return
        }
        dashboardQueue.async { [weak self] in
            guard let self else { return }
            let result: AutomationResult
            switch action {
            case .runSession:
                result = self.lms.runSession(group: group, date: date, startTime: time, settings: settings, onMessage: self.helperLine)
            case .takeAttendance(let present):
                result = self.lms.takeAttendance(group: group, date: date, startTime: time, present: present, settings: settings, onMessage: self.helperLine)
                self.logPlan(result)
            case .correctAttendance(let present):
                result = self.lms.correctAttendance(group: group, date: date, startTime: time, present: present, settings: settings, onMessage: self.helperLine)
                self.logPlan(result)
            }
            self.log("[LMS] \(result.success ? "✓" : "✗") \(result.message)")
            DispatchQueue.main.async { completion(result) }
        }
    }

    func saveSignIn(_ account: LmsAccount, verify: Bool, completion: @escaping (AutomationResult) -> Void) {
        dashboardQueue.async { [weak self] in
            guard let self else { return }
            if verify {
                let result = self.lms.verifySignIn(account, showBrowser: self.settings.showBrowser, onMessage: self.helperLine)
                guard result.success else {
                    DispatchQueue.main.async { completion(result) }
                    return
                }
            }
            let saved = self.credentials.save(account)
            let result = AutomationResult(success: saved, message: saved ? "Saved. \(account.email) will be used on the dashboard." : "The Keychain did not accept the sign-in.")
            DispatchQueue.main.async { completion(result) }
        }
    }

    func forgetSignIn() {
        credentials.delete()
    }

    func checkHelper() -> String {
        switch AutomationEnvironment.locate() {
        case .failure(let error): return "✗ \(error.localizedDescription)"
        case .success(let environment):
            let result = helper.run("version", request: [:], timeout: 20)
            return result.success
                ? "✓ Node \(result.body["node"]?.string ?? "?") · helper at \(environment.helperDirectory.path)"
                : "✗ \(result.message)"
        }
    }

    /// Reads an Excel file through the helper. Synchronous; call it off the main thread.
    func readWorkbook(command: String, path: String) -> AutomationResult {
        helper.run(command, request: ["path": path], timeout: 60)
    }

    /// A visible browser on one of the app's profiles, to sign in to Zoom (or the dashboard) once.
    func openBrowserProfile(_ profile: String, url: String) {
        let result = helper.start("open-profile", request: ["profile": profile, "url": url], onMessage: { [weak self] in self?.helperLine($0) }, onExit: { _ in })
        if case .failure(let error) = result {
            log("[PROFILE] ✗ \(error.localizedDescription)")
        }
    }

    // MARK: - Web meetings

    var webMeetingScheduleIDs: Set<UUID> {
        webLock.lock()
        defer { webLock.unlock() }
        return Set(webMeetings.keys)
    }

    /// Health check: sign in and count the group's sessions on a date. Presses nothing.
    func checkLmsSession(group: String, date: String) -> HealthProbeResults.LmsProbe {
        lms.checkSession(group: group, date: date, onMessage: helperLine)
    }

    var activeWebMeetingCount: Int {
        webLock.lock()
        defer { webLock.unlock() }
        return webMeetings.count
    }

    func isWebMeetingRunning(for scheduleID: UUID) -> Bool {
        webLock.lock()
        defer { webLock.unlock() }
        return webMeetings[scheduleID] != nil
    }

    /// Opens a scheduled meeting in the Zoom Web Client with its account's browser profile.
    /// Returns a sentence for the log and the menu.
    func startWebMeeting(schedule: ZoomSchedule, profile: ZoomAccountProfile, group: StudentGroup?, link: URL) -> Result<String, AutomationSetupError> {
        if isWebMeetingRunning(for: schedule.id) { return .success("already running in the Web Client") }
        let recorder = group.map { WebAttendanceRecorder(group: $0, schedule: schedule, store: attendanceStore) }
        let webProfile = profile.resolvedWebProfileName
        let request: [String: Any] = [
            "meetingUrl": link.absoluteString,
            "profile": webProfile,
            "headless": UserDefaults.standard.bool(forKey: "web.headless"),
            "sessionId": schedule.id.uuidString,
            "captureAttendance": recorder != nil,
            "autoAdmit": schedule.enablesAutoAdmit,
            "startAsHost": true
        ]
        let started = helper.start("web-meeting", request: request, onMessage: { [weak self] message in
            guard let self else { return }
            switch message {
            case .event(let name, let data):
                switch name {
                case "admitted":
                    DispatchQueue.main.async { self.onWebAdmitted?() }
                case "attendanceSnapshot":
                    let names = data["names"]?.array?.compactMap(\.string) ?? []
                    if let session = recorder?.record(names: names) {
                        self.log("[WEB] \(schedule.name): \(names.count) in the meeting, \(session.presentCount) matched.")
                    }
                case "signInRequired":
                    self.report(OperationsEvent(kind: .zoomFailed, severity: .warning, groupCode: group?.dashboardGroupName, sessionDate: LmsFollowUpQueue.dashboardDateAndTime(Date()).0, scheduleID: schedule.id,
                                                title: "Zoom Web needs a sign-in ⚠️", message: "Sign in to Zoom in the browser window for \(profile.name)."), notify: true)
                default:
                    break
                }
            default:
                self.helperLine(message)
            }
        }, onExit: { [weak self] result in
            guard let self else { return }
            self.webLock.lock()
            let meeting = self.webMeetings.removeValue(forKey: schedule.id)
            self.webLock.unlock()
            if let finalized = meeting?.recorder?.finalize() {
                self.log("[WEB] Attendance closed for \(finalized.groupName): present=\(finalized.presentCount) absent=\(finalized.absentCount) review=\(finalized.needsReviewCount)")
            }
            self.log("[WEB] \(schedule.name): \(result?.message ?? "the browser closed").")
            DispatchQueue.main.async { self.onChange?() }
        })
        switch started {
        case .failure(let error):
            return .failure(error)
        case .success(let process):
            webLock.lock()
            webMeetings[schedule.id] = WebMeeting(process: process, schedule: schedule, recorder: recorder, startedAt: Date())
            webLock.unlock()
            DispatchQueue.main.async { [weak self] in self?.onChange?() }
            return .success("opened in the Zoom Web Client with profile '\(webProfile)'")
        }
    }

    /// Stops admitting in a Web meeting at its end time. The browser closes; Zoom keeps the
    /// meeting itself running for everyone else only if another host is present, exactly as when
    /// a host closes their tab.
    func stopWebMeeting(scheduleID: UUID) {
        webLock.lock()
        let meeting = webMeetings[scheduleID]
        webLock.unlock()
        meeting?.process.stop(grace: 10)
    }

    // MARK: - Co-host

    /// Every 20 seconds while a desktop register is live: make the group's configured people
    /// co-host once they are in the meeting. Each person is handled once per meeting.
    private func coHostPass() {
        guard UserDefaults.standard.object(forKey: "roles.enabled") == nil || UserDefaults.standard.bool(forKey: "roles.enabled") else { return }
        guard let session = liveAttendanceSession(), session.endedAt == nil else { return }
        let configuration = configurationProvider()
        guard let group = configuration.studentGroups.first(where: { $0.id == session.groupID }),
              !group.coHostCandidates.isEmpty,
              let zoom = ZoomAXSupport.zoomApplication() else { return }

        if coHostSessionKey != session.id {
            coHostSessionKey = session.id
            coHostGranted = []
            coHostFailures = [:]
        }
        let readout = ZoomAXSupport.participantsReadout(pid: zoom.pid)
        guard readout.listAvailable else { return }
        let history = coHostHistory.load()

        for row in readout.admitted where !row.roles.contains(.me) {
            guard let match = CoHostMatcher.match(observedName: row.displayName, candidates: group.coHostCandidates, history: history, groupID: group.id) else { continue }
            let key = NameNormalizer.normalize(row.displayName)
            guard !coHostGranted.contains(key), coHostFailures[key, default: 0] < Self.coHostMaximumTries else { continue }
            if row.roles.contains(.coHost) || row.roles.contains(.host) {
                coHostGranted.insert(key)
                continue
            }
            log("[COHOST] \(row.displayName) matched \(match.candidate.name) (\(match.source.rawValue), \(match.confidence)); assigning.")
            let outcome = ZoomAXSupport.makeCoHost(displayName: row.displayName, pid: zoom.pid)
            if outcome.isSuccess {
                coHostGranted.insert(key)
                coHostHistory.remember(CoHostAssignmentRecord(groupID: group.id, candidateName: match.candidate.name, observedName: row.displayName, assignedAt: Date()))
                log("[COHOST] ✓ \(row.displayName): \(outcome.message).")
                if outcome == .assigned {
                    report(OperationsEvent(kind: .general, severity: .info, groupCode: group.dashboardGroupName, sessionDate: LmsFollowUpQueue.dashboardDateAndTime(session.startedAt).0, scheduleID: session.scheduleID,
                                           title: "Co-host assigned", message: "\(row.displayName) (\(match.candidate.name))"), notify: operations == nil)
                }
            } else {
                let tries = coHostFailures[key, default: 0] + 1
                coHostFailures[key] = tries
                if tries < Self.coHostMaximumTries {
                    log("[COHOST] ✗ \(row.displayName): \(outcome.message). Trying again next pass (\(tries)/\(Self.coHostMaximumTries)).")
                } else {
                    log("[COHOST] ✗ \(row.displayName): \(outcome.message). Gave up after \(tries) tries; make them co-host by hand.")
                    report(OperationsEvent(kind: .general, severity: .warning, groupCode: group.dashboardGroupName, sessionDate: LmsFollowUpQueue.dashboardDateAndTime(session.startedAt).0, scheduleID: session.scheduleID,
                                           title: "Co-host not assigned ⚠️", message: "\(row.displayName) (\(match.candidate.name)): Zoom refused (\(outcome.message)).\n\nMake them co-host by hand in Participants."), notify: true)
                }
            }
        }
    }

    // MARK: - Launch agent

    func syncLaunchAgent() {
        let meetings = UserDefaults.standard.bool(forKey: "scheduler.openAppForMeetings")
        let sheetSync = RecordingSyncSettings.load().enabled
        let configuration = meetings ? configurationProvider() : SchedulerConfiguration()
        let dailyWake = sheetSync ? recordingSync.schedule : nil
        DispatchQueue.global(qos: .utility).async { [weak self] in
            let message = LaunchAgentScheduler.install(configuration: configuration, enabled: meetings || sheetSync, dailyWake: dailyWake)
            if meetings || sheetSync { self?.log("[SCHEDULER] \(message)") }
        }
    }

    // MARK: - Notifications

    func notify(title: String, body: String) {
        guard Bundle.main.bundleIdentifier != nil else { return }
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
    }
}
