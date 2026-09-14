import Foundation

/// One official student on a group's roster.
///
/// Identity is the `id`, never the array position or the name: names get
/// corrected, and a roster edit tomorrow must not silently rewrite who was
/// present yesterday.
public struct Student: Codable, Equatable, Identifiable, Hashable {
    public var id: UUID
    public var officialName: String
    /// Optional external identifier from the institution's own records.
    public var externalID: String?
    public var email: String?
    /// Names this student has been seen using in Zoom, learned from manual
    /// corrections. Group-scoped by virtue of living on the group's roster.
    public var aliases: [String]

    public init(
        id: UUID = UUID(),
        officialName: String,
        externalID: String? = nil,
        email: String? = nil,
        aliases: [String] = []
    ) {
        self.id = id
        self.officialName = officialName
        self.externalID = externalID
        self.email = email
        self.aliases = aliases
    }

    public var normalizedOfficialName: String {
        NameNormalizer.normalize(officialName)
    }

    private enum CodingKeys: String, CodingKey {
        case id, officialName, externalID, email, aliases
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decode(UUID.self, forKey: .id)
        officialName = try container.decode(String.self, forKey: .officialName)
        externalID = try container.decodeIfPresent(String.self, forKey: .externalID)
        email = try container.decodeIfPresent(String.self, forKey: .email)
        aliases = try container.decodeIfPresent([String].self, forKey: .aliases) ?? []
    }
}

/// A class group: its roster, its learned aliases, and who to ignore.
public struct StudentGroup: Codable, Equatable, Identifiable, Hashable {
    public var id: UUID
    public var name: String
    public var students: [Student]
    /// Names that are never students — the host account, a co-teacher, and so on.
    public var ignoredParticipantNames: [String]
    /// Confidence at or above which an AI match is accepted without review.
    public var autoAcceptConfidence: Double
    /// The group exactly as the DEPI dashboard lists it (e.g. CAI5_AIS4_S7), when the group's
    /// name here is different. Empty means the name is used.
    public var lmsGroupCode: String?
    /// Who may be made co-host in this group's meetings, by the names Zoom shows for them.
    public var coHostCandidates: [CoHostCandidate]

    public init(
        id: UUID = UUID(),
        name: String,
        students: [Student] = [],
        ignoredParticipantNames: [String] = [],
        autoAcceptConfidence: Double = 0.90,
        lmsGroupCode: String? = nil,
        coHostCandidates: [CoHostCandidate] = []
    ) {
        self.id = id
        self.name = name
        self.students = students
        self.ignoredParticipantNames = ignoredParticipantNames
        self.autoAcceptConfidence = autoAcceptConfidence
        self.lmsGroupCode = lmsGroupCode
        self.coHostCandidates = coHostCandidates
    }

    public func isIgnored(_ displayName: String) -> Bool {
        let candidate = NameNormalizer.normalize(displayName)
        return ignoredParticipantNames.contains { NameNormalizer.normalize($0) == candidate }
    }

    private enum CodingKeys: String, CodingKey {
        case id, name, students, ignoredParticipantNames, autoAcceptConfidence, lmsGroupCode, coHostCandidates
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decode(UUID.self, forKey: .id)
        name = try container.decode(String.self, forKey: .name)
        students = try container.decodeIfPresent([Student].self, forKey: .students) ?? []
        ignoredParticipantNames =
            try container.decodeIfPresent([String].self, forKey: .ignoredParticipantNames) ?? []
        autoAcceptConfidence =
            try container.decodeIfPresent(Double.self, forKey: .autoAcceptConfidence) ?? 0.90
        lmsGroupCode = try container.decodeIfPresent(String.self, forKey: .lmsGroupCode)
        coHostCandidates = try container.decodeIfPresent([CoHostCandidate].self, forKey: .coHostCandidates) ?? []
    }
}

/// One continuous stretch during which a name was visible in the list.
/// Where a session's attendance evidence came from.
///
/// Accessibility snapshots are the automatic fallback. A stronger source — an
/// official Zoom attendance report — can be imported later and supersede the
/// timing evidence without the snapshot system going away.
public enum AttendanceEvidenceSource: String, Codable, Equatable {
    case accessibilitySnapshots
    case zoomReport
    case manual
}

