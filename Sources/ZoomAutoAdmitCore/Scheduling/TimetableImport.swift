import Foundation

/// One row of the DEPI timetable as the helper read it.
public struct TimetableRow: Equatable {
    public var rowNumber: Int
    public var sessionNumber: String
    /// yyyy-MM-dd
    public var date: String?
    public var type: String
    public var topic: String
    /// HH:mm
    public var startTime: String?
    public var endTime: String?
    public var timeRange: String
    /// Why the workbook itself rules this row out; empty when it does not.
    public var issue: String

    public init(rowNumber: Int, sessionNumber: String, date: String?, type: String, topic: String, startTime: String?, endTime: String?, timeRange: String, issue: String) {
        self.rowNumber = rowNumber
        self.sessionNumber = sessionNumber
        self.date = date
        self.type = type
        self.topic = topic
        self.startTime = startTime
        self.endTime = endTime
        self.timeRange = timeRange
        self.issue = issue
    }

    init?(_ value: AutomationValue) {
        guard let object = value.object else { return nil }
        rowNumber = Int(object["rowNumber"]?.number ?? 0)
        sessionNumber = object["sessionNumber"]?.string ?? ""
        date = object["date"]?.string
        type = object["type"]?.string ?? ""
        topic = object["topic"]?.string ?? ""
        startTime = object["startTime"]?.string
        endTime = object["endTime"]?.string
        timeRange = object["timeRange"]?.string ?? ""
        issue = object["issue"]?.string ?? ""
    }
}

public struct Timetable: Equatable {
    public var groupCode: String
    public var rows: [TimetableRow]

    public init(groupCode: String, rows: [TimetableRow]) {
        self.groupCode = groupCode
        self.rows = rows
    }

    public init?(result: AutomationResult) {
        guard result.success, let rows = result.body["rows"]?.array else { return nil }
        self.groupCode = result.body["groupCode"]?.string ?? ""
        self.rows = rows.compactMap(TimetableRow.init)
    }
}

/// What importing a timetable would do, decided before anything is saved so it can be shown.
public struct TimetableImportPlan: Equatable {
    public struct Decision: Equatable {
        public var row: TimetableRow
        /// Nil when the row becomes a schedule; otherwise why it does not.
        public var skipReason: String?
        public var schedule: ZoomSchedule?
    }

    public var decisions: [Decision]

    public var schedules: [ZoomSchedule] { decisions.compactMap(\.schedule) }
    public var skipped: [Decision] { decisions.filter { $0.schedule == nil } }
}

