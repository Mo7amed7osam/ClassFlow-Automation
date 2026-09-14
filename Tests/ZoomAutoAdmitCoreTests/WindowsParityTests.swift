import Foundation
import XCTest
@testable import ZoomAutoAdmitCore

private let cairo: Calendar = {
    var calendar = Calendar(identifier: .gregorian)
    calendar.timeZone = TimeZone(identifier: "Africa/Cairo")!
    return calendar
}()

private func date(_ year: Int, _ month: Int, _ day: Int, _ hour: Int, _ minute: Int) -> Date {
    cairo.date(from: DateComponents(year: year, month: month, day: day, hour: hour, minute: minute))!
}

final class LmsFollowUpQueueTests: XCTestCase {
    private var url: URL!

    override func setUp() {
        url = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString)
            .appendingPathComponent("follow-up.json")
    }

    override func tearDown() {
        try? FileManager.default.removeItem(at: url.deletingLastPathComponent())
    }

    func testSchedulingTheSameClassTwiceChangesNothing() {
        let queue = LmsFollowUpQueue(fileURL: url)
        let start = date(2026, 9, 3, 19, 0)
        let first = queue.schedule(group: "CAI5_AIS4_S7", sessionStartedAt: start, steps: [.takeAttendance, .correctAttendance], attendanceGroupID: nil, scheduleID: nil, calendar: cairo)
        XCTAssertEqual(first.count, 2)
        XCTAssertEqual(first.first?.sessionDate, "2026-09-03")
        XCTAssertEqual(first.first?.sessionStart, "19:00")
        XCTAssertEqual(first.first?.dueAt, start.addingTimeInterval(90 * 60))

        let again = queue.schedule(group: "cai5_ais4_s7", sessionStartedAt: start, steps: [.takeAttendance, .correctAttendance], attendanceGroupID: nil, scheduleID: nil, calendar: cairo)
        XCTAssertTrue(again.isEmpty)
        XCTAssertEqual(queue.read().count, 2)
    }

    func testDueFailRetryAndGiveUp() {
        let queue = LmsFollowUpQueue(fileURL: url)
        let start = date(2026, 9, 3, 19, 0)
        queue.schedule(group: "G", sessionStartedAt: start, steps: [.takeAttendance], attendanceGroupID: nil, scheduleID: nil, calendar: cairo)

        XCTAssertTrue(queue.due(at: start.addingTimeInterval(60 * 60)).isEmpty)
        let now = start.addingTimeInterval(91 * 60)
        guard let item = queue.due(at: now).first else { return XCTFail("expected a due step") }

        queue.fail(item, reason: "page did not load", at: now)
        XCTAssertTrue(queue.due(at: now).isEmpty, "a failed step waits before retrying")
        XCTAssertEqual(queue.due(at: now.addingTimeInterval(16 * 60)).first?.attempts, 1)

        for _ in 1..<LmsFollowUpQueue.maximumAttempts {
            let current = queue.read()[0]
            queue.fail(current, reason: "still failing", at: now)
        }
        XCTAssertTrue(queue.due(at: now.addingTimeInterval(60 * 60)).isEmpty, "given up after the maximum attempts")
        XCTAssertEqual(queue.read().count, 1, "a step that was given up on stays visible")

        queue.complete(queue.read()[0])
        XCTAssertTrue(queue.read().isEmpty)
    }

    func testWorkOlderThanADayAndAHalfIsNotDone() {
        let queue = LmsFollowUpQueue(fileURL: url)
        let start = date(2026, 9, 1, 19, 0)
        queue.schedule(group: "G", sessionStartedAt: start, steps: [.takeAttendance], attendanceGroupID: nil, scheduleID: nil, calendar: cairo)
        XCTAssertTrue(queue.due(at: start.addingTimeInterval(40 * 60 * 60)).isEmpty)
    }
}

