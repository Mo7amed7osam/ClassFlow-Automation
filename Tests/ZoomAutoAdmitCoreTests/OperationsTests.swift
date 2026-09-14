import XCTest
@testable import ZoomAutoAdmitCore

private let calendar = Calendar.current

private func today(_ hour: Int, _ minute: Int) -> Date {
    calendar.date(bySettingHour: hour, minute: minute, second: 0, of: Date())!
}

final class OperationsEventStoreTests: XCTestCase {
    func testAppendsLoadsAndPrunes() throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString).appendingPathComponent("events.jsonl")
        let store = OperationsEventStore(fileURL: url)
        let old = OperationsEvent(at: Date().addingTimeInterval(-40 * 24 * 60 * 60), kind: .zoomStarted, severity: .success, title: "old", message: "")
        let recent = OperationsEvent(kind: .attendanceUploaded, severity: .success, groupCode: "CAI5_IND1_G1", sessionDate: "2026-09-14", title: "Attendance uploaded", message: "23 Joined")
        store.append(old)
        store.append(recent)
        XCTAssertEqual(store.load().map(\.title), ["old", "Attendance uploaded"])
        XCTAssertEqual(store.load(since: Date().addingTimeInterval(-60)).count, 1)
        XCTAssertEqual(recent.sessionKey, "cai5_ind1_g1|2026-09-14", "the same key the recording sync uses")

        // A damaged line is skipped, not fatal.
        let handle = try FileHandle(forWritingTo: url)
        try handle.seekToEnd()
        try handle.write(contentsOf: Data("not json\n".utf8))
        try handle.close()
        XCTAssertEqual(store.load().count, 2)

        store.prune()
        XCTAssertEqual(store.load().map(\.title), ["Attendance uploaded"])
    }
}

final class NotificationThrottleTests: XCTestCase {
    private func notice(_ severity: OperationsSeverity, key: String = "g1|2026-09-14|take") -> OperationsNotice {
        OperationsNotice(key: key, title: "CAI5_IND1_G1", body: "Attendance upload failed", severity: severity)
    }

    func testRepeatsAreGroupedAndInfoNeverNotifies() {
        let throttle = NotificationThrottle()
        let start = Date()
        XCTAssertFalse(throttle.decide(notice(.info), now: start).post)

        let first = throttle.decide(notice(.warning), now: start)
        XCTAssertTrue(first.post)
        XCTAssertEqual(first.title, "CAI5_IND1_G1")

        let repeatSoon = throttle.decide(notice(.warning), now: start.addingTimeInterval(15 * 60))
        XCTAssertFalse(repeatSoon.post, "a retry 15 minutes later is grouped, not shown again")
        XCTAssertEqual(repeatSoon.identifier, first.identifier, "repeats replace the same notification")

        let escalated = throttle.decide(notice(.failure), now: start.addingTimeInterval(16 * 60))
        XCTAssertTrue(escalated.post, "getting worse is news")
        XCTAssertEqual(escalated.title, "CAI5_IND1_G1 (×3)")

        XCTAssertFalse(throttle.decide(notice(.failure), now: start.addingTimeInterval(30 * 60)).post)
        XCTAssertTrue(throttle.decide(notice(.failure), now: start.addingTimeInterval(47 * 60)).post, "at most every 30 minutes")

        XCTAssertTrue(throttle.decide(notice(.failure), now: start.addingTimeInterval(3 * 60 * 60)).post, "after the quiet period a key starts over")
        XCTAssertEqual(throttle.decide(notice(.failure, key: "other"), now: start).occurrences, 1)
    }

    func testSuccessClearsAClassesFailures() {
        let throttle = NotificationThrottle()
        let start = Date()
        XCTAssertTrue(throttle.decide(notice(.failure), now: start).post)
        throttle.clear(prefix: "g1|2026-09-14")
        XCTAssertTrue(throttle.decide(notice(.failure), now: start.addingTimeInterval(60)).post)
    }
}

