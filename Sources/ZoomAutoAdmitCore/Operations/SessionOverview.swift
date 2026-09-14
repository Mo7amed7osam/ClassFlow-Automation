import Foundation

/// How a status should look: one small vocabulary for every card.
public enum StatusTone: String, Equatable {
    case good
    case active
    case pending
    case idle
    case warning
    case bad
}

public enum ZoomPhase: String, Equatable, CaseIterable {
    case notStarted, starting, running, ended, failed

    public var displayName: String {
        switch self {
        case .notStarted: return "Not Started"
        case .starting: return "Starting"
        case .running: return "Running"
        case .ended: return "Ended"
        case .failed: return "Failed"
        }
    }

    public var tone: StatusTone {
        switch self {
        case .notStarted: return .idle
        case .starting: return .pending
        case .running: return .active
        case .ended: return .good
        case .failed: return .bad
        }
    }
}

public enum AttendancePhase: String, Equatable, CaseIterable {
    case waiting, recording, completed, failed

    public var displayName: String {
        switch self {
        case .waiting: return "Waiting"
        case .recording: return "Recording"
        case .completed: return "Completed"
        case .failed: return "Failed"
        }
    }

    public var tone: StatusTone {
        switch self {
        case .waiting: return .idle
        case .recording: return .active
        case .completed: return .good
        case .failed: return .bad
        }
    }
}

public enum LmsPhase: String, Equatable, CaseIterable {
    case notStarted, sessionRunning, attendancePending, attendanceSubmitted, correctionPending, completed, failed

    public var displayName: String {
        switch self {
        case .notStarted: return "Not Started"
        case .sessionRunning: return "Session Running"
        case .attendancePending: return "Attendance Pending"
        case .attendanceSubmitted: return "Attendance Submitted"
        case .correctionPending: return "Correction Pending"
        case .completed: return "Completed"
        case .failed: return "Failed"
        }
    }

    public var tone: StatusTone {
        switch self {
        case .notStarted: return .idle
        case .sessionRunning: return .active
        case .attendancePending, .correctionPending: return .pending
        case .attendanceSubmitted, .completed: return .good
        case .failed: return .bad
        }
    }
}

public enum RecordingPhase: String, Equatable, CaseIterable {
    case waiting, driveFound, uploading, attached, conflict, failed

    public var displayName: String {
        switch self {
        case .waiting: return "Waiting"
        case .driveFound: return "Drive Found"
        case .uploading: return "Uploading"
        case .attached: return "Attached"
        case .conflict: return "Conflict"
        case .failed: return "Failed"
        }
    }

    public var tone: StatusTone {
        switch self {
        case .waiting: return .idle
        case .driveFound: return .pending
        case .uploading: return .active
        case .attached: return .good
        case .conflict: return .warning
        case .failed: return .bad
        }
    }
}

/// Everything one card shows about one class occurrence.
public struct SessionOverview: Equatable, Identifiable {
    public var id: String { "\(scheduleID.uuidString)|\(Int(startsAt.timeIntervalSince1970))" }
    public var scheduleID: UUID
    public var scheduleName: String
    public var groupName: String?
    public var groupCode: String?
    public var sessionDate: String
    public var sessionKey: String?
    public var startsAt: Date
    public var endsAt: Date?
    public var accountName: String?

    public var zoom: ZoomPhase
    public var zoomDetail: String?
    public var attendance: AttendancePhase
    public var attendanceDetail: [String]
    public var present: Int?
    public var rosterCount: Int?
    public var needsReview: Int?
    public var absent: Int?
    public var lms: LmsPhase
    public var lmsDetail: [String]
    public var recording: RecordingPhase
    public var recordingDetail: String?

    public var nextAction: String?
    public var nextActionAt: Date?
    public var lastSuccess: String?
    public var lastSuccessAt: Date?
    public var lastError: String?
    public var lastErrorAt: Date?

    public var needsAttention: Bool {
        zoom == .failed || attendance == .failed || lms == .failed || recording == .conflict || recording == .failed
    }
}

/// What the dashboard reads, gathered from the existing stores at one moment.
public struct OperationsInputs {
    public var configuration: SchedulerConfiguration
    public var attendanceSessions: [AttendanceSession]
    public var followUps: [LmsFollowUp]
    public var recordings: [RecordingSyncRecord]
    public var events: [OperationsEvent]
    public var lmsSettings: LmsSettings
    /// The schedule whose Zoom workflow is running right now, if any.
    public var workflowScheduleID: UUID?
    public var webMeetingScheduleIDs: Set<UUID>
    public var recordingSyncEnabled: Bool
    public var nextRecordingSync: Date?

