import Foundation

public enum RecordingSyncState: String, Codable, CaseIterable, Equatable {
    /// Found in the sheet, not on the LMS yet (or waiting for the session to end, the attendance to
    /// finish, or the next sync).
    case pending
    /// A sync is working on it right now.
    case processing
    /// The Drive link is on the LMS session. Final.
    case attached
    /// Needs a person: ambiguous sessions, a different link already on the session, or two
    /// different Drive links in the sheet for one session. Never retried automatically.
    case conflict
    /// Something broke (helper, sign-in, page). Retried by Retry Failed, and by the daily sync a few times.
    case failed
}

/// Why a record is where it is, in words a person can act on and a code the app can.
public enum RecordingSyncIssue: String, Codable, Equatable {
    case noSession
    case ambiguousSessions
    case sessionNotEnded
    case attendanceNotComplete
    case existingLinkDiffers
    case multipleDriveLinksInSheet
    case sheetLinkChanged
    case groupMapping
    case rehearsed
    case failed
}

/// What a session's record link held when the app first read it, before anything was written.
public enum LmsRecordLinkFound: String, Codable, Equatable {
    case empty
    case zoomLink
    case sameDriveLink
    case otherDriveLink
    case otherLink

    public static func classify(_ current: String, driveURL: String) -> LmsRecordLinkFound {
        let value = current.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !value.isEmpty else { return .empty }
        if let id = RecordingLinkRules.driveFileID(value) {
            return id == RecordingLinkRules.driveFileID(driveURL) ? .sameDriveLink : .otherDriveLink
        }
        if let url = URLComponents(string: value), url.scheme == "https",
           url.host?.lowercased().hasSuffix("zoom.us") == true, url.path.lowercased().contains("/rec/") {
            return .zoomLink
        }
        return .otherLink
    }

    public var displayName: String {
        switch self {
        case .empty: return "Empty"
        case .zoomLink: return "Zoom link"
        case .sameDriveLink: return "Same Drive link"
        case .otherDriveLink: return "Other Drive link"
        case .otherLink: return "Other link"
        }
    }
}

/// One Drive recording link on its way from the Google Sheet to one LMS session.
public struct RecordingSyncRecord: Codable, Equatable, Identifiable {
    public var id: String
    public var groupCode: String
    /// yyyy-MM-dd
    public var sessionDate: String
    public var sheetTab: String
    public var sheetRow: Int
    public var fileName: String
    public var driveURL: String
    public var lmsSessionURL: String?
    public var state: RecordingSyncState
    public var issue: RecordingSyncIssue?
    public var error: String?
    /// What the LMS already held when it was not overwritten.
    public var conflictingLink: String?
    /// The Zoom recording link the Drive link replaced, when replacing Zoom links is switched on.
    public var replacedLink: String?
    /// How the last LMS step ended: attached, alreadyAttached, replacedZoom, wouldAttach, ...
    public var lmsOutcome: String?
    /// The record link the session held when the app first read it, and what kind of link it was.
    public var foundLink: String?
    public var foundLinkKind: LmsRecordLinkFound?
    /// The helper's own sentence for the last LMS step, kept for the detail view.
    public var lastMessage: String?
    public var attempts: Int
    public var createdAt: Date
    public var updatedAt: Date
    public var lastAttemptAt: Date?
    public var completedAt: Date?

    public init(groupCode: String, sessionDate: String, sheetTab: String, sheetRow: Int, fileName: String, driveURL: String, now: Date) {
        id = RecordingSyncRecord.makeID(groupCode: groupCode, sessionDate: sessionDate)
        self.groupCode = groupCode
        self.sessionDate = sessionDate
        self.sheetTab = sheetTab
        self.sheetRow = sheetRow
        self.fileName = fileName
        self.driveURL = driveURL
        state = .pending
        attempts = 0
        createdAt = now
        updatedAt = now
    }

    public static func makeID(groupCode: String, sessionDate: String) -> String {
        "\(LmsGroupMapping.key(groupCode))|\(sessionDate)"
    }

    /// Whether an automatic sync should try this record.
    public var isDueForAutomaticSync: Bool {
        switch state {
        case .pending: return true
        case .failed: return attempts < RecordingSyncRecord.automaticRetryLimit
        case .processing, .attached, .conflict: return false
        }
    }

    public static let automaticRetryLimit = 5
}

