import Foundation

public enum HealthSection: String, CaseIterable, Equatable {
    case zoom = "Zoom"
    case lms = "LMS"
    case google = "Google Sheets"
    case recording = "Recording"
    case system = "System"
}

public enum HealthState: String, Equatable {
    case pass, warning, fail, skipped

    public var symbol: String {
        switch self {
        case .pass: return "✓"
        case .warning: return "⚠"
        case .fail: return "✗"
        case .skipped: return "–"
        }
    }
}

public struct HealthItem: Equatable {
    public var section: HealthSection
    public var name: String
    public var state: HealthState
    public var detail: String?
    public var fix: String?

    public init(_ section: HealthSection, _ name: String, _ state: HealthState, detail: String? = nil, fix: String? = nil) {
        self.section = section
        self.name = name
        self.state = state
        self.detail = detail
        self.fix = fix
    }
}

public struct HealthReport: Equatable {
    public var scheduleID: UUID
    public var scheduleName: String
    public var groupCode: String?
    public var sessionDate: String
    public var startsAt: Date
    public var checkedAt: Date
    public var includesNetwork: Bool
    public var items: [HealthItem]

    public var failures: [HealthItem] { items.filter { $0.state == .fail } }
    public var warnings: [HealthItem] { items.filter { $0.state == .warning } }
    /// Report only: a failure never stops a class, it says what will not work.
    public var isReady: Bool { failures.isEmpty }

    public var headline: String {
        if !isReady {
            return failures.count == 1 ? "\(scheduleName): \(failures[0].name) failed" : "\(scheduleName): \(failures.count) checks failed"
        }
        return warnings.isEmpty ? "\(scheduleName) is ready" : "\(scheduleName) is ready, with \(warnings.count) warning(s)"
    }

    /// The full report as text, in the order of the sections.
    public var text: String {
        var lines = ["Pre-flight Check", "", groupCode ?? scheduleName, ""]
        for section in HealthSection.allCases {
            let rows = items.filter { $0.section == section }
            guard !rows.isEmpty else { continue }
            lines.append(section.rawValue)
            for row in rows {
                lines.append("\(row.state.symbol) \(row.name)\(row.detail.map { " — \($0)" } ?? "")")
            }
            lines.append("")
        }
        if isReady {
            lines.append(warnings.isEmpty ? "Ready to start session" : "Ready to start session, with \(warnings.count) warning(s)")
        } else {
            lines.append("Will not work as planned")
            for failure in failures {
                lines.append("")
                lines.append("Reason: \(failure.name)\(failure.detail.map { " — \($0)" } ?? "")")
                if let fix = failure.fix { lines.append("Fix: \(fix)") }
            }
        }
        if !includesNetwork {
            lines.append("")
            lines.append("LMS and Google were not contacted in this check.")
        }
        return lines.joined(separator: "\n")
    }
}

/// What the probes found. Network parts are nil when they were not run.
public struct HealthProbeResults: Equatable {
    public struct LmsProbe: Equatable {
        public var signedIn: Bool
        /// Sessions listed for the group on the class's date; nil when the list was not reached.
        public var sessions: Int?
        public var message: String

        public init(signedIn: Bool, sessions: Int?, message: String) {
            self.signedIn = signedIn
            self.sessions = sessions
            self.message = message
        }
    }

    public struct SheetProbe: Equatable {
        public var tokenValid: Bool
        public var tabs: [String]?
        public var message: String

        public init(tokenValid: Bool, tabs: [String]?, message: String) {
            self.tokenValid = tokenValid
            self.tabs = tabs
            self.message = message
        }
    }

    public var zoomInstalled: Bool
    public var preflight: PreflightReport
    public var lmsCredentialsSaved: Bool
    public var lms: LmsProbe?
    public var recordingSyncEnabled: Bool
    public var googleConnected: Bool
    public var spreadsheetConfigured: Bool
    public var sheet: SheetProbe?
    /// When Google was connected, and whether its OAuth app is in Testing mode (7-day tokens).
    public var googleConnectedAt: Date?
    public var googleTestingMode: Bool
    /// nil when the helper answered; otherwise why it did not.
    public var helperProblem: String?
    public var freeDiskBytes: Int64?

    public init(
        zoomInstalled: Bool,
        preflight: PreflightReport,
        lmsCredentialsSaved: Bool,
        lms: LmsProbe? = nil,
        recordingSyncEnabled: Bool,
        googleConnected: Bool,
        spreadsheetConfigured: Bool,
        sheet: SheetProbe? = nil,
        googleConnectedAt: Date? = nil,
        googleTestingMode: Bool = false,
        helperProblem: String? = nil,
        freeDiskBytes: Int64? = nil
    ) {
        self.zoomInstalled = zoomInstalled
        self.preflight = preflight
        self.lmsCredentialsSaved = lmsCredentialsSaved
        self.lms = lms
        self.recordingSyncEnabled = recordingSyncEnabled
        self.googleConnected = googleConnected
        self.spreadsheetConfigured = spreadsheetConfigured
        self.sheet = sheet
        self.googleConnectedAt = googleConnectedAt
        self.googleTestingMode = googleTestingMode
        self.helperProblem = helperProblem
        self.freeDiskBytes = freeDiskBytes
    }
}

