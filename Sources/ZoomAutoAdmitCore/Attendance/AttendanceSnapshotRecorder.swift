import Foundation
import ZoomAXSupport

/// Accumulates attendance evidence from periodic snapshots of Zoom's
/// participants list.
///
/// The question this answers is narrow on purpose: *was this Zoom identity
/// observed inside the meeting?* It does not track joining and leaving. With a
/// snapshot every fifteen minutes any such tracking would be invention — a
/// student missing from one snapshot has not necessarily left, and one present
/// in two has not necessarily stayed between them.
///
/// Evidence therefore accumulates as a union: once an identity has been seen,
/// it stays seen. A failed snapshot can never remove anybody.
public final class AttendanceSnapshotRecorder {
    public private(set) var observations: [ParticipantObservation] = []
    public private(set) var snapshots: [AttendanceSnapshot] = []
    /// Snapshots attempted that produced no readable list.
    public private(set) var missedSnapshots = 0

    private var indexByNormalizedName: [String: Int] = [:]
    private let group: StudentGroup

    /// `existingSnapshots` and `missedSnapshots` matter when a register is
    /// picked back up after the app restarted mid-meeting: without them the
    /// recorder would look brand new and re-run the meeting-started snapshot.
    public init(
        group: StudentGroup,
        existing: [ParticipantObservation] = [],
        existingSnapshots: [AttendanceSnapshot] = [],
        missedSnapshots: Int = 0
    ) {
        self.group = group
        self.observations = existing
        self.snapshots = existingSnapshots
        self.missedSnapshots = missedSnapshots
        for (index, observation) in existing.enumerated() {
            indexByNormalizedName[observation.normalizedName] = index
        }
    }

    /// Builds a snapshot from a participants readout and folds it in.
    ///
    /// Returns nil when the list could not be read: that is a missed snapshot,
    /// not an empty meeting.
    @discardableResult
    public func capture(
        _ readout: ZoomAXSupport.ParticipantsReadout,
        reason: SnapshotReason,
        at now: Date = Date()
    ) -> AttendanceSnapshot? {
        guard readout.listAvailable else {
            missedSnapshots += 1
            return nil
        }

        var participants: [SnapshotParticipant] = []
        for row in readout.admitted {
            // The host is running the app, not sitting the class.
            guard !row.isSelfOrHost else { continue }
            guard !group.isIgnored(row.displayName), !AttendanceIgnoreRules.current.matches(row.displayName) else { continue }

            let normalized = NameNormalizer.normalize(row.displayName)
            guard !normalized.isEmpty else { continue }

            participants.append(SnapshotParticipant(
                rawZoomName: row.displayName,
                normalizedZoomName: normalized,
                roles: row.roles.map(\.rawValue).sorted()
            ))
        }

        let snapshot = AttendanceSnapshot(
            capturedAt: now,
            reason: reason,
            reportedCount: readout.reportedCount,
            participants: participants
        )
        merge(snapshot)
        return snapshot
    }

    /// Folds a snapshot into the running union of observed identities.
    public func merge(_ snapshot: AttendanceSnapshot) {
        snapshots.append(snapshot)

        for participant in snapshot.participants {
            guard !participant.normalizedZoomName.isEmpty else { continue }

            if let index = indexByNormalizedName[participant.normalizedZoomName] {
                observations[index].observe(at: snapshot.capturedAt, rawName: participant.rawZoomName)
                if participant.roles.contains("host") || participant.roles.contains("coHost") {
                    observations[index].sawHostRole = true
                }
            } else {
                observations.append(ParticipantObservation(
                    rawName: participant.rawZoomName,
                    normalizedName: participant.normalizedZoomName,
                    observedAt: [snapshot.capturedAt],
                    sawHostRole: participant.roles.contains("host") || participant.roles.contains("coHost")
                ))
                indexByNormalizedName[participant.normalizedZoomName] = observations.count - 1
            }
        }
    }

    /// Identities observed at least once, which is the whole attendance claim.
    public var observedIdentityCount: Int { observations.count }

    public var lastSnapshotAt: Date? { snapshots.map(\.capturedAt).max() }
}