final class SessionOverviewBuilderTests: XCTestCase {
    private let profile = ZoomAccountProfile(name: "G1 account", accountIdentifier: "g1@example.com")
    private lazy var group = StudentGroup(name: "Group One", students: (1...25).map { Student(officialName: "Student \($0)") }, lmsGroupCode: "CAI5_IND1_G1")
    private lazy var schedule = ZoomSchedule(
        name: "CAI5_IND1_G1 — Daily",
        recurrence: .daily,
        startTime: TimeOfDay(hour: 17, minute: 55),
        endTime: TimeOfDay(hour: 21, minute: 0),
        accountProfileID: profile.id,
        meeting: MeetingReference(name: "G1", kind: .meetingID("123456789")),
        attendanceGroupID: group.id
    )
    private var configuration: SchedulerConfiguration { SchedulerConfiguration(accountProfiles: [profile], schedules: [schedule], studentGroups: [group]) }
    private var date: String { LmsFollowUpQueue.dashboardDateAndTime(today(17, 55)).0 }
    private let settings = LmsSettings(runSessionOnMeetingStart: true, takeAttendance: true, correctAttendance: true)

    private func register(finalized: Bool, present: Int = 23) -> AttendanceSession {
        var session = AttendanceSession(groupID: group.id, groupName: group.name, scheduleID: schedule.id, meetingName: "G1", startedAt: today(17, 56), rosterSnapshot: group.students)
        session.records = group.students.enumerated().map { index, student in
            AttendanceRecord(studentID: student.id, studentName: student.officialName, status: index < present ? .present : (index == present ? .needsReview : .absent))
        }
        session.snapshots = (0..<20).map { AttendanceSnapshot(capturedAt: today(18, $0), reason: .periodic, reportedCount: 20, participants: []) }
        if finalized {
            session.endedAt = today(21, 0)
            session.finalizedAt = today(21, 0)
        }
        return session
    }

    private func todayCard(_ inputs: OperationsInputs, now: Date) -> SessionOverview {
        SessionOverviewBuilder.build(inputs, now: now).first { calendar.isDate($0.startsAt, inSameDayAs: now) }!
    }

    private func event(_ kind: OperationsEventKind, _ severity: OperationsSeverity = .success, _ message: String = "") -> OperationsEvent {
        OperationsEvent(at: today(18, 0), kind: kind, severity: severity, groupCode: "CAI5_IND1_G1", sessionDate: date, scheduleID: schedule.id, title: kind.rawValue, message: message)
    }

    private func take(dueAt: Date, attempts: Int = 0, error: String? = nil) -> LmsFollowUp {
        LmsFollowUp(group: "CAI5_IND1_G1", sessionDate: date, sessionStart: "17:55", step: .takeAttendance, dueAt: dueAt, attempts: attempts, lastError: error, attendanceGroupID: group.id, scheduleID: schedule.id)
    }

    private func correct(dueAt: Date) -> LmsFollowUp {
        LmsFollowUp(group: "CAI5_IND1_G1", sessionDate: date, sessionStart: "17:55", step: .correctAttendance, dueAt: dueAt, attendanceGroupID: group.id, scheduleID: schedule.id)
    }

    func testARunningClassDuringItsFirstHour() {
        let now = today(19, 0)
        let inputs = OperationsInputs(
            configuration: configuration,
            attendanceSessions: [register(finalized: false)],
            followUps: [take(dueAt: today(19, 25)), correct(dueAt: today(20, 55))],
            events: [event(.zoomStarted), event(.lmsSessionStarted)],
            lmsSettings: settings,
            recordingSyncEnabled: true
        )
        let card = todayCard(inputs, now: now)
        XCTAssertEqual(card.groupCode, "CAI5_IND1_G1")
        XCTAssertEqual(card.accountName, "G1 account")
        XCTAssertEqual(card.zoom, .running)
        XCTAssertEqual(card.attendance, .recording)
        XCTAssertEqual(card.present, 23)
        XCTAssertEqual(card.rosterCount, 25)
        XCTAssertEqual(card.needsReview, 1)
        XCTAssertEqual(card.attendanceDetail.first, "20 snapshot(s)")
        XCTAssertEqual(card.lms, .attendancePending)
        XCTAssertTrue(card.lmsDetail.contains("✓ Session started"))
        XCTAssertTrue(card.lmsDetail.contains("⏳ Attendance upload in 25 min"))
        XCTAssertEqual(card.recording, .waiting)
        XCTAssertEqual(card.recordingDetail, "Waiting for Drive link")
        XCTAssertEqual(card.nextAction, "Take attendance at 19:25")
        XCTAssertFalse(card.needsAttention)
    }

