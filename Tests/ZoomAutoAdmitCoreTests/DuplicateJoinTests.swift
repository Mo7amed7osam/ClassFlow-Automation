import XCTest
@testable import ZoomAutoAdmitCore

final class DuplicateJoinTests: XCTestCase {
    private func observation(_ name: String) -> ParticipantObservation {
        ParticipantObservation(rawName: name, normalizedName: NameNormalizer.normalize(name), observedAt: [Date()])
    }

    func testAStudentWhoJoinsTwiceIsOnePresentStudentNotAStranger() {
        let amir = Student(officialName: "Amir Girges Abdou Girges", aliases: ["amir abdu"])
        let rafeek = Student(officialName: "RAFEEK MAGDY GERGES SALIB")
        let absent = Student(officialName: "Elham Bakrey Mohammed Khesha")
        let first = observation("amir abdu")
        let second = observation("Amir Girges")
        let titled = observation("Doctor Amir Girges")
        let stranger = observation("Dr. Hanan I. EL-Shorbagy")
        let session = AttendanceSession(
            groupID: UUID(), groupName: "G1", meetingName: "m", startedAt: Date(),
            rosterSnapshot: [amir, rafeek, absent],
            observations: [first, second, titled, stranger, observation("rafeek magdy gerges")]
        )

        let register = AttendanceReconciler.reconcile(session: session, autoAcceptConfidence: 0.9, ignoring: AttendanceIgnoreRules(names: [String]()))
        let amirRecord = register.records.first { $0.studentID == amir.id }
        XCTAssertEqual(amirRecord?.status, .present)
        XCTAssertEqual(register.records.filter { $0.status == .present }.count, 2, "two present students, not three")
        XCTAssertTrue(amirRecord?.reason?.contains("also joined as “Amir Girges”") == true, amirRecord?.reason ?? "")
        XCTAssertTrue(amirRecord?.reason?.contains("Doctor Amir Girges") == true, "a title in front of the name does not make a new person")
        XCTAssertEqual(register.unmatchedZoomNames, ["Dr. Hanan I. EL-Shorbagy"], "someone who only loosely resembles a present student stays visible")
        XCTAssertNotEqual(register.records.first { $0.studentID == absent.id }?.status, .present, "an extra identity is never evidence for someone else")
    }

    func testTheSecondNameNeverTakesAnAbsentStudentsPlace() {
        // "Ahmed Ali" could be either student; the absent one gets it, not a merge.
        let ahmedAliHassan = Student(officialName: "Ahmed Ali Hassan")
        let ahmedAliMahmoud = Student(officialName: "Ahmed Ali Mahmoud")
        let session = AttendanceSession(
            groupID: UUID(), groupName: "G", meetingName: "m", startedAt: Date(),
            rosterSnapshot: [ahmedAliHassan, ahmedAliMahmoud],
            observations: [observation("Ahmed Ali Hassan"), observation("Ahmed Ali Mahmoud")]
        )
        let register = AttendanceReconciler.reconcile(session: session, autoAcceptConfidence: 0.9, ignoring: AttendanceIgnoreRules(names: [String]()))
        XCTAssertEqual(register.records.filter { $0.status == .present }.count, 2)
        XCTAssertTrue(register.unmatchedZoomNames.isEmpty)
    }

    func testTitlesAreIgnoredWhenScoring() {
        let student = Student(officialName: "Amir Girges Abdou Girges")
        XCTAssertEqual(DeterministicMatcher.score(rawObservedName: "Dr Amir Girges", student: student)?.score ?? 0, 1.0, accuracy: 0.001)
        XCTAssertEqual(DeterministicMatcher.score(rawObservedName: "م. Amir Girges", student: student)?.score ?? 0, 1.0, accuracy: 0.001)
        XCTAssertEqual(DeterministicMatcher.score(rawObservedName: "Dr", student: Student(officialName: "Dr"))?.source, .exact, "a lone title is still a name")
    }

    /// Matching another Zoom name to a present student by hand keeps the name they already had.
    func testAManualMatchAddsTheNameInsteadOfReplacingIt() {
        let amir = Student(officialName: "Amir Girges Abdou Girges", aliases: ["amir abdu"])
        let first = observation("amir abdu")
        let other = observation("Dr Amit Gigs")
        let session = AttendanceSession(
            groupID: UUID(), groupName: "G1", meetingName: "m", startedAt: Date(),
            rosterSnapshot: [amir], observations: [first, other]
        )
        let reconciled = AttendanceReconciler.reconcile(session: session, autoAcceptConfidence: 0.9, ignoring: AttendanceIgnoreRules(names: [String]()))
        XCTAssertEqual(reconciled.unmatchedZoomNames, ["Dr Amit Gigs"])

        let matched = AttendanceReconciler.applyManualMatch(session: reconciled, studentID: amir.id, observationID: other.id, status: .present)
        let record = matched.records.first { $0.studentID == amir.id }
        XCTAssertEqual(record?.status, .present)
        XCTAssertEqual(record?.matchedZoomName, "Dr Amit Gigs")
        XCTAssertEqual(record?.additionalZoomNames, ["amir abdu"], "his real name stays linked")
        XCTAssertTrue(matched.unmatchedZoomNames.isEmpty)

        // And both survive the next automatic pass, with nothing left unmatched.
        let again = AttendanceReconciler.reconcile(session: matched, autoAcceptConfidence: 0.9, ignoring: AttendanceIgnoreRules(names: [String]()))
        XCTAssertEqual(Set(again.records.first { $0.studentID == amir.id }?.claimedObservationIDs ?? []), [first.id, other.id])
        XCTAssertTrue(again.unmatchedZoomNames.isEmpty)

        // A third clear name later still joins him although his record is a manual one.
        var later = again
        let third = observation("Amir Girges")
        later.observations.append(third)
        let thirdPass = AttendanceReconciler.reconcile(session: later, autoAcceptConfidence: 0.9, ignoring: AttendanceIgnoreRules(names: [String]()))
        XCTAssertTrue(thirdPass.records.first { $0.studentID == amir.id }?.claimedObservationIDs.contains(third.id) == true)
        XCTAssertTrue(thirdPass.unmatchedZoomNames.isEmpty)
    }
}