    public init(
        configuration: SchedulerConfiguration,
        attendanceSessions: [AttendanceSession] = [],
        followUps: [LmsFollowUp] = [],
        recordings: [RecordingSyncRecord] = [],
        events: [OperationsEvent] = [],
        lmsSettings: LmsSettings = LmsSettings(),
        workflowScheduleID: UUID? = nil,
        webMeetingScheduleIDs: Set<UUID> = [],
        recordingSyncEnabled: Bool = false,
        nextRecordingSync: Date? = nil
    ) {
        self.configuration = configuration
        self.attendanceSessions = attendanceSessions
        self.followUps = followUps
        self.recordings = recordings
        self.events = events
        self.lmsSettings = lmsSettings
        self.workflowScheduleID = workflowScheduleID
        self.webMeetingScheduleIDs = webMeetingScheduleIDs
        self.recordingSyncEnabled = recordingSyncEnabled
        self.nextRecordingSync = nextRecordingSync
    }
}

/// Derives each card from the existing state. Pure: the same inputs always give the same cards.
public enum SessionOverviewBuilder {
    /// How far back a card stays on the dashboard, and how far ahead classes are listed.
    public static func window(now: Date, calendar: Calendar) -> (from: Date, through: Date) {
        let today = calendar.startOfDay(for: now)
        return (today, calendar.date(byAdding: .day, value: 2, to: today) ?? now)
    }

    public static func build(_ inputs: OperationsInputs, now: Date = Date(), calendar: Calendar = .current) -> [SessionOverview] {
        let (from, through) = window(now: now, calendar: calendar)
        var cards: [SessionOverview] = []
        for schedule in inputs.configuration.schedules {
            let starts = ScheduleTimeline.occurrences(of: schedule, after: from.addingTimeInterval(-1), through: through, calendar: calendar)
            for start in starts {
                cards.append(overview(schedule: schedule, startsAt: start, inputs: inputs, now: now, calendar: calendar))
            }
        }
        return cards.sorted { $0.startsAt < $1.startsAt }
    }

    public static func overview(
        schedule: ZoomSchedule,
        startsAt: Date,
        inputs: OperationsInputs,
        now: Date,
        calendar: Calendar
    ) -> SessionOverview {
        let configuration = inputs.configuration
        let group = configuration.group(for: schedule)
        let groupCode = group?.dashboardGroupName
        let (date, time) = LmsFollowUpQueue.dashboardDateAndTime(startsAt, calendar: calendar)
        let endsAt = ScheduleTimeline.endDate(for: schedule, startedAt: startsAt, calendar: calendar)
        let key = groupCode.map { OperationsSessionKey.make(groupCode: $0, sessionDate: date) }

        let events = inputs.events.filter { event in
            if let scheduleID = event.scheduleID, scheduleID == schedule.id, event.sessionDate == date { return true }
            if let key, event.sessionKey == key { return true }
            return false
        }.sorted { $0.at < $1.at }

        let register = register(for: schedule, group: group, date: date, startsAt: startsAt, sessions: inputs.attendanceSessions, calendar: calendar)

        var card = SessionOverview(
            scheduleID: schedule.id,
            scheduleName: schedule.name,
            groupName: group?.name,
            groupCode: groupCode,
            sessionDate: date,
            sessionKey: key,
            startsAt: startsAt,
            endsAt: endsAt,
            accountName: configuration.profile(for: schedule)?.name,
            zoom: .notStarted,
            attendance: .waiting,
            attendanceDetail: [],
            lms: .notStarted,
            lmsDetail: [],
            recording: .waiting
        )

        // Zoom
        (card.zoom, card.zoomDetail) = zoomPhase(schedule: schedule, startsAt: startsAt, endsAt: endsAt, events: events, register: register, inputs: inputs, now: now)

        // Attendance
        if group == nil {
            card.attendanceDetail = ["No attendance group is linked"]
        } else if let register {
            card.present = register.presentCount
            card.rosterCount = register.rosterSnapshot.count
            card.needsReview = register.needsReviewCount
            card.absent = register.absentCount
            card.attendanceDetail = ["\(register.snapshots.count) snapshot(s)", "\(register.presentCount)/\(register.rosterSnapshot.count) matched"]
            if register.missedSnapshotCount > 0 { card.attendanceDetail.append("\(register.missedSnapshotCount) snapshot(s) missed") }
            if register.isFinalized {
                card.attendance = .completed
            } else if register.snapshots.isEmpty, register.missedSnapshotCount >= 3 {
                card.attendance = .failed
            } else {
                card.attendance = .recording
            }
        }

        // LMS
        let queued = inputs.followUps.filter { item in
            groupCode.map { LmsGroupMapping.key(item.group) == LmsGroupMapping.key($0) } == true && item.sessionDate == date
        }
        (card.lms, card.lmsDetail) = lmsPhase(queued: queued, events: events, settings: inputs.lmsSettings, hasGroup: group != nil, now: now)

        // Recording
        if let key, let record = inputs.recordings.first(where: { $0.id == key }) {
            switch record.state {
            case .pending:
                card.recording = .driveFound
                card.recordingDetail = record.error ?? "Drive link found; waiting for the next sync"
            case .processing:
                card.recording = .uploading
                card.recordingDetail = "Moving the Drive link to the LMS"
            case .attached:
                card.recording = .attached
                card.recordingDetail = record.foundLinkKind.map { "Attached (LMS had: \($0.displayName))" } ?? "Attached"
            case .conflict:
                card.recording = .conflict
                card.recordingDetail = record.error
            case .failed:
                card.recording = .failed
                card.recordingDetail = record.error
            }
        } else {
            card.recording = .waiting
            card.recordingDetail = inputs.recordingSyncEnabled ? "Waiting for Drive link" : "Recording sync is off"
        }

        // Next action, last success, last error
        (card.nextAction, card.nextActionAt) = nextAction(schedule: schedule, startsAt: startsAt, endsAt: endsAt, card: card, queued: queued, inputs: inputs, now: now)
        if let success = events.last(where: { $0.severity == .success }) {
            card.lastSuccess = "\(success.title): \(success.message)"
            card.lastSuccessAt = success.at
        }
        let failureEvent = events.last(where: { $0.severity == .failure && $0.kind != .healthCheck })
        let queueError = queued.compactMap { item in item.lastError.map { (item, $0) } }.max { $0.0.dueAt < $1.0.dueAt }
        if let failureEvent, card.needsAttention || queueError == nil {
            card.lastError = "\(failureEvent.title): \(failureEvent.message)"
            card.lastErrorAt = failureEvent.at
        } else if let (item, error) = queueError, item.attempts > 0 {
            card.lastError = "\(item.step.displayName): \(error)"
        } else if card.recording == .conflict || card.recording == .failed {
            card.lastError = card.recordingDetail
        }
        return card
    }

