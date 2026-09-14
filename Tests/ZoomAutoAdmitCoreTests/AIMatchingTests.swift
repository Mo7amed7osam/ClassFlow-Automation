import Foundation
import XCTest
@testable import ZoomAutoAdmitCore

/// Everything the model returns is untrusted input. These tests are mostly
/// about what the app refuses to believe.
final class AIMatchValidationTests: XCTestCase {
    private let students = [
        Student(officialName: "Mohamed Ahmed Hassan"),
        Student(officialName: "Sara Mostafa Ali")
    ]

    private func observation(_ name: String) -> ParticipantObservation {
        ParticipantObservation(
            rawName: name,
            normalizedName: NameNormalizer.normalize(name),
            observedAt: [Date()]
        )
    }

    private func ids(_ observations: [ParticipantObservation]) -> AIMatchRequestIDs {
        AIMatchRequestIDs(
            students: ["s0": students[0].id, "s1": students[1].id],
            observations: Dictionary(uniqueKeysWithValues: observations.enumerated().map { ("z\($0.offset)", $0.element.id) })
        )
    }

    func testValidProposalIsAccepted() {
        let observed = [observation("Mohamed Hassan")]
        let response = AIMatchResponse(
            matches: [AIMatchProposal(studentID: "s0", observedNameID: "z0", confidence: 0.94, reason: "family name")]
        )

        let result = AIMatchValidator.validate(
            response, students: students, observations: observed, requestIDs: ids(observed)
        )

        XCTAssertEqual(result.matches.count, 1)
        XCTAssertEqual(result.matches[0].studentID, students[0].id)
        XCTAssertEqual(result.matches[0].confidence, 0.94)
        XCTAssertTrue(result.rejected.isEmpty)
    }

    func testHallucinatedStudentIDIsRejected() {
        let observed = [observation("Mohamed Hassan")]
        let response = AIMatchResponse(matches: [
            AIMatchProposal(studentID: "nobody", observedNameID: "z0", confidence: 0.99)
        ])
        let result = AIMatchValidator.validate(
            response, students: students, observations: observed, requestIDs: ids(observed)
        )

        XCTAssertTrue(result.matches.isEmpty)
        XCTAssertEqual(result.rejected.count, 1)
    }

    /// The absolute rule: the model cannot invent an attendee.
    func testObservedNameIDThatWasNeverOfferedIsRejected() {
        let observed = [observation("Mohamed Hassan")]
        let response = AIMatchResponse(matches: [
            AIMatchProposal(studentID: "s0", observedNameID: "z999", confidence: 0.99)
        ])
        let result = AIMatchValidator.validate(
            response, students: students, observations: observed, requestIDs: ids(observed)
        )

        XCTAssertTrue(result.matches.isEmpty, "no observation, no attendance")
        XCTAssertTrue(result.rejected[0].contains("unknown observed name id"))
    }

    /// The same student joining twice is normal: two observed names may both go to one student.
    func testOneStudentMayTakeTwoObservedNames() {
        let observed = [observation("Mohamed Hassan"), observation("M. Hassan")]
        let response = AIMatchResponse(
            matches: [
                AIMatchProposal(studentID: "s0", observedNameID: "z0", confidence: 0.9),
                AIMatchProposal(studentID: "s0", observedNameID: "z1", confidence: 0.8)
            ]
        )
        let result = AIMatchValidator.validate(
            response, students: students, observations: observed, requestIDs: ids(observed)
        )

        XCTAssertEqual(result.matches.count, 2, "both names are that student's")
        XCTAssertEqual(Set(result.matches.map(\.studentID)).count, 1)
    }

