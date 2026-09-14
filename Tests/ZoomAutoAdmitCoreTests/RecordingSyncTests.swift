import Foundation
import XCTest
@testable import ZoomAutoAdmitCore

private let drive1 = "https://drive.google.com/file/d/1AAAAAAAAAAAAAAAAAAAAAAAAAAAAA/view?usp=sharing"
private let drive2 = "https://drive.google.com/file/d/2BBBBBBBBBBBBBBBBBBBBBBBBBBBBB/view?usp=sharing"

private func cairoDate(_ year: Int, _ month: Int, _ day: Int, _ hour: Int, _ minute: Int) -> Date {
    var calendar = Calendar(identifier: .gregorian)
    calendar.timeZone = TimeZone(identifier: "Africa/Cairo")!
    return calendar.date(from: DateComponents(year: year, month: month, day: day, hour: hour, minute: minute))!
}

final class RecordingSheetParserTests: XCTestCase {
    /// The shape Sheets returns with includeGridData, after chips are normalized.
    private let fixture = """
    {"sheets":[
      {"properties":{"title":"CAI5_IND1_G1"},"data":[{"rowData":[
        {"values":[{"formattedValue":"File Name"},{"formattedValue":"Type"},{"formattedValue":"Date"},{"formattedValue":"Shared Link"}]},
        {"values":[{"formattedValue":"Week 9 S3.mp4"},{"formattedValue":"Recording"},{"formattedValue":"12/09/2026","effectiveValue":{"numberValue":46277}},{"formattedValue":"Week 9 S3.mp4","hyperlink":"\(drive1)"}]},
        {"values":[{"formattedValue":"Week 9 S4.mp4"},{"formattedValue":"Recording"},{"formattedValue":"14/09/2026"},{"formattedValue":""}]},
        {"values":[{"formattedValue":"Folder"},{"formattedValue":"Recording"},{"formattedValue":"2026-09-15"},{"formattedValue":"https://drive.google.com/drive/folders/1AAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}]},
        {},
        {"values":[{"formattedValue":"Chip"},{"formattedValue":"Recording"},{"formattedValue":"Sep 16, 2026"},{"formattedValue":"Week 9","chipRuns":[{"chipProperties":{"richLinkProperties":{"uri":"\(drive2)"}}}]}]}
      ]}]},
      {"properties":{"title":"CAI5_IND1_G2"},"data":[{"rowData":[
        {"values":[{"formattedValue":"G2.mp4"},{"formattedValue":"Recording"},{"formattedValue":"2026-09-12"},{"formattedValue":"\(drive2)"}]}
      ]}]},
      {"properties":{"title":"Notes"},"data":[{"rowData":[]}]}
    ]}
    """

    func testTabsAreReadDynamicallyWithDatesAndLinksFromEveryCellForm() throws {
        let tabs = try RecordingSheetParser.parse(spreadsheetJSON: Data(fixture.utf8))
        XCTAssertEqual(tabs.map(\.title), ["CAI5_IND1_G1", "CAI5_IND1_G2", "Notes"])
        let g1 = tabs[0].rows
        XCTAssertEqual(g1.count, 4, "header and empty rows are skipped")
        XCTAssertEqual(g1[0].date, "2026-09-12", "a real date cell reads from its serial number")
        XCTAssertEqual(g1[0].driveURL, drive1, "a link on text reads from the hyperlink")
        XCTAssertEqual(g1[0].rowNumber, 2)
        XCTAssertNil(g1[1].driveURL, "missing link")
        XCTAssertEqual(g1[1].date, "2026-09-14", "day-first text date")
        XCTAssertNil(g1[2].driveURL, "a folder is not a recording file")
        XCTAssertEqual(g1[3].driveURL, drive2, "a smart chip's link is read")
        XCTAssertEqual(g1[3].date, "2026-09-16")
        XCTAssertEqual(tabs[1].rows.first?.driveURL, drive2)
    }

