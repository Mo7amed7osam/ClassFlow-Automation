import AppKit
import Foundation
import ZoomAutoAdmitCore

/// The attendance register: summary, per-student status, and the controls to
/// correct anything the app got wrong.
final class AttendanceWindowController: NSWindowController {
    private let store: AttendanceStore
    private let configurationProvider: () -> SchedulerConfiguration
    private let configurationWriter: (SchedulerConfiguration) -> Void
    private let liveSessionProvider: () -> AttendanceSession?
    private let finalizeHandler: () -> AttendanceSession?

    private var sessions: [AttendanceSession] = []
    private var selectedSession: AttendanceSession?

    /// The register, grouped by status. Needs Review comes first: it is the
    /// only section that asks anything of the reader.
    private enum ListItem {
        case header(title: String, count: Int, tint: NSColor)
        case record(AttendanceRecord)
        /// A Zoom identity that matched nobody on the roster.
        case unresolved(ParticipantObservation)
    }
    private var items: [ListItem] = []
    private let searchField = NSSearchField()

    private let sessionPopUp = NSPopUpButton()
    /// Present / Needs Review / Absent, as a summary strip.
    private let statStrip = NSStackView()
    private let summaryLabel = NSTextField(labelWithString: "")
    private let statusLabel = NSTextField(labelWithString: "")
    private let table = NSTableView()
    /// The same register, in the order the official list is written in.
    private let rosterTable = NSTableView()
    private var rosterRecords: [AttendanceRecord] = []
    private let copyRosterButton = NSButton(title: "Copy Full List", target: nil, action: nil)
    private let tabView = NSTabView()
    private let unmatchedLabel = NSTextField(wrappingLabelWithString: "")
    private let finalizeButton = NSButton(title: "Finalize Attendance", target: nil, action: nil)
    private let aiButton = NSButton(title: "Match with AI", target: nil, action: nil)
    private let exportButton = NSButton(title: "Export CSV…", target: nil, action: nil)
    private let aiDetailsButton = NSButton(title: "AI Details…", target: nil, action: nil)
    /// Kept so the last exchange can be inspected after the fact.
    private struct AIExchangeRecord {
        let exchange: OpenRouterClient.Exchange
        let summary: AIReconciliation.Summary?
    }
    private var lastExchange: AIExchangeRecord?
    private var aiDetailsWindow: NSWindow?

