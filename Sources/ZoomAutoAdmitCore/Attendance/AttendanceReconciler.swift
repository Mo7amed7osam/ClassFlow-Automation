import Foundation

/// Turns raw observations into an attendance register.
///
/// Three rules govern everything here:
///
/// * A student is only ever `present` when a real observation backs it. Nothing
///   — not fuzzy scoring, not AI — can conjure attendance without an observed
///   Zoom identity.
/// * Manual decisions are never overwritten by a later automatic pass.
/// * Nobody is `absent` until the session is finalized; before that an unseen
///   student is `notSeenYet`, because students join late.
public enum AttendanceReconciler {
    /// Recomputes the register from the current observations.
    ///
    /// Safe to call repeatedly during a meeting: existing manual records are
    /// carried through untouched.
    public static func reconcile(
        session: AttendanceSession,
        autoAcceptConfidence: Double,
        finalizing: Bool = false,
        at now: Date = Date(),
        ignoring ignoreRules: AttendanceIgnoreRules = .current
    ) -> AttendanceSession {
        // Ignored people leave the register before anything is matched.
        let session = AttendanceIgnoring.apply(ignoreRules, to: session)
        var updated = session

        let manualRecords = session.records.filter(\.isManual)
        let manuallyUsedObservations = Set(manualRecords.flatMap(\.claimedObservationIDs))
        let manuallyDecidedStudents = Set(manualRecords.map(\.studentID))

        // Manual decisions remove both the student and their observation from
        // consideration, so automatic matching cannot contradict them.
        let students = session.rosterSnapshot.filter { !manuallyDecidedStudents.contains($0.id) }
        let observations = session.observations.filter { !manuallyUsedObservations.contains($0.id) }

        let outcome = DeterministicMatcher.match(
            students: students,
            observations: observations,
            autoAcceptConfidence: autoAcceptConfidence
        )

        var records = manualRecords
        var consumedObservations = manuallyUsedObservations

        for candidate in outcome.accepted {
            guard let student = session.rosterSnapshot.first(where: { $0.id == candidate.studentID }),
                  let observation = session.observation(withID: candidate.observationID) else {
                continue
            }
            consumedObservations.insert(observation.id)
            records.append(AttendanceRecord(
                studentID: student.id,
                studentName: student.officialName,
                status: .present,
                matchedObservationID: observation.id,
                matchedZoomName: observation.rawName,
                matchSource: candidate.source,
                confidence: candidate.score,
                reason: candidate.reason
            ))
        }

        // A student who joined twice: the extra identity backs the same record.
        for candidate in outcome.duplicates {
            guard let observation = session.observation(withID: candidate.observationID),
                  let index = records.firstIndex(where: { $0.studentID == candidate.studentID && $0.status == .present }) else {
                continue
            }
            consumedObservations.insert(observation.id)
            records[index].addIdentity(observation)
            let note = "also joined as “\(observation.rawName)”"
            records[index].reason = [records[index].reason, note].compactMap { $0 }.joined(separator: "; ")
        }

        // Students decided by hand are outside automatic matching, but a clear further Zoom name of
        // one who is present is still the same person joining again.
        let reviewObservations = Set(outcome.review.map(\.observationID))
        for observation in observations where !consumedObservations.contains(observation.id) && !reviewObservations.contains(observation.id) {
            let manualPresent = records.indices.filter { records[$0].isManual && records[$0].status == .present }
            let scored = manualPresent.compactMap { index -> (Int, Double)? in
                guard let student = session.rosterSnapshot.first(where: { $0.id == records[index].studentID }),
                      let pairing = DeterministicMatcher.score(rawObservedName: observation.rawName, student: student) else { return nil }
                return (index, pairing.score)
            }
            guard let best = scored.max(by: { $0.1 < $1.1 }), best.1 >= DeterministicMatcher.duplicateFloor else { continue }
            let contested = students.contains { student in
                !records.contains { $0.studentID == student.id }
                    && (DeterministicMatcher.score(rawObservedName: observation.rawName, student: student)?.score ?? 0) >= best.1 - 0.05
            }
            guard !contested else { continue }
            consumedObservations.insert(observation.id)
            records[best.0].addIdentity(observation)
        }

        for candidate in outcome.review {
            guard let student = session.rosterSnapshot.first(where: { $0.id == candidate.studentID }),
                  let observation = session.observation(withID: candidate.observationID) else {
                continue
            }
            // A review candidate does not consume the observation: it may yet
            // belong to somebody else.
            records.append(AttendanceRecord(
                studentID: student.id,
                studentName: student.officialName,
                status: .needsReview,
                matchedObservationID: observation.id,
                matchedZoomName: observation.rawName,
                matchSource: candidate.source,
                confidence: candidate.score,
                reason: candidate.reason
            ))
        }

        // Everyone still unaccounted for.
        let decided = Set(records.map(\.studentID))
        for student in session.rosterSnapshot where !decided.contains(student.id) {
            records.append(AttendanceRecord(
                studentID: student.id,
                studentName: student.officialName,
                status: finalizing ? .absent : .notSeenYet,
                matchSource: .none
            ))
        }

        // Finalizing converts anything still unseen into a decision.
        if finalizing {
            for index in records.indices where records[index].status == .notSeenYet {
                records[index].status = .absent
            }
        }

        updated.records = records.sorted { $0.studentName.localizedCompare($1.studentName) == .orderedAscending }
        updated.unmatchedZoomNames = session.observations
            .filter { !consumedObservations.contains($0.id) }
            .filter { observation in
                !records.contains { $0.claimedObservationIDs.contains(observation.id) && $0.status == .present }
            }
            .map(\.rawName)

        if finalizing {
            updated.finalizedAt = now
            if updated.endedAt == nil { updated.endedAt = now }
        }
        return updated
    }