    func testDateForms() {
        XCTAssertEqual(RecordingSheetParser.dateFromSerial(46277), "2026-09-12")
        XCTAssertEqual(RecordingSheetParser.parseDateText("2026-09-12"), "2026-09-12")
        XCTAssertEqual(RecordingSheetParser.parseDateText("12/9/2026"), "2026-09-12")
        XCTAssertEqual(RecordingSheetParser.parseDateText("9/13/2026"), "2026-09-13", "month first only when the second part cannot be a month")
        XCTAssertEqual(RecordingSheetParser.parseDateText("12 Sep 2026"), "2026-09-12")
        XCTAssertNil(RecordingSheetParser.parseDateText("31/02/2026"))
        XCTAssertNil(RecordingSheetParser.parseDateText("soon"))
    }

    func testDriveLinkRulesMatchTheHelper() {
        XCTAssertEqual(RecordingLinkRules.driveFileID(drive1), "1AAAAAAAAAAAAAAAAAAAAAAAAAAAAA")
        XCTAssertEqual(RecordingLinkRules.driveFileID("https://drive.google.com/open?id=1AAAAAAAAAAAAAAAAAAAAAAAAAAAAA"), "1AAAAAAAAAAAAAAAAAAAAAAAAAAAAA")
        XCTAssertNil(RecordingLinkRules.driveFileID("http://drive.google.com/file/d/1AAAAAAAAAAAAAAAAAAAAAAAAAAAAA/view"))
        XCTAssertNil(RecordingLinkRules.driveFileID("https://zoom.us/rec/share/abc"))
    }
}

final class RecordingSyncPlannerTests: XCTestCase {
    private let g1 = StudentGroup(name: "Group One", lmsGroupCode: "CAI5_IND1_G1")
    private let g2 = StudentGroup(name: "CAI5_IND1_G2")

    private func row(_ tab: String, _ number: Int, _ date: String?, _ link: String?) -> RecordingSheetRow {
        RecordingSheetRow(tab: tab, rowNumber: number, fileName: "f\(number)", type: "Recording", date: date, rawDate: date ?? "??", driveURL: link, rawLink: link ?? "")
    }

    func testMultipleTabsBecomeOneRecordPerGroupAndDate() {
        let tabs = [
            RecordingSheetTab(title: "CAI5_IND1_G1", rows: [row("CAI5_IND1_G1", 2, "2026-09-12", drive1), row("CAI5_IND1_G1", 3, "2026-09-14", nil), row("CAI5_IND1_G1", 4, nil, drive2)]),
            RecordingSheetTab(title: "cai5_ind1_g2", rows: [row("cai5_ind1_g2", 2, "2026-09-12", drive2), row("cai5_ind1_g2", 3, "2026-09-12", drive2)]),
            RecordingSheetTab(title: "Archive", rows: [row("Archive", 2, "2026-09-01", drive1)])
        ]
        let (records, report) = RecordingSyncPlanner.merge(tabs: tabs, groups: [g1, g2], existing: [])
        XCTAssertEqual(Set(records.map(\.id)), ["cai5_ind1_g1|2026-09-12", "cai5_ind1_g2|2026-09-12"])
        XCTAssertEqual(records.first { $0.groupCode == "CAI5_IND1_G1" }?.state, .pending, "the tab maps through the group's LMS code")
        XCTAssertEqual(records.first { $0.groupCode == "CAI5_IND1_G2" }?.state, .pending, "the same link twice is one record")
        XCTAssertEqual(report.unknownTabs, ["Archive"])
        XCTAssertEqual(report.rowsWithoutLink, 1)
        XCTAssertEqual(report.rowsWithoutDate.count, 1)
    }