    init(
        store: AttendanceStore,
        configurationProvider: @escaping () -> SchedulerConfiguration,
        configurationWriter: @escaping (SchedulerConfiguration) -> Void,
        liveSessionProvider: @escaping () -> AttendanceSession?,
        finalizeHandler: @escaping () -> AttendanceSession?
    ) {
        self.store = store
        self.configurationProvider = configurationProvider
        self.configurationWriter = configurationWriter
        self.liveSessionProvider = liveSessionProvider
        self.finalizeHandler = finalizeHandler

        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 780, height: 620),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        window.title = "Attendance"
        window.center()
        window.setFrameAutosaveName("AttendanceWindow")
        window.minSize = NSSize(width: 720, height: 540)
        super.init(window: window)
        window.contentView = makeContentView()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) is not used")
    }

    func present() {
        reload()
        if let window, let screen = NSScreen.main, !screen.visibleFrame.intersects(window.frame) {
            window.center()
        }
        NSApp.activate(ignoringOtherApps: true)
        showWindow(nil)
        window?.makeKeyAndOrderFront(nil)
    }

    // MARK: Layout

    private func makeContentView() -> NSView {
        sessionPopUp.target = self
        sessionPopUp.action = #selector(sessionChanged)

        summaryLabel.font = .systemFont(ofSize: 17, weight: .semibold)
        statusLabel.font = .systemFont(ofSize: NSFont.smallSystemFontSize)
        statusLabel.textColor = .secondaryLabelColor
        finalizeButton.keyEquivalent = "\r"
        finalizeButton.bezelStyle = .rounded
        [aiButton, exportButton].forEach { $0.bezelStyle = .rounded }

        let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("student"))
        column.title = "Students"
        column.width = 520
        table.addTableColumn(column)
        table.headerView = nil
        table.rowHeight = 58
        table.intercellSpacing = NSSize(width: 0, height: 4)
        table.selectionHighlightStyle = .none
        table.style = .inset
        table.delegate = self
        table.dataSource = self

        let scroll = Self.listScrollView(for: table)

        // Reading the register back onto another system is a different job from
        // reviewing it: it wants the official order, every student, and nothing
        // to search for. Its own tab keeps both jobs honest.
        let rosterColumn = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("rosterStudent"))
        rosterColumn.title = "Roster"
        rosterColumn.width = 520
        rosterTable.addTableColumn(rosterColumn)
        rosterTable.headerView = nil
        rosterTable.rowHeight = 30
        rosterTable.intercellSpacing = NSSize(width: 0, height: 2)
        rosterTable.selectionHighlightStyle = .none
        rosterTable.style = .inset
        rosterTable.usesAlternatingRowBackgroundColors = true
        rosterTable.delegate = self
        rosterTable.dataSource = self
        let rosterScroll = Self.listScrollView(for: rosterTable)

        copyRosterButton.target = self
        copyRosterButton.action = #selector(copyRosterOrder)
        copyRosterButton.bezelStyle = .rounded
        copyRosterButton.toolTip = "Copy every student, in roster order, as plain text."

        let rosterHint = NSTextField(wrappingLabelWithString:
            "Roster order — the order this group was entered in. "
            + "Every student is listed, and the search box above does not filter this tab."
        )
        rosterHint.font = .systemFont(ofSize: NSFont.smallSystemFontSize)
        rosterHint.textColor = .secondaryLabelColor

        unmatchedLabel.font = .systemFont(ofSize: NSFont.smallSystemFontSize)
        unmatchedLabel.textColor = .secondaryLabelColor

        finalizeButton.target = self
        finalizeButton.action = #selector(finalizeAttendance)
        aiButton.target = self
        aiButton.action = #selector(matchWithAI)
        aiButton.toolTip = "Ask OpenRouter about the names local matching could not settle."
        exportButton.target = self
        exportButton.action = #selector(exportCSV)

        aiDetailsButton.target = self
        aiDetailsButton.action = #selector(showAIDetails)
        aiDetailsButton.isEnabled = false
        aiDetailsButton.bezelStyle = .rounded
        aiDetailsButton.toolTip = "See what was sent to the model and what it replied."

        let actions = NSStackView(views: [aiButton, aiDetailsButton, finalizeButton, NSView(), exportButton])
        actions.orientation = .horizontal
        actions.spacing = 10

        statStrip.orientation = .horizontal
        statStrip.spacing = 10

        searchField.placeholderString = "Search students"
        searchField.target = self
        searchField.action = #selector(searchChanged)
        searchField.sendsSearchStringImmediately = false
        searchField.widthAnchor.constraint(equalToConstant: 240).isActive = true

        let pickerRow = DesignKit.horizontal([sessionPopUp, searchField], spacing: 12)
        let header = NSStackView(views: [summaryLabel, statusLabel, pickerRow, statStrip])
        header.orientation = .vertical
        header.alignment = .leading
        header.spacing = 10
        header.setCustomSpacing(2, after: summaryLabel)
        header.setCustomSpacing(14, after: statusLabel)

        let reviewTab = NSTabViewItem(identifier: "review")
        reviewTab.label = "Review"
        reviewTab.view = Self.tabContent([scroll, unmatchedLabel], stretching: scroll)

        let rosterTab = NSTabViewItem(identifier: "rosterOrder")
        rosterTab.label = "Roster Order"
        rosterTab.view = Self.tabContent(
            [rosterScroll, rosterHint, DesignKit.horizontal([copyRosterButton, NSView()], spacing: 10)],
            stretching: rosterScroll
        )

        tabView.addTabViewItem(reviewTab)
        tabView.addTabViewItem(rosterTab)
        tabView.translatesAutoresizingMaskIntoConstraints = false

        let root = NSStackView(views: [header, tabView, actions])
        root.orientation = .vertical
        root.alignment = .leading
        root.spacing = 16
        root.edgeInsets = NSEdgeInsets(
            top: DesignKit.Metrics.windowMargin,
            left: DesignKit.Metrics.windowMargin,
            bottom: DesignKit.Metrics.windowMargin,
            right: DesignKit.Metrics.windowMargin
        )
        root.translatesAutoresizingMaskIntoConstraints = false

        let container = NSView()
        container.addSubview(root)
        NSLayoutConstraint.activate([
            root.leadingAnchor.constraint(equalTo: container.leadingAnchor),
            root.trailingAnchor.constraint(equalTo: container.trailingAnchor),
            root.topAnchor.constraint(equalTo: container.topAnchor),
            root.bottomAnchor.constraint(equalTo: container.bottomAnchor),
            tabView.widthAnchor.constraint(equalTo: root.widthAnchor, constant: -32),
            tabView.heightAnchor.constraint(greaterThanOrEqualToConstant: 400),
            sessionPopUp.widthAnchor.constraint(equalToConstant: 420),
            actions.widthAnchor.constraint(equalTo: root.widthAnchor, constant: -32)
        ])
        return container
    }

    private static func listScrollView(for table: NSTableView) -> NSScrollView {
        let scroll = NSScrollView()
        scroll.documentView = table
        scroll.hasVerticalScroller = true
        scroll.borderType = .noBorder
        scroll.drawsBackground = false
        scroll.wantsLayer = true
        scroll.layer?.cornerRadius = DesignKit.Metrics.cardCornerRadius
        scroll.layer?.borderWidth = 1
        scroll.layer?.borderColor = NSColor.separatorColor.withAlphaComponent(0.6).cgColor
        scroll.layer?.backgroundColor = NSColor.controlBackgroundColor.cgColor
        return scroll
    }

    private static func tabContent(_ views: [NSView], stretching: NSView) -> NSView {
        let stack = NSStackView(views: views)
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 10
        stack.edgeInsets = NSEdgeInsets(top: 12, left: 12, bottom: 12, right: 12)
        stack.translatesAutoresizingMaskIntoConstraints = false

        let container = NSView()
        container.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: container.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: container.trailingAnchor),
            stack.topAnchor.constraint(equalTo: container.topAnchor),
            stack.bottomAnchor.constraint(equalTo: container.bottomAnchor),
            stretching.widthAnchor.constraint(equalTo: stack.widthAnchor, constant: -24)
        ])
        for view in views where view !== stretching {
            view.widthAnchor.constraint(equalTo: stack.widthAnchor, constant: -24).isActive = true
        }
        return container
    }

    // MARK: Data

    private func reload() {
        // The ignore list applies to history too: a name added today disappears from last week's review.
        var all = store.loadAll().map { AttendanceIgnoring.apply(.current, to: $0) }
        // A meeting in progress is not on disk as "the current one" yet.
        if let live = liveSessionProvider() {
            all.removeAll { $0.id == live.id }
            all.insert(AttendanceIgnoring.apply(.current, to: live), at: 0)
        }
        sessions = all

        let previousID = selectedSession?.id
        sessionPopUp.removeAllItems()
        for session in sessions {
            sessionPopUp.addItem(withTitle: title(for: session))
        }
        if sessions.isEmpty {
            sessionPopUp.addItem(withTitle: "No attendance sessions yet")
        }

        if let previousID, let index = sessions.firstIndex(where: { $0.id == previousID }) {
            sessionPopUp.selectItem(at: index)
            selectedSession = sessions[index]
        } else {
            selectedSession = sessions.first
        }

        refresh()
    }

    private func rebuildItems() {
        guard let session = selectedSession else {
            items = []
            rosterRecords = []
            return
        }

        // Unfiltered on purpose: this tab exists to be read straight through.
        rosterRecords = session.recordsInRosterOrder

        let query = searchField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        let visible = session.records.filter { record in
            guard !query.isEmpty else { return true }
            return record.studentName.lowercased().contains(query)
                || (record.matchedZoomName?.lowercased().contains(query) ?? false)
        }

        let order: [(AttendanceStatus, String, NSColor)] = [
            (.needsReview, "Needs Review", .systemOrange),
            (.present, "Present", .systemGreen),
            (.notSeenYet, "Not Seen Yet", .secondaryLabelColor),
            (.absent, "Absent", .systemRed)
        ]

        items = order.flatMap { status, title, tint -> [ListItem] in
            let matching = visible.filter { $0.status == status }
            guard !matching.isEmpty else { return [] }
            return [.header(title: title, count: matching.count, tint: tint)]
                + matching.map { ListItem.record($0) }
        }

        // Zoom identities nobody on the roster claimed. These are the other half
        // of the reconciliation: a student is only missing because some name
        // went unrecognised, and this is where that name is.
        let claimed = Set(
            session.records
                .filter { $0.status == .present || $0.status == .needsReview }
                .compactMap(\.matchedObservationID)
        )
        let unresolved = session.observations
            .filter { !claimed.contains($0.id) }
            .filter { observation in
                guard !query.isEmpty else { return true }
                return observation.rawName.lowercased().contains(query)
            }

        if !unresolved.isEmpty {
            items.append(.header(title: "Unmatched Zoom Names", count: unresolved.count, tint: .systemPurple))
            items.append(contentsOf: unresolved.map { ListItem.unresolved($0) })
        }
    }

    private func refresh() {
        guard let session = selectedSession else {
            summaryLabel.stringValue = "No attendance yet"
            statusLabel.stringValue = "Link a schedule to a group, and attendance is recorded automatically."
            unmatchedLabel.stringValue = ""
            finalizeButton.isEnabled = false
            aiButton.isEnabled = false
            exportButton.isEnabled = false
            copyRosterButton.isEnabled = false
            rebuildItems()
            table.reloadData()
            rosterTable.reloadData()
            return
        }

        let formatter = DateFormatter()
        formatter.dateStyle = .medium
        formatter.timeStyle = .short
        summaryLabel.stringValue = "\(session.groupName) · \(formatter.string(from: session.startedAt))"

        let notSeen = session.records.filter { $0.status == .notSeenYet }.count
        var parts = [
            "\(session.rosterSnapshot.count) students",
            "Present: \(session.presentCount)",
            "Needs Review: \(session.needsReviewCount)"
        ]
        parts.append(session.isFinalized ? "Absent: \(session.absentCount)" : "Not seen yet: \(notSeen)")
        statusLabel.stringValue = session.isFinalized
            ? "Finalized · \(session.rosterSnapshot.count) students"
            : "In progress · \(session.rosterSnapshot.count) students"

        statStrip.arrangedSubviews.forEach { $0.removeFromSuperview() }
        statStrip.addArrangedSubview(DesignKit.statTile(
            value: String(session.presentCount),
            caption: "Present",
            tint: .systemGreen
        ))
        statStrip.addArrangedSubview(DesignKit.statTile(
            value: String(session.needsReviewCount),
            caption: "Needs Review",
            tint: .systemOrange
        ))
        statStrip.addArrangedSubview(DesignKit.statTile(
            value: String(session.isFinalized ? session.absentCount : notSeen),
            caption: session.isFinalized ? "Absent" : "Not Seen Yet",
            tint: session.isFinalized ? .systemRed : .secondaryLabelColor
        ))
        _ = parts

        var footer: [String] = []
        if !session.unmatchedZoomNames.isEmpty {
            footer.append("Unmatched Zoom names: " + session.unmatchedZoomNames.joined(separator: ", "))
        }
        if !session.snapshots.isEmpty {
            footer.append("Evidence: \(session.snapshots.count) snapshot(s)"
                + (session.missedSnapshotCount > 0 ? ", \(session.missedSnapshotCount) missed" : ""))
        }
        unmatchedLabel.stringValue = footer.joined(separator: "\n")

        finalizeButton.isEnabled = !session.isFinalized
        aiButton.isEnabled = APIKeyStore.hasKey
        aiButton.toolTip = APIKeyStore.hasKey
            ? "Ask OpenRouter about the names local matching could not settle."
            : "Add an OpenRouter API key in Settings to enable AI matching."
        exportButton.isEnabled = true
        copyRosterButton.isEnabled = true

        rebuildItems()
        table.reloadData()
        rosterTable.reloadData()
    }

    @objc private func searchChanged() {
        rebuildItems()
        table.reloadData()
        rosterTable.reloadData()
    }

    private func title(for session: AttendanceSession) -> String {
        let formatter = DateFormatter()
        formatter.dateStyle = .medium
        formatter.timeStyle = .short
        let state = session.isFinalized ? "" : " — in progress"
        return "\(session.groupName) · \(formatter.string(from: session.startedAt))\(state)"
    }

    private func update(_ session: AttendanceSession) {
        selectedSession = session
        store.save(session)
        if let index = sessions.firstIndex(where: { $0.id == session.id }) {
            sessions[index] = session
        }
        refresh()
    }

    // MARK: Actions

    @objc private func sessionChanged() {
        let index = sessionPopUp.indexOfSelectedItem
        guard sessions.indices.contains(index) else { return }
        selectedSession = sessions[index]
        refresh()
    }

    @objc private func finalizeAttendance() {
        guard let session = selectedSession else { return }

        let alert = NSAlert()
        alert.messageText = "Finalize attendance for \(session.groupName)?"
        alert.informativeText = "Students who were never seen will be marked Absent. The Zoom meeting is not ended."
        alert.addButton(withTitle: "Cancel")
        alert.addButton(withTitle: "Finalize")
        guard alert.runModal() == .alertSecondButtonReturn else { return }

        // A live session is finalized through the coordinator so recording stops
        // cleanly; a historical one is finalized in place.
        if let live = liveSessionProvider(), live.id == session.id, let finalized = finalizeHandler() {
            update(finalized)
        } else {
            update(AttendanceReconciler.reconcile(
                session: session,
                autoAcceptConfidence: autoAcceptConfidence(for: session),
                finalizing: true
            ))
        }
        reload()
    }

    @objc private func matchWithAI() {
        guard let session = selectedSession else { return }
        let (request, ids) = AIReconciliation.request(for: session)
        guard request.isWorthSending else {
            presentInfo(
                "Nothing to send",
                detail: """
                Local matching already resolved everything it could, so there is \
                nothing ambiguous left to ask about.

                Students still unresolved: \(request.students.count)
                Zoom names still unclaimed: \(request.observedNames.count)
                """
            )
            return
        }

        aiButton.isEnabled = false
        aiButton.title = "Matching…"
        let threshold = autoAcceptConfidence(for: session)
        let client = OpenRouterClient(configuration: .init(model: SchedulerDefaults.aiModel))

        Task { [weak self] in
            let exchange = await client.proposeMatches(for: request)
            await MainActor.run {
                guard let self else { return }
                self.aiButton.title = "Match with AI"
                self.aiButton.isEnabled = true

                guard let response = exchange.response else {
                    // Local results stand; nothing is lost.
                    self.lastExchange = AIExchangeRecord(exchange: exchange, summary: nil)
                    self.aiDetailsButton.isEnabled = true
                    self.presentInfo(
                        "AI matching unavailable",
                        detail: """
                        \(exchange.error?.message ?? "Unknown error")

                        The attendance recorded locally is unchanged. \
                        Open AI Details to see exactly what was sent.
                        """
                    )
                    return
                }

                let summary = AIReconciliation.apply(
                    response,
                    to: session,
                    ids: ids,
                    autoAcceptConfidence: threshold
                )
                self.lastExchange = AIExchangeRecord(exchange: exchange, summary: summary)
                self.aiDetailsButton.isEnabled = true
                self.update(summary.session)
                // Whatever the AI settled is now roster knowledge, so the same
                // Zoom names resolve locally next time without another request.
                let learned = self.learnConfirmedAliases(from: summary.session)

                var detail = """
                \(summary.appliedCount) matched
                \(summary.reviewCount) need review
                \(summary.unmatchedObservedNameCount) Zoom name(s) still unmatched
                """
                if !learned.isEmpty {
                    let names = learned.map { "\($0.alias) → \($0.studentName)" }.joined(separator: "\n")
                    detail += "\n\nRemembered for future meetings:\n\(names)"
                }
                if !summary.rejected.isEmpty {
                    detail += "\n\n\(summary.rejected.count) proposal(s) were rejected as unusable."
                }
                detail += "\n\nOpen AI Details to see the request and the reply."
                self.presentInfo("AI matching finished", detail: detail)
            }
        }
    }

    /// Shows exactly what left the machine and exactly what came back.
    ///
    /// Without this, a run that matches nothing is indistinguishable from a run
    /// that never sent the name in the first place.
    @objc private func showAIDetails() {
        guard let record = lastExchange else { return }
        presentText(
            title: "AI Matching Details",
            body: Self.aiDetailsReport(
                exchange: record.exchange,
                summary: record.summary,
                model: SchedulerDefaults.aiModel
            )
        )
    }

    /// The transcript itself, built without touching any view, so what the
    /// window is supposed to show can be asserted directly.
    static func aiDetailsReport(
        exchange: OpenRouterClient.Exchange,
        summary: AIReconciliation.Summary?,
        model: String
    ) -> String {
        var report = ["REQUEST SENT", ""]
        report.append("Model: \(model)")
        report.append("Students sent (\(exchange.request.students.count)):")
        for student in exchange.request.students {
            report.append("  [\(student.id)] \(student.officialName)")
        }
        report.append("")
        report.append("Zoom names sent (\(exchange.request.observedNames.count)):")
        for name in exchange.request.observedNames {
            report.append("  [\(name.id)] \(name.displayName)")
        }
        report.append("")
        report.append("Prompt:")
        report.append(exchange.prompt)

        report.append("")
        report.append("──────────────────────────────")
        report.append("RESPONSE RECEIVED")
        report.append("")
        if let status = exchange.httpStatus { report.append("HTTP \(status), attempts: \(exchange.attempts)") }
        if let error = exchange.error { report.append("Error: \(error.message)") }
        report.append(exchange.rawResponse ?? "(nothing came back)")

        if let summary {
            report.append("")
            report.append("──────────────────────────────")
            report.append("WHAT WAS APPLIED")
            report.append("")
            report.append("Accepted as Present: \(summary.appliedCount)")
            report.append("Sent to Needs Review: \(summary.reviewCount)")
            report.append("Zoom names still unmatched: \(summary.unmatchedObservedNameCount)")
            if summary.rejected.isEmpty {
                report.append("Rejected proposals: none")
            } else {
                report.append("")
                report.append("Rejected proposals (\(summary.rejected.count)):")
                // These are the model's own answers that failed validation.
                for reason in summary.rejected { report.append("  • \(reason)") }
            }
        }

        return report.joined(separator: "\n")
    }

    private func presentText(title: String, body: String) {
        let frame = NSRect(x: 0, y: 0, width: 700, height: 560)
        let window = NSWindow(
            contentRect: frame,
            styleMask: [.titled, .closable, .resizable],
            backing: .buffered,
            defer: false
        )
        window.title = title
        window.center()
        // The reference below is the only one; without this the window is freed
        // underneath it when closed.
        window.isReleasedWhenClosed = false

        window.contentView = Self.makeTextView(body: body, frame: frame)

        aiDetailsWindow = window
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
    }

    /// A scrolling, read-only text view that actually lays its text out.
    ///
    /// A bare `NSTextView()` has a zero frame, and therefore a zero-width text
    /// container, which lays out no glyphs at all: the window comes up empty.
    /// It has to be sized to the clip view and told to track its width.
    static func makeTextView(body: String, frame: NSRect) -> NSScrollView {
        let scroll = NSScrollView(frame: frame)
        scroll.hasVerticalScroller = true
        scroll.autoresizingMask = [.width, .height]
        scroll.borderType = .noBorder

        let contentSize = scroll.contentSize
        let text = NSTextView(frame: NSRect(origin: .zero, size: contentSize))
        text.minSize = NSSize(width: 0, height: 0)
        text.maxSize = NSSize(
            width: CGFloat.greatestFiniteMagnitude,
            height: CGFloat.greatestFiniteMagnitude
        )
        text.isVerticallyResizable = true
        text.isHorizontallyResizable = false
        text.autoresizingMask = [.width]
        text.textContainer?.containerSize = NSSize(
            width: contentSize.width,
            height: CGFloat.greatestFiniteMagnitude
        )
        text.textContainer?.widthTracksTextView = true
        text.isEditable = false
        text.isSelectable = true
        text.font = .monospacedSystemFont(ofSize: 11, weight: .regular)
        text.textContainerInset = NSSize(width: 14, height: 14)
        text.string = body

        scroll.documentView = text
        return scroll
    }

    @objc private func exportCSV() {
        guard let session = selectedSession else { return }
        let panel = NSSavePanel()
        panel.nameFieldStringValue = "attendance-\(session.groupName).csv"
        panel.allowedContentTypes = [.commaSeparatedText]
        guard panel.runModal() == .OK, let url = panel.url else { return }

        do {
            try Data(AttendanceExport.csv(for: session).utf8).write(to: url)
        } catch {
            presentInfo("Could not export", detail: error.localizedDescription)
        }
    }

    private func autoAcceptConfidence(for session: AttendanceSession) -> Double {
        configurationProvider().studentGroups.first { $0.id == session.groupID }?.autoAcceptConfidence ?? 0.9
    }

    private func presentInfo(_ message: String, detail: String) {
        let alert = NSAlert()
        alert.messageText = message
        alert.informativeText = detail
        alert.runModal()
    }
}