/// Local completion state: `Zoom Auto Admit/RecordingSync/records.json`. The Google Sheet is never written.
public final class RecordingSyncStore {
    public let fileURL: URL
    private let lock = NSLock()

    public init(fileURL: URL = RecordingSyncStore.defaultURL) {
        self.fileURL = fileURL
    }

    public static var defaultURL: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Zoom Auto Admit", isDirectory: true)
            .appendingPathComponent("RecordingSync", isDirectory: true)
            .appendingPathComponent("records.json")
    }

    public func load() -> [RecordingSyncRecord] {
        lock.lock()
        defer { lock.unlock() }
        return read()
    }

    public func replaceAll(_ records: [RecordingSyncRecord]) {
        lock.lock()
        defer { lock.unlock() }
        write(records)
    }

    public func update(_ record: RecordingSyncRecord) {
        lock.lock()
        defer { lock.unlock() }
        var records = read()
        if let index = records.firstIndex(where: { $0.id == record.id }) {
            records[index] = record
        } else {
            records.append(record)
        }
        write(records)
    }

    /// A crash mid-sync must not leave a record stuck in processing forever.
    public func recoverInterrupted(now: Date) {
        lock.lock()
        defer { lock.unlock() }
        var records = read()
        var changed = false
        for index in records.indices where records[index].state == .processing {
            records[index].state = .pending
            records[index].error = "The previous sync was interrupted; it will be tried again."
            records[index].updatedAt = now
            changed = true
        }
        if changed { write(records) }
    }

    private func read() -> [RecordingSyncRecord] {
        guard let data = try? Data(contentsOf: fileURL) else { return [] }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        do {
            return try decoder.decode([RecordingSyncRecord].self, from: data)
        } catch {
            // The next write would replace an unreadable file with an empty list; keep a copy first.
            UnreadableFile.preserve(fileURL)
            return []
        }
    }

    private func write(_ records: [RecordingSyncRecord]) {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        guard let data = try? encoder.encode(records.sorted { ($0.sessionDate, $0.groupCode) > ($1.sessionDate, $1.groupCode) }) else { return }
        try? FileManager.default.createDirectory(at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? data.write(to: fileURL, options: .atomic)
    }
}

/// What reading the sheet found, beyond the records themselves.
public struct RecordingSheetReport: Equatable {
    public var tabsRead: [String] = []
    /// Tabs that are not the LMS code of any group here - read and left alone.
    public var unknownTabs: [String] = []
    public var rowsWithoutLink: Int = 0
    public var rowsWithoutDate: [String] = []
    public var newRecords: Int = 0
    public var unchanged: Int = 0
    public var conflicts: Int = 0

    public init() {}
}