/// Why a snapshot was taken.
public enum SnapshotReason: String, Codable, Equatable {
    case meetingStarted = "meeting_started"
    case periodic
    case postAdmit = "post_admit"
    case final
    case manual

    public var displayName: String {
        switch self {
        case .meetingStarted: return "Meeting started"
        case .periodic: return "Periodic"
        case .postAdmit: return "After admitting"
        case .final: return "Final"
        case .manual: return "Manual"
        }
    }
}

/// One admitted participant as seen in a single snapshot.
public struct SnapshotParticipant: Codable, Equatable, Hashable {
    public var rawZoomName: String
    public var normalizedZoomName: String
    public var roles: [String]

    public init(rawZoomName: String, normalizedZoomName: String, roles: [String] = []) {
        self.rawZoomName = rawZoomName
        self.normalizedZoomName = normalizedZoomName
        self.roles = roles
    }
}

/// "These identities were visible as admitted PANELIST participants at this
/// moment." Nothing more is claimed.
public struct AttendanceSnapshot: Codable, Equatable, Identifiable, Hashable {
    public var id: UUID
    public var capturedAt: Date
    public var reason: SnapshotReason
    /// The count Zoom itself displayed, kept as corroboration.
    public var reportedCount: Int?
    public var participants: [SnapshotParticipant]

    public init(
        id: UUID = UUID(),
        capturedAt: Date,
        reason: SnapshotReason,
        reportedCount: Int? = nil,
        participants: [SnapshotParticipant] = []
    ) {
        self.id = id
        self.capturedAt = capturedAt
        self.reason = reason
        self.reportedCount = reportedCount
        self.participants = participants
    }
}

/// Everything observed about one Zoom display name during a session.
///
/// Keyed by normalized name because Zoom exposes no per-participant identifier.
///
/// The timing here is deliberately weak. Periodic sampling can say a name was
/// *seen at these moments*; it cannot honestly say when somebody joined, when
/// they left, or how long they stayed. Earlier versions of this type modelled
/// join/leave intervals and a running duration, which read as precise facts
/// while being sampling artefacts — a student seen at 18:00 and 19:45 was
/// credited with 105 minutes they may not have been there for. Those fields are
/// gone; what remains is only what a snapshot can actually support.
public struct ParticipantObservation: Codable, Equatable, Identifiable, Hashable {
    public var id: UUID
    public var rawName: String
    public var normalizedName: String
    /// Every moment this identity was seen. The count of these is the evidence.
    public var observedAt: [Date]
    /// Other spellings seen for this identity within this meeting.
    public var observedAliases: [String]
    public var sawHostRole: Bool

    public init(
        id: UUID = UUID(),
        rawName: String,
        normalizedName: String,
        observedAt: [Date] = [],
        observedAliases: [String] = [],
        sawHostRole: Bool = false
    ) {
        self.id = id
        self.rawName = rawName
        self.normalizedName = normalizedName
        self.observedAt = observedAt.sorted()
        self.observedAliases = observedAliases
        self.sawHostRole = sawHostRole
    }

    /// First moment this identity was seen — not a join time.
    public var firstObservedAt: Date? { observedAt.first }
    /// Last moment this identity was seen — not a leave time.
    public var lastObservedAt: Date? { observedAt.last }
    /// How many snapshots contained this identity.
    public var observationCount: Int { observedAt.count }

    public mutating func observe(at moment: Date, rawName: String) {
        if !observedAt.contains(moment) {
            observedAt.append(moment)
            observedAt.sort()
        }
        if rawName != self.rawName, !observedAliases.contains(rawName) {
            observedAliases.append(rawName)
        }
    }

    private enum CodingKeys: String, CodingKey {
        case id, rawName, normalizedName, observedAt, observedAliases, sawHostRole
        // Legacy keys, so sessions written by the interval-based version load.
        case firstSeen, lastSeen
    }