    static func register(for schedule: ZoomSchedule, group: StudentGroup?, date: String, startsAt: Date, sessions: [AttendanceSession], calendar: Calendar) -> AttendanceSession? {
        let sameDay = sessions.filter { LmsFollowUpQueue.dashboardDateAndTime($0.startedAt, calendar: calendar).0 == date }
        if let exact = sameDay.filter({ $0.scheduleID == schedule.id }).max(by: { $0.startedAt < $1.startedAt }) { return exact }
        guard let group else { return nil }
        // Registers without a schedule (a manual run) count when they started near this class.
        return sameDay
            .filter { $0.groupID == group.id && $0.scheduleID == nil && abs($0.startedAt.timeIntervalSince(startsAt)) < 3 * 60 * 60 }
            .max { $0.startedAt < $1.startedAt }
    }

    static func zoomPhase(
        schedule: ZoomSchedule,
        startsAt: Date,
        endsAt: Date?,
        events: [OperationsEvent],
        register: AttendanceSession?,
        inputs: OperationsInputs,
        now: Date
    ) -> (ZoomPhase, String?) {
        let zoomEvents = events.filter { [.zoomStarting, .zoomStarted, .zoomFailed, .zoomEnded].contains($0.kind) }
        let over = endsAt.map { now >= $0 } ?? false
        if inputs.workflowScheduleID == schedule.id { return (.starting, "Opening the meeting") }
        if inputs.webMeetingScheduleIDs.contains(schedule.id) { return (.running, "Running in Zoom Web") }
        if let register, register.endedAt == nil, !over { return (.running, "Meeting verified; register open") }
        if let last = zoomEvents.last {
            switch last.kind {
            case .zoomFailed: return (.failed, last.message)
            case .zoomEnded: return (.ended, last.message)
            case .zoomStarted: return over ? (.ended, "End time passed") : (.running, last.message)
            default: break
            }
        }
        if register != nil { return (over || register?.endedAt != nil ? .ended : .running, nil) }
        if now >= startsAt, now.timeIntervalSince(startsAt) < 10 * 60 { return (.starting, "Start time reached") }
        if now >= startsAt { return (over ? .ended : .notStarted, "No start was recorded") }
        return (.notStarted, nil)
    }