    func testDuplicateZoomNameAssignmentIsRejected() {
        let observed = [observation("Mohamed Hassan")]
        let response = AIMatchResponse(
            matches: [
                AIMatchProposal(studentID: "s0", observedNameID: "z0", confidence: 0.9),
                AIMatchProposal(studentID: "s1", observedNameID: "z0", confidence: 0.7)
            ]
        )
        let result = AIMatchValidator.validate(
            response, students: students, observations: observed, requestIDs: ids(observed)
        )

        XCTAssertEqual(result.matches.count, 1, "one Zoom identity cannot make two students present")
        XCTAssertEqual(result.matches[0].studentID, students[0].id)
    }

    func testOutOfRangeConfidenceIsRejected() {
        for bad in [1.5, -0.2, Double.nan, Double.infinity] {
            let observed = [observation("Mohamed Hassan")]
            let response = AIMatchResponse(matches: [
                AIMatchProposal(studentID: "s0", observedNameID: "z0", confidence: bad)
            ])
            let result = AIMatchValidator.validate(
                response, students: students, observations: observed, requestIDs: ids(observed)
            )
            XCTAssertTrue(result.matches.isEmpty, "confidence \(bad) must be rejected")
        }
    }

    // MARK: Decoding

    func testJSONInsideACodeFenceIsDecoded() throws {
        let text = """
        Here you go:
        ```json
        {"matches":[{"student_id":"s0","observed_name_id":"z0","confidence":0.9,"reason":"x"}],
         "unmatched_observed_name_ids":[]}
        ```
        """
        let decoded = try AIMatchValidator.decode(text)
        XCTAssertEqual(decoded.matches.count, 1)
    }

    func testProseWithoutJSONIsRejected() {
        XCTAssertThrowsError(try AIMatchValidator.decode("I think Mohamed was probably there."))
    }

    func testMalformedJSONIsRejected() {
        XCTAssertThrowsError(try AIMatchValidator.decode("{\"matches\": [ }"))
    }

    func testMissingRequiredFieldIsRejected() {
        XCTAssertThrowsError(try AIMatchValidator.decode("{\"matches\":[{\"student_id\":\"s0\"}]}"))
    }
}

final class OpenRouterRequestTests: XCTestCase {
    func testOnlyUnresolvedNamesAreIncluded() {
        let request = AIMatchRequest(
            students: [.init(id: "s0", officialName: "Mohamed Ahmed Hassan")],
            observedNames: [.init(id: "z0", displayName: "Mohamed Hassan")]
        )
        let prompt = OpenRouterClient.prompt(for: request)

        XCTAssertTrue(prompt.contains("Mohamed Ahmed Hassan"))
        XCTAssertTrue(prompt.contains("Mohamed Hassan"))
        XCTAssertTrue(prompt.contains("\"id\": \"s0\""))
        XCTAssertTrue(prompt.contains("\"id\": \"z0\""))
        XCTAssertTrue(prompt.contains("student_id"))
        XCTAssertTrue(prompt.contains("observed_name_id"))
        // Nothing about the rest of the app may travel.
        for forbidden in ["password", "api key", "authorization", "sk-or", "schedule", "zoommtg", "Bearer"] {
            XCTAssertFalse(prompt.lowercased().contains(forbidden.lowercased()), "prompt leaked \(forbidden)")
        }
    }

    func testNothingIsSentWhenThereIsNothingAmbiguous() async throws {
        var called = false
        let client = OpenRouterClient(apiKeyProvider: {
            called = true
            return "should-not-be-read"
        })

        let empty = AIMatchRequest(students: [], observedNames: [])
        XCTAssertFalse(empty.isWorthSending)

        let exchange = await client.proposeMatches(for: empty)
        let response = try XCTUnwrap(exchange.response)
        XCTAssertTrue(response.matches.isEmpty)
        XCTAssertFalse(called, "no key is even read when there is nothing to ask")
    }