    // Written explicitly because the legacy keys have no stored properties.
    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(id, forKey: .id)
        try container.encode(rawName, forKey: .rawName)
        try container.encode(normalizedName, forKey: .normalizedName)
        try container.encode(observedAt, forKey: .observedAt)
        try container.encode(observedAliases, forKey: .observedAliases)
        try container.encode(sawHostRole, forKey: .sawHostRole)
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decode(UUID.self, forKey: .id)
        rawName = try container.decode(String.self, forKey: .rawName)
        normalizedName = try container.decode(String.self, forKey: .normalizedName)
        observedAliases = try container.decodeIfPresent([String].self, forKey: .observedAliases) ?? []
        sawHostRole = try container.decodeIfPresent(Bool.self, forKey: .sawHostRole) ?? false

        if let moments = try container.decodeIfPresent([Date].self, forKey: .observedAt) {
            observedAt = moments.sorted()
        } else {
            // An older record knew only when it was first and last seen; keep
            // exactly that much rather than inventing intermediate sightings.
            let first = try container.decodeIfPresent(Date.self, forKey: .firstSeen)
            let last = try container.decodeIfPresent(Date.self, forKey: .lastSeen)
            observedAt = [first, last].compactMap { $0 }.reduce(into: [Date]()) { result, moment in
                if !result.contains(moment) { result.append(moment) }
            }.sorted()
        }
    }
}

public enum AttendanceStatus: String, Codable, Equatable {
    case present
    case absent
    case needsReview
    /// Before finalization, a student who has not been seen is not yet absent.
    case notSeenYet
}

/// Why the app believes someone was present. Kept for auditing: an attendance
/// register that cannot explain itself is not worth much.
public enum MatchSource: String, Codable, Equatable {
    case exact
    case alias
    case token
    case fuzzy
    case ai
    case manual
    case none
}

public struct AttendanceRecord: Codable, Equatable, Identifiable, Hashable {
    public var id: UUID
    public var studentID: UUID
    public var studentName: String
    public var status: AttendanceStatus
    public var matchedObservationID: UUID?
    public var matchedZoomName: String?
    public var matchSource: MatchSource
    public var confidence: Double?
    public var reason: String?
    /// Manual decisions are never overwritten by a later automatic pass.
    public var isManual: Bool

    public init(
        id: UUID = UUID(),
        studentID: UUID,
        studentName: String,
        status: AttendanceStatus,
        matchedObservationID: UUID? = nil,
        matchedZoomName: String? = nil,
        matchSource: MatchSource = .none,
        confidence: Double? = nil,
        reason: String? = nil,
        isManual: Bool = false
    ) {
        self.id = id
        self.studentID = studentID
        self.studentName = studentName
        self.status = status
        self.matchedObservationID = matchedObservationID
        self.matchedZoomName = matchedZoomName
        self.matchSource = matchSource
        self.confidence = confidence
        self.reason = reason
        self.isManual = isManual
    }
}

/// One meeting occurrence's attendance.
///
/// The roster is snapshotted into the session at creation so that editing the
/// group tomorrow cannot change what yesterday's register says.
public struct AttendanceSession: Codable, Equatable, Identifiable {
    public var id: UUID
    public var groupID: UUID
    public var groupName: String
    public var scheduleID: UUID?
    public var meetingName: String
    public var startedAt: Date
    public var endedAt: Date?
    public var finalizedAt: Date?
    /// Immutable copy of the roster as it stood when the meeting began.
    public var rosterSnapshot: [Student]
    public var observations: [ParticipantObservation]
    public var records: [AttendanceRecord]
    /// Zoom names that matched nobody.
    public var unmatchedZoomNames: [String]
    /// Every snapshot taken, so a historical session stays reproducible.
    public var snapshots: [AttendanceSnapshot]
    /// Snapshots that were attempted and failed. Recorded so a thin register can
    /// be explained rather than mistaken for a poorly attended class.
    public var missedSnapshotCount: Int
    public var evidenceSource: AttendanceEvidenceSource