public enum TimetableImporter {
    /// Turns online rows into one-time schedules on their exact dates.
    ///
    /// Skips what the workbook excluded (Physical, No Session, invalid), dates already past, and
    /// any date and time the account already has a schedule for. Existing schedules are never
    /// changed. Imported schedules stay disabled unless `enable` says otherwise, so an import
    /// cannot start real meetings until someone has looked at it.
    public static func plan(
        timetable: Timetable,
        account: ZoomAccountProfile,
        meeting: MeetingReference,
        attendanceGroupID: UUID?,
        existing: [ZoomSchedule],
        enable: Bool,
        template: ZoomSchedule? = nil,
        now: Date = Date(),
        calendar: Calendar = .current
    ) -> TimetableImportPlan {
        var taken = Set(existing.compactMap { schedule -> String? in
            guard schedule.accountProfileID == account.id, case .oneTime(let y, let m, let d) = schedule.recurrence else { return nil }
            return key(String(format: "%04d-%02d-%02d", y, m, d), String(format: "%02d:%02d", schedule.startTime.hour, schedule.startTime.minute))
        })

        var decisions: [TimetableImportPlan.Decision] = []
        for row in timetable.rows {
            if !row.issue.isEmpty {
                decisions.append(.init(row: row, skipReason: row.issue, schedule: nil))
                continue
            }
            guard let date = row.date, let start = row.startTime,
                  let (year, month, day) = parseDate(date), let startTime = parseTime(start),
                  let startDate = calendar.date(from: DateComponents(year: year, month: month, day: day, hour: startTime.hour, minute: startTime.minute)) else {
                decisions.append(.init(row: row, skipReason: "Invalid date or time", schedule: nil))
                continue
            }
            if startDate <= now {
                decisions.append(.init(row: row, skipReason: "Already past", schedule: nil))
                continue
            }
            let rowKey = key(date, start)
            if taken.contains(rowKey) {
                decisions.append(.init(row: row, skipReason: "This account already has a meeting at that date and time", schedule: nil))
                continue
            }
            taken.insert(rowKey)

            let nameParts = [timetable.groupCode, row.sessionNumber, row.topic].filter { !$0.isEmpty }
            let schedule = ZoomSchedule(
                name: nameParts.joined(separator: " • "),
                isEnabled: enable,
                recurrence: .oneTime(year: year, month: month, day: day),
                startTime: startTime,
                // The slot's end is informational in the timetable; here it stops Auto Admit only.
                endTime: row.endTime.flatMap(parseTime),
                accountProfileID: account.id,
                meeting: meeting,
                enablesAutoAdmit: template?.enablesAutoAdmit ?? true,
                attendanceGroupID: attendanceGroupID,
                mutesMicrophoneBeforeJoining: template?.mutesMicrophoneBeforeJoining ?? true,
                disablesCameraBeforeJoining: template?.disablesCameraBeforeJoining ?? true,
                launchZoomMinutesEarly: template?.launchZoomMinutesEarly ?? 2
            )
            decisions.append(.init(row: row, skipReason: nil, schedule: schedule))
        }
        return TimetableImportPlan(decisions: decisions)
    }

    private static func key(_ date: String, _ time: String) -> String { "\(date)|\(time)" }

    static func parseDate(_ text: String) -> (Int, Int, Int)? {
        let parts = text.split(separator: "-").compactMap { Int($0) }
        guard parts.count == 3 else { return nil }
        return (parts[0], parts[1], parts[2])
    }

    static func parseTime(_ text: String) -> TimeOfDay? {
        let parts = text.split(separator: ":").compactMap { Int($0) }
        guard parts.count == 2, (0...23).contains(parts[0]), (0...59).contains(parts[1]) else { return nil }
        return TimeOfDay(hour: parts[0], minute: parts[1])
    }
}

/// A roster read from Excel by the helper.
public struct WorkbookRosterRow: Equatable {
    public var order: Int?
    public var name: String
    public var externalID: String?
    public var aliases: [String]
    public var email: String?

    public init(order: Int?, name: String, externalID: String?, aliases: [String], email: String?) {
        self.order = order
        self.name = name
        self.externalID = externalID
        self.aliases = aliases
        self.email = email
    }

    public static func rows(from result: AutomationResult) -> [WorkbookRosterRow] {
        (result.body["students"]?.array ?? []).compactMap { value in
            guard let object = value.object, let name = object["name"]?.string else { return nil }
            return WorkbookRosterRow(
                order: object["order"]?.number.map { Int($0) },
                name: name,
                externalID: object["externalId"]?.string,
                aliases: object["aliases"]?.array?.compactMap(\.string) ?? [],
                email: object["email"]?.string
            )
        }
    }

    /// Adds rows to a roster, skipping any official name already on it.
    public static func merge(_ rows: [WorkbookRosterRow], into existing: [Student]) -> (students: [Student], added: Int, skipped: [String]) {
        var students = existing
        var known = Set(existing.map(\.normalizedOfficialName))
        var added = 0
        var skipped: [String] = []
        for row in rows {
            let normalized = NameNormalizer.normalize(row.name)
            guard !normalized.isEmpty else { continue }
            if known.contains(normalized) {
                skipped.append(row.name)
                continue
            }
            known.insert(normalized)
            students.append(Student(officialName: row.name, externalID: row.externalID, email: row.email, aliases: row.aliases))
            added += 1
        }
        return (students, added, skipped)
    }
}
