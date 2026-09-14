import Foundation
import XCTest
import ZoomAXSupport
@testable import ZoomAutoAdmitCore

final class AttendanceIgnoreListTests: XCTestCase {
    private var directory: URL!

    override func setUp() {
        directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        AttendanceIgnoreRules.currentProvider = { .none }
    }

    override func tearDown() {
        AttendanceIgnoreRules.currentProvider = { .none }
        try? FileManager.default.removeItem(at: directory)
    }

    private func observation(_ name: String, host: Bool = false) -> ParticipantObservation {
        ParticipantObservation(rawName: name, normalizedName: NameNormalizer.normalize(name), observedAt: [Date(timeIntervalSince1970: 1000)], sawHostRole: host)
    }

    private func session(roster: [Student], observations: [ParticipantObservation], groupID: UUID = UUID(), startedAt: Date = Date(timeIntervalSince1970: 900)) -> AttendanceSession {
        AttendanceSession(groupID: groupID, groupName: "CAI5_IND1_G1", meetingName: "m", startedAt: startedAt, rosterSnapshot: roster, observations: observations)
    }

    func testStorePersistsAddsRemovesImportsAndExports() {
        let url = directory.appendingPathComponent("ignored-participants.json")
        let store = AttendanceIgnoreStore(fileURL: url)
        var changes = 0
        store.onChange = { changes += 1 }
        XCTAssertEqual(store.add(["Yossef ayoub", "eyouth coordinator", "  yossef   AYOUB "]), 2, "duplicates compare normalized")
        XCTAssertEqual(AttendanceIgnoreStore(fileURL: url).participants.map(\.name), ["Yossef ayoub", "eyouth coordinator"], "persisted to disk")
        XCTAssertEqual(store.remove(["EYOUTH COORDINATOR"]), 1)
        XCTAssertEqual(store.participants.map(\.name), ["Yossef ayoub"])
        XCTAssertEqual(changes, 2)

        let imported = AttendanceIgnoreStore.parseImport("Name,Role\nMohamed Hosam,admin\n\n\"Trainer One\",trainer\n")
        XCTAssertEqual(imported, ["Mohamed Hosam", "Trainer One"])
        XCTAssertEqual(store.add(imported, source: .imported), 2)
        XCTAssertEqual(store.exportText(), "Yossef ayoub\nMohamed Hosam\nTrainer One\n")
        XCTAssertTrue(store.rules.matches("mohamed hosam (Host)"))
    }

    func testIgnoredPeopleNeverReachTheRegister() {
        let ahmed = Student(officialName: "Ahmed Ali")
        let yossef = Student(officialName: "Yossef Ayoub")   // even if someone put staff on the roster
        let base = session(roster: [ahmed, yossef], observations: [observation("Ahmed Ali"), observation("Yossef ayoub"), observation("eyouth coordinator", host: true)])
        let rules = AttendanceIgnoreRules(names: ["Yossef ayoub", "eyouth coordinator"])

        let reconciled = AttendanceReconciler.reconcile(session: base, autoAcceptConfidence: 0.9, finalizing: true, ignoring: rules)
        XCTAssertEqual(reconciled.observations.map(\.rawName), ["Ahmed Ali"])
        XCTAssertEqual(Set(reconciled.ignoredObservations.map(\.rawName)), ["Yossef ayoub", "eyouth coordinator"])
        XCTAssertTrue(reconciled.unmatchedZoomNames.isEmpty, "never an unmatched name")
        XCTAssertEqual(reconciled.records.first { $0.studentID == ahmed.id }?.status, .present)
        XCTAssertNotEqual(reconciled.records.first { $0.studentID == yossef.id }?.status, .present, "an ignored observation is never evidence")

        let (request, _) = AIReconciliation.request(for: base, ignoring: rules)
        XCTAssertFalse(request.observedNames.contains { rules.matches($0.displayName) }, "never sent to OpenRouter")

        // Taken off the list, the evidence comes back.
        let restored = AttendanceReconciler.reconcile(session: reconciled, autoAcceptConfidence: 0.9, ignoring: .none)
        XCTAssertEqual(restored.observations.count, 3)
        XCTAssertTrue(restored.ignoredObservations.isEmpty)
    }

