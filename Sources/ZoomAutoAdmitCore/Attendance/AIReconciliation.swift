import Foundation

/// Applies AI proposals on top of a locally reconciled register.
///
/// The AI layer only ever sees what local matching could not settle, and only
/// ever *proposes*. Everything it returns is validated first, and a proposal
/// below the group's threshold becomes Needs Review rather than Present — a
/// false Needs Review costs a glance, a false Present costs a wrong register.
public enum AIReconciliation {
    public struct Summary: Equatable {
        public let session: AttendanceSession
        public let appliedCount: Int
        public let reviewCount: Int
        public let unmatchedObservedNameCount: Int
        public let rejected: [String]
        public let failure: AIMatchError?

        public var aiWasUnavailable: Bool { failure != nil }
    }

    /// Builds the request from a locally reconciled session.
    ///
    /// Only students with no decision yet and Zoom names nobody claimed are
    /// included; anything already resolved stays local and costs nothing.
    public static func request(
        for session: AttendanceSession,
        ignoring ignoreRules: AttendanceIgnoreRules = .current,
        includePresentStudents: Bool = true
    ) -> (request: AIMatchRequest, ids: AIMatchRequestIDs) {
        // Belt and braces: an ignored name never reaches OpenRouter, even from a session that
        // was not reconciled since the name was added to the list.
        let rules = AttendanceIgnoring.rules(for: session, global: ignoreRules)
        // Derive unresolved students from the session roster itself, not from
        // fuzzy candidate generation. This also behaves correctly for a session
        // whose records have not yet been reconciled.
        let resolvedStudentIDs = Set(
            session.records
                .filter { $0.isManual || $0.status == .present }
                .map(\.studentID)
        )
        let unresolved = session.rosterSnapshot.filter { !resolvedStudentIDs.contains($0.id) }
        // Students already present go too, marked, so a second Zoom name of theirs is recognised
        // as them instead of being forced onto somebody still missing.
        let present = Set(session.records.filter { $0.status == .present }.map(\.studentID))
        // The automatic run at finalize sends only unresolved students, never matched ones.
        let alreadyPresent = includePresentStudents ? session.rosterSnapshot.filter { present.contains($0.id) } : []

        let claimed = Set(
            session.records
                .filter { $0.isManual || $0.status == .present }
                .flatMap(\.claimedObservationIDs)
        )
        let freeObservations = session.observations.filter { !claimed.contains($0.id) && !rules.matches($0.rawName) }

        var studentIDs: [String: UUID] = [:]
        var candidates: [AIMatchRequest.Candidate] = []
        for (index, student) in (unresolved + alreadyPresent).enumerated() {
            // Opaque per-request ids: the model never receives a real UUID and
            // cannot reference anything outside this request.
            let key = "s\(index)"
            studentIDs[key] = student.id
            candidates.append(.init(id: key, officialName: student.officialName, alreadyPresent: present.contains(student.id)))
        }
        if freeObservationsAreEmpty(session: session, claimed: claimed, rules: rules) {
            candidates = []
        }

        var observationIDs: [String: UUID] = [:]
        var observedNames: [AIMatchRequest.ObservedName] = []
        for (index, observation) in freeObservations.enumerated() {
            let key = "z\(index)"
            observationIDs[key] = observation.id
            observedNames.append(.init(id: key, displayName: observation.rawName))
        }

        return (
            AIMatchRequest(students: candidates, observedNames: observedNames),
            AIMatchRequestIDs(students: studentIDs, observations: observationIDs)
        )
    }

    /// Nothing to ask about: every observation is already claimed or ignored.
    private static func freeObservationsAreEmpty(session: AttendanceSession, claimed: Set<UUID>, rules: AttendanceIgnoreRules) -> Bool {
        session.observations.allSatisfy { claimed.contains($0.id) || rules.matches($0.rawName) }
    }

    /// Folds validated proposals into the session.
    public static func apply(
        _ response: AIMatchResponse,
        to session: AttendanceSession,
        ids: AIMatchRequestIDs,
        autoAcceptConfidence: Double
    ) -> Summary {
        let validated = AIMatchValidator.validate(
            response,
            students: session.rosterSnapshot,
            observations: session.observations,
            requestIDs: ids
        )

        var updated = session
        var applied = 0
        var review = 0
        var pairedObservations = Set<UUID>()

        var assignedThisReply = Set<UUID>()
        for match in validated.matches {
            guard let index = updated.records.firstIndex(where: { $0.studentID == match.studentID }) else {
                continue
            }
            let confident = !match.needsReview && match.confidence >= autoAcceptConfidence
            // A second Zoom name of a student who is already present (or was just placed by this
            // reply): it joins that student's identities. Never a status change, never a manual
            // decision overwritten - an extra name only adds evidence.
            if updated.records[index].status == .present || assignedThisReply.contains(match.studentID) {
                guard confident, let observation = updated.observation(withID: match.observationID) else { continue }
                updated.records[index].addIdentity(observation)
                pairedObservations.insert(match.observationID)
                applied += 1
                continue
            }
            // A human decision is never overwritten by the model.
            guard !updated.records[index].isManual else { continue }
            // Nor is a student the local matcher already settled confidently.
            guard updated.records[index].status != .present
                || updated.records[index].matchSource == .ai else { continue }

            assignedThisReply.insert(match.studentID)
            updated.records[index].status = confident ? .present : .needsReview
            updated.records[index].matchedObservationID = match.observationID
            updated.records[index].matchedZoomName = match.zoomName
            updated.records[index].matchSource = .ai
            updated.records[index].confidence = match.confidence
            updated.records[index].reason = match.reason
            pairedObservations.insert(match.observationID)

            if confident { applied += 1 } else { review += 1 }
        }

        let claimed = Set(
            updated.records
                .filter { $0.status == .present }
                .flatMap(\.claimedObservationIDs)
        )
        updated.unmatchedZoomNames = updated.observations
            .filter { !claimed.contains($0.id) }
            .map(\.rawName)

        return Summary(
            session: updated,
            appliedCount: applied,
            reviewCount: review,
            unmatchedObservedNameCount: Set(ids.observations.values)
                .subtracting(pairedObservations)
                .count,
            rejected: validated.rejected,
            failure: nil
        )
    }

    /// Result when AI could not be reached: local results stand untouched.
    public static func unavailable(_ error: AIMatchError, session: AttendanceSession) -> Summary {
        Summary(
            session: session,
            appliedCount: 0,
            reviewCount: 0,
            unmatchedObservedNameCount: session.unmatchedZoomNames.count,
            rejected: [],
            failure: error
        )
    }
}
