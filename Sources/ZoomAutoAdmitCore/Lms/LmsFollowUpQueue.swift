import Foundation

/// What a class still owes the dashboard after its meeting has started.
public enum LmsFollowUpStep: String, Codable, CaseIterable, Equatable {
    /// Fill in the attendance sheet, an hour and a half in.
    case takeAttendance
    /// Move whoever turned up late from Not-joined to Joined, three hours in.
    case correctAttendance
    /// Retired: recording links now come from the Google Sheet sync once the session has ended and
    /// its attendance is done, not from a timer. Kept so older queue files still load.
    case attachRecording

    public var displayName: String {
        switch self {
        case .takeAttendance: return "Take attendance"
        case .correctAttendance: return "Correct late joiners"
        case .attachRecording: return "Attach recording"
        }
    }
}

/// One outstanding job: a class, a step, and when it is due. Attempts and the last reason are
/// kept so a job that keeps failing is visible rather than retrying in silence.
public struct LmsFollowUp: Codable, Equatable, Identifiable {
    public var id: String
    /// The group exactly as the dashboard shows it, e.g. CAI5_AIS4_S7.
    public var group: String
    /// yyyy-MM-dd and HH:mm, local time, as the dashboard lists sessions.
    public var sessionDate: String
    public var sessionStart: String
    public var step: LmsFollowUpStep
    public var dueAt: Date
    public var attempts: Int
    public var lastError: String?
    /// The attendance register the names are read from. Nil for a class run without one.
    public var attendanceGroupID: UUID?
    public var scheduleID: UUID?
    /// The Zoom browser profile whose My Recordings holds the class, for attachRecording.
    public var recordingProfile: String?

    public init(
        group: String,
        sessionDate: String,
        sessionStart: String,
        step: LmsFollowUpStep,
        dueAt: Date,
        attempts: Int = 0,
        lastError: String? = nil,
        attendanceGroupID: UUID? = nil,
        scheduleID: UUID? = nil,
        recordingProfile: String? = nil
    ) {
        self.id = LmsFollowUp.makeID(group: group, date: sessionDate, start: sessionStart, step: step)
        self.group = group
        self.sessionDate = sessionDate
        self.sessionStart = sessionStart
        self.step = step
        self.dueAt = dueAt
        self.attempts = attempts
        self.lastError = lastError
        self.attendanceGroupID = attendanceGroupID
        self.scheduleID = scheduleID
        self.recordingProfile = recordingProfile
    }

    public static func makeID(group: String, date: String, start: String, step: LmsFollowUpStep) -> String {
        "\(group.lowercased())|\(date)|\(start)|\(step.rawValue)"
    }

    public var describe: String { "\(group) \(sessionDate) \(sessionStart) · \(step.displayName)" }
}

/// The work a class leaves behind, written down instead of remembered.
///
/// Attendance is filled in long after anyone has stopped watching, and the app may well be
/// closed and reopened in between. Held in memory, a class that ran while the Mac restarted would
/// never get its attendance, with nothing to show that anything was missed. In a file, what is
/// due is due whenever the app next runs.
///
/// The queue decides what is due and remembers outcomes. It never touches the dashboard itself.
public final class LmsFollowUpQueue {
    public static let takeAttendanceAfter: TimeInterval = 90 * 60
    public static let correctAttendanceAfter: TimeInterval = 3 * 60 * 60
    public static let attachRecordingAfter: TimeInterval = 4 * 60 * 60
    /// A failing step is retried a while, then left in the file with its reason on it.
    public static let retryAfter: TimeInterval = 15 * 60
    public static let maximumAttempts = 8
    /// Work older than this is for a person to look at, not for a program to write.
    public static let tooOld: TimeInterval = 36 * 60 * 60

    public let fileURL: URL
    private let lock = NSLock()
    private let fileManager: FileManager

    public init(fileURL: URL = LmsFollowUpQueue.defaultURL, fileManager: FileManager = .default) {
        self.fileURL = fileURL
        self.fileManager = fileManager
    }