    func testAManualMatchToAnIgnoredNameIsUndone() {
        let ahmed = Student(officialName: "Ahmed Ali")
        let trainer = observation("Trainer Omar")
        var base = session(roster: [ahmed], observations: [trainer])
        base = AttendanceReconciler.applyManualMatch(session: base, studentID: ahmed.id, observationID: trainer.id, status: .present)
        XCTAssertEqual(base.records.first?.status, .present)
        let ignored = AttendanceIgnoring.apply(AttendanceIgnoreRules(names: ["trainer omar"]), to: base)
        XCTAssertEqual(ignored.records.first?.status, .notSeenYet)
        XCTAssertNil(ignored.records.first?.matchedObservationID)
    }

    func testRepeatedPersistsDoNotDuplicateIgnoredEvidence() {
        let trainer = observation("Trainer Omar")
        var current = session(roster: [], observations: [trainer])
        let rules = AttendanceIgnoreRules(names: ["Trainer Omar"])
        for _ in 0..<3 {
            current.observations = [trainer]   // what a live recorder hands back each time
            current = AttendanceReconciler.reconcile(session: current, autoAcceptConfidence: 0.9, ignoring: rules)
        }
        XCTAssertEqual(current.ignoredObservations.count, 1)
        XCTAssertTrue(current.observations.isEmpty)
    }

    func testThisMeetingOnlyIgnoresStayWithTheSession() {
        var base = session(roster: [], observations: [observation("Guest Visitor")])
        base.meetingIgnoredNames = ["guest visitor"]
        let reconciled = AttendanceReconciler.reconcile(session: base, autoAcceptConfidence: 0.9, ignoring: .none)
        XCTAssertTrue(reconciled.observations.isEmpty)
        XCTAssertTrue(reconciled.unmatchedZoomNames.isEmpty)
        let next = session(roster: [], observations: [observation("Guest Visitor")])
        XCTAssertEqual(AttendanceReconciler.reconcile(session: next, autoAcceptConfidence: 0.9, ignoring: .none).unmatchedZoomNames, ["Guest Visitor"])
    }

    func testTheRecorderSkipsGloballyIgnoredRowsAtCapture() {
        AttendanceIgnoreRules.currentProvider = { AttendanceIgnoreRules(names: ["Yossef ayoub"]) }
        let recorder = AttendanceSnapshotRecorder(group: StudentGroup(name: "G"))
        let readout = ZoomAXSupport.ParticipantsReadout(
            listAvailable: true,
            admitted: ["Yossef ayoub", "Ahmed Ali"].enumerated().map { index, name in
                ZoomAXSupport.ParticipantRow(rawText: name, displayName: name, roles: [], indexPath: [index])
            },
            waiting: [],
            reportedCount: 2
        )
        let snapshot = recorder.capture(readout, reason: .meetingStarted)
        XCTAssertEqual(snapshot?.participants.map(\.rawZoomName), ["Ahmed Ali"])
    }

