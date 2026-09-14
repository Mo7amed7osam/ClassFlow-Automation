import AppKit
import ZoomAutoAdmitCore

/// The menu bar item and its menu.
///
/// The menu is rebuilt from `AppState` every time it opens, so it always shows
/// current state without the app pushing UI updates around.
final class MenuBarController: NSObject, NSMenuDelegate {
    var onSetMonitoring: ((Bool) -> Void)?
    var onOpenAccessibilitySettings: (() -> Void)?
    var onCheckAgain: (() -> Void)?
    var onOpenSchedules: (() -> Void)?
    var onOpenSettings: (() -> Void)?
    var onOpenAttendance: (() -> Void)?
    var onOpenAutomation: (() -> Void)?
    var onOpenDashboard: (() -> Void)?
    var onFinalizeAttendance: (() -> Void)?
    var onSnapshotNow: (() -> Void)?
    /// Snapshot evidence lines while a class is running. Deliberately not a
    /// live-presence readout: snapshots cannot support that claim.
    var attendanceSummary: (() -> [String]?)?
    var onRunSchedule: ((ZoomSchedule) -> Void)?
    var onShowRunDetails: (() -> Void)?
    var onQuit: (() -> Void)?
    var schedulerConfiguration: (() -> SchedulerConfiguration)?
    var nextScheduled: (() -> (schedule: ZoomSchedule, date: Date)?)?
    var isWorkflowRunning: (() -> Bool)?

    private let state: AppState
    private let statusItem: NSStatusItem
    private let menu = NSMenu()

    init(state: AppState) {
        self.state = state
        self.statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        super.init()
        menu.delegate = self
        statusItem.menu = menu
        refresh()
    }

    func refresh() {
        precondition(Thread.isMainThread)
        let image = NSImage(
            systemSymbolName: state.menuBarSymbolName,
            accessibilityDescription: "Zoom Auto Admit — \(state.statusText)"
        )
        image?.isTemplate = true
        statusItem.button?.image = image
        statusItem.button?.toolTip = "Zoom Auto Admit — \(state.statusText)"
    }

    func menuWillOpen(_ menu: NSMenu) {
        refresh()
        rebuild()
    }

    // MARK: Menu construction

    private func rebuild() {
        menu.removeAllItems()

        let header = NSMenuItem(title: "Zoom Auto Admit", action: nil, keyEquivalent: "")
        header.isEnabled = false
        menu.addItem(header)
        menu.addItem(.separator())

        addStatusSection()
        menu.addItem(.separator())
        addPreflightSection()
        addNextMeetingSection()
        menu.addItem(.separator())
        addAttendanceSection()
        addActionsSection()
    }