// MARK: - Rows

extension AttendanceWindowController: NSTableViewDataSource, NSTableViewDelegate {
    /// One roster line: position, official name, status, and the Zoom name that
    /// earned it. The position is what makes this transcribable at a glance.
    private func rosterRowView(_ record: AttendanceRecord, position: Int) -> NSView {
        let number = NSTextField(labelWithString: "\(position).")
        number.font = .monospacedDigitSystemFont(ofSize: NSFont.smallSystemFontSize, weight: .regular)
        number.textColor = .tertiaryLabelColor
        number.alignment = .right
        number.widthAnchor.constraint(equalToConstant: 30).isActive = true

        let name = NSTextField(labelWithString: record.studentName)
        name.font = .systemFont(ofSize: NSFont.systemFontSize)
        name.lineBreakMode = .byTruncatingTail

        let status = NSTextField(labelWithString: AttendanceExport.displayStatus(record.status))
        status.font = .systemFont(ofSize: NSFont.smallSystemFontSize, weight: .medium)
        status.textColor = Self.tint(for: record.status)
        status.alignment = .right

        var views: [NSView] = [number, name, NSView()]
        if let zoomName = record.matchedZoomName, !zoomName.isEmpty {
            let zoom = NSTextField(labelWithString: zoomName)
            zoom.font = .systemFont(ofSize: NSFont.smallSystemFontSize)
            zoom.textColor = .secondaryLabelColor
            zoom.lineBreakMode = .byTruncatingTail
            views.append(zoom)
        }
        views.append(status)

        let row = NSStackView(views: views)
        row.orientation = .horizontal
        row.spacing = 8
        row.edgeInsets = NSEdgeInsets(top: 0, left: 6, bottom: 0, right: 10)
        return row
    }