final class LmsPresentNamesTests: XCTestCase {
    func testOnlyPresentRecordsAreSentAndTheRightRegisterIsFound() {
        let groupID = UUID()
        let ahmed = Student(officialName: "Ahmed Ali")
        let mona = Student(officialName: "Mona Samir")
        let omar = Student(officialName: "Omar Adel")
        var morning = AttendanceSession(groupID: groupID, groupName: "G", meetingName: "m", startedAt: date(2026, 9, 3, 10, 2), rosterSnapshot: [ahmed, mona, omar])
        morning.records = [
            AttendanceRecord(studentID: omar.id, studentName: omar.officialName, status: .present),
            AttendanceRecord(studentID: ahmed.id, studentName: ahmed.officialName, status: .present),
            AttendanceRecord(studentID: mona.id, studentName: mona.officialName, status: .needsReview)
        ]
        let evening = AttendanceSession(groupID: groupID, groupName: "G", meetingName: "m", startedAt: date(2026, 9, 3, 19, 1), rosterSnapshot: [ahmed])
        let otherGroup = AttendanceSession(groupID: UUID(), groupName: "X", meetingName: "m", startedAt: date(2026, 9, 3, 10, 0), rosterSnapshot: [])

        XCTAssertEqual(LmsPresentNames.present(in: morning), ["Ahmed Ali", "Omar Adel"], "roster order, Present only")
        XCTAssertEqual(LmsPresentNames.needsReview(in: morning), ["Mona Samir"])

        let item = LmsFollowUp(group: "G", sessionDate: "2026-09-03", sessionStart: "10:00", step: .takeAttendance, dueAt: Date(), attendanceGroupID: groupID)
        XCTAssertEqual(LmsPresentNames.session(for: item, in: [evening, otherGroup, morning], calendar: cairo)?.id, morning.id)
        let nextDay = LmsFollowUp(group: "G", sessionDate: "2026-09-04", sessionStart: "10:00", step: .takeAttendance, dueAt: Date(), attendanceGroupID: groupID)
        XCTAssertNil(LmsPresentNames.session(for: nextDay, in: [morning, evening], calendar: cairo))
    }

    func testDashboardGroupNamePrefersTheLmsCode() {
        XCTAssertEqual(StudentGroup(name: "Saturday AI").dashboardGroupName, "Saturday AI")
        XCTAssertEqual(StudentGroup(name: "Saturday AI", lmsGroupCode: " CAI5_AIS4_S7 ").dashboardGroupName, "CAI5_AIS4_S7")
        XCTAssertEqual(StudentGroup(name: "G", lmsGroupCode: "  ").dashboardGroupName, "G")
    }

    func testGroupsSavedBeforeTheNewFieldsStillLoad() throws {
        let json = #"{"id":"6F9619FF-8B86-D011-B42D-00C04FC964FF","name":"Old","students":[]}"#
        let group = try JSONDecoder().decode(StudentGroup.self, from: Data(json.utf8))
        XCTAssertNil(group.lmsGroupCode)
        XCTAssertTrue(group.coHostCandidates.isEmpty)
    }
}

final class TimetableImporterTests: XCTestCase {
    private let account = ZoomAccountProfile(name: "DEPI", accountIdentifier: "a@example.com")

    private func row(_ number: String, _ date: String?, _ time: String?, type: String = "Online", issue: String = "") -> TimetableRow {
        TimetableRow(rowNumber: 1, sessionNumber: number, date: date, type: type, topic: "Topic \(number)", startTime: time, endTime: "22:00", timeRange: "", issue: issue)
    }