    func testTheLmsStatesFollowTheQueueAndTheEvents() {
        let now = today(20, 0)
        var inputs = OperationsInputs(configuration: configuration, attendanceSessions: [register(finalized: false)], lmsSettings: settings)

        inputs.followUps = [correct(dueAt: today(20, 55))]
        inputs.events = [event(.lmsSessionStarted), event(.attendanceUploaded, .success, "23 Joined\n2 Not Joined")]
        XCTAssertEqual(todayCard(inputs, now: now).lms, .correctionPending)

        inputs.followUps = []
        inputs.lmsSettings.correctAttendance = false
        XCTAssertEqual(todayCard(inputs, now: now).lms, .completed, "uploaded with the correction switched off is done")
        inputs.lmsSettings.correctAttendance = true
        inputs.events = [event(.lmsSessionStarted), event(.attendanceUploaded)]
        inputs.followUps = []
        XCTAssertEqual(todayCard(inputs, now: now).lms, .attendanceSubmitted)
        inputs.events.append(event(.attendanceCorrected))
        XCTAssertEqual(todayCard(inputs, now: now).lms, .completed)

        inputs.events = [event(.lmsSessionStarted)]
        XCTAssertEqual(todayCard(inputs, now: now).lms, .sessionRunning)

        inputs.followUps = [take(dueAt: today(19, 25), attempts: LmsFollowUpQueue.maximumAttempts, error: "LMS session not found")]
        let failed = todayCard(inputs, now: now)
        XCTAssertEqual(failed.lms, .failed)
        XCTAssertTrue(failed.needsAttention)

        inputs.followUps = [take(dueAt: today(19, 40), attempts: 2, error: "LMS session not found")]
        let retrying = todayCard(inputs, now: now)
        XCTAssertEqual(retrying.lms, .attendancePending)
        XCTAssertEqual(retrying.lastError, "Take attendance: LMS session not found")
        XCTAssertTrue(retrying.lmsDetail.contains("⏳ Attendance upload retrying (attempt 3)"))
    }

    func testAfterClassTheRecordingStateComesFromTheSyncRecord() {
        let now = today(23, 0)
        var record = RecordingSyncRecord(groupCode: "CAI5_IND1_G1", sessionDate: date, sheetTab: "CAI5_IND1_G1", sheetRow: 2, fileName: "f", driveURL: "https://drive.google.com/file/d/1AAAAAAAAAAAAAAAAAAAAAAAAAAAAA/view", now: now)
        var inputs = OperationsInputs(configuration: configuration, attendanceSessions: [register(finalized: true)], events: [event(.zoomStarted)], lmsSettings: settings, recordingSyncEnabled: true, nextRecordingSync: today(23, 59))

        let ended = todayCard(inputs, now: now)
        XCTAssertEqual(ended.zoom, .ended)
        XCTAssertEqual(ended.attendance, .completed)
        XCTAssertEqual(ended.recording, .waiting)
        XCTAssertEqual(ended.nextAction, "Recording sync at 23:59")

        for (state, phase) in [(RecordingSyncState.pending, RecordingPhase.driveFound), (.processing, .uploading), (.attached, .attached), (.conflict, .conflict), (.failed, .failed)] {
            record.state = state
            record.error = state == .conflict ? "The session already has a different record link" : nil
            inputs.recordings = [record]
            let card = todayCard(inputs, now: now)
            XCTAssertEqual(card.recording, phase, "\(state)")
            XCTAssertEqual(card.needsAttention, phase == .conflict || phase == .failed)
        }
        XCTAssertEqual(todayCard(inputs, now: now).lastError, nil, "a failed record without a reason shows no empty error line")
        record.state = .conflict
        record.error = "The session already has a different record link"
        inputs.recordings = [record]
        XCTAssertEqual(todayCard(inputs, now: now).lastError, "The session already has a different record link")
    }