    private static func tint(for status: AttendanceStatus) -> NSColor {
        switch status {
        case .present: return .systemGreen
        case .needsReview: return .systemOrange
        case .absent: return .systemRed
        case .notSeenYet: return .secondaryLabelColor
        }
    }

    /// Puts the whole register on the pasteboard in roster order.
    @objc private func copyRosterOrder() {
        guard let session = selectedSession else { return }
        let text = AttendanceExport.rosterOrderReport(for: session)
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
        copyRosterButton.title = "Copied"
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { [weak self] in
            self?.copyRosterButton.title = "Copy Full List"
        }
    }

    func numberOfRows(in tableView: NSTableView) -> Int {
        tableView === rosterTable ? rosterRecords.count : items.count
    }

    func tableView(_ tableView: NSTableView, isGroupRow row: Int) -> Bool {
        guard tableView !== rosterTable else { return false }
        guard items.indices.contains(row) else { return false }
        if case .header = items[row] { return true }
        return false
    }

    func tableView(_ tableView: NSTableView, heightOfRow row: Int) -> CGFloat {
        guard tableView !== rosterTable else { return 30 }
        guard items.indices.contains(row) else { return 44 }
        switch items[row] {
        case .header: return 30
        case .record(let record): return record.matchedZoomName == nil ? 44 : 60
        case .unresolved: return 52
        }
    }