    func testMissingKeyIsReportedNotCrashed() async {
        let client = OpenRouterClient(apiKeyProvider: { nil })
        let exchange = await client.proposeMatches(
            for: AIMatchRequest(students: [.init(id: "s0", officialName: "A")], observedNames: [.init(id: "z0", displayName: "B")])
        )

        XCTAssertNil(exchange.response)
        XCTAssertEqual(exchange.error, .noAPIKey)
        // The prompt is still captured, so the user can see what would have been sent.
        XCTAssertFalse(exchange.prompt.isEmpty)
    }

    func testResponseContentExtraction() {
        let body = """
        {"choices":[{"message":{"role":"assistant","content":"{\\"matches\\":[]}"}}]}
        """
        XCTAssertEqual(OpenRouterClient.extractContent(from: Data(body.utf8)), "{\"matches\":[]}")
        XCTAssertNil(OpenRouterClient.extractContent(from: Data("{}".utf8)))
    }

    func testRetriesAreBounded() {
        XCTAssertEqual(OpenRouterClient.Configuration(maxAttempts: 99).maxAttempts, 3)
        XCTAssertEqual(OpenRouterClient.Configuration(maxAttempts: 0).maxAttempts, 1)
    }

    /// The key must never appear anywhere a human or a log could see it.
    func testRedactionNeverRevealsTheKey() {
        let key = "sk-or-v1-abcdefghijklmnop1234"
        let redacted = APIKeyStore.redacted(key)

        XCTAssertFalse(redacted.contains("abcdefghijklmnop"))
        XCTAssertTrue(redacted.hasSuffix("1234"))
        XCTAssertEqual(APIKeyStore.redacted(nil), "not set")
        XCTAssertEqual(APIKeyStore.redacted("short"), "not set")
    }
}

final class AIReconciliationTests: XCTestCase {
    private let base = Date(timeIntervalSince1970: 1_800_000_000)

    private func observation(_ name: String) -> ParticipantObservation {
        ParticipantObservation(
            rawName: name,
            normalizedName: NameNormalizer.normalize(name),
            observedAt: [base, base.addingTimeInterval(600)]
        )
    }

    private func session(
        students: [Student],
        observations: [ParticipantObservation]
    ) -> AttendanceSession {
        AttendanceReconciler.reconcile(
            session: AttendanceSession(
                groupID: UUID(),
                groupName: "Group A",
                meetingName: "Week 2",
                startedAt: base,
                rosterSnapshot: students,
                observations: observations
            ),
            autoAcceptConfidence: 0.9
        )
    }

    /// Students still missing go first; students already present go too, marked, so a second
    /// Zoom name of theirs is recognised instead of being forced onto somebody missing.
    func testUnresolvedAndAlreadyPresentStudentsAreSentWithFreeNames() {
        let matched = Student(officialName: "Sara Mostafa Ali")
        let unmatched = Student(officialName: "Ahmed Tarek")
        let current = session(
            students: [matched, unmatched],
            observations: [observation("Sara Mostafa Ali"), observation("Ahmed's iPhone")]
        )

        let (request, ids) = AIReconciliation.request(for: current)

        XCTAssertEqual(request.students.map(\.officialName), ["Ahmed Tarek", "Sara Mostafa Ali"])
        XCTAssertEqual(request.students.map(\.alreadyPresent), [false, true])
        XCTAssertEqual(request.observedNames.map(\.displayName), ["Ahmed's iPhone"], "a claimed name is never sent")
        XCTAssertEqual(ids.students.count, 2)
        XCTAssertEqual(ids.observations.count, 1)
        XCTAssertTrue(OpenRouterClient.prompt(for: request).contains("\"already_present\": true"))
        XCTAssertTrue(OpenRouterClient.prompt(for: request).contains("A student MAY appear more than once"))
    }