    static func lmsPhase(queued: [LmsFollowUp], events: [OperationsEvent], settings: LmsSettings, hasGroup: Bool, now: Date) -> (LmsPhase, [String]) {
        var lines: [String] = []
        guard hasGroup else { return (.notStarted, ["No group, so no LMS steps"]) }
        guard settings.isAnythingEnabled else { return (.notStarted, ["LMS steps are switched off"]) }
        let rehearse = settings.dryRun ? " (rehearse)" : ""

        let sessionStarted = events.last(where: { $0.kind == .lmsSessionStarted })
        let sessionFailed = events.last(where: { $0.kind == .lmsSessionFailed })
        if let sessionStarted { lines.append("✓ Session started\(rehearse)") }
        else if let sessionFailed { lines.append("✗ Run Session: \(sessionFailed.message)") }

        let uploaded = events.last(where: { $0.kind == .attendanceUploaded })
        let corrected = events.last(where: { $0.kind == .attendanceCorrected })
        if let uploaded { lines.append("✓ \(uploaded.message)") }
        if let corrected { lines.append("✓ \(corrected.message)") }

        let take = queued.first { $0.step == .takeAttendance }
        let correct = queued.first { $0.step == .correctAttendance }
        let gaveUp = queued.first { $0.attempts >= LmsFollowUpQueue.maximumAttempts }
            ?? queued.first { now.timeIntervalSince($0.dueAt) > LmsFollowUpQueue.tooOld }

        func due(_ item: LmsFollowUp) -> String {
            let minutes = Int(item.dueAt.timeIntervalSince(now) / 60)
            if minutes > 0 { return minutes < 120 ? "in \(minutes) min" : "at \(Self.time(item.dueAt))" }
            return item.attempts > 0 ? "retrying (attempt \(item.attempts + 1))" : "due now"
        }

        if let gaveUp {
            lines.append("✗ \(gaveUp.step.displayName) gave up: \(gaveUp.lastError ?? "no answer")")
            return (.failed, lines)
        }
        if let take {
            lines.append("⏳ Attendance upload \(due(take))")
            if let error = take.lastError, take.attempts > 0 { lines.append("Last try: \(error)") }
            return (.attendancePending, lines)
        }
        if let correct {
            lines.append("⏳ Late-joiner correction \(due(correct))")
            if let error = correct.lastError, correct.attempts > 0 { lines.append("Last try: \(error)") }
            return (.correctionPending, lines)
        }
        if corrected != nil || (uploaded != nil && !settings.correctAttendance) { return (.completed, lines) }
        if uploaded != nil { return (.attendanceSubmitted, lines) }
        if let last = events.last(where: { $0.kind == .lmsStepGaveUp || $0.kind == .lmsStepFailed }), last.kind == .lmsStepGaveUp {
            return (.failed, lines + ["✗ \(last.message)"])
        }
        if sessionStarted != nil { return (.sessionRunning, lines) }
        if sessionFailed != nil { return (.failed, lines) }
        return (.notStarted, lines)
    }

    static func nextAction(
        schedule: ZoomSchedule,
        startsAt: Date,
        endsAt: Date?,
        card: SessionOverview,
        queued: [LmsFollowUp],
        inputs: OperationsInputs,
        now: Date
    ) -> (String?, Date?) {
        var candidates: [(String, Date)] = []
        if startsAt > now {
            let check = startsAt.addingTimeInterval(-OperationsTiming.networkCheckLead)
            if check > now { candidates.append(("Health check at \(time(check))", check)) }
            candidates.append(("Start Zoom at \(time(startsAt))", startsAt))
        }
        for item in queued where item.attempts < LmsFollowUpQueue.maximumAttempts {
            let at = max(item.dueAt, now)
            candidates.append(("\(item.step.displayName) at \(time(at))", at))
        }
        if let endsAt, endsAt > now, card.zoom == .running || card.attendance == .recording {
            candidates.append(("Close the register at \(time(endsAt))", endsAt))
        }
        if candidates.isEmpty, card.recording == .waiting || card.recording == .driveFound, inputs.recordingSyncEnabled,
           let sync = inputs.nextRecordingSync, card.zoom == .ended || card.attendance == .completed {
            candidates.append(("Recording sync at \(time(sync))", sync))
        }
        guard let first = candidates.min(by: { $0.1 < $1.1 }) else { return (nil, nil) }
        return (first.0, first.1)
    }

    static func time(_ date: Date) -> String {
        let formatter = DateFormatter()
        formatter.dateFormat = "HH:mm"
        return formatter.string(from: date)
    }
}

public enum OperationsTiming {
    /// The LMS and Google checks run this long before a class, away from Run Session at the start.
    public static let networkCheckLead: TimeInterval = 30 * 60
}
