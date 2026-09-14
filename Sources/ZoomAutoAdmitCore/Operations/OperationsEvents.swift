import Foundation

/// Something that happened to one class, written down as it happens.
///
/// The scheduler, attendance, LMS queue and recording sync each keep their own state, but some
/// outcomes are never persisted by them: a finished LMS step is removed from its queue, a
/// workflow result lives only in the menu for a few seconds. The event log keeps those outcomes
/// so the dashboard can show them and the notification history can list them. It is a record,
/// not a state machine: nothing decides what to do next from it.
public enum OperationsEventKind: String, Codable, Equatable {
    case healthCheck
    case zoomStarting
    case zoomStarted
    case zoomFailed
    case zoomEnded
    case lmsSessionStarted
    case lmsSessionFailed
    case attendanceUploaded
    case attendanceCorrected
    case lmsStepFailed
    case lmsStepGaveUp
    case recordingAttached
    case recordingConflict
    case recordingFailed
    case recordingWaiting
    case general
}

public enum OperationsSeverity: String, Codable, Equatable, Comparable {
    case info
    case success
    case warning
    case failure

    private var rank: Int {
        switch self {
        case .info: return 0
        case .success: return 1
        case .warning: return 2
        case .failure: return 3
        }
    }

    public static func < (lhs: OperationsSeverity, rhs: OperationsSeverity) -> Bool { lhs.rank < rhs.rank }
}

public struct OperationsEvent: Codable, Equatable, Identifiable {
    public var id: UUID
    public var at: Date
    public var kind: OperationsEventKind
    public var severity: OperationsSeverity
    /// The dashboard group code, e.g. CAI5_IND1_G1.
    public var groupCode: String?
    /// yyyy-MM-dd, local time, as the LMS lists sessions.
    public var sessionDate: String?
    public var scheduleID: UUID?
    public var title: String
    public var message: String
    /// Whether a notification was shown for it: nil when it was never meant for one,
    /// false when it was grouped into an earlier notification.
    public var notified: Bool?

    public init(
        id: UUID = UUID(),
        at: Date = Date(),
        kind: OperationsEventKind,
        severity: OperationsSeverity,
        groupCode: String? = nil,
        sessionDate: String? = nil,
        scheduleID: UUID? = nil,
        title: String,
        message: String,
        notified: Bool? = nil
    ) {
        self.id = id
        self.at = at
        self.kind = kind
        self.severity = severity
        self.groupCode = groupCode
        self.sessionDate = sessionDate
        self.scheduleID = scheduleID
        self.title = title
        self.message = message
        self.notified = notified
    }

    /// Group code + date, the same key the recording sync uses for a session.
    public var sessionKey: String? {
        guard let groupCode, let sessionDate else { return nil }
        return OperationsSessionKey.make(groupCode: groupCode, sessionDate: sessionDate)
    }
}

public enum OperationsSessionKey {
    public static func make(groupCode: String, sessionDate: String) -> String {
        RecordingSyncRecord.makeID(groupCode: groupCode, sessionDate: sessionDate)
    }
}

/// `Zoom Auto Admit/Operations/events.jsonl`: one JSON event per line, kept for 30 days.
public final class OperationsEventStore {
    public static let retention: TimeInterval = 30 * 24 * 60 * 60

    public let fileURL: URL
    private let lock = NSLock()

    public init(fileURL: URL = OperationsEventStore.defaultURL) {
        self.fileURL = fileURL
    }

    public static var defaultURL: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Zoom Auto Admit", isDirectory: true)
            .appendingPathComponent("Operations", isDirectory: true)
            .appendingPathComponent("events.jsonl")
    }

    private static let encoder: JSONEncoder = {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.sortedKeys]
        return encoder
    }()

    private static let decoder: JSONDecoder = {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return decoder
    }()

    public func append(_ event: OperationsEvent) {
        guard var line = try? Self.encoder.encode(event) else { return }
        line.append(0x0A)
        lock.lock()
        defer { lock.unlock() }
        try? FileManager.default.createDirectory(at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        if let handle = try? FileHandle(forWritingTo: fileURL) {
            defer { try? handle.close() }
            _ = try? handle.seekToEnd()
            try? handle.write(contentsOf: line)
        } else {
            try? line.write(to: fileURL, options: .atomic)
        }
    }

    /// Events at or after `since`, oldest first. A line that does not decode is skipped.
    public func load(since: Date? = nil) -> [OperationsEvent] {
        lock.lock()
        defer { lock.unlock() }
        return readLocked().filter { since == nil || $0.at >= since! }
    }

    /// Drops events older than the retention period.
    public func prune(now: Date = Date()) {
        lock.lock()
        defer { lock.unlock() }
        let all = readLocked()
        let kept = all.filter { now.timeIntervalSince($0.at) <= Self.retention }
        guard kept.count != all.count else { return }
        var data = Data()
        for event in kept {
            guard let line = try? Self.encoder.encode(event) else { continue }
            data.append(line)
            data.append(0x0A)
        }
        try? data.write(to: fileURL, options: .atomic)
    }

    private func readLocked() -> [OperationsEvent] {
        guard let data = try? Data(contentsOf: fileURL) else { return [] }
        return data.split(separator: 0x0A).compactMap { try? Self.decoder.decode(OperationsEvent.self, from: Data($0)) }
    }
}