    func testZoomFailureAndUpcomingClasses() {
        let failedInputs = OperationsInputs(configuration: configuration, events: [event(.zoomFailed, .failure, "Reason:\nThe account is not signed in")], lmsSettings: settings)
        let failed = todayCard(failedInputs, now: today(18, 10))
        XCTAssertEqual(failed.zoom, .failed)
        XCTAssertTrue(failed.needsAttention)
        XCTAssertEqual(failed.lastError, "zoomFailed: Reason:\nThe account is not signed in")

        let upcoming = todayCard(OperationsInputs(configuration: configuration, lmsSettings: settings), now: today(17, 0))
        XCTAssertEqual(upcoming.zoom, .notStarted)
        XCTAssertEqual(upcoming.attendance, .waiting)
        XCTAssertEqual(upcoming.lms, .notStarted)
        XCTAssertEqual(upcoming.nextAction, "Health check at 17:25")

        let starting = OperationsInputs(configuration: configuration, workflowScheduleID: schedule.id)
        XCTAssertEqual(todayCard(starting, now: today(17, 56)).zoom, .starting)

        let cards = SessionOverviewBuilder.build(OperationsInputs(configuration: configuration), now: today(12, 0))
        XCTAssertEqual(cards.count, 2, "today and tomorrow")
    }
}

final class HealthCheckerTests: XCTestCase {
    private let profile = ZoomAccountProfile(name: "G1 account", accountIdentifier: "g1@example.com")
    private lazy var group = StudentGroup(name: "Group One", students: [Student(officialName: "A")], lmsGroupCode: "CAI5_IND1_G1")
    private lazy var schedule = ZoomSchedule(
        name: "CAI5_IND1_G1 — Monday",
        recurrence: .daily,
        startTime: TimeOfDay(hour: 17, minute: 55),
        accountProfileID: profile.id,
        meeting: MeetingReference(name: "G1", kind: .meetingID("123456789")),
        attendanceGroupID: group.id
    )
    private var configuration: SchedulerConfiguration { SchedulerConfiguration(accountProfiles: [profile], schedules: [schedule], studentGroups: [group]) }
    private let settings = LmsSettings(runSessionOnMeetingStart: true, takeAttendance: true)

    private func probes(
        issues: [PreflightIssue] = [],
        lms: HealthProbeResults.LmsProbe? = .init(signedIn: true, sessions: 1, message: "1 session"),
        sheet: HealthProbeResults.SheetProbe? = .init(tokenValid: true, tabs: ["CAI5_IND1_G1", "CAI5_IND1_G2"], message: "2 tabs"),
        connectedAt: Date? = nil,
        testingMode: Bool = false
    ) -> HealthProbeResults {
        HealthProbeResults(
            zoomInstalled: true,
            preflight: PreflightReport(scheduleName: schedule.name, startsAt: today(17, 55), issues: issues),
            lmsCredentialsSaved: true,
            lms: lms,
            recordingSyncEnabled: true,
            googleConnected: true,
            spreadsheetConfigured: true,
            sheet: sheet,
            googleConnectedAt: connectedAt,
            googleTestingMode: testingMode,
            helperProblem: nil,
            freeDiskBytes: 50 * 1024 * 1024 * 1024
        )
    }

    private func report(_ probes: HealthProbeResults, now: Date = Date()) -> HealthReport {
        HealthChecker.report(schedule: schedule, startsAt: today(17, 55), configuration: configuration, lmsSettings: settings, probes: probes, now: now)
    }