/// Turns probe results into the report. The Zoom part reuses `PreflightChecker`'s findings.
public enum HealthChecker {
    public static let testingModeTokenLifetime: TimeInterval = 7 * 24 * 60 * 60
    public static let lowDiskBytes: Int64 = 5 * 1024 * 1024 * 1024
    public static let criticalDiskBytes: Int64 = 1024 * 1024 * 1024

    public static func report(
        schedule: ZoomSchedule,
        startsAt: Date,
        configuration: SchedulerConfiguration,
        lmsSettings: LmsSettings,
        probes: HealthProbeResults,
        now: Date = Date(),
        calendar: Calendar = .current
    ) -> HealthReport {
        let group = configuration.group(for: schedule)
        let (date, _) = LmsFollowUpQueue.dashboardDateAndTime(startsAt, calendar: calendar)
        var items: [HealthItem] = []
        items += zoomItems(schedule: schedule, configuration: configuration, probes: probes)
        items += lmsItems(group: group, configuration: configuration, settings: lmsSettings, probes: probes)
        items += googleItems(group: group, probes: probes, now: now)
        items += recordingItems(probes: probes)
        items += systemItems(probes: probes)
        return HealthReport(
            scheduleID: schedule.id,
            scheduleName: schedule.name,
            groupCode: group?.dashboardGroupName,
            sessionDate: date,
            startsAt: startsAt,
            checkedAt: now,
            includesNetwork: probes.lms != nil || probes.sheet != nil,
            items: items
        )
    }

    static func zoomItems(schedule: ZoomSchedule, configuration: SchedulerConfiguration, probes: HealthProbeResults) -> [HealthItem] {
        let issues = Dictionary(probes.preflight.issues.map { ($0.kind, $0) }, uniquingKeysWith: { first, _ in first })
        func item(_ name: String, failing kinds: [PreflightIssue.Kind], passDetail: String? = nil) -> HealthItem {
            for kind in kinds {
                if let issue = issues[kind] {
                    return HealthItem(.zoom, name, issue.severity == .blocking ? .fail : .warning, detail: issue.message, fix: issue.remedy)
                }
            }
            return HealthItem(.zoom, name, .pass, detail: passDetail)
        }

        var items = [
            probes.zoomInstalled
                ? HealthItem(.zoom, "Zoom installed", .pass)
                : HealthItem(.zoom, "Zoom installed", .fail, detail: "zoom.us is not in Applications", fix: "Install Zoom"),
            item("Accessibility permission", failing: [.accessibilityMissing])
        ]
        if issues[.accessibilityMissing] != nil {
            items.append(HealthItem(.zoom, "Zoom account", .skipped, detail: "Needs Accessibility first"))
            return items
        }
        if let profile = configuration.profile(for: schedule) {
            if issues[.zoomNotRunning] != nil {
                items.append(HealthItem(.zoom, "Zoom account", .skipped, detail: "Zoom isn't running; checked when it opens"))
            } else {
                items.append(item("Zoom account", failing: [.zoomUnreachable, .accountNotFound, .accountAmbiguous], passDetail: profile.accountIdentifier))
            }
        } else {
            items.append(HealthItem(.zoom, "Zoom account", .fail, detail: "The schedule has no account profile", fix: "Choose an account in Schedules"))
        }
        items.append(item("Meeting can start", failing: [.meetingNotConfigured, .meetingAlreadyRunning]))
        items.append(item("Attendance ready", failing: [.noAttendanceGroup, .rosterEmpty]))
        return items
    }

    static func lmsItems(group: StudentGroup?, configuration: SchedulerConfiguration, settings: LmsSettings, probes: HealthProbeResults) -> [HealthItem] {
        guard settings.isAnythingEnabled else { return [HealthItem(.lms, "LMS steps", .skipped, detail: "Switched off")] }
        guard let group else { return [HealthItem(.lms, "Group mapping", .skipped, detail: "No attendance group is linked")] }
        var items: [HealthItem] = []
        items.append(probes.lmsCredentialsSaved
            ? HealthItem(.lms, "Credentials available", .pass)
            : HealthItem(.lms, "Credentials available", .fail, detail: "No LMS sign-in is saved", fix: "Add it in Automation → LMS"))
        if let problem = LmsGroupMapping.problem(for: group, in: configuration) {
            items.append(HealthItem(.lms, "Group mapping", .fail, detail: problem.message, fix: "Give each group its own LMS code in Schedules → Groups"))
        } else {
            items.append(HealthItem(.lms, "Group mapping", .pass, detail: group.dashboardGroupName))
        }
        guard let lms = probes.lms else {
            items.append(HealthItem(.lms, "Login works", .skipped, detail: "Checked 30 min before class or with Run Health Check"))
            items.append(HealthItem(.lms, "Session exists", .skipped))
            return items
        }
        guard lms.signedIn else {
            items.append(HealthItem(.lms, "Login works", .fail, detail: lms.message, fix: "Reconnect the LMS account in Automation → LMS"))
            items.append(HealthItem(.lms, "Session exists", .skipped))
            return items
        }
        items.append(HealthItem(.lms, "Login works", .pass))
        switch lms.sessions {
        case .some(1):
            items.append(HealthItem(.lms, "Session exists", .pass))
        case .some(0):
            items.append(HealthItem(.lms, "Session exists", .warning, detail: "No \(group.dashboardGroupName) session is listed for that day", fix: "Check the class date on the LMS; Run Session and attendance will not find it"))
        case .some(let count):
            items.append(HealthItem(.lms, "Session exists", .warning, detail: "\(count) sessions are listed for that day", fix: "The scheduled time decides which one is used"))
        case .none:
            items.append(HealthItem(.lms, "Session exists", .warning, detail: lms.message))
        }
        return items
    }