    func testOnlineFutureRowsBecomeDisabledOneTimeSchedules() {
        let timetable = Timetable(groupCode: "CAI5_AIS4_S8", rows: [
            row("1", "2026-09-01", "19:00"),
            row("2", "2026-09-10", "19:00"),
            row("3", "2026-09-12", "19:00", type: "Physical", issue: "Excluded: Physical"),
            row("4", "2026-09-14", "19:00")
        ])
        let existing = ZoomSchedule(name: "Already", recurrence: .oneTime(year: 2026, month: 9, day: 14), startTime: TimeOfDay(hour: 19, minute: 0), accountProfileID: account.id, meeting: MeetingReference(name: "m", kind: .meetingID("1")))
        let groupID = UUID()
        let plan = TimetableImporter.plan(
            timetable: timetable,
            account: account,
            meeting: MeetingReference(name: "CAI5_AIS4_S8", kind: .meetingID("94698416251")),
            attendanceGroupID: groupID,
            existing: [existing],
            enable: false,
            now: date(2026, 9, 5, 12, 0),
            calendar: cairo
        )

        XCTAssertEqual(plan.schedules.count, 1)
        let schedule = plan.schedules[0]
        XCTAssertEqual(schedule.name, "CAI5_AIS4_S8 • 2 • Topic 2")
        XCTAssertEqual(schedule.recurrence, .oneTime(year: 2026, month: 9, day: 10))
        XCTAssertEqual(schedule.startTime, TimeOfDay(hour: 19, minute: 0))
        XCTAssertEqual(schedule.endTime, TimeOfDay(hour: 22, minute: 0))
        XCTAssertFalse(schedule.isEnabled, "imports never start real meetings until enabled")
        XCTAssertEqual(schedule.attendanceGroupID, groupID)
        XCTAssertEqual(plan.skipped.map(\.skipReason), ["Already past", "Excluded: Physical", "This account already has a meeting at that date and time"])
    }
}

final class CoHostMatcherTests: XCTestCase {
    private let mohab = CoHostCandidate(name: "Mohab Mohamed")
    private let sara = CoHostCandidate(name: "Sara Ali", aliases: ["Sara A."])

    func testConfiguredNamesAliasesAndDecoratedNames() {
        XCTAssertEqual(CoHostMatcher.match(observedName: "mohab  mohamed", candidates: [mohab, sara])?.source, .configuredName)
        XCTAssertEqual(CoHostMatcher.match(observedName: "Sara A.", candidates: [mohab, sara])?.candidate.name, "Sara Ali")
        XCTAssertEqual(CoHostMatcher.match(observedName: "Mohab Mohamed __Coordinator", candidates: [mohab, sara])?.confidence, 96)
        XCTAssertEqual(CoHostMatcher.match(observedName: "Mohab", candidates: [mohab, sara])?.candidate.name, "Mohab Mohamed")
    }

    func testSomebodyElseIsNeverMatched() {
        XCTAssertNil(CoHostMatcher.match(observedName: "Mohab Ahmed", candidates: [mohab, sara]))
        XCTAssertNil(CoHostMatcher.match(observedName: "Mohamed Mohab", candidates: [mohab]), "word order matters")
        let other = CoHostCandidate(name: "Mohab Ahmed")
        XCTAssertNil(CoHostMatcher.match(observedName: "Mohab", candidates: [mohab, other]), "a shared first name is ambiguous")
    }

    func testRememberedAssignmentsCountOnlyWhileThePersonIsListed() {
        let groupID = UUID()
        let history = [CoHostAssignmentRecord(groupID: groupID, candidateName: "Mohab Mohamed", observedName: "M. Instructor", assignedAt: Date())]
        XCTAssertEqual(CoHostMatcher.match(observedName: "M. Instructor", candidates: [mohab], history: history, groupID: groupID)?.source, .previousAssignment)
        XCTAssertNil(CoHostMatcher.match(observedName: "M. Instructor", candidates: [sara], history: history, groupID: groupID))
        XCTAssertNil(CoHostMatcher.match(observedName: "M. Instructor", candidates: [mohab], history: history, groupID: UUID()))
    }
}

final class ZoomEngineAllocatorTests: XCTestCase {
    func testAutoMovesToWebOnlyWhenTheDesktopIsBusy() {
        let allocator = ZoomEngineAllocator()
        let first = UUID()
        let second = UUID()
        XCTAssertEqual(allocator.allocate(scheduleID: first, preference: .auto, hasWebLink: true, desktopHasActiveMeeting: false), .desktop)
        XCTAssertEqual(allocator.allocate(scheduleID: second, preference: .auto, hasWebLink: true, desktopHasActiveMeeting: false), .web, "the desktop is reserved by the first class")
        allocator.release(scheduleID: first)
        XCTAssertEqual(allocator.allocate(scheduleID: second, preference: .auto, hasWebLink: true, desktopHasActiveMeeting: true), .web)
        XCTAssertEqual(allocator.allocate(scheduleID: second, preference: .web, hasWebLink: true, desktopHasActiveMeeting: false), .web)
        XCTAssertEqual(allocator.allocate(scheduleID: UUID(), preference: .web, hasWebLink: false, desktopHasActiveMeeting: false), .desktop, "a personal meeting has no web link")
    }