    func testTwoDifferentDriveLinksForOneSessionAreAConflict() {
        let tabs = [RecordingSheetTab(title: "CAI5_IND1_G1", rows: [row("CAI5_IND1_G1", 2, "2026-09-12", drive1), row("CAI5_IND1_G1", 3, "2026-09-12", drive2)])]
        let record = RecordingSyncPlanner.merge(tabs: tabs, groups: [g1], existing: []).records.first
        XCTAssertEqual(record?.state, .conflict)
        XCTAssertEqual(record?.issue, .multipleDriveLinksInSheet)

        // The sheet is cleaned up down to the first link: the conflict clears on the next read.
        let cleaned = [RecordingSheetTab(title: "CAI5_IND1_G1", rows: [row("CAI5_IND1_G1", 2, "2026-09-12", drive1)])]
        let fixed = RecordingSyncPlanner.merge(tabs: cleaned, groups: [g1], existing: [record!]).records.first
        XCTAssertEqual(fixed?.state, .pending)
        XCTAssertNil(fixed?.issue)
        // Down to the other link: also back to pending, with that link.
        let other = [RecordingSheetTab(title: "CAI5_IND1_G1", rows: [row("CAI5_IND1_G1", 3, "2026-09-12", drive2)])]
        let switched = RecordingSyncPlanner.merge(tabs: other, groups: [g1], existing: [record!]).records.first
        XCTAssertEqual(switched?.state, .pending)
        XCTAssertEqual(switched?.driveURL, drive2)
    }

    func testAnLmsConflictIsNotClearedByReadingTheSameSheetAgain() {
        var conflict = RecordingSyncRecord(groupCode: "CAI5_IND1_G1", sessionDate: "2026-09-12", sheetTab: "CAI5_IND1_G1", sheetRow: 2, fileName: "f", driveURL: drive1, now: Date())
        conflict.state = .conflict
        conflict.issue = .existingLinkDiffers
        let same = [RecordingSheetTab(title: "CAI5_IND1_G1", rows: [row("CAI5_IND1_G1", 2, "2026-09-12", drive1)])]
        XCTAssertEqual(RecordingSyncPlanner.merge(tabs: same, groups: [g1], existing: [conflict]).records.first?.state, .conflict)
    }

    func testAlreadyProcessedRowsAreNeverUpdatedTwice() {
        var attached = RecordingSyncRecord(groupCode: "CAI5_IND1_G1", sessionDate: "2026-09-12", sheetTab: "CAI5_IND1_G1", sheetRow: 2, fileName: "f", driveURL: drive1, now: Date())
        attached.state = .attached
        attached.completedAt = Date()
        let same = [RecordingSheetTab(title: "CAI5_IND1_G1", rows: [row("CAI5_IND1_G1", 2, "2026-09-12", "https://drive.google.com/file/d/1AAAAAAAAAAAAAAAAAAAAAAAAAAAAA/view")])]
        let unchanged = RecordingSyncPlanner.merge(tabs: same, groups: [g1], existing: [attached])
        XCTAssertEqual(unchanged.records.first?.state, .attached)
        XCTAssertEqual(unchanged.report.unchanged, 1)
        XCTAssertFalse(unchanged.records.first!.isDueForAutomaticSync)

        let changed = [RecordingSheetTab(title: "CAI5_IND1_G1", rows: [row("CAI5_IND1_G1", 2, "2026-09-12", drive2)])]
        let result = RecordingSyncPlanner.merge(tabs: changed, groups: [g1], existing: [attached]).records.first
        XCTAssertEqual(result?.state, .conflict, "a new link for an attached session is for a person to decide")
        XCTAssertEqual(result?.issue, .sheetLinkChanged)
        XCTAssertEqual(result?.driveURL, drive1, "the attached link is still what the record says")
    }
}

final class RecordingSyncGateTests: XCTestCase {
    func testAttendanceWorkflowAndMappingMustBeDone() {
        let group = StudentGroup(name: "CAI5_IND1_G1", lmsGroupCode: "CAI5_IND1_G1")
        let configuration = SchedulerConfiguration(studentGroups: [group])
        let record = RecordingSyncRecord(groupCode: "CAI5_IND1_G1", sessionDate: "2026-09-12", sheetTab: "CAI5_IND1_G1", sheetRow: 2, fileName: "f", driveURL: drive1, now: Date())
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "Africa/Cairo")!

