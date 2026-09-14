import Foundation

/// Someone who shows up in Zoom but is never a student: a trainer, a coordinator, an admin.
public struct IgnoredParticipant: Codable, Equatable, Hashable {
    public enum Source: String, Codable, Equatable {
        case manual
        case imported
        case detected
    }

    public var name: String
    public var addedAt: Date
    public var source: Source

    public init(name: String, addedAt: Date = Date(), source: Source = .manual) {
        self.name = name.trimmingCharacters(in: .whitespacesAndNewlines)
        self.addedAt = addedAt
        self.source = source
    }
}

/// The names attendance must not see, compared the way the matcher compares names.
public struct AttendanceIgnoreRules: Equatable {
    public private(set) var normalizedNames: Set<String>

    public init<S: Sequence>(names: S) where S.Element == String {
        normalizedNames = Set(names.map(Self.canonical).filter { !$0.isEmpty })
    }

    /// The person's name out of whatever Zoom showed. Older registers kept the whole row text -
    /// "eyouth coordinator, (Host, me), participant ID: 248703" - whose ID changes every meeting,
    /// so role tags and participant IDs are dropped before comparing.
    public static func canonical(_ raw: String) -> String {
        var value = raw.lowercased()
        value = value.replacingOccurrences(of: "participant\\s*id\\s*:?\\s*\\d+", with: " ", options: .regularExpression)
        value = value.replacingOccurrences(
            of: "\\(\\s*(host|co-host|cohost|me|you|guest)(\\s*,\\s*(host|co-host|cohost|me|you|guest))*\\s*\\)",
            with: " ",
            options: .regularExpression
        )
        return NameNormalizer.normalize(value)
    }

    public static let none = AttendanceIgnoreRules(names: [String]())

    public var isEmpty: Bool { normalizedNames.isEmpty }

    public func matches(_ rawName: String) -> Bool {
        guard !normalizedNames.isEmpty else { return false }
        return normalizedNames.contains(Self.canonical(rawName))
    }

    public func adding<S: Sequence>(_ names: S) -> AttendanceIgnoreRules where S.Element == String {
        var copy = self
        copy.normalizedNames.formUnion(names.map(Self.canonical).filter { !$0.isEmpty })
        return copy
    }

    /// The global list, set by the app at launch. Core code reads it through here so every
    /// attendance path applies the same list without each caller having to remember it.
    public static var currentProvider: () -> AttendanceIgnoreRules = { .none }
    public static var current: AttendanceIgnoreRules { currentProvider() }
}

/// The global ignore list on disk: `Zoom Auto Admit/ignored-participants.json`.
///
/// Kept beside, not inside, the Attendance folder, which holds one file per session and is
/// read back as sessions.
public final class AttendanceIgnoreStore {
    public let fileURL: URL
    private let lock = NSLock()
    private var cached: [IgnoredParticipant]?
    public var onChange: (() -> Void)?

    public init(fileURL: URL = AttendanceIgnoreStore.defaultURL) {
        self.fileURL = fileURL
    }

    public static var defaultURL: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Zoom Auto Admit", isDirectory: true)
            .appendingPathComponent("ignored-participants.json")
    }

    public var participants: [IgnoredParticipant] {
        lock.lock()
        defer { lock.unlock() }
        return loadLocked()
    }

    public var rules: AttendanceIgnoreRules {
        AttendanceIgnoreRules(names: participants.map(\.name))
    }

    /// Adds names not already on the list (compared normalized). Returns how many were new.
    @discardableResult
    public func add(_ names: [String], source: IgnoredParticipant.Source = .manual, at now: Date = Date()) -> Int {
        lock.lock()
        var list = loadLocked()
        var known = Set(list.map { AttendanceIgnoreRules.canonical($0.name) })
        var added = 0
        for name in names {
            let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
            let key = AttendanceIgnoreRules.canonical(trimmed)
            guard !key.isEmpty, !known.contains(key) else { continue }
            known.insert(key)
            list.append(IgnoredParticipant(name: trimmed, addedAt: now, source: source))
            added += 1
        }
        if added > 0 { saveLocked(list) }
        lock.unlock()
        if added > 0 { onChange?() }
        return added
    }

    @discardableResult
    public func remove(_ names: [String]) -> Int {
        let keys = Set(names.map(AttendanceIgnoreRules.canonical))
        lock.lock()
        var list = loadLocked()
        let before = list.count
        list.removeAll { keys.contains(AttendanceIgnoreRules.canonical($0.name)) }
        let removed = before - list.count
        if removed > 0 { saveLocked(list) }
        lock.unlock()
        if removed > 0 { onChange?() }
        return removed
    }

    /// One name per line; a CSV's first column is taken; blank lines and a "name" header skipped.
    public static func parseImport(_ text: String) -> [String] {
        text.components(separatedBy: .newlines).compactMap { line in
            let first = line.split(separator: ",", maxSplits: 1, omittingEmptySubsequences: false).first.map(String.init) ?? ""
            let name = first.trimmingCharacters(in: CharacterSet.whitespaces.union(CharacterSet(charactersIn: "\"")))
            guard !name.isEmpty, !["name", "ignored participant", "ignored participants"].contains(name.lowercased()) else { return nil }
            return name
        }
    }

    public func exportText() -> String {
        participants.map(\.name).joined(separator: "\n") + "\n"
    }

    private func loadLocked() -> [IgnoredParticipant] {
        if let cached { return cached }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        let list = (try? Data(contentsOf: fileURL)).flatMap { try? decoder.decode([IgnoredParticipant].self, from: $0) } ?? []
        cached = list
        return list
    }

    private func saveLocked(_ list: [IgnoredParticipant]) {
        cached = list
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        guard let data = try? encoder.encode(list) else { return }
        try? FileManager.default.createDirectory(at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? data.write(to: fileURL, options: .atomic)
    }
}