    func testWebLinkCarriesThePasscode() {
        let meeting = MeetingReference(name: "m", kind: .meetingID("https://us06web.zoom.us/j/94698416251?pwd=abc.1"), passcode: "abc.1")
        XCTAssertEqual(ZoomEngineAllocator.webLink(for: meeting)?.absoluteString, "https://zoom.us/j/94698416251?pwd=abc.1")
        XCTAssertNil(ZoomEngineAllocator.webLink(for: MeetingReference(name: "m", kind: .instantMeeting)))
    }

    func testProfilesSavedBeforeEnginesKeepUsingTheDesktop() throws {
        let json = #"{"id":"6F9619FF-8B86-D011-B42D-00C04FC964FF","name":"DEPI Main","accountIdentifier":"a@b.c"}"#
        let profile = try JSONDecoder().decode(ZoomAccountProfile.self, from: Data(json.utf8))
        XCTAssertEqual(profile.preferredEngine, .desktop)
        XCTAssertEqual(profile.resolvedWebProfileName, "DEPI-Main")
    }

    func testWebAttendanceDropsTheHostAndKeepsStudents() {
        XCTAssertEqual(WebAttendanceRecorder.stripRole("Coordinator (Host, Me)").isHost, true)
        XCTAssertEqual(WebAttendanceRecorder.stripRole("Ahmed Ali (Guest)").name, "Ahmed Ali")
        XCTAssertEqual(WebAttendanceRecorder.stripRole("Ahmed Ali (Guest)").isHost, false)
        XCTAssertEqual(WebAttendanceRecorder.stripRole("Mona (Cairo)").name, "Mona (Cairo)")
    }
}

final class LaunchAgentSchedulerTests: XCTestCase {
    func testWakeTimesComeAheadOfEachStartInTheHorizon() {
        let profile = UUID()
        let weekly = ZoomSchedule(name: "Sat", recurrence: .selectedWeekdays([.saturday]), startTime: TimeOfDay(hour: 18, minute: 0), accountProfileID: profile, meeting: MeetingReference(name: "m", kind: .meetingID("1")), launchZoomMinutesEarly: 2)
        let disabled = ZoomSchedule(name: "Off", isEnabled: false, recurrence: .daily, startTime: TimeOfDay(hour: 9, minute: 0), accountProfileID: profile, meeting: MeetingReference(name: "m", kind: .meetingID("1")))
        let configuration = SchedulerConfiguration(schedules: [weekly, disabled])
        // 2026-08-28 is a Friday.
        let dates = LaunchAgentScheduler.launchDates(configuration: configuration, now: date(2026, 8, 28, 12, 0), calendar: cairo)
        XCTAssertEqual(dates.count, 2, "two Saturdays inside fourteen days")
        XCTAssertEqual(dates.first, date(2026, 8, 29, 17, 53))

        let plist = LaunchAgentScheduler.plist(bundleIdentifier: "com.example.app", dates: dates, calendar: cairo)
        XCTAssertEqual(plist["ProgramArguments"] as? [String], ["/usr/bin/open", "-g", "-b", "com.example.app"])
        let intervals = plist["StartCalendarInterval"] as? [[String: Int]]
        XCTAssertEqual(intervals?.first, ["Month": 8, "Day": 29, "Hour": 17, "Minute": 53])
    }
}