        XCTAssertNil(RecordingSyncGate.blocker(for: record, configuration: configuration, followUps: [], sessions: [], calendar: calendar))

        let queued = LmsFollowUp(group: "CAI5_IND1_G1", sessionDate: "2026-09-12", sessionStart: "13:55", step: .correctAttendance, dueAt: Date(), attendanceGroupID: group.id)
        XCTAssertEqual(RecordingSyncGate.blocker(for: record, configuration: configuration, followUps: [queued], sessions: [], calendar: calendar)?.issue, .attendanceNotComplete)
        var givenUp = queued
        givenUp.attempts = LmsFollowUpQueue.maximumAttempts
        XCTAssertNil(RecordingSyncGate.blocker(for: record, configuration: configuration, followUps: [givenUp], sessions: [], calendar: calendar), "a step that was given up on does not block forever")
        var stale = queued
        stale.dueAt = Date().addingTimeInterval(-(LmsFollowUpQueue.tooOld + 60))
        XCTAssertNil(RecordingSyncGate.blocker(for: record, configuration: configuration, followUps: [stale], sessions: [], calendar: calendar), "a step too old to run does not block for a week")

        var register = AttendanceSession(groupID: group.id, groupName: group.name, meetingName: "m", startedAt: cairoDate(2026, 9, 12, 13, 55), rosterSnapshot: [])
        XCTAssertEqual(RecordingSyncGate.blocker(for: record, configuration: configuration, followUps: [], sessions: [register], calendar: calendar)?.issue, .attendanceNotComplete)
        register.finalizedAt = Date()
        XCTAssertNil(RecordingSyncGate.blocker(for: record, configuration: configuration, followUps: [], sessions: [register], calendar: calendar))

        let twin = StudentGroup(name: "Other", lmsGroupCode: "cai5_ind1_g1")
        XCTAssertEqual(RecordingSyncGate.blocker(for: record, configuration: SchedulerConfiguration(studentGroups: [group, twin]), followUps: [], sessions: [], calendar: calendar)?.issue, .groupMapping)
        XCTAssertEqual(RecordingSyncGate.blocker(for: record, configuration: SchedulerConfiguration(), followUps: [], sessions: [], calendar: calendar)?.issue, .groupMapping)
    }
}

final class RecordingSyncOutcomeTests: XCTestCase {
    private var record: RecordingSyncRecord {
        var record = RecordingSyncRecord(groupCode: "CAI5_IND1_G1", sessionDate: "2026-09-12", sheetTab: "CAI5_IND1_G1", sheetRow: 2, fileName: "f", driveURL: drive1, now: Date())
        record.state = .processing
        return record
    }

    private func result(_ success: Bool, _ body: [String: AutomationValue], failure: String? = nil) -> AutomationResult {
        var full = body
        full["success"] = .bool(success)
        full["message"] = .string("msg")
        if let failure { full["failure"] = .string(failure) }
        return AutomationResult(body: full)
    }