    /// A second name the model pairs with a present student is linked to them, not made a new attendee.
    func testASecondNameForAPresentStudentIsLinkedToThem() {
        let sara = Student(officialName: "Sara Mostafa Ali")
        let ahmed = Student(officialName: "Ahmed Tarek")
        let current = session(
            students: [sara, ahmed],
            observations: [observation("Sara Mostafa Ali"), observation("sara phone")]
        )
        let (request, ids) = AIReconciliation.request(for: current)
        let saraKey = request.students.first { $0.officialName == "Sara Mostafa Ali" }!.id
        let response = AIMatchResponse(matches: [AIMatchProposal(studentID: saraKey, observedNameID: "z0", confidence: 0.95)])

        let summary = AIReconciliation.apply(response, to: current, ids: ids, autoAcceptConfidence: 0.9)
        let record = summary.session.records.first { $0.studentID == sara.id }
        XCTAssertEqual(record?.status, .present)
        XCTAssertEqual(record?.matchedZoomName, "Sara Mostafa Ali", "the name she was matched on stays")
        XCTAssertEqual(record?.additionalZoomNames, ["sara phone"])
        XCTAssertEqual(summary.session.records.first { $0.studentID == ahmed.id }?.status, .notSeenYet)
        XCTAssertTrue(summary.session.unmatchedZoomNames.isEmpty)
    }

    /// Regression for the live case: local Latin/Arabic similarity is zero, but
    /// pressing Match with AI must still send both sides of the problem.
    func testRafeekAndArabicObservationAlwaysReachAI() {
        let student = Student(officialName: "RAFEEK MAGDY GERGES SALIB")
        let current = session(students: [student], observations: [observation("رفيق")])

        XCTAssertNotEqual(current.records[0].status, .present)
        let (request, ids) = AIReconciliation.request(for: current)

        XCTAssertEqual(request.students.map(\.officialName), ["RAFEEK MAGDY GERGES SALIB"])
        XCTAssertEqual(request.observedNames.map(\.displayName), ["رفيق"])
        XCTAssertEqual(Set(request.students.map(\.id)), Set(ids.students.keys))
        XCTAssertEqual(Set(request.observedNames.map(\.id)), Set(ids.observations.keys))

        let prompt = OpenRouterClient.prompt(for: request)
        XCTAssertTrue(prompt.contains("RAFEEK MAGDY GERGES SALIB"))
        XCTAssertTrue(prompt.contains("رفيق"))
    }

    /// No fuzzy score, transliteration score, token overlap, script check, or
    /// local candidate floor may trim the request after deterministic matches.
    func testAllRemainingStudentsAndObservationsAreSentAsOneGlobalProblem() {
        let students = [
            Student(officialName: "RAFEEK MAGDY GERGES SALIB"),
            Student(officialName: "Mohamed Hatem Sadek Alsaeid"),
            Student(officialName: "AYMAN ABDELFATTAH MAHMOUD ZYAN"),
            Student(officialName: "Prof Wafaa Osman Abd ElFatah Abd ElHady")
        ]
        let observed = ["رفيق", "د محمد حاتم", "أيمن", "وفاء"].map(observation)
        let current = session(students: students, observations: observed)

        let (request, _) = AIReconciliation.request(for: current)

        XCTAssertEqual(Set(request.students.map(\.officialName)), Set(students.map(\.officialName)))
        XCTAssertEqual(Set(request.observedNames.map(\.displayName)), Set(observed.map(\.rawName)))
        XCTAssertEqual(request.students.count, 4)
        XCTAssertEqual(request.observedNames.count, 4)
    }

    /// A device name is the classic case local matching cannot settle.
    func testConfidentProposalBecomesPresent() {
        let student = Student(officialName: "Ahmed Tarek")
        let current = session(students: [student], observations: [observation("Ahmed's iPhone")])
        let (_, ids) = AIReconciliation.request(for: current)
        XCTAssertNotEqual(current.records[0].status, .present, "local matching must not have settled this")

        let summary = AIReconciliation.apply(
            AIMatchResponse(
                matches: [AIMatchProposal(studentID: "s0", observedNameID: "z0", confidence: 0.96, reason: "known device")]
            ),
            to: current,
            ids: ids,
            autoAcceptConfidence: 0.9
        )

        XCTAssertEqual(summary.appliedCount, 1)
        XCTAssertEqual(summary.session.records[0].status, .present)
        XCTAssertEqual(summary.session.records[0].matchSource, .ai)
        XCTAssertEqual(summary.session.records[0].confidence, 0.96)
        XCTAssertEqual(summary.session.records[0].matchedZoomName, "Ahmed's iPhone")
    }