    public init(
        id: UUID = UUID(),
        groupID: UUID,
        groupName: String,
        scheduleID: UUID? = nil,
        meetingName: String,
        startedAt: Date,
        endedAt: Date? = nil,
        finalizedAt: Date? = nil,
        rosterSnapshot: [Student],
        observations: [ParticipantObservation] = [],
        records: [AttendanceRecord] = [],
        unmatchedZoomNames: [String] = [],
        snapshots: [AttendanceSnapshot] = [],
        missedSnapshotCount: Int = 0,
        evidenceSource: AttendanceEvidenceSource = .accessibilitySnapshots
    ) {
        self.id = id
        self.groupID = groupID
        self.groupName = groupName
        self.scheduleID = scheduleID
        self.meetingName = meetingName
        self.startedAt = startedAt
        self.endedAt = endedAt
        self.finalizedAt = finalizedAt
        self.rosterSnapshot = rosterSnapshot
        self.observations = observations
        self.records = records
        self.unmatchedZoomNames = unmatchedZoomNames
        self.snapshots = snapshots
        self.missedSnapshotCount = missedSnapshotCount
        self.evidenceSource = evidenceSource
    }

    private enum CodingKeys: String, CodingKey {
        case id, groupID, groupName, scheduleID, meetingName, startedAt, endedAt, finalizedAt
        case rosterSnapshot, observations, records, unmatchedZoomNames
        case snapshots, missedSnapshotCount, evidenceSource
    }

    // Sessions written before snapshots existed must keep loading.
    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decode(UUID.self, forKey: .id)
        groupID = try container.decode(UUID.self, forKey: .groupID)
        groupName = try container.decode(String.self, forKey: .groupName)
        scheduleID = try container.decodeIfPresent(UUID.self, forKey: .scheduleID)
        meetingName = try container.decode(String.self, forKey: .meetingName)
        startedAt = try container.decode(Date.self, forKey: .startedAt)
        endedAt = try container.decodeIfPresent(Date.self, forKey: .endedAt)
        finalizedAt = try container.decodeIfPresent(Date.self, forKey: .finalizedAt)
        rosterSnapshot = try container.decodeIfPresent([Student].self, forKey: .rosterSnapshot) ?? []
        observations = try container.decodeIfPresent([ParticipantObservation].self, forKey: .observations) ?? []
        records = try container.decodeIfPresent([AttendanceRecord].self, forKey: .records) ?? []
        unmatchedZoomNames = try container.decodeIfPresent([String].self, forKey: .unmatchedZoomNames) ?? []
        snapshots = try container.decodeIfPresent([AttendanceSnapshot].self, forKey: .snapshots) ?? []
        missedSnapshotCount = try container.decodeIfPresent(Int.self, forKey: .missedSnapshotCount) ?? 0
        evidenceSource = try container.decodeIfPresent(
            AttendanceEvidenceSource.self,
            forKey: .evidenceSource
        ) ?? .accessibilitySnapshots
    }

    public var lastSnapshotAt: Date? { snapshots.map(\.capturedAt).max() }

    public var isFinalized: Bool { finalizedAt != nil }

    public var presentCount: Int { records.filter { $0.status == .present }.count }
    public var absentCount: Int { records.filter { $0.status == .absent }.count }
    public var needsReviewCount: Int { records.filter { $0.status == .needsReview }.count }

    /// The register in roster order — the order the group was entered in,
    /// which is the order the register has to be read back in.
    ///
    /// `records` is sorted by name so the review list reads alphabetically, but
    /// transcribing attendance onto an external platform means walking the
    /// official list top to bottom. Re-sorting by name at that moment turns a
    /// copying job into a searching job, once per student.
    ///
    /// A record whose student is no longer on the snapshot keeps its place at
    /// the end rather than being dropped: an unexpected row is still evidence.
    public var recordsInRosterOrder: [AttendanceRecord] {
        var byStudent: [UUID: AttendanceRecord] = [:]
        for record in records { byStudent[record.studentID] = record }

        var ordered = rosterSnapshot.compactMap { byStudent[$0.id] }
        let placed = Set(ordered.map(\.studentID))
        ordered.append(contentsOf: records.filter { !placed.contains($0.studentID) })
        return ordered
    }

    public func observation(withID id: UUID) -> ParticipantObservation? {
        observations.first { $0.id == id }
    }
}