    func testEveryHelperAnswerLandsInTheRightState() {
        let url: AutomationValue = .string("https://dashboard.depi.eyouthbusiness.com/group_admin/sessions/x")
        let attached = RecordingSyncOutcome.apply(result(true, ["outcome": .string("attached"), "lmsSessionUrl": url]), to: record, dryRun: false)
        XCTAssertEqual(attached.state, .attached)
        XCTAssertNotNil(attached.completedAt)
        XCTAssertEqual(attached.lmsSessionURL, url.string)
        XCTAssertEqual(RecordingSyncOutcome.apply(result(true, ["outcome": .string("alreadyAttached")]), to: record, dryRun: false).state, .attached, "the same Drive link already there is complete")

        XCTAssertEqual(RecordingSyncOutcome.apply(result(false, ["issue": .string("noSession")]), to: record, dryRun: false).state, .pending, "no session: retry next sync")
        XCTAssertEqual(RecordingSyncOutcome.apply(result(false, ["issue": .string("sessionNotEnded")]), to: record, dryRun: false).issue, .sessionNotEnded)
        XCTAssertEqual(RecordingSyncOutcome.apply(result(false, ["issue": .string("ambiguousSessions")]), to: record, dryRun: false).state, .conflict, "duplicate LMS sessions: never guess")
        let conflict = RecordingSyncOutcome.apply(result(false, ["issue": .string("existingLinkDiffers"), "currentLink": .string("https://zoom.us/rec/share/x")]), to: record, dryRun: false)
        XCTAssertEqual(conflict.state, .conflict)
        XCTAssertEqual(conflict.conflictingLink, "https://zoom.us/rec/share/x")
        XCTAssertFalse(conflict.isDueForAutomaticSync, "a conflict is never retried on its own")

        var failed = RecordingSyncOutcome.apply(result(false, ["issue": .string("failed")]), to: record, dryRun: false)
        XCTAssertEqual(failed.state, .failed)
        XCTAssertEqual(failed.attempts, 1)
        XCTAssertTrue(failed.isDueForAutomaticSync)
        failed.attempts = RecordingSyncRecord.automaticRetryLimit
        XCTAssertFalse(failed.isDueForAutomaticSync)
        XCTAssertEqual(RecordingSyncOutcome.apply(result(false, [:], failure: "busy"), to: record, dryRun: false).state, .pending)
    }

    func testRehearseNeverCompletesARecord() {
        let rehearsed = RecordingSyncOutcome.apply(result(true, ["outcome": .string("wouldAttach"), "dryRun": .bool(true)]), to: record, dryRun: true)
        XCTAssertEqual(rehearsed.state, .pending)
        XCTAssertEqual(rehearsed.issue, .rehearsed)
        XCTAssertNil(rehearsed.completedAt)
        XCTAssertEqual(RecordingSyncOutcome.apply(result(true, ["outcome": .string("wouldReplaceZoom")]), to: record, dryRun: true).state, .pending)
        XCTAssertEqual(RecordingSyncOutcome.apply(result(true, ["outcome": .string("attached")]), to: record, dryRun: true).state, .pending, "even a mistaken 'attached' during rehearse does not complete")
    }

    func testStorePersistsAndRecoversAnInterruptedSync() {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString).appendingPathComponent("records.json")
        defer { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()) }
        let store = RecordingSyncStore(fileURL: url)
        store.update(record)
        XCTAssertEqual(RecordingSyncStore(fileURL: url).load().first?.state, .processing)
        store.recoverInterrupted(now: Date())
        XCTAssertEqual(RecordingSyncStore(fileURL: url).load().first?.state, .pending)
    }
}

final class DailyJobScheduleTests: XCTestCase {
    func testRunsOnceADayAtEightCairoAndCatchesUp() {
        let schedule = DailyJobSchedule()
        let early = cairoDate(2026, 9, 15, 7, 59)
        XCTAssertFalse(schedule.isDue(now: early, lastRunDay: "2026-09-14"))
        XCTAssertEqual(schedule.nextRun(after: early, lastRunDay: "2026-09-14"), cairoDate(2026, 9, 15, 8, 0))
        let eight = cairoDate(2026, 9, 15, 8, 0)
        XCTAssertTrue(schedule.isDue(now: eight, lastRunDay: "2026-09-14"))
        XCTAssertFalse(schedule.isDue(now: cairoDate(2026, 9, 15, 13, 0), lastRunDay: "2026-09-15"), "once a day")
        XCTAssertTrue(schedule.isDue(now: cairoDate(2026, 9, 15, 13, 0), lastRunDay: "2026-09-13"), "the Mac slept through 08:00: it runs when it wakes")
        XCTAssertEqual(schedule.nextRun(after: cairoDate(2026, 9, 15, 13, 0), lastRunDay: "2026-09-15"), cairoDate(2026, 9, 16, 8, 0))
        XCTAssertEqual(schedule.dayKey(cairoDate(2026, 9, 15, 23, 30)), "2026-09-15", "the day is Cairo's day")
    }