    /// Applies a human decision, which outranks everything automatic.
    public static func applyManualMatch(
        session: AttendanceSession,
        studentID: UUID,
        observationID: UUID?,
        status: AttendanceStatus
    ) -> AttendanceSession {
        var updated = session
        guard let student = session.rosterSnapshot.first(where: { $0.id == studentID }) else {
            return session
        }

        let observation = observationID.flatMap { session.observation(withID: $0) }
        // Attendance still requires evidence, even by hand.
        let resolvedStatus: AttendanceStatus = (status == .present && observation == nil) ? .needsReview : status

        // Matching another Zoom name to a student who was already seen adds that name; the name
        // they were already matched on stays linked. The same person often joins twice.
        let previous = updated.records.first { $0.studentID == studentID }
        var record = AttendanceRecord(
            studentID: student.id,
            studentName: student.officialName,
            status: resolvedStatus,
            matchedObservationID: observation?.id,
            matchedZoomName: observation?.rawName,
            matchSource: .manual,
            confidence: observation == nil ? nil : 1.0,
            reason: "Set manually",
            isManual: true
        )
        if resolvedStatus == .present, let observation, let previous,
           previous.status == .present || previous.status == .needsReview {
            for earlier in previous.claimedObservationIDs where earlier != observation.id {
                // Only a name nobody else holds, and only from a record that really was present
                // on it or a review guess the operator is now confirming as the same person.
                guard previous.status == .present,
                      let earlierObservation = session.observation(withID: earlier),
                      !updated.records.contains(where: { $0.studentID != studentID && $0.claimedObservationIDs.contains(earlier) }) else { continue }
                record.addIdentity(earlierObservation)
            }
            if let names = record.additionalZoomNames, !names.isEmpty {
                record.reason = "Set manually; also joined as " + names.map { "“\($0)”" }.joined(separator: ", ")
            }
        }

        if let index = updated.records.firstIndex(where: { $0.studentID == studentID }) {
            updated.records[index] = record
        } else {
            updated.records.append(record)
        }

        // One observation cannot also be evidence for somebody else.
        if let observationID = observation?.id {
            for index in updated.records.indices
            where updated.records[index].studentID != studentID {
                updated.records[index].removeIdentity(observationID)
            }
            for index in updated.records.indices
            where updated.records[index].studentID != studentID
                && updated.records[index].matchedObservationID == observationID {
                updated.records[index].matchedObservationID = nil
                updated.records[index].matchedZoomName = nil
                updated.records[index].confidence = nil
                updated.records[index].matchSource = .none
                if !updated.records[index].isManual {
                    updated.records[index].status = updated.isFinalized ? .absent : .notSeenYet
                }
            }
        }

        let claimedNames = Set(updated.records.filter { $0.status == .present }.flatMap(\.claimedObservationIDs).compactMap { session.observation(withID: $0)?.rawName })
        updated.unmatchedZoomNames = updated.unmatchedZoomNames.filter { name in
            !claimedNames.contains(name) && (observation.map { $0.rawName != name } ?? true)
        }
        return updated
    }

    /// Clears a decision and lets automatic matching consider the student again.
    public static func clearMatch(session: AttendanceSession, studentID: UUID) -> AttendanceSession {
        var updated = session
        updated.records.removeAll { $0.studentID == studentID }
        return updated
    }
}