/// Keeps ignored people out of a register.
///
/// Their observations are moved aside into `ignoredObservations` rather than deleted: every
/// consumer of `observations` - matching, AI, the review list, unmatched names, the LMS upload -
/// then simply never sees them, and taking a name off the list brings its evidence back.
public enum AttendanceIgnoring {
    public static func rules(for session: AttendanceSession, global: AttendanceIgnoreRules) -> AttendanceIgnoreRules {
        global.adding(session.meetingIgnoredNames)
    }

    public static func apply(_ global: AttendanceIgnoreRules, to session: AttendanceSession) -> AttendanceSession {
        let rules = rules(for: session, global: global)
        var updated = session

        // A live recorder hands back every observation on each persist, ignored ones included, so
        // the two lists overlap; one entry per observation, the fresher `observations` copy winning.
        var seen = Set<UUID>()
        let all = (session.observations + session.ignoredObservations).filter { seen.insert($0.id).inserted }
        updated.observations = all.filter { !rules.matches($0.rawName) }
        updated.ignoredObservations = all.filter { rules.matches($0.rawName) }
        let ignoredIDs = Set(updated.ignoredObservations.map(\.id))

        if !ignoredIDs.isEmpty {
            for index in updated.records.indices {
                guard let observationID = updated.records[index].matchedObservationID, ignoredIDs.contains(observationID) else { continue }
                // An ignored person is never evidence for a student, manual or not.
                updated.records[index].matchedObservationID = nil
                updated.records[index].matchedZoomName = nil
                updated.records[index].confidence = nil
                updated.records[index].matchSource = .none
                updated.records[index].isManual = false
                updated.records[index].reason = "Matched name is on the ignore list"
                updated.records[index].status = updated.isFinalized ? .absent : .notSeenYet
            }
        }
        updated.unmatchedZoomNames = updated.unmatchedZoomNames.filter { !rules.matches($0) }
        return updated
    }
}

/// A Zoom name in the meeting that is not on the roster and does not look like a student.
public struct UnknownParticipant: Equatable {
    public enum Reason: Equatable {
        case staffWord(String)
        case hostRole
        case recurring(sessions: Int)

        public var description: String {
            switch self {
            case .staffWord(let word): return "name contains “\(word)”"
            case .hostRole: return "has a host or co-host role"
            case .recurring(let count): return "unmatched in \(count) earlier meeting(s)"
            }
        }
    }

    public var name: String
    public var reasons: [Reason]
}

/// Finds the people worth asking about on a meeting's first snapshot.
///
/// Only names nothing on the roster claims - not Present, not even Needs Review - are
/// considered, and only those that look like staff or keep turning up unmatched. A single
/// unknown student joining once is left to the normal review.
public enum UnknownParticipantDetector {
    public static let staffWords: [String] = [
        "trainer", "instructor", "coordinator", "coordinater", "admin", "administrator", "moderator",
        "mentor", "facilitator", "supervisor", "manager", "organizer", "support", "host", "cohost",
        "eyouth", "depi", "teacher", "lecturer", "staff",
        "مدرب", "منسق", "مشرف", "ادمن", "اداره", "مدير", "مضيف"
    ]

    public static func detect(
        session: AttendanceSession,
        history: [AttendanceSession],
        minimumRecurrence: Int = 2
    ) -> [UnknownParticipant] {
        let claimed = Set(session.records.filter { $0.status == .present || $0.status == .needsReview }.compactMap(\.matchedObservationID))
        // Name parts worth comparing: "dr", "mr" and initials say nothing about who someone is.
        let rosterParts = Set(session.rosterSnapshot.flatMap { NameNormalizer.tokens($0.officialName) }.filter { $0.count >= 3 && !["prof", "eng"].contains($0) })
        let earlier = history.filter { $0.groupID == session.groupID && $0.id != session.id && $0.startedAt < session.startedAt }

        return session.observations
            .filter { !claimed.contains($0.id) }
            .compactMap { observation in
                var reasons: [UnknownParticipant.Reason] = []
                let tokens = Set(NameNormalizer.tokens(observation.rawName))
                if let word = staffWords.first(where: { tokens.contains(NameNormalizer.normalize($0)) }) {
                    reasons.append(.staffWord(word))
                }
                if observation.sawHostRole { reasons.append(.hostRole) }
                let key = AttendanceIgnoreRules.canonical(observation.rawName)
                let recurring = earlier.filter { past in
                    past.unmatchedZoomNames.contains { AttendanceIgnoreRules.canonical($0) == key }
                }.count
                // A recurring name that shares a name part with the roster is far more likely a
                // student under another spelling ("Amal Abdelrazek" for "Amal Abdelrazik ...");
                // that belongs in the normal review, where adding them as a new student is not offered.
                let resemblesStudent = !tokens.isDisjoint(with: rosterParts)
                if recurring >= minimumRecurrence, reasons.isEmpty ? !resemblesStudent : true {
                    reasons.append(.recurring(sessions: recurring))
                }
                return reasons.isEmpty ? nil : UnknownParticipant(name: observation.rawName, reasons: reasons)
            }
    }
}