    func testTheLaunchAgentWakesTheAppBeforeTheDailySync() {
        let dates = LaunchAgentScheduler.launchDates(configuration: SchedulerConfiguration(), dailyWake: DailyJobSchedule(), now: cairoDate(2026, 9, 15, 9, 0))
        XCTAssertEqual(dates.first, cairoDate(2026, 9, 16, 7, 55))
        XCTAssertEqual(dates.count, LaunchAgentScheduler.horizonDays)
    }
}

final class GoogleOAuthTests: XCTestCase {
    private final class MemoryStore: GoogleCredentialStoring {
        var refreshToken: String?
        var clientSecret: String?
        func saveRefreshToken(_ token: String) -> Bool { refreshToken = token; return true }
        func saveClientSecret(_ secret: String?) -> Bool { clientSecret = secret; return true }
        func deleteRefreshToken() -> Bool { refreshToken = nil; return true }
    }

    func testPKCEMatchesTheRFCExample() {
        XCTAssertEqual(PKCE(verifier: "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk").challenge, "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM")
    }

    func testAuthorizationURLAsksForOfflineReadOnlyAccess() {
        let url = GoogleOAuthClient.authorizationURL(configuration: GoogleOAuthConfiguration(clientID: "id.apps.googleusercontent.com", clientSecret: nil), redirectURI: "http://127.0.0.1:5555", state: "s", pkce: PKCE(verifier: "v"))
        let items = Dictionary(uniqueKeysWithValues: URLComponents(url: url, resolvingAgainstBaseURL: false)!.queryItems!.map { ($0.name, $0.value ?? "") })
        XCTAssertEqual(items["access_type"], "offline")
        XCTAssertEqual(items["code_challenge_method"], "S256")
        XCTAssertEqual(items["redirect_uri"], "http://127.0.0.1:5555")
        XCTAssertTrue(items["scope"]!.contains("spreadsheets.readonly"))
        XCTAssertFalse(items["scope"]!.contains("auth/spreadsheets "), "never write access")
    }