final class AutomationMessageTests: XCTestCase {
    func testHelperLinesParse() {
        XCTAssertEqual(AutomationMessage.parse(line: #"{"type":"log","level":"warn","message":"slow"}"#), .log(level: "warn", message: "slow"))
        guard case .result(let body)? = AutomationMessage.parse(line: #"{"type":"result","success":true,"plan":{"joinedCount":2},"present":["A"]}"#) else {
            return XCTFail("expected a result")
        }
        XCTAssertEqual(body["success"], .bool(true))
        XCTAssertEqual(body["plan"]?["joinedCount"]?.number, 2)
        XCTAssertEqual(body["present"]?.array?.first?.string, "A")
        XCTAssertEqual(AutomationMessage.parse(line: "plain text"), .log(level: "info", message: "plain text"))
        XCTAssertNil(AutomationMessage.parse(line: "   "))
    }

    func testLineBufferJoinsSplitWrites() {
        var lines: [String] = []
        let buffer = LineBuffer { lines.append($0) }
        buffer.append(Data("{\"a\":".utf8))
        buffer.append(Data("1}\nsecond\nthi".utf8))
        buffer.append(Data("rd".utf8))
        buffer.flush()
        XCTAssertEqual(lines, ["{\"a\":1}", "second", "third"])
    }

    func testTheRealHelperAnswersWhenNodeIsInstalled() throws {
        guard case .success = AutomationEnvironment.locate(bundle: Bundle(for: Self.self)) else {
            throw XCTSkip("Node.js or the helper's packages are not installed here")
        }
        let helper = AutomationHelper(environmentProvider: { AutomationEnvironment.locate(bundle: Bundle(for: Self.self)) })
        let version = helper.run("version", request: [:], timeout: 30)
        XCTAssertTrue(version.success, version.message)
        let missing = helper.run("lms-run-session", request: [:], timeout: 30)
        XCTAssertFalse(missing.success)
        XCTAssertEqual(missing.message, "No group was given.")
    }
}

final class LmsGroupMappingTests: XCTestCase {
    /// The real data problem: G2's roster renamed to G1's name.
    func testTwoRostersAnsweringToOneDashboardGroupAreRefused() {
        let g1 = StudentGroup(name: "CAI5_IND1_G1", students: Array(repeating: Student(officialName: "a"), count: 24))
        let g2 = StudentGroup(name: "CAI5_IND1_G1", students: Array(repeating: Student(officialName: "b"), count: 19))
        let broken = SchedulerConfiguration(studentGroups: [g1, g2])
        XCTAssertEqual(LmsGroupMapping.sharedDashboardGroups(in: broken).count, 1)
        XCTAssertNotNil(LmsGroupMapping.problem(for: g2, in: broken))

        var fixedG1 = g1
        fixedG1.lmsGroupCode = "CAI5_IND1_G1"
        var fixedG2 = g2
        fixedG2.name = "CAI5_IND1_G2"
        fixedG2.lmsGroupCode = "CAI5_IND1_G2"
        let fixed = SchedulerConfiguration(studentGroups: [fixedG1, fixedG2])
        XCTAssertTrue(LmsGroupMapping.sharedDashboardGroups(in: fixed).isEmpty)
        XCTAssertNil(LmsGroupMapping.problem(for: fixedG2, in: fixed))

        // A code shared through different spellings is still shared.
        var sneaky = fixedG2
        sneaky.lmsGroupCode = " cai5_ind1_g1 "
        XCTAssertNotNil(LmsGroupMapping.problem(for: sneaky, in: SchedulerConfiguration(studentGroups: [fixedG1, sneaky])))
    }

    func testAQueuedStepStopsWhenItsGroupNowMapsElsewhere() {
        var group = StudentGroup(name: "CAI5_IND1_G2", lmsGroupCode: "CAI5_IND1_G2")
        let item = LmsFollowUp(group: "CAI5_IND1_G1", sessionDate: "2026-09-15", sessionStart: "17:55", step: .takeAttendance, dueAt: Date(), attendanceGroupID: group.id)
        let configuration = SchedulerConfiguration(studentGroups: [group])
        XCTAssertEqual(LmsGroupMapping.problem(for: item, in: configuration), .changedSinceScheduled(expected: "CAI5_IND1_G1", now: "CAI5_IND1_G2"))

        group.lmsGroupCode = "CAI5_IND1_G1"
        XCTAssertNil(LmsGroupMapping.problem(for: item, in: SchedulerConfiguration(studentGroups: [group])))
        XCTAssertEqual(LmsGroupMapping.problem(for: item, in: SchedulerConfiguration()), .groupMissing)
    }
}