    /// Below the threshold is a question for a human, not an answer.
    func testLowConfidenceProposalBecomesNeedsReview() {
        let student = Student(officialName: "Ahmed Tarek")
        let current = session(students: [student], observations: [observation("Ahmed's iPhone")])
        let (_, ids) = AIReconciliation.request(for: current)

        let summary = AIReconciliation.apply(
            AIMatchResponse(
                matches: [AIMatchProposal(studentID: "s0", observedNameID: "z0", confidence: 0.63, reason: "first name only")]
            ),
            to: current,
            ids: ids,
            autoAcceptConfidence: 0.9
        )

        XCTAssertEqual(summary.appliedCount, 0)
        XCTAssertEqual(summary.reviewCount, 1)
        XCTAssertEqual(summary.session.records[0].status, .needsReview)
    }

    func testExplicitNeedsReviewWinsEvenAboveAcceptanceThreshold() {
        let student = Student(officialName: "Rafeek Magdy Gerges Salib")
        let current = session(students: [student], observations: [observation("رفيق")])
        let (_, ids) = AIReconciliation.request(for: current)

        let summary = AIReconciliation.apply(
            AIMatchResponse(matches: [
                AIMatchProposal(
                    studentID: "s0",
                    observedNameID: "z0",
                    confidence: 0.97,
                    needsReview: true,
                    reason: "Competing first-name candidates"
                )
            ]),
            to: current,
            ids: ids,
            autoAcceptConfidence: 0.9
        )

        XCTAssertEqual(summary.appliedCount, 0)
        XCTAssertEqual(summary.reviewCount, 1)
        XCTAssertEqual(summary.unmatchedObservedNameCount, 0)
        XCTAssertEqual(summary.session.records[0].status, .needsReview)
    }

    func testValidatorCannotClaimAnObservationOmittedFromThisRequest() {
        let student = Student(officialName: "Ahmed Tarek")
        let offered = observation("Ahmed's iPhone")
        let alreadyClaimed = observation("Sara Mostafa Ali")
        let current = session(students: [student], observations: [offered, alreadyClaimed])
        let ids = AIMatchRequestIDs(
            students: ["s0": student.id],
            observations: ["z0": offered.id]
        )

        let summary = AIReconciliation.apply(
            AIMatchResponse(matches: [
                AIMatchProposal(studentID: "s0", observedNameID: "z1", confidence: 0.99)
            ]),
            to: current,
            ids: ids,
            autoAcceptConfidence: 0.9
        )

        XCTAssertEqual(summary.appliedCount, 0)
        XCTAssertEqual(summary.rejected.count, 1)
        XCTAssertNotEqual(summary.session.records[0].status, .present)
    }

    func testManualDecisionsAreNeverOverwrittenByAI() {
        let student = Student(officialName: "Ahmed Tarek")
        var current = session(students: [student], observations: [observation("Ahmed's iPhone")])
        current = AttendanceReconciler.applyManualMatch(
            session: current,
            studentID: student.id,
            observationID: nil,
            status: .absent
        )

        let summary = AIReconciliation.apply(
            AIMatchResponse(
                matches: [AIMatchProposal(studentID: "s0", observedNameID: "z0", confidence: 0.99)]
            ),
            to: current,
            ids: AIMatchRequestIDs(
                students: ["s0": student.id],
                observations: ["z0": current.observations[0].id]
            ),
            autoAcceptConfidence: 0.9
        )

        XCTAssertEqual(summary.session.records[0].status, .absent)
        XCTAssertTrue(summary.session.records[0].isManual)
    }

