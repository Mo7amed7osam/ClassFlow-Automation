import XCTest
@testable import ZoomAutoAdmitCore

final class EndOfClassTests: XCTestCase {
    func testEndOfClassStepsRunEndBeforeTheRecordingAndPersist() {
        let defaults = UserDefaults(suiteName: "EndOfClassTests-\(UUID().uuidString)")!
        XCTAssertTrue(LmsSettings.load(from: defaults).endOfClassSteps.isEmpty, "both are off until switched on")
        let settings = LmsSettings(endSessionAtEnd: true, attachZoomRecording: true)
        settings.save(to: defaults)
        let loaded = LmsSettings.load(from: defaults)
        XCTAssertEqual(loaded.endOfClassSteps, [.endSession, .attachRecording])
        XCTAssertTrue(loaded.isAnythingEnabled)
        XCTAssertTrue(loaded.followUpSteps.isEmpty, "nothing extra is queued when the class starts")
    }

    func testEndOfClassStepsAreDueAtTheEndTimeAndQueuedOnce() {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString).appendingPathComponent("follow-up.json")
        let queue = LmsFollowUpQueue(fileURL: url)
        let start = Date().addingTimeInterval(-3 * 60 * 60)
        let end = Date()
        let written = queue.schedule(group: "CAI5_IND1_G1", sessionStartedAt: start, steps: [.endSession, .attachRecording], attendanceGroupID: UUID(), scheduleID: UUID(), recordingProfile: "CAI5_IND1_G1", dueAt: end)
        XCTAssertEqual(written.map(\.step), [.endSession, .attachRecording])
        XCTAssertTrue(written.allSatisfy { $0.dueAt == end })
        XCTAssertEqual(written.last?.recordingProfile, "CAI5_IND1_G1")
        XCTAssertEqual(written.first?.sessionStart, LmsFollowUpQueue.dashboardDateAndTime(start).1, "the session is still found by its start time")
        XCTAssertTrue(queue.schedule(group: "CAI5_IND1_G1", sessionStartedAt: start, steps: [.endSession], attendanceGroupID: nil, scheduleID: nil, dueAt: end).isEmpty, "an end time reached twice ends the session once")
        XCTAssertEqual(queue.due(at: end).map(\.step), [.endSession, .attachRecording])
    }

    func testTheDashboardShowsTheZoomRecording() {
        let profile = ZoomAccountProfile(name: "G1", accountIdentifier: "g1@example.com")
        let group = StudentGroup(name: "CAI5_IND1_G1", lmsGroupCode: "CAI5_IND1_G1")
        let schedule = ZoomSchedule(name: "G1", recurrence: .daily, startTime: TimeOfDay(hour: 17, minute: 55), endTime: TimeOfDay(hour: 21, minute: 0), accountProfileID: profile.id, meeting: MeetingReference(name: "G1", kind: .meetingID("1")), attendanceGroupID: group.id)
        let configuration = SchedulerConfiguration(accountProfiles: [profile], schedules: [schedule], studentGroups: [group])
        let now = Calendar.current.date(bySettingHour: 21, minute: 30, second: 0, of: Date())!
        let date = LmsFollowUpQueue.dashboardDateAndTime(now).0
        var pending = LmsFollowUp(group: "CAI5_IND1_G1", sessionDate: date, sessionStart: "17:55", step: .attachRecording, dueAt: now, attempts: 1, lastError: "No cloud recording is listed for CAI5_IND1_G1 yet.", scheduleID: schedule.id)
        var inputs = OperationsInputs(configuration: configuration, followUps: [pending], lmsSettings: LmsSettings(endSessionAtEnd: true, attachZoomRecording: true))
        var card = SessionOverviewBuilder.build(inputs, now: now).first { Calendar.current.isDate($0.startsAt, inSameDayAs: now) }!
        XCTAssertEqual(card.recording, .waiting)
        XCTAssertTrue(card.recordingDetail?.contains("No cloud recording is listed") == true)

        pending.attempts = LmsFollowUpQueue.maximumAttempts
        inputs.followUps = [pending]
        card = SessionOverviewBuilder.build(inputs, now: now).first { Calendar.current.isDate($0.startsAt, inSameDayAs: now) }!
        XCTAssertEqual(card.recording, .failed)

        inputs.followUps = []
        inputs.events = [
            OperationsEvent(kind: .lmsSessionEnded, severity: .success, groupCode: "CAI5_IND1_G1", sessionDate: date, title: "LMS session ended ✅", message: ""),
            OperationsEvent(kind: .recordingZoomAttached, severity: .success, groupCode: "CAI5_IND1_G1", sessionDate: date, title: "Zoom recording attached ✅", message: "")
        ]
        card = SessionOverviewBuilder.build(inputs, now: now).first { Calendar.current.isDate($0.startsAt, inSameDayAs: now) }!
        XCTAssertEqual(card.recording, .attached)
        XCTAssertTrue(card.lmsDetail.contains("✓ LMS session ended ✅"))
    }
}

final class LifecycleReliabilityTests: XCTestCase {
    func testAutomaticAIRequestNeverSendsMatchedStudents() {
        let present = Student(officialName: "Amir Girges Abdou Girges")
        let missing = Student(officialName: "Amany Esmat Mohammed Mahmoud")
        let seen = ParticipantObservation(rawName: "amir abdu", normalizedName: "amir abdu", observedAt: [Date()])
        let unknown = ParticipantObservation(rawName: "Dr-wafaa Osman", normalizedName: "dr wafaa osman", observedAt: [Date()])
        var session = AttendanceSession(groupID: UUID(), groupName: "G1", meetingName: "m", startedAt: Date(), rosterSnapshot: [present, missing], observations: [seen, unknown])
        session.records = [
            AttendanceRecord(studentID: present.id, studentName: present.officialName, status: .present, matchedObservationID: seen.id, matchedZoomName: seen.rawName, matchSource: .alias, confidence: 1),
            AttendanceRecord(studentID: missing.id, studentName: missing.officialName, status: .absent)
        ]
        let (request, _) = AIReconciliation.request(for: session, ignoring: AttendanceIgnoreRules(names: [String]()), includePresentStudents: false)
        XCTAssertEqual(request.students.map(\.officialName), ["Amany Esmat Mohammed Mahmoud"])
        XCTAssertEqual(request.observedNames.map(\.displayName), ["Dr-wafaa Osman"])

        let ignored = AIReconciliation.request(for: session, ignoring: AttendanceIgnoreRules(names: ["Dr-wafaa Osman"]), includePresentStudents: false).request
        XCTAssertTrue(ignored.observedNames.isEmpty, "an ignored participant never reaches the AI")
    }

    func testRunSessionIsAQueuedStepDueImmediately() {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString).appendingPathComponent("follow-up.json")
        let queue = LmsFollowUpQueue(fileURL: url)
        let now = Date()
        let written = queue.schedule(group: "CAI5_IND1_G2", sessionStartedAt: now, steps: [.runSession], attendanceGroupID: nil, scheduleID: nil, dueAt: now)
        XCTAssertEqual(written.first?.step, .runSession)
        XCTAssertEqual(LmsFollowUpQueue.delay(for: .runSession), 0)
        queue.fail(written[0], reason: "The dashboard did not respond while opening the session.", at: now)
        let retried = queue.read().first!
        XCTAssertEqual(retried.attempts, 1)
        XCTAssertEqual(retried.dueAt.timeIntervalSince(now), LmsFollowUpQueue.retryAfter, accuracy: 1, "a failed Run Session is retried, not dropped")
    }
}
