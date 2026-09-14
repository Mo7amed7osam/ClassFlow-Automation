import AppKit
import ZoomAutoAdmitCore

/// The operations dashboard: one card per class today and tomorrow, and the notification history.
final class DashboardWindowController: NSWindowController, NSTableViewDataSource, NSTableViewDelegate {
    private let center: OperationsCenter
    private let configurationProvider: () -> SchedulerConfiguration
    var onOpenAutomation: (() -> Void)?
    var onOpenAttendance: (() -> Void)?
    var onRunFollowUp: ((String) -> Void)?

    private let summaryLabel = NSTextField(labelWithString: "")
    private let statusLabel = NSTextField(labelWithString: "")
    private let modePicker = NSSegmentedControl(labels: ["Sessions", "Notifications"], trackingMode: .selectOne, target: nil, action: nil)
    private let healthButton = NSButton(title: "Run Health Check", target: nil, action: nil)
    private let cardsStack = NSStackView()
    private let cardsScroll = NSScrollView()
    private let historyTable = NSTableView()
    private let historyScroll = NSScrollView()
    private var history: [OperationsEvent] = []
    private var cards: [SessionOverview] = []
    private var focusedSessionKey: String?
    private var refreshTimer: Timer?
    private var building = false

    init(center: OperationsCenter, configurationProvider: @escaping () -> SchedulerConfiguration) {
        self.center = center
        self.configurationProvider = configurationProvider
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 1000, height: 760),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        window.title = "Operations Dashboard"
        window.minSize = NSSize(width: 760, height: 520)
        window.center()
        window.setFrameAutosaveName("OperationsDashboardWindow")
        super.init(window: window)
        window.contentView = makeContent()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) is not used") }

    var isVisible: Bool { window?.isVisible == true }

    func present(focus sessionKey: String? = nil) {
        focusedSessionKey = sessionKey
        if sessionKey != nil {
            modePicker.selectedSegment = 0
            modeChanged()
        }
        refresh()
        NSApp.activate(ignoringOtherApps: true)
        showWindow(nil)
        window?.makeKeyAndOrderFront(nil)
        refreshTimer?.invalidate()
        refreshTimer = Timer.scheduledTimer(withTimeInterval: 20, repeats: true) { [weak self] _ in
            guard let self, self.isVisible else { return }
            self.refresh()
        }
    }

    // MARK: Layout

    private func makeContent() -> NSView {
        let title = NSTextField(labelWithString: "Today")
        title.font = .systemFont(ofSize: 22, weight: .bold)
        summaryLabel.textColor = .secondaryLabelColor
        statusLabel.textColor = .secondaryLabelColor
        statusLabel.lineBreakMode = .byTruncatingTail

        modePicker.selectedSegment = 0
        modePicker.target = self
        modePicker.action = #selector(modeChanged)
        healthButton.target = self
        healthButton.action = #selector(runNextHealthCheck)
        healthButton.bezelStyle = .rounded
        healthButton.keyEquivalent = "\r"
        let refreshButton = NSButton(title: "Refresh", target: self, action: #selector(refreshPressed))
        let automationButton = NSButton(title: "Automation…", target: self, action: #selector(openAutomation))
        let attendanceButton = NSButton(title: "Attendance…", target: self, action: #selector(openAttendance))

        let titles = NSStackView(views: [title, summaryLabel])
        titles.orientation = .vertical
        titles.alignment = .leading
        titles.spacing = 2
        let spacer = NSView()
        spacer.setContentHuggingPriority(.defaultLow, for: .horizontal)
        let header = NSStackView(views: [titles, spacer, modePicker, attendanceButton, automationButton, refreshButton, healthButton])
        header.orientation = .horizontal
        header.alignment = .centerY
        header.spacing = 8

        cardsStack.orientation = .vertical
        cardsStack.alignment = .leading
        cardsStack.spacing = 14
        cardsStack.translatesAutoresizingMaskIntoConstraints = false
        let document = DashboardFlippedView()
        document.translatesAutoresizingMaskIntoConstraints = false
        document.addSubview(cardsStack)
        cardsScroll.documentView = document
        cardsScroll.hasVerticalScroller = true
        cardsScroll.drawsBackground = false
        NSLayoutConstraint.activate([
            cardsStack.topAnchor.constraint(equalTo: document.topAnchor, constant: 4),
            cardsStack.leadingAnchor.constraint(equalTo: document.leadingAnchor),
            cardsStack.trailingAnchor.constraint(equalTo: document.trailingAnchor),
            cardsStack.bottomAnchor.constraint(equalTo: document.bottomAnchor, constant: -12),
            document.widthAnchor.constraint(equalTo: cardsScroll.contentView.widthAnchor)
        ])

        for (id, title, width) in [("time", "Time", 130.0), ("severity", "", 28.0), ("session", "Class", 190.0), ("title", "Notification", 260.0), ("message", "Details", 420.0)] {
            let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier(id))
            column.title = title
            column.width = CGFloat(width)
            historyTable.addTableColumn(column)
        }
        historyTable.dataSource = self
        historyTable.delegate = self
        historyTable.usesAlternatingRowBackgroundColors = true
        historyTable.rowHeight = 34
        historyTable.target = self
        historyTable.doubleAction = #selector(historyDoubleClicked)
        historyScroll.documentView = historyTable
        historyScroll.hasVerticalScroller = true
        historyScroll.borderType = .bezelBorder
        historyScroll.isHidden = true

        let body = NSView()
        for view in [cardsScroll, historyScroll] {
            view.translatesAutoresizingMaskIntoConstraints = false
            body.addSubview(view)
            NSLayoutConstraint.activate([
                view.topAnchor.constraint(equalTo: body.topAnchor),
                view.bottomAnchor.constraint(equalTo: body.bottomAnchor),
                view.leadingAnchor.constraint(equalTo: body.leadingAnchor),
                view.trailingAnchor.constraint(equalTo: body.trailingAnchor)
            ])
        }

        let root = NSStackView(views: [header, body, statusLabel])
        root.orientation = .vertical
        root.alignment = .leading
        root.spacing = 12
        root.edgeInsets = NSEdgeInsets(top: 16, left: 20, bottom: 12, right: 20)
        for view in [header, body, statusLabel] {
            view.translatesAutoresizingMaskIntoConstraints = false
            view.widthAnchor.constraint(equalTo: root.widthAnchor, constant: -40).isActive = true
        }
        body.setContentHuggingPriority(.defaultLow, for: .vertical)
        return root
    }

    // MARK: Refresh

    func refresh() {
        guard !building else { return }
        building = true
        let center = self.center
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            let cards = center.overviews()
            let history = center.history()
            DispatchQueue.main.async {
                guard let self else { return }
                self.building = false
                self.cards = cards
                self.history = history
                self.render()
            }
        }
    }

    private func render() {
        let calendar = Calendar.current
        let running = cards.filter { $0.zoom == .running || $0.zoom == .starting }.count
        let attention = cards.filter { $0.needsAttention || center.report(for: $0)?.isReady == false }.count
        let todayCount = cards.filter { calendar.isDateInToday($0.startsAt) }.count
        summaryLabel.stringValue = "\(DateFormatter.localizedString(from: Date(), dateStyle: .full, timeStyle: .none)) · \(todayCount) class(es) today · \(running) running · \(attention) need attention"

        let scrollOrigin = cardsScroll.contentView.bounds.origin
        cardsStack.arrangedSubviews.forEach { $0.removeFromSuperview() }
        if cards.isEmpty {
            cardsStack.addArrangedSubview(NSTextField(labelWithString: "No classes are scheduled today or tomorrow."))
        }
        var focusView: NSView?
        for (dayTitle, dayCards) in [("Today", cards.filter { calendar.isDateInToday($0.startsAt) }), ("Tomorrow", cards.filter { calendar.isDateInTomorrow($0.startsAt) })] where !dayCards.isEmpty {
            let heading = NSTextField(labelWithString: dayTitle)
            heading.font = .systemFont(ofSize: 15, weight: .semibold)
            cardsStack.addArrangedSubview(heading)
            for pair in stride(from: 0, to: dayCards.count, by: 2) {
                let rowCards = dayCards[pair..<min(pair + 2, dayCards.count)].map { card -> NSView in
                    let view = makeCard(card)
                    if let focusedSessionKey, card.sessionKey == focusedSessionKey, calendar.isDateInToday(card.startsAt) || focusView == nil { focusView = view }
                    return view
                }
                var views = rowCards
                if views.count == 1 { views.append(NSView()) }
                let row = NSStackView(views: views)
                row.orientation = .horizontal
                row.alignment = .top
                row.distribution = .fillEqually
                row.spacing = 14
                row.translatesAutoresizingMaskIntoConstraints = false
                cardsStack.addArrangedSubview(row)
                row.widthAnchor.constraint(equalTo: cardsStack.widthAnchor).isActive = true
            }
        }
        cardsStack.layoutSubtreeIfNeeded()
        if let focusView {
            focusView.scrollToVisible(focusView.bounds)
            focusedSessionKey = nil
        } else {
            cardsScroll.contentView.scroll(to: scrollOrigin)
        }
        historyTable.reloadData()
    }

    private func makeCard(_ card: SessionOverview) -> NSView {
        let report = center.report(for: card)
        let checking = center.isChecking(card)

        let box = DashboardCardView()
        box.wantsLayer = true
        box.layer?.cornerRadius = 10
        box.layer?.borderWidth = card.needsAttention || report?.isReady == false ? 2 : 1
        box.layer?.borderColor = (card.needsAttention || report?.isReady == false ? NSColor.systemRed : NSColor.separatorColor).cgColor
        box.layer?.backgroundColor = NSColor.controlBackgroundColor.cgColor
        if let focusedSessionKey, card.sessionKey == focusedSessionKey {
            box.layer?.borderColor = NSColor.controlAccentColor.cgColor
            box.layer?.borderWidth = 3
        }

        let title = NSTextField(labelWithString: card.groupCode ?? card.scheduleName)
        title.font = .systemFont(ofSize: 16, weight: .bold)
        let time = NSTextField(labelWithString: timeRange(card))
        time.textColor = .secondaryLabelColor
        let spacer = NSView()
        spacer.setContentHuggingPriority(.defaultLow, for: .horizontal)
        let top = NSStackView(views: [title, spacer, time])
        top.orientation = .horizontal

        let subtitle = NSTextField(labelWithString: [card.accountName.map { "Zoom: \($0)" }, card.scheduleName].compactMap { $0 }.joined(separator: " · "))
        subtitle.textColor = .secondaryLabelColor
        subtitle.font = .systemFont(ofSize: 11)
        subtitle.lineBreakMode = .byTruncatingTail

        let grid = NSGridView()
        grid.rowSpacing = 5
        grid.columnSpacing = 10
        func addRow(_ label: String, _ phase: String, _ tone: StatusTone, _ details: [String]) {
            let name = NSTextField(labelWithString: label)
            name.font = .systemFont(ofSize: 12, weight: .semibold)
            let value = wrapping(([dot(tone) + " " + phase] + details).joined(separator: "\n"), size: 12)
            value.textColor = .labelColor
            let attributed = NSMutableAttributedString(string: value.stringValue, attributes: [.font: NSFont.systemFont(ofSize: 12), .foregroundColor: NSColor.secondaryLabelColor])
            let firstLine = (value.stringValue as NSString).range(of: "\n").location
            let headRange = NSRange(location: 0, length: firstLine == NSNotFound ? attributed.length : firstLine)
            attributed.addAttributes([.foregroundColor: NSColor.labelColor, .font: NSFont.systemFont(ofSize: 12, weight: .medium)], range: headRange)
            attributed.addAttribute(.foregroundColor, value: color(tone), range: NSRange(location: 0, length: 1))
            value.attributedStringValue = attributed
            grid.addRow(with: [name, value])
        }
        addRow("Zoom", card.zoom.displayName, card.zoom.tone, card.zoomDetail.map { [$0] } ?? [])
        addRow("Attendance", card.attendance.displayName, card.attendance.tone, card.attendanceDetail)
        addRow("LMS", card.lms.displayName, card.lms.tone, card.lmsDetail)
        addRow("Recording", card.recording.displayName, card.recording.tone, card.recordingDetail.map { [$0] } ?? [])
        grid.column(at: 0).xPlacement = .leading
        grid.column(at: 0).width = 78

        var lines: [NSView] = [top, subtitle, grid]

        if let present = card.present, let total = card.rosterCount {
            let students = NSTextField(labelWithString: "Students   Present \(present)/\(total) · Needs Review \(card.needsReview ?? 0) · Absent \(card.absent ?? 0)")
            students.font = .systemFont(ofSize: 12, weight: .medium)
            lines.append(students)
        }
        if let next = card.nextAction {
            let label = NSTextField(labelWithString: "Next: \(next)")
            label.font = .systemFont(ofSize: 12, weight: .semibold)
            label.textColor = .controlAccentColor
            lines.append(label)
        }
        if let success = card.lastSuccess {
            let label = wrapping("✓ \(success)\(card.lastSuccessAt.map { " · \(shortTime($0))" } ?? "")", size: 11)
            label.textColor = .systemGreen
            lines.append(label)
        }
        if let error = card.lastError {
            let label = wrapping("✗ \(error)\(card.lastErrorAt.map { " · \(shortTime($0))" } ?? "")", size: 11)
            label.textColor = .systemRed
            lines.append(label)
        }
        let preflight: NSTextField
        if checking {
            preflight = NSTextField(labelWithString: "Pre-flight: checking…")
            preflight.textColor = .secondaryLabelColor
        } else if let report {
            let state = report.isReady ? (report.warnings.isEmpty ? "✓ Ready" : "⚠ Ready, \(report.warnings.count) warning(s)") : "✗ \(report.failures.count) check(s) failed"
            preflight = NSTextField(labelWithString: "Pre-flight: \(state) · \(shortTime(report.checkedAt))\(report.includesNetwork ? "" : " (local only)")")
            preflight.textColor = report.isReady ? (report.warnings.isEmpty ? .systemGreen : .systemOrange) : .systemRed
        } else {
            preflight = NSTextField(labelWithString: "Pre-flight: not run yet")
            preflight.textColor = .secondaryLabelColor
        }
        preflight.font = .systemFont(ofSize: 11, weight: .medium)
        lines.append(preflight)

        let check = CardButton(title: checking ? "Checking…" : "Health Check", target: self, action: #selector(cardHealthCheck(_:)))
        check.cardID = card.id
        check.isEnabled = !checking
        check.controlSize = .small
        let details = CardButton(title: "Report…", target: self, action: #selector(cardReport(_:)))
        details.cardID = card.id
        details.isEnabled = report != nil
        details.controlSize = .small
        var buttons: [NSView] = [check, details]
        if let key = card.sessionKey, let overdue = center.automation?.followUps.read().first(where: {
            OperationsSessionKey.make(groupCode: $0.group, sessionDate: $0.sessionDate) == key && $0.dueAt <= Date()
        }) {
            let run = CardButton(title: "Run \(overdue.step.displayName) now", target: self, action: #selector(cardRunStep(_:)))
            run.cardID = overdue.id
            run.controlSize = .small
            buttons.append(run)
        }
        let buttonRow = NSStackView(views: buttons)
        buttonRow.orientation = .horizontal
        buttonRow.spacing = 6
        lines.append(buttonRow)

        let stack = NSStackView(views: lines)
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 7
        stack.edgeInsets = NSEdgeInsets(top: 12, left: 14, bottom: 12, right: 14)
        stack.translatesAutoresizingMaskIntoConstraints = false
        box.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.topAnchor.constraint(equalTo: box.topAnchor),
            stack.bottomAnchor.constraint(equalTo: box.bottomAnchor),
            stack.leadingAnchor.constraint(equalTo: box.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: box.trailingAnchor),
            top.widthAnchor.constraint(equalTo: stack.widthAnchor, constant: -28)
        ])
        return box
    }

    private func wrapping(_ text: String, size: CGFloat) -> NSTextField {
        let field = NSTextField(wrappingLabelWithString: text)
        field.font = .systemFont(ofSize: size)
        field.preferredMaxLayoutWidth = 380
        field.isSelectable = true
        return field
    }

    private func dot(_ tone: StatusTone) -> String { tone == .idle ? "○" : "●" }

    private func color(_ tone: StatusTone) -> NSColor {
        switch tone {
        case .good, .active: return .systemGreen
        case .pending: return .systemYellow
        case .idle: return .tertiaryLabelColor
        case .warning: return .systemOrange
        case .bad: return .systemRed
        }
    }

    private func timeRange(_ card: SessionOverview) -> String {
        let formatter = DateFormatter()
        formatter.dateFormat = "EEE d MMM · HH:mm"
        let hours = DateFormatter()
        hours.dateFormat = "HH:mm"
        let end = card.endsAt.map { "–\(hours.string(from: $0))" } ?? ""
        return formatter.string(from: card.startsAt) + end
    }

    private func shortTime(_ date: Date) -> String {
        let formatter = DateFormatter()
        formatter.dateFormat = Calendar.current.isDateInToday(date) ? "HH:mm" : "d MMM HH:mm"
        return formatter.string(from: date)
    }

    // MARK: Actions

    @objc private func modeChanged() {
        let sessions = modePicker.selectedSegment == 0
        cardsScroll.isHidden = !sessions
        historyScroll.isHidden = sessions
        healthButton.isHidden = !sessions
    }

    @objc private func refreshPressed() { refresh() }
    @objc private func openAutomation() { onOpenAutomation?() }
    @objc private func openAttendance() { onOpenAttendance?() }

    /// The next class that has not ended, or the running one.
    @objc private func runNextHealthCheck() {
        let now = Date()
        guard let card = cards.first(where: { $0.zoom == .running || $0.zoom == .starting })
            ?? cards.first(where: { $0.startsAt > now }) else {
            statusLabel.stringValue = "No upcoming class to check."
            return
        }
        runHealthCheck(card)
    }

    @objc private func cardHealthCheck(_ sender: CardButton) {
        guard let card = cards.first(where: { $0.id == sender.cardID }) else { return }
        runHealthCheck(card)
    }

    private func runHealthCheck(_ card: SessionOverview) {
        guard let schedule = configurationProvider().schedules.first(where: { $0.id == card.scheduleID }) else { return }
        statusLabel.stringValue = "Checking \(card.groupCode ?? card.scheduleName): Zoom, LMS sign-in, Google Sheets, helper… (up to a minute)"
        center.runHealthCheck(schedule: schedule, startsAt: card.startsAt, includeNetwork: true, reason: "Run Health Check") { [weak self] report in
            guard let self else { return }
            self.statusLabel.stringValue = "\(report.headline) · checked \(self.shortTime(report.checkedAt))"
            self.refresh()
            self.show(report)
        }
        refresh()
    }

    @objc private func cardReport(_ sender: CardButton) {
        guard let card = cards.first(where: { $0.id == sender.cardID }), let report = center.report(for: card) else { return }
        show(report)
    }

    @objc private func cardRunStep(_ sender: CardButton) {
        guard let id = sender.cardID else { return }
        statusLabel.stringValue = "Running the LMS step now; follow it in Automation → Log."
        onRunFollowUp?(id)
    }

    private func show(_ report: HealthReport) {
        let alert = NSAlert()
        alert.alertStyle = report.isReady ? .informational : .warning
        alert.messageText = report.isReady ? "Ready to start session" : "Session will not work as planned"
        let text = NSTextView(frame: NSRect(x: 0, y: 0, width: 460, height: 360))
        text.string = report.text
        text.font = .monospacedSystemFont(ofSize: 12, weight: .regular)
        text.isEditable = false
        text.textContainerInset = NSSize(width: 6, height: 6)
        let scroll = NSScrollView(frame: NSRect(x: 0, y: 0, width: 460, height: 360))
        scroll.documentView = text
        scroll.hasVerticalScroller = true
        scroll.borderType = .bezelBorder
        alert.accessoryView = scroll
        if let window { alert.beginSheetModal(for: window) } else { alert.runModal() }
    }

    @objc private func historyDoubleClicked() {
        let row = historyTable.clickedRow
        guard history.indices.contains(row), let key = history[row].sessionKey else { return }
        focusedSessionKey = key
        modePicker.selectedSegment = 0
        modeChanged()
        render()
    }

    // MARK: History table

    func numberOfRows(in tableView: NSTableView) -> Int { history.count }

    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        guard let column = tableColumn, history.indices.contains(row) else { return nil }
        let event = history[row]
        let text: String
        switch column.identifier.rawValue {
        case "time":
            let formatter = DateFormatter()
            formatter.dateFormat = "d MMM HH:mm"
            text = formatter.string(from: event.at)
        case "severity":
            switch event.severity {
            case .success: text = "✅"
            case .warning: text = "⚠️"
            case .failure: text = "❌"
            case .info: text = "ℹ️"
            }
        case "session":
            text = [event.groupCode, event.sessionDate].compactMap { $0 }.joined(separator: " · ")
        case "title":
            text = event.title + (event.notified == false ? " (grouped)" : "")
        default:
            text = event.message.replacingOccurrences(of: "\n\n", with: " · ").replacingOccurrences(of: "\n", with: " ")
        }
        let field = NSTextField(labelWithString: text)
        field.lineBreakMode = .byTruncatingTail
        field.toolTip = column.identifier.rawValue == "message" ? event.message : nil
        if event.notified == false { field.textColor = .secondaryLabelColor }
        return field
    }
}

private final class DashboardFlippedView: NSView {
    override var isFlipped: Bool { true }
}

private final class DashboardCardView: NSView {
    override func updateLayer() {
        super.updateLayer()
        layer?.backgroundColor = NSColor.controlBackgroundColor.cgColor
    }
    override var wantsUpdateLayer: Bool { true }
}

private final class CardButton: NSButton {
    var cardID: String?
}