    /// AI being down must cost nothing that was already worked out locally.
    func testUnavailableAIPreservesLocalResults() {
        let matched = Student(officialName: "Sara Mostafa Ali")
        let unmatched = Student(officialName: "Ahmed Tarek")
        let current = session(
            students: [matched, unmatched],
            observations: [observation("Sara Mostafa Ali"), observation("Ahmed's iPhone")]
        )

        let summary = AIReconciliation.unavailable(.network("offline"), session: current)

        XCTAssertTrue(summary.aiWasUnavailable)
        XCTAssertEqual(summary.session.records.first { $0.studentName == "Sara Mostafa Ali" }?.status, .present)
        XCTAssertEqual(summary.appliedCount, 0)
    }

    func testHallucinatedProposalsAreReportedAndDiscarded() {
        let student = Student(officialName: "Ahmed Tarek")
        let current = session(students: [student], observations: [observation("Ahmed's iPhone")])

        let summary = AIReconciliation.apply(
            AIMatchResponse(
                matches: [AIMatchProposal(studentID: "s0", observedNameID: "z999", confidence: 0.99)]
            ),
            to: current,
            ids: AIMatchRequestIDs(students: ["s0": student.id], observations: [:]),
            autoAcceptConfidence: 0.9
        )

        XCTAssertEqual(summary.appliedCount, 0)
        XCTAssertEqual(summary.rejected.count, 1)
        XCTAssertNotEqual(summary.session.records[0].status, .present)
    }
}

/// The prompt has to tell the model how to calibrate, not just what to avoid.
///
/// The rule that only warned about ambiguity produced 0.85 for a Zoom name whose
/// given name belonged to exactly one student on the list, which fell under the
/// 0.90 auto-accept threshold and cost a manual review for a pairing nothing
/// competed with.
final class PromptCalibrationTests: XCTestCase {
    private func prompt() -> String {
        OpenRouterClient.prompt(for: AIMatchRequest(
            students: [.init(id: "s0", officialName: "Zahraa Hagag Abdelsamea Abdelrahman")],
            observedNames: [.init(id: "z0", displayName: "Zahraa Swelim")]
        ))
    }

    func testUniquenessIsStatedAsAConfidentCase() {
        let text = prompt().lowercased()
        XCTAssertTrue(text.contains("exactly one student"))
        XCTAssertTrue(text.contains("no other student competes"))
    }

    func testASurnameThatDiffersIsNotTreatedAsDoubt() {
        let text = prompt().lowercased()
        XCTAssertTrue(text.contains("surname"))
        XCTAssertTrue(text.contains("married name"))
    }

    func testAmbiguityIsJudgedAgainstThisListOnly() {
        XCTAssertTrue(prompt().lowercased().contains("common in the wider world"))
    }

    func testConfidenceBandsAreGiven() {
        let text = prompt()
        XCTAssertTrue(text.contains("0.90 to 1.00"))
        XCTAssertTrue(text.contains("0.70 to 0.89"))
        XCTAssertTrue(text.contains("below 0.70"))
    }

    /// The original ambiguity rule must survive: uniqueness guidance is an
    /// addition, not a licence to guess between competing students.
    func testTheAmbiguityRuleIsStillPresent() {
        let text = prompt().lowercased()
        XCTAssertTrue(text.contains("multiple plausible"))
        XCTAssertTrue(text.contains("never chosen randomly"))
    }

    func testTheCalibrationRulesLeakNothing() {
        let text = prompt().lowercased()
        for forbidden in ["password", "api key", "authorization", "sk-or", "schedule", "zoommtg", "Bearer"] {
            XCTAssertFalse(text.contains(forbidden.lowercased()), "prompt leaked \(forbidden)")
        }
    }
}
