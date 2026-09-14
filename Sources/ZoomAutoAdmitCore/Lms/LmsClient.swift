import Foundation

/// What the app does on the DEPI dashboard, and when. Stored in UserDefaults; the sign-in
/// itself is in the Keychain.
public struct LmsSettings: Equatable {
    /// Press Run Session when a scheduled meeting goes live.
    public var runSessionOnMeetingStart: Bool
    /// Upload the attendance 90 minutes in.
    public var takeAttendance: Bool
    /// Correct late joiners 3 hours in.
    public var correctAttendance: Bool
    /// Put the Zoom recording's link on the session 4 hours in (needs a signed-in Web profile).
    public var attachRecording: Bool
    /// Show the dashboard browser instead of running it hidden.
    public var showBrowser: Bool
    /// Rehearse: open everything and report the decision, but press nothing that writes.
    public var dryRun: Bool

    public init(
        runSessionOnMeetingStart: Bool = false,
        takeAttendance: Bool = false,
        correctAttendance: Bool = false,
        attachRecording: Bool = false,
        showBrowser: Bool = false,
        dryRun: Bool = false
    ) {
        self.runSessionOnMeetingStart = runSessionOnMeetingStart
        self.takeAttendance = takeAttendance
        self.correctAttendance = correctAttendance
        self.attachRecording = attachRecording
        self.showBrowser = showBrowser
        self.dryRun = dryRun
    }

    public var followUpSteps: [LmsFollowUpStep] {
        var steps: [LmsFollowUpStep] = []
        if takeAttendance { steps.append(.takeAttendance) }
        if correctAttendance { steps.append(.correctAttendance) }
        if attachRecording { steps.append(.attachRecording) }
        return steps
    }

    public var isAnythingEnabled: Bool { runSessionOnMeetingStart || !followUpSteps.isEmpty }

    private enum Key {
        static let run = "lms.runSessionOnMeetingStart"
        static let take = "lms.takeAttendance"
        static let correct = "lms.correctAttendance"
        static let recording = "lms.attachRecording"
        static let show = "lms.showBrowser"
        static let dryRun = "lms.dryRun"
    }

    public static func load(from defaults: UserDefaults = .standard) -> LmsSettings {
        LmsSettings(
            runSessionOnMeetingStart: defaults.bool(forKey: Key.run),
            takeAttendance: defaults.bool(forKey: Key.take),
            correctAttendance: defaults.bool(forKey: Key.correct),
            attachRecording: defaults.bool(forKey: Key.recording),
            showBrowser: defaults.bool(forKey: Key.show),
            dryRun: defaults.bool(forKey: Key.dryRun)
        )
    }

    public func save(to defaults: UserDefaults = .standard) {
        defaults.set(runSessionOnMeetingStart, forKey: Key.run)
        defaults.set(takeAttendance, forKey: Key.take)
        defaults.set(correctAttendance, forKey: Key.correct)
        defaults.set(attachRecording, forKey: Key.recording)
        defaults.set(showBrowser, forKey: Key.show)
        defaults.set(dryRun, forKey: Key.dryRun)
    }
}

/// The dashboard group a register belongs to. The group's own LMS code wins; its name is the
/// fallback, since groups are usually named exactly as the dashboard lists them.
public extension StudentGroup {
    var dashboardGroupName: String {
        let code = lmsGroupCode?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return code.isEmpty ? name.trimmingCharacters(in: .whitespacesAndNewlines) : code
    }
}

public enum LmsPresentNames {
    /// The roster names to mark Joined: Present records only. A Needs Review row is an open
    /// question and is left Not-joined until someone decides it - the late-joiner pass then
    /// picks up whatever was settled in the meantime.
    public static func present(in session: AttendanceSession) -> [String] {
        session.recordsInRosterOrder
            .filter { $0.status == .present }
            .map(\.studentName)
    }

    public static func needsReview(in session: AttendanceSession) -> [String] {
        session.recordsInRosterOrder
            .filter { $0.status == .needsReview }
            .map(\.studentName)
    }

    /// The register for a class: the one for that group that started nearest the session time,
    /// on the same day.
    public static func session(
        for followUp: LmsFollowUp,
        in sessions: [AttendanceSession],
        calendar: Calendar = .current
    ) -> AttendanceSession? {
        guard let groupID = followUp.attendanceGroupID else { return nil }
        return sessions
            .filter { $0.groupID == groupID }
            .filter { LmsFollowUpQueue.dashboardDateAndTime($0.startedAt, calendar: calendar).0 == followUp.sessionDate }
            .min { left, right in
                abs(minutes(of: left.startedAt, calendar: calendar) - minutes(of: followUp.sessionStart))
                    < abs(minutes(of: right.startedAt, calendar: calendar) - minutes(of: followUp.sessionStart))
            }
    }