    func testTheRefreshTokenIsUsedPersistedAndDroppedWhenRevoked() async throws {
        let store = MemoryStore()
        store.refreshToken = "refresh-1"
        store.clientSecret = "secret"
        var bodies: [String] = []
        var answer: (Int, String) = (200, #"{"access_token":"access-1","expires_in":3600,"refresh_token":"refresh-2"}"#)
        let client = GoogleOAuthClient(configuration: { GoogleOAuthConfiguration(clientID: "id", clientSecret: nil) }, store: store, http: { request in
            bodies.append(String(data: request.httpBody ?? Data(), encoding: .utf8) ?? "")
            return (Data(answer.1.utf8), HTTPURLResponse(url: request.url!, statusCode: answer.0, httpVersion: nil, headerFields: nil)!)
        })
        let token = try await client.accessToken(now: Date())
        XCTAssertEqual(token, "access-1")
        XCTAssertTrue(bodies[0].contains("grant_type=refresh_token"))
        XCTAssertTrue(bodies[0].contains("client_secret=secret"), "the secret comes from the Keychain store")
        XCTAssertEqual(store.refreshToken, "refresh-2", "a rotated refresh token is kept")
        _ = try await client.accessToken(now: Date())
        XCTAssertEqual(bodies.count, 1, "a valid access token is reused")

        answer = (400, #"{"error":"invalid_grant"}"#)
        do {
            _ = try await client.accessToken(now: Date().addingTimeInterval(7200))
            XCTFail("expected a failure")
        } catch {}
        XCTAssertNil(store.refreshToken, "a revoked token is removed so the app asks to connect again")
    }

    func testTheKeychainKeepsTheRefreshToken() {
        let store = KeychainGoogleCredentialStore(service: "com.mohamedhosam.ZoomAutoAdmit.tests.\(UUID().uuidString)")
        defer { store.deleteRefreshToken(); store.saveClientSecret(nil) }
        XCTAssertNil(store.refreshToken)
        XCTAssertTrue(store.saveRefreshToken("1//token"))
        XCTAssertTrue(store.saveClientSecret("GOCSPX-secret"))
        XCTAssertEqual(store.refreshToken, "1//token")
        XCTAssertEqual(store.clientSecret, "GOCSPX-secret")
        XCTAssertTrue(store.deleteRefreshToken())
        XCTAssertNil(store.refreshToken)
    }

    func testTheLoopbackRedirectIsParsed() {
        let query = LoopbackRedirectReceiver.parseRequestLine("GET /?state=abc&code=4%2F0Ad&scope=x HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n")
        XCTAssertEqual(query["code"], "4/0Ad")
        XCTAssertEqual(query["state"], "abc")
        XCTAssertTrue(LoopbackRedirectReceiver.parseRequestLine("GET /favicon.ico HTTP/1.1").isEmpty)
    }
}

final class RecordingFoundLinkTests: XCTestCase {
    func testWhatTheSessionHeldIsClassified() {
        XCTAssertEqual(LmsRecordLinkFound.classify("", driveURL: drive1), .empty)
        XCTAssertEqual(LmsRecordLinkFound.classify("https://zoom.us/rec/share/AzzuFag", driveURL: drive1), .zoomLink)
        XCTAssertEqual(LmsRecordLinkFound.classify("https://us06web.zoom.us/rec/play/x", driveURL: drive1), .zoomLink)
        XCTAssertEqual(LmsRecordLinkFound.classify("https://drive.google.com/file/d/1AAAAAAAAAAAAAAAAAAAAAAAAAAAAA/view", driveURL: drive1), .sameDriveLink)
        XCTAssertEqual(LmsRecordLinkFound.classify(drive2, driveURL: drive1), .otherDriveLink)
        XCTAssertEqual(LmsRecordLinkFound.classify("https://youtu.be/x", driveURL: drive1), .otherLink)
    }

    func testTheFirstReadingIsKeptAcrossLaterAnswers() {
        var record = RecordingSyncRecord(groupCode: "CAI5_IND1_G1", sessionDate: "2026-09-12", sheetTab: "t", sheetRow: 2, fileName: "f", driveURL: drive1, now: Date())
        record = RecordingSyncOutcome.apply(AutomationResult(body: ["success": .bool(true), "message": .string("m"), "outcome": .string("wouldReplaceZoom"), "recordLinkState": .string("filled"), "currentLink": .string("https://zoom.us/rec/share/x")]), to: record, dryRun: true)
        XCTAssertEqual(record.foundLinkKind, .zoomLink)
        record = RecordingSyncOutcome.apply(AutomationResult(body: ["success": .bool(true), "message": .string("m"), "outcome": .string("alreadyAttached"), "recordLinkState": .string("filled"), "currentLink": .string(drive1)]), to: record, dryRun: false)
        XCTAssertEqual(record.foundLinkKind, .zoomLink, "after our own write the session holds our link; the original finding stays")
        let empty = RecordingSyncOutcome.apply(AutomationResult(body: ["success": .bool(true), "message": .string("m"), "outcome": .string("attached"), "recordLinkState": .string("empty"), "currentLink": .string("")]), to: RecordingSyncRecord(groupCode: "G", sessionDate: "2026-09-12", sheetTab: "t", sheetRow: 2, fileName: "f", driveURL: drive1, now: Date()), dryRun: false)
        XCTAssertEqual(empty.foundLinkKind, .empty)
        XCTAssertNil(empty.foundLink)
        let noSession = RecordingSyncOutcome.apply(AutomationResult(body: ["success": .bool(false), "message": .string("m"), "issue": .string("noSession")]), to: RecordingSyncRecord(groupCode: "G", sessionDate: "2026-09-12", sheetTab: "t", sheetRow: 2, fileName: "f", driveURL: drive1, now: Date()), dryRun: false)
        XCTAssertNil(noSession.foundLinkKind, "nothing was read when no session was found")
    }
}