    static func googleItems(group: StudentGroup?, probes: HealthProbeResults, now: Date) -> [HealthItem] {
        guard probes.recordingSyncEnabled else { return [] }
        var items: [HealthItem] = []
        guard probes.googleConnected else {
            return [HealthItem(.google, "OAuth token valid", .fail, detail: "Google is not connected", fix: "Connect Google in Automation → Recording Sync")]
        }
        let sheet = probes.sheet
        if sheet == nil {
            items.append(HealthItem(.google, "OAuth token valid", .skipped, detail: "Checked 30 min before class or with Run Health Check"))
        } else if let sheet, !sheet.tokenValid {
            items.append(HealthItem(.google, "OAuth token valid", .fail, detail: sheet.message, fix: "Reconnect Google in Automation → Recording Sync"))
            return items
        } else {
            var token = HealthItem(.google, "OAuth token valid", .pass)
            if probes.googleTestingMode, let connectedAt = probes.googleConnectedAt {
                let left = connectedAt.addingTimeInterval(testingModeTokenLifetime).timeIntervalSince(now)
                let days = Int((left / 86_400).rounded(.down))
                if left <= 0 {
                    token = HealthItem(.google, "OAuth token valid", .warning, detail: "Past the 7-day Testing-mode lifetime", fix: "Reconnect Google, or publish the Google app to production")
                } else if days <= 3 {
                    token = HealthItem(.google, "OAuth token valid", .warning, detail: "Recording sync token expires in \(max(days, 0)) day(s)", fix: "Reconnect Google, or publish the Google app to production")
                }
            }
            items.append(token)
        }
        guard probes.spreadsheetConfigured else {
            items.append(HealthItem(.google, "Spreadsheet accessible", .fail, detail: "No spreadsheet ID is set", fix: "Enter it in Automation → Recording Sync"))
            return items
        }
        if let sheet, sheet.tokenValid {
            if let tabs = sheet.tabs {
                let code = group.map { LmsGroupMapping.key($0.dashboardGroupName) }
                if let code, !tabs.contains(where: { LmsGroupMapping.key($0) == code }) {
                    items.append(HealthItem(.google, "Spreadsheet accessible", .warning, detail: "No tab named \(group!.dashboardGroupName)", fix: "Add a tab for the group, or its recordings are never found"))
                } else {
                    items.append(HealthItem(.google, "Spreadsheet accessible", .pass, detail: "\(tabs.count) tab(s)"))
                }
            } else {
                items.append(HealthItem(.google, "Spreadsheet accessible", .fail, detail: sheet.message, fix: "Share the sheet with the connected Google account"))
            }
        }
        return items
    }

    static func recordingItems(probes: HealthProbeResults) -> [HealthItem] {
        probes.recordingSyncEnabled
            ? [HealthItem(.recording, "Recording sync enabled", .pass)]
            : [HealthItem(.recording, "Recording sync enabled", .warning, detail: "Recording links will not be attached", fix: "Switch it on in Automation → Recording Sync")]
    }

    static func systemItems(probes: HealthProbeResults) -> [HealthItem] {
        var items: [HealthItem] = []
        if let bytes = probes.freeDiskBytes {
            let gigabytes = String(format: "%.1f GB free", Double(bytes) / 1_073_741_824)
            if bytes < criticalDiskBytes {
                items.append(HealthItem(.system, "Disk space available", .fail, detail: gigabytes, fix: "Free some disk space; registers and browser profiles need room"))
            } else if bytes < lowDiskBytes {
                items.append(HealthItem(.system, "Disk space available", .warning, detail: gigabytes))
            } else {
                items.append(HealthItem(.system, "Disk space available", .pass, detail: gigabytes))
            }
        }
        items.append(probes.helperProblem.map {
            HealthItem(.system, "Node helper available", .fail, detail: $0, fix: "See Automation → Web & Co-host → Check helper")
        } ?? HealthItem(.system, "Node helper available", .pass))
        return items
    }
}