/// Turns the sheet into records, merging with what is already known locally.
public enum RecordingSyncPlanner {
    public static func merge(
        tabs: [RecordingSheetTab],
        groups: [StudentGroup],
        existing: [RecordingSyncRecord],
        now: Date = Date()
    ) -> (records: [RecordingSyncRecord], report: RecordingSheetReport) {
        var report = RecordingSheetReport()
        var byID = Dictionary(uniqueKeysWithValues: existing.map { ($0.id, $0) })
        let codes = Dictionary(grouping: groups, by: { LmsGroupMapping.key($0.dashboardGroupName) })

        struct Candidate { var rows: [RecordingSheetRow] }
        var candidates: [String: Candidate] = [:]
        var groupForID: [String: String] = [:]

        for tab in tabs {
            report.tabsRead.append(tab.title)
            let key = LmsGroupMapping.key(tab.title)
            guard let matching = codes[key], let group = matching.first else {
                report.unknownTabs.append(tab.title)
                continue
            }
            for row in tab.rows {
                guard let drive = row.driveURL else {
                    report.rowsWithoutLink += 1
                    continue
                }
                guard let date = row.date else {
                    report.rowsWithoutDate.append("\(tab.title) row \(row.rowNumber): “\(row.rawDate)”")
                    continue
                }
                let id = RecordingSyncRecord.makeID(groupCode: group.dashboardGroupName, sessionDate: date)
                var fixed = row
                fixed.driveURL = drive
                candidates[id, default: Candidate(rows: [])].rows.append(fixed)
                groupForID[id] = group.dashboardGroupName
            }
        }

        for (id, candidate) in candidates.sorted(by: { $0.key < $1.key }) {
            let first = candidate.rows[0]
            let distinctFiles = Set(candidate.rows.compactMap { $0.driveURL.flatMap(RecordingLinkRules.driveFileID) })
            let groupCode = groupForID[id] ?? first.tab
            let date = first.date ?? ""

            if var record = byID[id] {
                let sameFile = RecordingLinkRules.driveFileID(record.driveURL).map { distinctFiles == [$0] } ?? false
                if sameFile {
                    if record.state == .conflict, record.issue == .multipleDriveLinksInSheet {
                        // The sheet was cleaned up down to the link already recorded: back to pending.
                        record.state = .pending
                        record.issue = nil
                        record.error = nil
                        record.sheetRow = first.rowNumber
                        record.fileName = first.fileName
                        record.updatedAt = now
                        byID[id] = record
                    }
                    report.unchanged += 1
                    continue
                }
                switch record.state {
                case .attached:
                    // Already on the LMS with another file: never update twice; a person decides.
                    record.state = .conflict
                    record.issue = .sheetLinkChanged
                    record.error = "The sheet now lists a different Drive link than the one already attached. Nothing was changed."
                    record.updatedAt = now
                    report.conflicts += 1
                case .pending, .failed, .conflict, .processing:
                    if distinctFiles.count > 1 {
                        record.state = .conflict
                        record.issue = .multipleDriveLinksInSheet
                        record.error = "The sheet lists \(distinctFiles.count) different Drive links for this session. Keep one row and sync again."
                        report.conflicts += 1
                    } else {
                        record.driveURL = first.driveURL ?? record.driveURL
                        record.sheetRow = first.rowNumber
                        record.fileName = first.fileName
                        if record.issue == .multipleDriveLinksInSheet || record.issue == .sheetLinkChanged {
                            record.state = .pending
                            record.issue = nil
                            record.error = nil
                        }
                    }
                    record.updatedAt = now
                }
                byID[id] = record
                continue
            }

            var record = RecordingSyncRecord(groupCode: groupCode, sessionDate: date, sheetTab: first.tab, sheetRow: first.rowNumber, fileName: first.fileName, driveURL: first.driveURL ?? "", now: now)
            if distinctFiles.count > 1 {
                record.state = .conflict
                record.issue = .multipleDriveLinksInSheet
                record.error = "The sheet lists \(distinctFiles.count) different Drive links for this session. Keep one row and sync again."
                report.conflicts += 1
            }
            byID[id] = record
            report.newRecords += 1
        }
        return (Array(byID.values), report)
    }
}

/// Whether a record may go to the LMS yet: group mapping, and the class's attendance workflow.
///
/// Whether the LMS session has ended is decided on the dashboard itself - the record link is only
/// offered once it has - so it is checked by the helper, not guessed here.
public enum RecordingSyncGate {
    public static func blocker(
        for record: RecordingSyncRecord,
        configuration: SchedulerConfiguration,
        followUps: [LmsFollowUp],
        sessions: [AttendanceSession],
        calendar: Calendar = .current
    ) -> (issue: RecordingSyncIssue, message: String)? {
        guard let group = configuration.studentGroups.first(where: { LmsGroupMapping.key($0.dashboardGroupName) == LmsGroupMapping.key(record.groupCode) }) else {
            return (.groupMapping, "No group here has the LMS code \(record.groupCode).")
        }
        if let problem = LmsGroupMapping.problem(for: group, in: configuration) {
            return (.groupMapping, problem.message)
        }
        let open = followUps.filter {
            LmsGroupMapping.key($0.group) == LmsGroupMapping.key(record.groupCode)
                && $0.sessionDate == record.sessionDate
                && ($0.step == .takeAttendance || $0.step == .correctAttendance)
                && $0.attempts < LmsFollowUpQueue.maximumAttempts
                // A step too old to ever run again must not hold the recording back for a week.
                && Date().timeIntervalSince($0.dueAt) <= LmsFollowUpQueue.tooOld
        }
        if let first = open.first {
            return (.attendanceNotComplete, "The attendance workflow is not finished yet (\(first.step.displayName) is still queued).")
        }
        let unfinished = sessions.filter {
            $0.groupID == group.id
                && LmsFollowUpQueue.dashboardDateAndTime($0.startedAt, calendar: calendar).0 == record.sessionDate
                && !$0.isFinalized
        }
        if !unfinished.isEmpty {
            return (.attendanceNotComplete, "The attendance register for this class is not finalized yet.")
        }
        return nil
    }
}

