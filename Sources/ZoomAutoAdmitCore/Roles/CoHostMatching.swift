import Foundation

/// Someone who may be made co-host in a group's meetings: an instructor, a coordinator.
/// People are configured once per group; nobody is picked per meeting.
public struct CoHostCandidate: Codable, Equatable, Hashable, Identifiable {
    public var id: UUID
    public var name: String
    /// Zoom display names already seen for this person.
    public var aliases: [String]

    public init(id: UUID = UUID(), name: String, aliases: [String] = []) {
        self.id = id
        self.name = name
        self.aliases = aliases
    }

    private enum CodingKeys: String, CodingKey { case id, name, aliases }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decodeIfPresent(UUID.self, forKey: .id) ?? UUID()
        name = try container.decode(String.self, forKey: .name)
        aliases = try container.decodeIfPresent([String].self, forKey: .aliases) ?? []
    }
}

public enum CoHostMatchSource: String, Codable, Equatable {
    case configuredName
    case alias
    case previousAssignment
    case nameRule
}

public struct CoHostMatch: Equatable {
    public var candidate: CoHostCandidate
    public var source: CoHostMatchSource
    public var confidence: Int
}

/// A co-host grant that Zoom confirmed, so the same Zoom name matches next time without rules.
public struct CoHostAssignmentRecord: Codable, Equatable {
    public var groupID: UUID
    public var candidateName: String
    public var observedName: String
    public var assignedAt: Date

    public init(groupID: UUID, candidateName: String, observedName: String, assignedAt: Date) {
        self.groupID = groupID
        self.candidateName = candidateName
        self.observedName = observedName
        self.assignedAt = assignedAt
    }
}

/// Recognises a Zoom display name as one of a group's co-host candidates.
///
/// Order: configured name, alias, a previous confirmed assignment, then the configured name
/// shortened or decorated. Only people already on the group's list can ever match.
public enum CoHostMatcher {
    public static func match(
        observedName: String,
        candidates: [CoHostCandidate],
        history: [CoHostAssignmentRecord] = [],
        groupID: UUID? = nil
    ) -> CoHostMatch? {
        let observed = NameNormalizer.normalize(observedName)
        guard !observed.isEmpty else { return nil }

        for candidate in candidates where NameNormalizer.normalize(candidate.name) == observed {
            return CoHostMatch(candidate: candidate, source: .configuredName, confidence: 100)
        }
        for candidate in candidates where candidate.aliases.contains(where: { NameNormalizer.normalize($0) == observed }) {
            return CoHostMatch(candidate: candidate, source: .alias, confidence: 98)
        }
        for entry in history where (groupID == nil || entry.groupID == groupID) && NameNormalizer.normalize(entry.observedName) == observed {
            // A remembered assignment only counts while that person is still on the list.
            if let candidate = candidates.first(where: { NameNormalizer.normalize($0.name) == NameNormalizer.normalize(entry.candidateName) }) {
                return CoHostMatch(candidate: candidate, source: .previousAssignment, confidence: 97)
            }
        }
        return byContainedName(observedName, candidates: candidates)
    }

    /// "Mohab Mohamed" configured:
    ///   "Mohab Mohamed __Coordinator" → yes, the configured name is there in full (two words at least)
    ///   "Mohab"                       → yes, when nobody else on the list could own it
    ///   "Mohab Ahmed"                 → no, "Ahmed" is not part of the configured name
    static func byContainedName(_ observedName: String, candidates: [CoHostCandidate]) -> CoHostMatch? {
        let observed = NameNormalizer.tokens(observedName)
        guard !observed.isEmpty else { return nil }
        var found: [(CoHostCandidate, Int)] = []
        for candidate in candidates {
            let configured = NameNormalizer.tokens(candidate.name)
            guard !configured.isEmpty else { continue }
            if configured.count >= 2, isOrderedSubsequence(configured, of: observed) {
                found.append((candidate, 96))
            } else if isOrderedSubsequence(observed, of: configured) {
                found.append((candidate, observed.count >= 2 ? 94 : 88))
            }
        }
        guard found.count == 1 else { return nil }
        return CoHostMatch(candidate: found[0].0, source: .nameRule, confidence: found[0].1)
    }

    /// Every word of `inner` appears in `outer`, in order: "Mohamed Mohab" is not "Mohab Mohamed".
    static func isOrderedSubsequence(_ inner: [String], of outer: [String]) -> Bool {
        guard !inner.isEmpty, inner.count <= outer.count else { return false }
        var index = 0
        for word in outer where index < inner.count && word == inner[index] {
            index += 1
        }
        return index == inner.count
    }
}

/// Confirmed assignments, remembered across meetings.
public final class CoHostHistoryStore {
    public let fileURL: URL
    private let lock = NSLock()

    public init(fileURL: URL = CoHostHistoryStore.defaultURL) {
        self.fileURL = fileURL
    }

    public static var defaultURL: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Zoom Auto Admit", isDirectory: true)
            .appendingPathComponent("Roles", isDirectory: true)
            .appendingPathComponent("co-host-history.json")
    }

    public func load() -> [CoHostAssignmentRecord] {
        lock.lock()
        defer { lock.unlock() }
        return read()
    }

    public func remember(_ record: CoHostAssignmentRecord) {
        lock.lock()
        defer { lock.unlock() }
        var records = read()
        records.removeAll { $0.groupID == record.groupID && NameNormalizer.normalize($0.observedName) == NameNormalizer.normalize(record.observedName) }
        records.insert(record, at: 0)
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted]
        guard let data = try? encoder.encode(Array(records.prefix(500))) else { return }
        try? FileManager.default.createDirectory(at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? data.write(to: fileURL, options: .atomic)
    }

    private func read() -> [CoHostAssignmentRecord] {
        guard let data = try? Data(contentsOf: fileURL) else { return [] }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return (try? decoder.decode([CoHostAssignmentRecord].self, from: data)) ?? []
    }
}
