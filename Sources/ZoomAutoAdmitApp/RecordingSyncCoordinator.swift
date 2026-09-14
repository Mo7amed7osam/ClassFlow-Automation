import AppKit
import Foundation
import ZoomAutoAdmitCore

/// Settings for the recording sync. Tokens and the client secret are in the Keychain, not here.
struct RecordingSyncSettings: Equatable {
    static let enabledKey = "recordingSync.enabled"
    static let spreadsheetKey = "recordingSync.spreadsheetID"
    static let lastSuccessKey = "recordingSync.lastSuccessAt"
    static let lastRunDayKey = "recordingSync.lastRunDay"
    static let replaceZoomKey = "recordingSync.replaceZoomRecordingLinks"
    static let clientIDKey = "google.oauthClientID"
    static let lastSummaryKey = "recordingSync.lastSummary"

    var enabled: Bool
    var spreadsheetID: String
    var replaceZoomRecordingLinks: Bool
    var clientID: String

    static func load(from defaults: UserDefaults = .standard) -> RecordingSyncSettings {
        RecordingSyncSettings(
            enabled: defaults.bool(forKey: enabledKey),
            spreadsheetID: defaults.string(forKey: spreadsheetKey) ?? "",
            replaceZoomRecordingLinks: defaults.bool(forKey: replaceZoomKey),
            clientID: defaults.string(forKey: clientIDKey) ?? ""
        )
    }

    func save(to defaults: UserDefaults = .standard) {
        defaults.set(enabled, forKey: Self.enabledKey)
        defaults.set(spreadsheetID.trimmingCharacters(in: .whitespacesAndNewlines), forKey: Self.spreadsheetKey)
        defaults.set(replaceZoomRecordingLinks, forKey: Self.replaceZoomKey)
        defaults.set(clientID.trimmingCharacters(in: .whitespacesAndNewlines), forKey: Self.clientIDKey)
    }
}

/// Google Sheet → DEPI LMS, once a day at 08:00 Cairo time, and on demand.
///
/// The sheet is only read. For each group tab, rows with a Drive link become records; a record
/// goes to the LMS only when its class's attendance workflow is done, and the helper then checks
/// on the dashboard that the session has ended and what link it already carries. Everything
/// runs one record at a time on its own queue, and the dashboard profile lock keeps it from
/// colliding with the attendance steps.
final class RecordingSyncCoordinator {
    let store: RecordingSyncStore
    let credentials: GoogleCredentialStoring
    let schedule = DailyJobSchedule()
    private(set) lazy var oauth = GoogleOAuthClient(
        configuration: { GoogleOAuthConfiguration(clientID: RecordingSyncSettings.load().clientID, clientSecret: nil) },
        store: credentials
    )
    private(set) lazy var sheets = GoogleSheetsClient(oauth: oauth)

    private let lms: LmsClient
    private let followUps: LmsFollowUpQueue
    private let attendanceStore: AttendanceStore
    private let queue = DispatchQueue(label: "com.mohamedhosam.ZoomAutoAdmit.recording-sync", qos: .utility)
    private var timer: DispatchSourceTimer?
    private var syncing = false

    var configurationProvider: () -> SchedulerConfiguration = { SchedulerConfiguration() }
    var liveAttendanceSession: () -> AttendanceSession? = { nil }
    var log: (String) -> Void = { _ in }
    var helperLine: AutomationHelper.LineHandler = { _ in }
    var notify: (String, String) -> Void = { _, _ in }
    var onChange: (() -> Void)?

    init(
        lms: LmsClient,
        followUps: LmsFollowUpQueue,
        attendanceStore: AttendanceStore,
        store: RecordingSyncStore = RecordingSyncStore(),
        credentials: GoogleCredentialStoring = KeychainGoogleCredentialStore()
    ) {
        self.lms = lms
        self.followUps = followUps
        self.attendanceStore = attendanceStore
        self.store = store
        self.credentials = credentials
    }

    var settings: RecordingSyncSettings { RecordingSyncSettings.load() }
    var isSyncing: Bool { queue.sync { syncing } }
    var isGoogleConnected: Bool { oauth.isAuthorized }
    var lastSuccessAt: Date? { UserDefaults.standard.object(forKey: RecordingSyncSettings.lastSuccessKey) as? Date }
    var lastSummary: String? { UserDefaults.standard.string(forKey: RecordingSyncSettings.lastSummaryKey) }
    var records: [RecordingSyncRecord] { store.load() }

    var nextRun: Date? {
        guard settings.enabled else { return nil }
        return schedule.nextRun(after: Date(), lastRunDay: UserDefaults.standard.string(forKey: RecordingSyncSettings.lastRunDayKey))
    }