    func testEverythingPassing() {
        let result = report(probes())
        XCTAssertTrue(result.isReady)
        XCTAssertTrue(result.warnings.isEmpty, result.text)
        XCTAssertTrue(result.includesNetwork)
        XCTAssertEqual(result.headline, "CAI5_IND1_G1 — Monday is ready")
        XCTAssertTrue(result.text.hasSuffix("Ready to start session"))
        for name in ["Zoom installed", "Accessibility permission", "Zoom account", "Meeting can start", "Credentials available", "Login works", "Group mapping", "Session exists", "OAuth token valid", "Spreadsheet accessible", "Recording sync enabled", "Disk space available", "Node helper available"] {
            XCTAssertEqual(result.items.first { $0.name == name }?.state, .pass, name)
        }
    }

    func testAnExpiredLmsLoginSaysWhyAndHowToFixIt() {
        let result = report(probes(lms: .init(signedIn: false, sessions: nil, message: "The dashboard stayed on the sign-in page; check the email and password.")))
        XCTAssertFalse(result.isReady)
        XCTAssertEqual(result.failures.map(\.name), ["Login works"])
        XCTAssertTrue(result.text.contains("Reason: Login works — The dashboard stayed on the sign-in page"))
        XCTAssertTrue(result.text.contains("Fix: Reconnect the LMS account in Automation → LMS"))
    }

    func testLocalOnlyChecksSkipTheNetworkParts() {
        let result = report(probes(lms: nil, sheet: nil))
        XCTAssertTrue(result.isReady)
        XCTAssertFalse(result.includesNetwork)
        XCTAssertEqual(result.items.first { $0.name == "Login works" }?.state, .skipped)
        XCTAssertEqual(result.items.first { $0.name == "OAuth token valid" }?.state, .skipped)
        XCTAssertTrue(result.text.contains("LMS and Google were not contacted"))
    }

    func testWarningsForTheSessionListTabsAndATestingModeToken() {
        let connected = Date().addingTimeInterval(-5 * 24 * 60 * 60 - 3600)
        let result = report(probes(
            lms: .init(signedIn: true, sessions: 0, message: "0"),
            sheet: .init(tokenValid: true, tabs: ["Archive"], message: "1 tab"),
            connectedAt: connected,
            testingMode: true
        ))
        XCTAssertTrue(result.isReady, "warnings never block")
        XCTAssertEqual(result.items.first { $0.name == "Session exists" }?.state, .warning)
        XCTAssertEqual(result.items.first { $0.name == "Spreadsheet accessible" }?.state, .warning)
        XCTAssertEqual(result.items.first { $0.name == "OAuth token valid" }?.detail, "Recording sync token expires in 1 day(s)")
        XCTAssertEqual(report(probes(connectedAt: connected, testingMode: false)).warnings.count, 0, "no expiry warning for a published Google app")
    }

    func testZoomFindingsComeFromThePreflightChecker() {
        let noAccess = report(probes(issues: [PreflightIssue(kind: .accessibilityMissing, severity: .blocking, message: "Accessibility permission is missing", remedy: "Grant it")]))
        XCTAssertEqual(noAccess.failures.map(\.name), ["Accessibility permission"])
        XCTAssertEqual(noAccess.items.first { $0.name == "Zoom account" }?.state, .skipped)

        let notSignedIn = report(probes(issues: [PreflightIssue(kind: .accountNotFound, severity: .blocking, message: "g1@example.com isn't signed in to Zoom", remedy: "Sign in")]))
        XCTAssertEqual(notSignedIn.failures.first?.name, "Zoom account")
        XCTAssertEqual(notSignedIn.failures.first?.fix, "Sign in")

        let twin = StudentGroup(name: "Other", lmsGroupCode: "cai5_ind1_g1")
        var shared = configuration
        shared.studentGroups.append(twin)
        let mapping = HealthChecker.report(schedule: schedule, startsAt: today(17, 55), configuration: shared, lmsSettings: settings, probes: probes())
        XCTAssertEqual(mapping.failures.map(\.name), ["Group mapping"])
    }
}