    private static func minutes(of date: Date, calendar: Calendar) -> Int {
        let parts = calendar.dateComponents([.hour, .minute], from: date)
        return (parts.hour ?? 0) * 60 + (parts.minute ?? 0)
    }

    private static func minutes(of hhmm: String) -> Int {
        let parts = hhmm.split(separator: ":").compactMap { Int($0) }
        return parts.count == 2 ? parts[0] * 60 + parts[1] : 0
    }
}

/// Typed calls to the helper's dashboard commands.
public final class LmsClient {
    private let helper: AutomationHelper
    private let credentials: LmsCredentialStoring

    public init(helper: AutomationHelper = AutomationHelper(), credentials: LmsCredentialStoring = LmsCredentialStore()) {
        self.helper = helper
        self.credentials = credentials
    }

    public var hasSignIn: Bool { credentials.read() != nil }

    public func verifySignIn(_ account: LmsAccount, showBrowser: Bool, onMessage: AutomationHelper.LineHandler? = nil) -> AutomationResult {
        helper.run("lms-verify-sign-in", request: [
            "credentials": ["email": account.email, "password": account.password],
            "headed": showBrowser
        ], timeout: 180, onMessage: onMessage)
    }

    public func runSession(group: String, date: String, startTime: String?, settings: LmsSettings, onMessage: AutomationHelper.LineHandler? = nil) -> AutomationResult {
        dashboard("lms-run-session", group: group, date: date, startTime: startTime, settings: settings, extra: [:], onMessage: onMessage)
    }

    public func takeAttendance(group: String, date: String, startTime: String?, present: [String], settings: LmsSettings, onMessage: AutomationHelper.LineHandler? = nil) -> AutomationResult {
        dashboard("lms-take-attendance", group: group, date: date, startTime: startTime, settings: settings, extra: ["present": present], onMessage: onMessage)
    }

    public func correctAttendance(group: String, date: String, startTime: String?, present: [String], settings: LmsSettings, onMessage: AutomationHelper.LineHandler? = nil) -> AutomationResult {
        dashboard("lms-correct-attendance", group: group, date: date, startTime: startTime, settings: settings, extra: ["present": present], onMessage: onMessage)
    }

    public func attachRecordLink(group: String, date: String, startTime: String?, link: String, replaceExisting: Bool, settings: LmsSettings, onMessage: AutomationHelper.LineHandler? = nil) -> AutomationResult {
        dashboard("lms-attach-record-link", group: group, date: date, startTime: startTime, settings: settings, extra: ["recordLink": link, "replaceExisting": replaceExisting], onMessage: onMessage)
    }

    /// Reads the recording from Zoom's My Recordings in `profile`, then attaches it.
    public func attachRecordingFromZoom(group: String, date: String, startTime: String?, profile: String, settings: LmsSettings, timeZone: TimeZone = .current, onMessage: AutomationHelper.LineHandler? = nil) -> AutomationResult {
        var extra: [String: Any] = ["profile": profile]
        extra["localUtcOffsetMinutes"] = timeZone.secondsFromGMT() / 60
        if let zoomZone = UserDefaults.standard.string(forKey: "lms.zoomAccountTimeZone").flatMap(TimeZone.init(identifier:)) {
            extra["zoomUtcOffsetMinutes"] = zoomZone.secondsFromGMT() / 60
        }
        return dashboard("recording-from-zoom", group: group, date: date, startTime: startTime, settings: settings, extra: extra, onMessage: onMessage)
    }

    private func dashboard(_ command: String, group: String, date: String, startTime: String?, settings: LmsSettings, extra: [String: Any], onMessage: AutomationHelper.LineHandler?) -> AutomationResult {
        guard let account = credentials.read() else {
            return AutomationResult(success: false, message: "No LMS sign-in is saved. Add it in Settings → LMS first.", failure: "notSignedIn")
        }
        var request: [String: Any] = [
            "credentials": ["email": account.email, "password": account.password],
            "group": group,
            "day": date,
            "headed": settings.showBrowser,
            "dryRun": settings.dryRun
        ]
        if let startTime { request["startTime"] = startTime }
        request.merge(extra) { _, new in new }
        return helper.run(command, request: request, timeout: 15 * 60, onMessage: onMessage)
    }
}