    private func addStatusSection() {
        menu.addItem(statusLine(state.statusText, level: state.statusLevel))

        if case .failed(_, let detail) = state.runOutcome {
            addWrapped(detail, indent: 4)
            let details = NSMenuItem(title: "View Details…", action: #selector(showRunDetails), keyEquivalent: "")
            details.target = self
            details.indentationLevel = 1
            menu.addItem(details)
            return
        }

        switch state.displayStatus {
        case .permissionRequired, .permissionGrantedRelaunchRequired:
            // Plain-language permission help instead of TCC vocabulary.
            addWrapped("Zoom Auto Admit needs Accessibility access to read and press Zoom controls.", indent: 4)
            let open = NSMenuItem(
                title: "Open System Settings",
                action: #selector(openAccessibilitySettings),
                keyEquivalent: ""
            )
            open.target = self
            open.indentationLevel = 1
            menu.addItem(open)

            let check = NSMenuItem(title: "Check Again", action: #selector(checkAgain), keyEquivalent: "")
            check.target = self
            check.indentationLevel = 1
            menu.addItem(check)

        default:
            addDetail("Zoom: \(state.zoomStatus)")
            addDetail("Waiting Room: \(state.waitingRoomStatus)")
            if state.lastAction != nil {
                addDetail("Last: \(state.formattedLastAction)")
            }
        }
    }

    /// Shown between the pre-flight check and the class starting, which is the
    /// window in which the user can still fix something.
    private func addPreflightSection() {
        guard let report = state.preflightReport, !report.issues.isEmpty else { return }

        menu.addItem(statusLine(
            report.isReady ? "Starts soon — \(report.warnings.count) warning(s)" : report.headline,
            level: report.isReady ? .warning : .error
        ))
        for issue in report.issues.prefix(3) {
            addWrapped(issue.remedy.map { "\(issue.message) — \($0)" } ?? issue.message, indent: 4)
        }
        menu.addItem(.separator())
    }

    private func addNextMeetingSection() {
        let heading = NSMenuItem(title: "Next meeting", action: nil, keyEquivalent: "")
        heading.isEnabled = false
        menu.addItem(heading)

        guard let next = nextScheduled?() else {
            addDetail("No upcoming meetings")
            return
        }

        let configuration = schedulerConfiguration?() ?? SchedulerConfiguration()
        addDetail(next.schedule.name)
        addDetail(Self.relativeFormatter.string(from: next.date))
        addDetail("Account: \(configuration.profile(for: next.schedule)?.name ?? "Not set")")
        addDetail("Auto Admit: \(next.schedule.enablesAutoAdmit ? "On" : "Off")")
    }

    private func addAttendanceSection() {
        guard let lines = attendanceSummary?(), !lines.isEmpty else { return }
        let heading = NSMenuItem(title: "Attendance backup", action: nil, keyEquivalent: "")
        heading.isEnabled = false
        menu.addItem(heading)
        lines.forEach(addDetail)
        menu.addItem(.separator())
    }

    private func addActionsSection() {
        let dashboard = NSMenuItem(title: "Dashboard…", action: #selector(openDashboard), keyEquivalent: "d")
        dashboard.target = self
        dashboard.toolTip = "Every class today and tomorrow: Zoom, attendance, LMS, recording, health checks"
        menu.addItem(dashboard)

        let attendance = NSMenuItem(title: "Attendance", action: nil, keyEquivalent: "")
        let attendanceMenu = NSMenu()
        let review = NSMenuItem(title: "Review Attendance…", action: #selector(openAttendance), keyEquivalent: "")
        review.target = self
        attendanceMenu.addItem(review)

        let snapshot = NSMenuItem(title: "Take Snapshot Now", action: #selector(snapshotNow), keyEquivalent: "")
        snapshot.target = self
        snapshot.isEnabled = attendanceSummary?() != nil
        attendanceMenu.addItem(snapshot)
        let finalize = NSMenuItem(
            title: "Finalize Attendance",
            action: #selector(finalizeAttendance),
            keyEquivalent: ""
        )
        finalize.target = self
        finalize.isEnabled = attendanceSummary?() != nil
        attendanceMenu.addItem(finalize)
        attendance.submenu = attendanceMenu
        menu.addItem(attendance)

        let automation = NSMenuItem(title: "Automation…", action: #selector(openAutomation), keyEquivalent: "")
        automation.target = self
        automation.toolTip = "DEPI dashboard, recording API, Zoom Web meetings and co-host"
        menu.addItem(automation)

        let schedules = NSMenuItem(title: "Schedules", action: nil, keyEquivalent: "")
        schedules.submenu = schedulesSubmenu()
        menu.addItem(schedules)

        if state.monitoringEnabled {
            let stop = NSMenuItem(title: "Stop Monitoring", action: #selector(stopMonitoring), keyEquivalent: "")
            stop.target = self
            menu.addItem(stop)
        } else {
            let start = NSMenuItem(title: "Start Monitoring", action: #selector(startMonitoring), keyEquivalent: "")
            start.target = self
            menu.addItem(start)
        }

        let settings = NSMenuItem(title: "Settings…", action: #selector(openSettings), keyEquivalent: ",")
        settings.target = self
        menu.addItem(settings)

        menu.addItem(.separator())
        let quit = NSMenuItem(title: "Quit", action: #selector(quit), keyEquivalent: "q")
        quit.target = self
        menu.addItem(quit)
    }

    private func schedulesSubmenu() -> NSMenu {
        let submenu = NSMenu()
        let configuration = schedulerConfiguration?() ?? SchedulerConfiguration()
        let workflowBusy = isWorkflowRunning?() ?? false

        if configuration.schedules.isEmpty {
            let empty = NSMenuItem(title: "No scheduled meetings", action: nil, keyEquivalent: "")
            empty.isEnabled = false
            submenu.addItem(empty)
            submenu.addItem(.separator())
        }

        for schedule in configuration.schedules {
            let level: StatusLevel = schedule.isEnabled ? .normal : .neutral
            let title = "\(level.dot) \(schedule.name)"
            let item = NSMenuItem(title: title, action: nil, keyEquivalent: "")
            item.attributedTitle = attributed(title, dotColor: level.color)
            item.isEnabled = false
            submenu.addItem(item)

            let account = configuration.profile(for: schedule)?.name ?? "No account"
            let summary = NSMenuItem(
                title: "\(schedule.recurrence.displayText) · \(schedule.startTime.displayText)",
                action: nil,
                keyEquivalent: ""
            )
            summary.isEnabled = false
            summary.indentationLevel = 1
            submenu.addItem(summary)

            let detail = NSMenuItem(
                title: "\(account) · \(schedule.meeting.displayText)",
                action: nil,
                keyEquivalent: ""
            )
            detail.isEnabled = false
            detail.indentationLevel = 1
            submenu.addItem(detail)

            let run = NSMenuItem(title: "Run Now", action: #selector(runSchedule(_:)), keyEquivalent: "")
            run.target = self
            run.representedObject = schedule
            run.indentationLevel = 1
            run.isEnabled = !workflowBusy
            submenu.addItem(run)

            submenu.addItem(.separator())
        }

        let manage = NSMenuItem(title: "Manage Schedules…", action: #selector(openSchedules), keyEquivalent: "")
        manage.target = self
        submenu.addItem(manage)
        return submenu
    }

    // MARK: Menu item helpers

    private func statusLine(_ text: String, level: StatusLevel) -> NSMenuItem {
        let item = NSMenuItem(title: "\(level.dot) \(text)", action: nil, keyEquivalent: "")
        item.attributedTitle = attributed("\(level.dot) \(text)", dotColor: level.color)
        item.isEnabled = false
        return item
    }

    /// Colours only the leading status dot, so the text keeps the system colour
    /// and stays legible in both appearances.
    private func attributed(_ text: String, dotColor: NSColor) -> NSAttributedString {
        let result = NSMutableAttributedString(
            string: text,
            attributes: [.font: NSFont.menuFont(ofSize: 0), .foregroundColor: NSColor.labelColor]
        )
        if let dotRange = text.range(of: text.prefix(1)) {
            result.addAttribute(
                .foregroundColor,
                value: dotColor,
                range: NSRange(dotRange, in: text)
            )
        }
        return result
    }

    private func addDetail(_ text: String) {
        let item = NSMenuItem(title: text, action: nil, keyEquivalent: "")
        item.isEnabled = false
        item.indentationLevel = 1
        menu.addItem(item)
    }

    /// Wraps long explanations across menu rows; a menu cannot wrap text itself.
    private func addWrapped(_ text: String, indent: Int) {
        var line = ""
        for word in text.split(separator: " ") {
            if line.count + word.count + 1 > 44 {
                addDetail(line)
                line = String(word)
            } else {
                line = line.isEmpty ? String(word) : "\(line) \(word)"
            }
        }
        if !line.isEmpty { addDetail(line) }
    }

    private static let relativeFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.dateStyle = .medium
        formatter.timeStyle = .short
        formatter.doesRelativeDateFormatting = true
        return formatter
    }()

    // MARK: Actions

    @objc private func startMonitoring() { onSetMonitoring?(true) }
    @objc private func stopMonitoring() { onSetMonitoring?(false) }
    @objc private func openAccessibilitySettings() { onOpenAccessibilitySettings?() }
    @objc private func checkAgain() { onCheckAgain?() }
    @objc private func openSchedules() { onOpenSchedules?() }
    @objc private func openSettings() { onOpenSettings?() }
    @objc private func openAttendance() { onOpenAttendance?() }
    @objc private func openAutomation() { onOpenAutomation?() }
    @objc private func finalizeAttendance() { onFinalizeAttendance?() }
    @objc private func openDashboard() { onOpenDashboard?() }
    @objc private func snapshotNow() { onSnapshotNow?() }
    @objc private func showRunDetails() { onShowRunDetails?() }
    @objc private func quit() { onQuit?() }

    @objc private func runSchedule(_ sender: NSMenuItem) {
        guard let schedule = sender.representedObject as? ZoomSchedule else { return }
        onRunSchedule?(schedule)
    }
}