    func testDetectorFlagsStaffHostsAndRecurringStrangersOnly() {
        let groupID = UUID()
        let ahmed = Student(officialName: "Ahmed Ali")
        var past1 = session(roster: [ahmed], observations: [], groupID: groupID, startedAt: Date(timeIntervalSince1970: 100))
        past1.unmatchedZoomNames = ["Yossef ayoub"]
        var past2 = session(roster: [ahmed], observations: [], groupID: groupID, startedAt: Date(timeIntervalSince1970: 200))
        past2.unmatchedZoomNames = ["yossef  ayoub"]

        var current = session(roster: [ahmed], observations: [
            observation("Ahmed Ali"),
            observation("Yossef ayoub"),
            observation("eyouth coordinator"),
            observation("Sara Host", host: true),
            observation("New Student Once")
        ], groupID: groupID, startedAt: Date(timeIntervalSince1970: 900))
        current = AttendanceReconciler.reconcile(session: current, autoAcceptConfidence: 0.9, ignoring: .none)

        let found = UnknownParticipantDetector.detect(session: current, history: [past1, past2, current])
        XCTAssertEqual(Set(found.map(\.name)), ["Yossef ayoub", "eyouth coordinator", "Sara Host"])
        XCTAssertEqual(found.first { $0.name == "Yossef ayoub" }?.reasons, [.recurring(sessions: 2)])
        XCTAssertEqual(found.first { $0.name == "eyouth coordinator" }?.reasons.first, .staffWord("coordinator"))
        XCTAssertFalse(found.contains { $0.name == "Ahmed Ali" }, "a matched student is never asked about")

        // Once ignored, they are not detected again.
        let ignored = AttendanceReconciler.reconcile(session: current, autoAcceptConfidence: 0.9, ignoring: AttendanceIgnoreRules(names: ["Yossef ayoub", "eyouth coordinator", "Sara Host"]))
        XCTAssertTrue(UnknownParticipantDetector.detect(session: ignored, history: [past1, past2]).isEmpty)
    }

    func testSessionsSavedBeforeTheIgnoreListStillLoad() throws {
        let original = session(roster: [], observations: [observation("A B")])
        var json = try JSONSerialization.jsonObject(with: JSONEncoder().encode(original)) as! [String: Any]
        json.removeValue(forKey: "ignoredObservations")
        json.removeValue(forKey: "meetingIgnoredNames")
        json.removeValue(forKey: "unknownParticipantsReviewed")
        let decoded = try JSONDecoder().decode(AttendanceSession.self, from: JSONSerialization.data(withJSONObject: json))
        XCTAssertTrue(decoded.ignoredObservations.isEmpty)
        XCTAssertFalse(decoded.unknownParticipantsReviewed)
    }
}

final class AttendanceIgnoreRealDataTests: XCTestCase {
    func testZoomRowDecorationsDoNotDefeatTheList() {
        let rules = AttendanceIgnoreRules(names: ["eyouth coordinator"])
        XCTAssertTrue(rules.matches("eyouth coordinator, (Host, me), participant ID: 248703"))
        XCTAssertTrue(rules.matches("eyouth coordinator, (Co-host, me), participant ID: 336483"))
        XCTAssertTrue(rules.matches("EYOUTH  coordinator (Host)"))
        XCTAssertFalse(rules.matches("eyouth coordinator assistant"))
        XCTAssertFalse(AttendanceIgnoreRules(names: ["Mona (Cairo)"]).matches("Mona"), "a real parenthetical stays part of the name")
    }

    func testRecurringLookalikesOfStudentsAreLeftToReview() {
        let groupID = UUID()
        let roster = [Student(officialName: "Amal Abdelrazik Mohamed AbuRohama"), Student(officialName: "AYMAN ABDELFATTAH MAHMOUD ZYAN")]
        func past(_ start: TimeInterval, _ names: [String]) -> AttendanceSession {
            var session = AttendanceSession(groupID: groupID, groupName: "G", meetingName: "m", startedAt: Date(timeIntervalSince1970: start), rosterSnapshot: roster)
            session.unmatchedZoomNames = names
            return session
        }
        let history = [past(100, ["amal abdelrazek", "Dr Ayman Zyan", "Youssef Ayoub"]), past(200, ["Amal Abdelrazek", "dr ayman zyan", "youssef ayoub"])]
        let names = ["Amal Abdelrazek", "Dr Ayman Zyan", "Youssef Ayoub", "DEPI Wavz"]
        let current = AttendanceSession(
            groupID: groupID, groupName: "G", meetingName: "m", startedAt: Date(timeIntervalSince1970: 900), rosterSnapshot: roster,
            observations: names.map { ParticipantObservation(rawName: $0, normalizedName: NameNormalizer.normalize($0), observedAt: [Date()]) }
        )
        let found = UnknownParticipantDetector.detect(session: current, history: history)
        XCTAssertEqual(Set(found.map(\.name)), ["Youssef Ayoub", "DEPI Wavz"])
    }
}