/// Folds one helper answer into a record.
public enum RecordingSyncOutcome {
    public static func apply(_ result: AutomationResult, to record: RecordingSyncRecord, dryRun: Bool, now: Date = Date()) -> RecordingSyncRecord {
        var updated = record
        updated.lastAttemptAt = now
        updated.updatedAt = now
        if let url = result.body["lmsSessionUrl"]?.string { updated.lmsSessionURL = url }
        let outcome = result.body["outcome"]?.string
        let issue = result.body["issue"]?.string
        updated.lmsOutcome = outcome ?? issue
        updated.lastMessage = result.message
        // Only the first reading counts: after a write the session holds our own link.
        if updated.foundLinkKind == nil, result.body["recordLinkState"]?.string != nil {
            let current = result.body["currentLink"]?.string ?? ""
            updated.foundLink = current.isEmpty ? nil : current
            updated.foundLinkKind = LmsRecordLinkFound.classify(current, driveURL: record.driveURL)
        }

        if result.success {
            switch outcome {
            case "attached", "alreadyAttached", "replacedZoom":
                updated.state = .attached
                updated.issue = nil
                updated.error = nil
                updated.conflictingLink = nil
                updated.completedAt = now
                if let replaced = result.body["replacedLink"]?.string { updated.replacedLink = replaced }
            case "wouldAttach", "wouldReplaceZoom":
                // A rehearsal proves the path; it never completes a record.
                updated.state = .pending
                updated.issue = .rehearsed
                updated.error = "Rehearsed: \(result.message)"
            default:
                updated.state = .failed
                updated.issue = .failed
                updated.error = "Unexpected answer: \(result.message)"
                updated.attempts += 1
            }
            if dryRun, updated.state == .attached, outcome != "alreadyAttached" {
                // Defensive: a dry run cannot have written anything.
                updated.state = .pending
                updated.issue = .rehearsed
                updated.completedAt = nil
            }
            return updated
        }

        updated.error = result.message
        switch issue {
        case "noSession":
            updated.state = .pending
            updated.issue = .noSession
        case "sessionNotEnded":
            updated.state = .pending
            updated.issue = .sessionNotEnded
        case "ambiguousSessions":
            updated.state = .conflict
            updated.issue = .ambiguousSessions
        case "existingLinkDiffers":
            updated.state = .conflict
            updated.issue = .existingLinkDiffers
            updated.conflictingLink = result.body["currentLink"]?.string
        default:
            if result.failure == "busy" {
                updated.state = .pending
                updated.issue = .failed
            } else {
                updated.state = .failed
                updated.issue = .failed
                updated.attempts += 1
            }
        }
        return updated
    }
}

/// Once a day at a fixed time in a fixed zone, catching up once if the Mac was asleep or the app closed.
public struct DailyJobSchedule: Equatable {
    public var hour: Int
    public var minute: Int
    public var timeZone: TimeZone

    public init(hour: Int = 8, minute: Int = 0, timeZone: TimeZone = TimeZone(identifier: "Africa/Cairo")!) {
        self.hour = hour
        self.minute = minute
        self.timeZone = timeZone
    }

    private var calendar: Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        return calendar
    }

    /// The zone's calendar day, e.g. "2026-09-15".
    public func dayKey(_ date: Date) -> String {
        let parts = calendar.dateComponents([.year, .month, .day], from: date)
        return String(format: "%04d-%02d-%02d", parts.year!, parts.month!, parts.day!)
    }

    public func runTime(onDayOf date: Date) -> Date {
        var parts = calendar.dateComponents([.year, .month, .day], from: date)
        parts.hour = hour
        parts.minute = minute
        return calendar.date(from: parts)!
    }

    /// Due when today's run time has passed and today has not run yet.
    public func isDue(now: Date, lastRunDay: String?) -> Bool {
        now >= runTime(onDayOf: now) && lastRunDay != dayKey(now)
    }

    public func nextRun(after now: Date, lastRunDay: String?) -> Date {
        if isDue(now: now, lastRunDay: lastRunDay) { return now }
        let today = runTime(onDayOf: now)
        if now < today { return today }
        return calendar.date(byAdding: .day, value: 1, to: today)!
    }
}