    func tableView(_ tableView: NSTableView, shouldSelectRow row: Int) -> Bool {
        guard tableView !== rosterTable else { return false }
        // Section headings are not selectable.
        guard items.indices.contains(row) else { return false }
        if case .header = items[row] { return false }
        return true
    }

    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        if tableView === rosterTable {
            guard rosterRecords.indices.contains(row) else { return nil }
            return rosterRowView(rosterRecords[row], position: row + 1)
        }
        guard items.indices.contains(row) else { return nil }
        switch items[row] {
        case .header(let title, let count, let tint):
            return sectionHeaderView(title: title, count: count, tint: tint)
        case .record(let record):
            return recordView(record, row: row)
        case .unresolved(let observation):
            return unresolvedView(observation, row: row)
        }
    }

    /// One observed Zoom identity that matched nobody, with a way to assign it.
    private func unresolvedView(_ observation: ParticipantObservation, row: Int) -> NSView {
        let marker = NSImageView()
        marker.image = NSImage(systemSymbolName: "questionmark.circle", accessibilityDescription: nil)
        marker.contentTintColor = .systemPurple
        marker.symbolConfiguration = NSImage.SymbolConfiguration(pointSize: 14, weight: .semibold)
        marker.widthAnchor.constraint(equalToConstant: 20).isActive = true

        let name = NSTextField(labelWithString: observation.rawName)
        name.font = .systemFont(ofSize: NSFont.systemFontSize, weight: .medium)
        name.lineBreakMode = .byTruncatingTail

        var evidence = "seen in \(observation.observationCount) snapshot"
            + (observation.observationCount == 1 ? "" : "s")
        if let first = observation.firstObservedAt, let last = observation.lastObservedAt {
            let formatter = DateFormatter()
            formatter.timeStyle = .short
            formatter.dateStyle = .none
            evidence = "seen \(formatter.string(from: first))–\(formatter.string(from: last)) · " + evidence
        }
        let detail = NSTextField(labelWithString: evidence)
        detail.font = .systemFont(ofSize: 11)
        detail.textColor = .secondaryLabelColor
        detail.lineBreakMode = .byTruncatingTail

        let text = NSStackView(views: [name, detail])
        text.orientation = .vertical
        text.alignment = .leading
        text.spacing = 2

        let assign = NSButton(title: "Assign to student…", target: self, action: #selector(assignUnresolved(_:)))
        assign.tag = row
        assign.controlSize = .small
        assign.bezelStyle = .rounded

        let stack = NSStackView(views: [marker, text, NSView(), assign])
        stack.orientation = .horizontal
        stack.alignment = .centerY
        stack.spacing = 8
        stack.edgeInsets = NSEdgeInsets(top: 6, left: 6, bottom: 6, right: 6)
        return stack
    }

    /// The reverse of Match…: start from the Zoom name and pick the student.
    @objc private func assignUnresolved(_ sender: NSButton) {
        guard let session = selectedSession,
              items.indices.contains(sender.tag),
              case .unresolved(let observation) = items[sender.tag] else {
            return
        }

        let alert = NSAlert()
        alert.messageText = "Who is “\(observation.rawName)”?"
        alert.informativeText = "Pick the official student this Zoom name belongs to."

        // Same reasoning as Match…: lead with the student this Zoom name most
        // likely belongs to rather than with whoever sorts first alphabetically.
        let suspected = session.records
            .first { $0.matchedObservationID == observation.id }?
            .studentID
        let candidates = Self.rankedStudents(
            session.rosterSnapshot,
            for: observation.rawName,
            suspected: suspected
        )
        guard !candidates.isEmpty else { return }

        let popUp = NSPopUpButton(frame: NSRect(x: 0, y: 0, width: 320, height: 25))
        for candidate in candidates { popUp.addItem(withTitle: candidate.title) }
        popUp.selectItem(at: 0)

        let remember = NSButton(checkboxWithTitle: "Remember this name for future meetings", target: nil, action: nil)
        remember.state = .on
        remember.frame = NSRect(x: 0, y: 0, width: 320, height: 20)

        let accessory = NSStackView(views: [popUp, remember])
        accessory.orientation = .vertical
        accessory.alignment = .leading
        accessory.spacing = 8
        accessory.frame = NSRect(x: 0, y: 0, width: 320, height: 56)
        alert.accessoryView = accessory
        alert.addButton(withTitle: "Cancel")
        alert.addButton(withTitle: "Assign")

        guard alert.runModal() == .alertSecondButtonReturn else { return }
        let student = candidates[popUp.indexOfSelectedItem].student

        update(AttendanceReconciler.applyManualMatch(
            session: session,
            studentID: student.id,
            observationID: observation.id,
            status: .present
        ))
        if remember.state == .on {
            learnAlias(observation.rawName, studentID: student.id, groupID: session.groupID)
        }
    }

    /// A status heading with its own count, so the shape of the class is
    /// readable at a glance instead of having to be counted.
    private func sectionHeaderView(title: String, count: Int, tint: NSColor) -> NSView {
        let label = NSTextField(labelWithString: title.uppercased())
        label.font = .systemFont(ofSize: 11, weight: .semibold)
        label.textColor = tint

        let countLabel = NSTextField(labelWithString: String(count))
        countLabel.font = .systemFont(ofSize: 11, weight: .semibold)
        countLabel.textColor = .tertiaryLabelColor

        let stack = NSStackView(views: [label, countLabel])
        stack.orientation = .horizontal
        stack.spacing = 6
        stack.edgeInsets = NSEdgeInsets(top: 10, left: 6, bottom: 2, right: 6)
        return stack
    }

    private func recordView(_ record: AttendanceRecord, row: Int) -> NSView {
        guard let session = selectedSession else { return NSView() }

        let marker = NSImageView()
        marker.image = NSImage(systemSymbolName: symbolName(for: record.status), accessibilityDescription: nil)
        marker.contentTintColor = color(for: record.status)
        marker.symbolConfiguration = NSImage.SymbolConfiguration(pointSize: 14, weight: .semibold)
        marker.widthAnchor.constraint(equalToConstant: 20).isActive = true

        let name = NSTextField(labelWithString: record.studentName)
        name.font = .systemFont(ofSize: NSFont.systemFontSize, weight: .medium)
        name.lineBreakMode = .byTruncatingTail

        var textViews: [NSView] = [name]
        if let evidence = evidenceLine(for: record, in: session) {
            let detail = NSTextField(labelWithString: evidence)
            detail.font = .systemFont(ofSize: 11)
            detail.textColor = .secondaryLabelColor
            detail.lineBreakMode = .byTruncatingTail
            textViews.append(detail)
        }

        let text = NSStackView(views: textViews)
        text.orientation = .vertical
        text.alignment = .leading
        text.spacing = 2

        var controls: [NSView] = [marker, text, NSView()]
        if let confidence = record.confidence, record.matchSource != .manual, record.status != .absent {
            controls.append(confidencePill(confidence))
        }
        controls.append(contentsOf: actionButtons(for: record, row: row, session: session))

        let stack = NSStackView(views: controls)
        stack.orientation = .horizontal
        stack.alignment = .centerY
        stack.spacing = 8
        stack.edgeInsets = NSEdgeInsets(top: 6, left: 6, bottom: 6, right: 6)
        return stack
    }

    /// Evidence, phrased as evidence: a sighting is not a presence duration.
    private func evidenceLine(for record: AttendanceRecord, in session: AttendanceSession) -> String? {
        guard let zoomName = record.matchedZoomName else { return nil }
        var parts = ["Zoom: \(zoomName)"]

        if let observation = record.matchedObservationID.flatMap({ session.observation(withID: $0) }),
           let first = observation.firstObservedAt,
           let last = observation.lastObservedAt {
            let formatter = DateFormatter()
            formatter.timeStyle = .short
            formatter.dateStyle = .none
            let seen = observation.observationCount
            parts.append(
                "seen \(formatter.string(from: first))–\(formatter.string(from: last))"
                + " in \(seen) snapshot\(seen == 1 ? "" : "s")"
            )
        }
        if record.isManual { parts.append("set manually") }
        else if record.matchSource != .none { parts.append(record.matchSource.rawValue) }
        return parts.joined(separator: " · ")
    }

    private func confidencePill(_ confidence: Double) -> NSView {
        let label = NSTextField(labelWithString: "\(Int(confidence * 100))%")
        label.font = .systemFont(ofSize: 10, weight: .semibold)
        label.textColor = .secondaryLabelColor
        label.alignment = .center

        let pill = NSView()
        pill.wantsLayer = true
        pill.layer?.backgroundColor = NSColor.quaternaryLabelColor.withAlphaComponent(0.25).cgColor
        pill.layer?.cornerRadius = 7
        pill.translatesAutoresizingMaskIntoConstraints = false
        label.translatesAutoresizingMaskIntoConstraints = false
        pill.addSubview(label)
        NSLayoutConstraint.activate([
            label.leadingAnchor.constraint(equalTo: pill.leadingAnchor, constant: 7),
            label.trailingAnchor.constraint(equalTo: pill.trailingAnchor, constant: -7),
            label.topAnchor.constraint(equalTo: pill.topAnchor, constant: 2),
            label.bottomAnchor.constraint(equalTo: pill.bottomAnchor, constant: -2)
        ])
        return pill
    }

    /// Only the actions that make sense for the row's current status.
    private func actionButtons(
        for record: AttendanceRecord,
        row: Int,
        session: AttendanceSession
    ) -> [NSView] {
        var buttons: [NSButton] = []

        if record.status != .present || record.matchedObservationID == nil {
            let match = NSButton(title: "Match…", target: self, action: #selector(matchRow(_:)))
            match.isEnabled = !session.observations.isEmpty
            buttons.append(match)
        }
        if record.status != .absent {
            buttons.append(NSButton(title: "Absent", target: self, action: #selector(markAbsentRow(_:))))
        }
        if record.isManual {
            buttons.append(NSButton(title: "Clear", target: self, action: #selector(clearRow(_:))))
        }

        for button in buttons {
            button.tag = row
            button.controlSize = .small
            button.bezelStyle = .rounded
        }
        return buttons
    }

    private func symbolName(for status: AttendanceStatus) -> String {
        switch status {
        case .present: return "checkmark.circle.fill"
        case .absent: return "xmark.circle.fill"
        case .needsReview: return "exclamationmark.triangle.fill"
        case .notSeenYet: return "clock"
        }
    }

    private func color(for status: AttendanceStatus) -> NSColor {
        switch status {
        case .present: return .systemGreen
        case .absent: return .systemRed
        case .needsReview: return .systemOrange
        case .notSeenYet: return .secondaryLabelColor
        }
    }

    /// Row tags index into `items`, which mixes headers and records.
    private func record(atRow row: Int) -> AttendanceRecord? {
        guard items.indices.contains(row), case .record(let record) = items[row] else { return nil }
        return record
    }

    @objc private func matchRow(_ sender: NSButton) {
        guard let session = selectedSession, let record = record(atRow: sender.tag) else { return }

        let alert = NSAlert()
        alert.messageText = "Match \(record.studentName)"
        alert.informativeText = "Choose the Zoom name this student used."

        // Offer the name the register already suspects, then everything else
        // best-first. Presenting an unsorted list would ask the reviewer to redo
        // by eye the ranking the matcher has already computed.
        let student = session.rosterSnapshot.first { $0.id == record.studentID }
        let ranked = Self.rankedObservations(
            session.observations,
            for: student,
            suspected: record.matchedObservationID
        )
        guard !ranked.isEmpty else { return }

        let popUp = NSPopUpButton(frame: NSRect(x: 0, y: 0, width: 320, height: 25))
        for candidate in ranked { popUp.addItem(withTitle: candidate.title) }
        popUp.selectItem(at: 0)

        let remember = NSButton(checkboxWithTitle: "Remember this name for future meetings", target: nil, action: nil)
        remember.state = .on
        remember.frame = NSRect(x: 0, y: 0, width: 320, height: 20)

        let accessory = NSStackView(views: [popUp, remember])
        accessory.orientation = .vertical
        accessory.alignment = .leading
        accessory.spacing = 8
        accessory.frame = NSRect(x: 0, y: 0, width: 320, height: 56)
        alert.accessoryView = accessory
        alert.addButton(withTitle: "Cancel")
        alert.addButton(withTitle: "Match")

        guard alert.runModal() == .alertSecondButtonReturn else { return }
        let observation = ranked[popUp.indexOfSelectedItem].observation

        update(AttendanceReconciler.applyManualMatch(
            session: session,
            studentID: record.studentID,
            observationID: observation.id,
            status: .present
        ))

        if remember.state == .on {
            learnAlias(observation.rawName, studentID: record.studentID, groupID: session.groupID)
        }
    }

    struct RankedObservation {
        let observation: ParticipantObservation
        let title: String
    }

    struct RankedStudent {
        let student: Student
        let title: String
    }

    /// Zoom names ordered by how well they fit one student.
    ///
    /// The pairing the register already suspects leads, then everything else by
    /// the matcher's own score, so the reviewer confirms a judgement instead of
    /// hunting through an alphabetical list.
    static func rankedObservations(
        _ observations: [ParticipantObservation],
        for student: Student?,
        suspected: UUID?
    ) -> [RankedObservation] {
        let scored = observations.map { observation -> (ParticipantObservation, Double) in
            guard let student else { return (observation, 0) }
            let score = DeterministicMatcher.score(
                rawObservedName: observation.rawName,
                student: student
            )?.score ?? 0
            return (observation, score)
        }

        let ordered = scored.sorted { left, right in
            if left.0.id == suspected { return true }
            if right.0.id == suspected { return false }
            if left.1 != right.1 { return left.1 > right.1 }
            return left.0.rawName.localizedCompare(right.0.rawName) == .orderedAscending
        }

        return ordered.map { observation, score in
            RankedObservation(
                observation: observation,
                title: title(for: observation.rawName, score: score, isSuspected: observation.id == suspected)
            )
        }
    }

    /// The mirror image, for "who is this Zoom name?".
    static func rankedStudents(
        _ students: [Student],
        for rawObservedName: String,
        suspected: UUID?
    ) -> [RankedStudent] {
        let scored = students.map { student -> (Student, Double) in
            let score = DeterministicMatcher.score(
                rawObservedName: rawObservedName,
                student: student
            )?.score ?? 0
            return (student, score)
        }

        let ordered = scored.sorted { left, right in
            if left.0.id == suspected { return true }
            if right.0.id == suspected { return false }
            if left.1 != right.1 { return left.1 > right.1 }
            return left.0.officialName.localizedCompare(right.0.officialName) == .orderedAscending
        }

        return ordered.map { student, score in
            RankedStudent(
                student: student,
                title: title(for: student.officialName, score: score, isSuspected: student.id == suspected)
            )
        }
    }

    /// A score is only worth showing when it means something; below the
    /// matcher's own review floor it is noise dressed up as a number.
    private static func title(for name: String, score: Double, isSuspected: Bool) -> String {
        if isSuspected { return "\(name)  ·  suggested" }
        guard score >= DeterministicMatcher.reviewFloor else { return name }
        return "\(name)  ·  \(Int((score * 100).rounded()))% match"
    }

    /// Turns the matches this session settled into roster knowledge.
    ///
    /// Without this a name worked out by AI in September is worked out by AI
    /// again in October, at the same cost and the same risk. Only records that
    /// are actually Present are learned: a Needs Review row is a question, and
    /// teaching the roster from a question would make a guess permanent.
    @discardableResult
    private func learnConfirmedAliases(from session: AttendanceSession) -> [LearnedAlias] {
        var configuration = configurationProvider()
        guard let index = configuration.studentGroups.firstIndex(where: { $0.id == session.groupID }) else {
            return []
        }

        let outcome = AliasLearning.learnConfirmedMatches(
            in: session,
            group: configuration.studentGroups[index]
        )
        guard outcome.didChangeGroup else { return [] }

        configuration.studentGroups[index] = outcome.group
        configurationWriter(configuration)
        return outcome.learned
    }

    /// Learned aliases are what make the next meeting resolve without AI.
    private func learnAlias(_ alias: String, studentID: UUID, groupID: UUID) {
        var configuration = configurationProvider()
        guard let index = configuration.studentGroups.firstIndex(where: { $0.id == groupID }) else { return }

        switch AliasLearning.learn(alias: alias, forStudent: studentID, in: configuration.studentGroups[index]) {
        case .success(let updated):
            configuration.studentGroups[index] = updated
            configurationWriter(configuration)
        case .failure(let error):
            presentInfo("Couldn't remember that name", detail: error.message)
        }
    }

    @objc private func markAbsentRow(_ sender: NSButton) {
        guard let session = selectedSession, let record = record(atRow: sender.tag) else { return }
        update(AttendanceReconciler.applyManualMatch(
            session: session,
            studentID: record.studentID,
            observationID: nil,
            status: .absent
        ))
    }

    @objc private func clearRow(_ sender: NSButton) {
        guard let session = selectedSession, let record = record(atRow: sender.tag) else { return }
        let cleared = AttendanceReconciler.clearMatch(session: session, studentID: record.studentID)
        update(AttendanceReconciler.reconcile(
            session: cleared,
            autoAcceptConfidence: autoAcceptConfidence(for: session),
            finalizing: cleared.isFinalized
        ))
    }
}