    public static var defaultURL: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Zoom Auto Admit", isDirectory: true)
            .appendingPathComponent("LMS", isDirectory: true)
            .appendingPathComponent("follow-up.json")
    }

    public func read() -> [LmsFollowUp] {
        lock.lock()
        defer { lock.unlock() }
        return load()
    }

    /// Writes down the steps for a class that has just started. Called again for the same class -
    /// a meeting reopened, the app restarted - it changes nothing, so attendance cannot be taken twice.
    @discardableResult
    public func schedule(
        group: String,
        sessionStartedAt start: Date,
        steps: [LmsFollowUpStep],
        attendanceGroupID: UUID?,
        scheduleID: UUID?,
        recordingProfile: String? = nil,
        calendar: Calendar = .current
    ) -> [LmsFollowUp] {
        let trimmed = group.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return [] }
        let (date, time) = Self.dashboardDateAndTime(start, calendar: calendar)
        lock.lock()
        defer { lock.unlock() }
        var items = load()
        var written: [LmsFollowUp] = []
        for step in steps {
            let item = LmsFollowUp(
                group: trimmed,
                sessionDate: date,
                sessionStart: time,
                step: step,
                dueAt: start.addingTimeInterval(Self.delay(for: step)),
                attendanceGroupID: attendanceGroupID,
                scheduleID: scheduleID,
                recordingProfile: recordingProfile
            )
            guard !items.contains(where: { $0.id == item.id }) else { continue }
            items.append(item)
            written.append(item)
        }
        if !written.isEmpty { save(items) }
        return written
    }

    public static func delay(for step: LmsFollowUpStep) -> TimeInterval {
        switch step {
        case .takeAttendance: return takeAttendanceAfter
        case .correctAttendance: return correctAttendanceAfter
        case .attachRecording: return attachRecordingAfter
        }
    }

    /// Everything due now, oldest first, skipping what has been given up on or is too old.
    public func due(at now: Date) -> [LmsFollowUp] {
        read()
            .filter { $0.dueAt <= now && $0.attempts < Self.maximumAttempts && now.timeIntervalSince($0.dueAt) <= Self.tooOld }
            .sorted { $0.dueAt < $1.dueAt }
    }

    /// Done: the entry goes, because it is not owed any more.
    public func complete(_ item: LmsFollowUp) {
        mutate { items in items.removeAll { $0.id == item.id } }
    }

    /// Not done: due again shortly, with the reason on it.
    public func fail(_ item: LmsFollowUp, reason: String, at now: Date, retryAfter: TimeInterval = LmsFollowUpQueue.retryAfter) {
        mutate { items in
            guard let index = items.firstIndex(where: { $0.id == item.id }) else { return }
            items[index].attempts += 1
            items[index].lastError = reason
            items[index].dueAt = now.addingTimeInterval(retryAfter)
        }
    }

    /// Pushes a step back without counting an attempt - the register is still being written.
    public func postpone(_ item: LmsFollowUp, until date: Date, reason: String) {
        mutate { items in
            guard let index = items.firstIndex(where: { $0.id == item.id }) else { return }
            items[index].dueAt = date
            items[index].lastError = reason
        }
    }

    public func remove(id: String) {
        mutate { items in items.removeAll { $0.id == id } }
    }

    /// Drops entries nobody will act on: given up and older than a week.
    public func prune(at now: Date) {
        mutate { items in
            items.removeAll { now.timeIntervalSince($0.dueAt) > 7 * 24 * 60 * 60 }
        }
    }

    public static func dashboardDateAndTime(_ date: Date, calendar: Calendar = .current) -> (String, String) {
        let parts = calendar.dateComponents([.year, .month, .day, .hour, .minute], from: date)
        return (
            String(format: "%04d-%02d-%02d", parts.year ?? 0, parts.month ?? 0, parts.day ?? 0),
            String(format: "%02d:%02d", parts.hour ?? 0, parts.minute ?? 0)
        )
    }

    private func mutate(_ change: (inout [LmsFollowUp]) -> Void) {
        lock.lock()
        defer { lock.unlock() }
        var items = load()
        change(&items)
        save(items)
    }

    private func load() -> [LmsFollowUp] {
        guard let data = try? Data(contentsOf: fileURL) else { return [] }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return (try? decoder.decode([LmsFollowUp].self, from: data)) ?? []
    }

    private func save(_ items: [LmsFollowUp]) {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        guard let data = try? encoder.encode(items) else { return }
        try? fileManager.createDirectory(at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? data.write(to: fileURL, options: .atomic)
    }
}