    func start() {
        store.recoverInterrupted(now: Date())
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + 45, repeating: 60, leeway: .seconds(5))
        timer.setEventHandler { [weak self] in self?.checkSchedule() }
        self.timer = timer
        timer.resume()
    }

    func stop() {
        timer?.cancel()
    }

    // MARK: Google

    func saveClient(id: String, secret: String?) {
        var current = settings
        current.clientID = id
        current.save()
        if let secret { credentials.saveClientSecret(secret) }
    }

    func connectGoogle(completion: @escaping (Result<Void, Error>) -> Void) {
        Task {
            do {
                try await oauth.authorize(openURL: { url in DispatchQueue.main.async { NSWorkspace.shared.open(url) } })
                log("[SHEETS] Google access authorized; the refresh token is in the Keychain.")
                DispatchQueue.main.async { completion(.success(())) }
            } catch {
                log("[SHEETS] ✗ Google sign-in: \(error.localizedDescription)")
                DispatchQueue.main.async { completion(.failure(error)) }
            }
        }
    }

    func disconnectGoogle() {
        oauth.disconnect()
        log("[SHEETS] Google access removed from this Mac.")
    }

    /// Reads the sheet and says which tabs are groups here; changes nothing.
    func testConnection(completion: @escaping (String) -> Void) {
        let spreadsheetID = settings.spreadsheetID
        let configuration = configurationProvider()
        Task {
            let message: String
            do {
                let tabs = try await sheets.readTabs(spreadsheetID: spreadsheetID)
                let merged = RecordingSyncPlanner.merge(tabs: tabs, groups: configuration.studentGroups, existing: [])
                let groupTabs = tabs.filter { !merged.report.unknownTabs.contains($0.title) }
                let links = groupTabs.flatMap(\.rows).filter { $0.driveURL != nil }.count
                message = "✓ Connected. \(tabs.count) tab(s); groups: \(groupTabs.map(\.title).joined(separator: ", ").ifEmpty("none"))"
                    + (merged.report.unknownTabs.isEmpty ? "" : "; not a group here: \(merged.report.unknownTabs.joined(separator: ", "))")
                    + ". \(links) row(s) with a Drive link."
            } catch {
                message = "✗ \(error.localizedDescription)"
            }
            log("[SHEETS] Test connection: \(message)")
            DispatchQueue.main.async { completion(message) }
        }
    }

    // MARK: Sync

    private func checkSchedule() {
        let current = settings
        guard current.enabled, !syncing else { return }
        let lastRunDay = UserDefaults.standard.string(forKey: RecordingSyncSettings.lastRunDayKey)
        guard schedule.isDue(now: Date(), lastRunDay: lastRunDay) else { return }
        // Marked before running, so a failing sync is not retried every minute; Sync Now still works.
        UserDefaults.standard.set(schedule.dayKey(Date()), forKey: RecordingSyncSettings.lastRunDayKey)
        runLocked(reason: "daily 08:00 Cairo", retryAllFailed: false, only: nil)
    }

    func syncNow() {
        queue.async { [weak self] in self?.runLocked(reason: "Sync now", retryAllFailed: false, only: nil) }
    }

    func retryFailed() {
        queue.async { [weak self] in self?.runLocked(reason: "Retry failed", retryAllFailed: true, only: nil) }
    }

    /// After a person has reviewed a conflict on the LMS: the chosen records go back to pending and sync.
    func retry(ids: [String]) {
        queue.async { [weak self] in
            guard let self else { return }
            let now = Date()
            for var record in self.store.load() where ids.contains(record.id) && record.state != .attached {
                record.state = .pending
                record.attempts = 0
                record.error = "Sent back to pending by hand."
                record.updatedAt = now
                self.store.update(record)
            }
            self.runLocked(reason: "Retry selected", retryAllFailed: true, only: Set(ids))
        }
    }

    private func runLocked(reason: String, retryAllFailed: Bool, only: Set<String>?) {
        guard !syncing else {
            log("[SHEETS] A sync is already running; \(reason) skipped.")
            return
        }
        syncing = true
        changed()
        defer {
            syncing = false
            changed()
        }
        let current = settings
        let lmsSettings = LmsSettings.load()
        log("──── Recording sync (\(reason))\(lmsSettings.dryRun ? " · REHEARSE" : "") ────")

        guard !current.spreadsheetID.isEmpty else {
            log("[SHEETS] ✗ No spreadsheet ID is set.")
            return
        }
        let tabs: [RecordingSheetTab]
        switch waitFor({ try await self.sheets.readTabs(spreadsheetID: current.spreadsheetID) }) {
        case .success(let read): tabs = read
        case .failure(let error):
            log("[SHEETS] ✗ The sheet could not be read: \(error.localizedDescription)")
            if case GoogleOAuthError.notAuthorized = error { notify("Recording sync needs Google", "Connect Google in Automation → Recording Sync.") }
            record(summary: "Sheet not read: \(error.localizedDescription)", success: false)
            return
        }

        let configuration = configurationProvider()
        let now = Date()
        let merged = RecordingSyncPlanner.merge(tabs: tabs, groups: configuration.studentGroups, existing: store.load(), now: now)
        store.replaceAll(merged.records)
        let report = merged.report
        log("[SHEETS] Read \(report.tabsRead.count) tab(s): \(report.newRecords) new, \(report.unchanged) unchanged, \(report.conflicts) conflict(s), \(report.rowsWithoutLink) row(s) without a Drive link.")
        if !report.unknownTabs.isEmpty { log("[SHEETS] Tabs that are not an LMS group code here (ignored): \(report.unknownTabs.joined(separator: ", "))") }
        for row in report.rowsWithoutDate.prefix(10) { log("[SHEETS] Row without a readable date (ignored): \(row)") }
        changed()

        var sessions = attendanceStore.loadAll()
        if let live = liveAttendanceSession() {
            sessions.removeAll { $0.id == live.id }
            sessions.append(live)
        }
        let queued = followUps.read()
        var attached = 0, conflicts = 0, failed = 0, waiting = 0

        let due = store.load()
            .filter { only == nil || only!.contains($0.id) }
            .filter { $0.isDueForAutomaticSync || (retryAllFailed && $0.state == .failed) }
            .sorted { ($0.sessionDate, $0.groupCode) < ($1.sessionDate, $1.groupCode) }

        for var record in due {
            if let block = RecordingSyncGate.blocker(for: record, configuration: configuration, followUps: queued, sessions: sessions) {
                record.state = .pending
                record.issue = block.issue
                record.error = block.message
                record.updatedAt = Date()
                store.update(record)
                waiting += 1
                log("[SHEETS] \(record.groupCode) \(record.sessionDate): waiting - \(block.message)")
                continue
            }

            record.state = .processing
            record.updatedAt = Date()
            store.update(record)
            changed()
            log("[SHEETS] \(record.groupCode) \(record.sessionDate): moving \(RecordingLinkRules.preview(record.driveURL)) to the LMS.")
            let result = lms.syncRecordLink(
                group: record.groupCode,
                date: record.sessionDate,
                driveURL: record.driveURL,
                replaceZoomRecordingLinks: current.replaceZoomRecordingLinks,
                settings: lmsSettings,
                onMessage: helperLine
            )
            let updated = RecordingSyncOutcome.apply(result, to: record, dryRun: lmsSettings.dryRun)
            store.update(updated)
            changed()
            switch updated.state {
            case .attached:
                attached += 1
                log("[SHEETS] ✓ \(updated.groupCode) \(updated.sessionDate): \(result.message)")
            case .conflict:
                conflicts += 1
                log("[SHEETS] ⚠ \(updated.groupCode) \(updated.sessionDate): \(result.message)")
            case .failed:
                failed += 1
                log("[SHEETS] ✗ \(updated.groupCode) \(updated.sessionDate): \(result.message)")
            case .pending, .processing:
                waiting += 1
                log("[SHEETS] … \(updated.groupCode) \(updated.sessionDate): \(result.message)")
            }
        }

        let summary = "\(attached) attached, \(waiting) waiting, \(conflicts) conflict(s), \(failed) failed"
            + (lmsSettings.dryRun ? " (rehearse: nothing written)" : "")
        log("[SHEETS] Done: \(summary).")
        record(summary: summary, success: true)
        if conflicts > 0 || failed > 0 {
            notify("Recording sync needs attention", summary)
        }
    }

    private func record(summary: String, success: Bool) {
        let stamp = DateFormatter.localizedString(from: Date(), dateStyle: .short, timeStyle: .short)
        UserDefaults.standard.set("\(stamp): \(summary)", forKey: RecordingSyncSettings.lastSummaryKey)
        if success { UserDefaults.standard.set(Date(), forKey: RecordingSyncSettings.lastSuccessKey) }
    }

    private func changed() {
        DispatchQueue.main.async { [weak self] in self?.onChange?() }
    }

    /// Runs async work from this serial queue and waits for it.
    private func waitFor<T>(_ work: @escaping () async throws -> T) -> Result<T, Error> {
        let semaphore = DispatchSemaphore(value: 0)
        var outcome: Result<T, Error> = .failure(GoogleOAuthError.timedOut)
        Task {
            do { outcome = .success(try await work()) } catch { outcome = .failure(error) }
            semaphore.signal()
        }
        if semaphore.wait(timeout: .now() + 120) == .timedOut {
            return .failure(GoogleSheetsError.http(0, "Google Sheets did not answer within 2 minutes."))
        }
        return outcome
    }
}

private extension String {
    func ifEmpty(_ fallback: String) -> String { isEmpty ? fallback : self }
}
