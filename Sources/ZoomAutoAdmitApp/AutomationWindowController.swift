import AppKit
import ZoomAutoAdmitCore

/// The Automation window: the DEPI dashboard, what a class still owes it, the recording API,
/// Web meetings, co-host and the helper's own setup, plus a live log of what it all did.
final class AutomationWindowController: NSWindowController, NSTableViewDataSource, NSTableViewDelegate {
    private let coordinator: AutomationCoordinator
    private let configurationProvider: () -> SchedulerConfiguration

    // LMS
    private let emailField = NSTextField()
    private let passwordField = NSSecureTextField()
    private let signInStatus = NSTextField(labelWithString: "")
    private let runSessionButton = NSButton(checkboxWithTitle: "Press Run Session when a scheduled meeting goes live", target: nil, action: nil)
    private let takeAttendanceButton = NSButton(checkboxWithTitle: "Upload attendance 90 minutes after the start", target: nil, action: nil)
    private let correctAttendanceButton = NSButton(checkboxWithTitle: "Correct late joiners 3 hours after the start", target: nil, action: nil)
    private let showBrowserButton = NSButton(checkboxWithTitle: "Show the browser while it works", target: nil, action: nil)
    private let dryRunButton = NSButton(checkboxWithTitle: "Rehearse only (open everything, press nothing that writes)", target: nil, action: nil)
    private let zoomTimeZoneField = NSTextField()

    // Follow-ups and manual actions
    private let followUpTable = NSTableView()
    private var followUpRows: [LmsFollowUp] = []
    private let manualGroupPopUp = NSPopUpButton()
    private let manualGroupField = NSTextField()
    private let manualDatePicker = NSDatePicker()
    private let manualTimeField = NSTextField()
    private let manualStatus = NSTextField(wrappingLabelWithString: "")


    // Recording Sync
    private let googleClientIDField = NSTextField()
    private let googleClientSecretField = NSSecureTextField()
    private let googleStatus = NSTextField(wrappingLabelWithString: "")
    private let spreadsheetField = NSTextField()
    private let syncEnabledButton = NSButton(checkboxWithTitle: "Sync every day at 08:00 Cairo time", target: nil, action: nil)
    private let replaceZoomButton = NSButton(checkboxWithTitle: "Treat an existing Zoom recording link (zoom.us/rec/…) as temporary and replace it", target: nil, action: nil)
    private let syncStatus = NSTextField(wrappingLabelWithString: "")
    private let syncTable = NSTableView()
    private var syncRows: [RecordingSyncRecord] = []
    private let syncDetail = NSTextView()
    private let tabs = NSTabView()

    // More
    private let webHeadlessButton = NSButton(checkboxWithTitle: "Run Zoom Web meetings without a visible window (after the first sign-in)", target: nil, action: nil)
    private let rolesEnabledButton = NSButton(checkboxWithTitle: "Make each group's co-host candidates co-host when they join", target: nil, action: nil)
    private let openAppButton = NSButton(checkboxWithTitle: "Open this app before scheduled meetings when it is not running", target: nil, action: nil)
    private let nodePathField = NSTextField()
    private let helperStatus = NSTextField(wrappingLabelWithString: "")

    // Log
    private let logView = NSTextView()

    init(coordinator: AutomationCoordinator, configurationProvider: @escaping () -> SchedulerConfiguration) {
        self.coordinator = coordinator
        self.configurationProvider = configurationProvider
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 820, height: 640),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        window.title = "Automation"
        window.minSize = NSSize(width: 720, height: 520)
        window.center()
        window.setFrameAutosaveName("AutomationWindow")
        super.init(window: window)

        tabs.addTabViewItem(tab("LMS", makeLmsTab()))
        tabs.addTabViewItem(tab("Follow-ups", makeFollowUpTab()))
        tabs.addTabViewItem(tab("Recording Sync", makeRecordingSyncTab()))
        tabs.addTabViewItem(tab("Web & Co-host", makeMoreTab()))
        tabs.addTabViewItem(tab("Log", makeLogTab()))
        window.contentView = tabs
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) is not used") }

    func present() {
        refresh()
        NSApp.activate(ignoringOtherApps: true)
        showWindow(nil)
        window?.makeKeyAndOrderFront(nil)
    }

    var isVisible: Bool { window?.isVisible == true }

    func refresh() {
        let settings = coordinator.settings
        runSessionButton.state = settings.runSessionOnMeetingStart ? .on : .off
        takeAttendanceButton.state = settings.takeAttendance ? .on : .off
        correctAttendanceButton.state = settings.correctAttendance ? .on : .off
        showBrowserButton.state = settings.showBrowser ? .on : .off
        dryRunButton.state = settings.dryRun ? .on : .off
        zoomTimeZoneField.stringValue = UserDefaults.standard.string(forKey: "lms.zoomAccountTimeZone") ?? ""
        if let email = coordinator.savedLmsEmail {
            signInStatus.stringValue = "✓ Signed in as \(email). The password is kept in your Keychain."
            if emailField.stringValue.isEmpty { emailField.stringValue = email }
        } else {
            signInStatus.stringValue = "No sign-in saved yet."
        }

        followUpRows = coordinator.followUps.read().sorted { $0.dueAt < $1.dueAt }
        followUpTable.reloadData()

        let groups = configurationProvider().studentGroups
        let selected = manualGroupPopUp.titleOfSelectedItem
        manualGroupPopUp.removeAllItems()
        manualGroupPopUp.addItems(withTitles: groups.map(\.dashboardGroupName))
        if let selected { manualGroupPopUp.selectItem(withTitle: selected) }


        refreshRecordingSync()

        webHeadlessButton.state = UserDefaults.standard.bool(forKey: "web.headless") ? .on : .off
        rolesEnabledButton.state = (UserDefaults.standard.object(forKey: "roles.enabled") as? Bool ?? true) ? .on : .off
        openAppButton.state = UserDefaults.standard.bool(forKey: "scheduler.openAppForMeetings") ? .on : .off
        nodePathField.stringValue = UserDefaults.standard.string(forKey: AutomationEnvironment.nodePathDefaultsKey) ?? ""

        let lines = coordinator.logSnapshot
        let text = lines.joined(separator: "\n")
        if logView.string != text {
            logView.string = text
            logView.scrollToEndOfDocument(nil)
        }
    }

    // MARK: Tabs

    private func makeLmsTab() -> NSView {
        emailField.placeholderString = "coordinator@example.com"
        passwordField.placeholderString = "Password"
        [emailField, passwordField].forEach { $0.widthAnchor.constraint(equalToConstant: 320).isActive = true }
        for button in [runSessionButton, takeAttendanceButton, correctAttendanceButton, showBrowserButton, dryRunButton] {
            button.target = self
            button.action = #selector(lmsSettingsChanged)
        }
        zoomTimeZoneField.placeholderString = "e.g. America/Los_Angeles (empty: same as this Mac)"
        zoomTimeZoneField.target = self
        zoomTimeZoneField.action = #selector(lmsSettingsChanged)
        zoomTimeZoneField.widthAnchor.constraint(equalToConstant: 320).isActive = true

        return scrolling([
            DesignKit.section("DEPI dashboard sign-in", rows: [
                DesignKit.horizontal([label("Email"), emailField]),
                DesignKit.horizontal([label("Password"), passwordField]),
                DesignKit.horizontal([
                    button("Test & Save", #selector(saveSignIn)),
                    button("Save Without Testing", #selector(saveSignInWithoutTest)),
                    button("Remove", #selector(forgetSignIn))
                ]),
                signInStatus,
                DesignKit.caption("The password is stored in the macOS Keychain and only ever typed into the dashboard's own sign-in form by the helper.")
            ]),
            DesignKit.section("For every scheduled meeting linked to a group", rows: [
                runSessionButton,
                takeAttendanceButton,
                correctAttendanceButton,
                DesignKit.caption("""
                The dashboard group is the group's LMS code (Schedules → Groups), or its name. Students marked \
                Present are sent as Joined; everyone else the dashboard lists is Not-joined. If any row cannot be \
                ticked nothing is submitted. What is due is written to disk, so it still happens after a restart.
                """)
            ]),
            DesignKit.section("While it works", rows: [
                showBrowserButton,
                dryRunButton,
                DesignKit.horizontal([label("Zoom account time zone"), zoomTimeZoneField]),
                DesignKit.caption("My Recordings prints times in the Zoom profile's time zone. Set it when that differs from this Mac.")
            ])
        ])
    }

    private func makeFollowUpTab() -> NSView {
        let columns: [(String, String, CGFloat)] = [("due", "Due", 130), ("class", "Class", 250), ("step", "Step", 150), ("state", "Last result", 240)]
        for (id, title, width) in columns {
            let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier(id))
            column.title = title
            column.width = width
            followUpTable.addTableColumn(column)
        }
        followUpTable.dataSource = self
        followUpTable.delegate = self
        followUpTable.usesAlternatingRowBackgroundColors = true
        let tableScroll = NSScrollView()
        tableScroll.documentView = followUpTable
        tableScroll.hasVerticalScroller = true
        tableScroll.borderType = .bezelBorder
        tableScroll.heightAnchor.constraint(equalToConstant: 180).isActive = true
        tableScroll.widthAnchor.constraint(equalToConstant: 740).isActive = true

        manualGroupField.placeholderString = "or type a group, e.g. CAI5_AIS4_S7"
        manualGroupField.widthAnchor.constraint(equalToConstant: 240).isActive = true
        manualDatePicker.datePickerElements = .yearMonthDay
        manualDatePicker.datePickerStyle = .textFieldAndStepper
        manualDatePicker.dateValue = Date()
        manualTimeField.placeholderString = "19:00"
        manualTimeField.widthAnchor.constraint(equalToConstant: 70).isActive = true
        manualStatus.preferredMaxLayoutWidth = 700

        return scrolling([
            DesignKit.section("What classes still owe the dashboard", rows: [
                tableScroll,
                DesignKit.horizontal([
                    button("Run Selected Now", #selector(runSelectedFollowUp)),
                    button("Remove Selected", #selector(removeSelectedFollowUp)),
                    button("Run Everything Due", #selector(runAllDue)),
                    button("Refresh", #selector(refreshPressed))
                ])
            ]),
            DesignKit.section("Do it now for one class", rows: [
                DesignKit.horizontal([label("Group"), manualGroupPopUp, manualGroupField]),
                DesignKit.horizontal([label("Day"), manualDatePicker, label("Start"), manualTimeField]),
                DesignKit.horizontal([
                    button("Run Session", #selector(manualRunSession)),
                    button("Upload Attendance", #selector(manualTakeAttendance)),
                    button("Correct Late Joiners", #selector(manualCorrectAttendance))
                ]),
                manualStatus,
                DesignKit.caption("Attendance uses that day's register for the group. The start time picks between two sessions of one group on the same day.")
            ])
        ])
    }

    // MARK: Recording Sync

    private func makeRecordingSyncTab() -> NSView {
        googleClientIDField.placeholderString = "OAuth client ID (Desktop app) from Google Cloud"
        googleClientSecretField.placeholderString = "Client secret (stored in Keychain)"
        spreadsheetField.placeholderString = "Spreadsheet ID from its URL: docs.google.com/spreadsheets/d/<ID>/edit"
        [googleClientIDField, googleClientSecretField, spreadsheetField].forEach { $0.widthAnchor.constraint(equalToConstant: 460).isActive = true }
        spreadsheetField.target = self
        spreadsheetField.action = #selector(syncSettingsChanged)
        for control in [syncEnabledButton, replaceZoomButton] {
            control.target = self
            control.action = #selector(syncSettingsChanged)
        }
        googleStatus.preferredMaxLayoutWidth = 700
        syncStatus.preferredMaxLayoutWidth = 700

        let columns: [(String, String, CGFloat, CGFloat)] = [("group", "Session", 112, 90), ("date", "Date", 92, 80), ("drive", "Drive", 70, 60), ("lms", "LMS", 150, 110), ("detail", "Details", 320, 160)]
        for (id, title, width, minimum) in columns {
            let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("sync.\(id)"))
            column.title = title
            column.width = width
            column.minWidth = minimum
            column.resizingMask = [.userResizingMask, .autoresizingMask]
            syncTable.addTableColumn(column)
        }
        // Details takes whatever width is left; the full text is in the panel under the table.
        syncTable.columnAutoresizingStyle = .lastColumnOnlyAutoresizingStyle
        syncTable.identifier = NSUserInterfaceItemIdentifier("recordingSyncTable")
        syncTable.dataSource = self
        syncTable.delegate = self
        syncTable.allowsMultipleSelection = true
        syncTable.usesAlternatingRowBackgroundColors = true
        let scroll = NSScrollView()
        scroll.documentView = syncTable
        scroll.hasVerticalScroller = true
        scroll.hasHorizontalScroller = true
        scroll.borderType = .bezelBorder
        scroll.heightAnchor.constraint(equalToConstant: 260).isActive = true
        scroll.widthAnchor.constraint(equalToConstant: 760).isActive = true

        syncDetail.isEditable = false
        syncDetail.isSelectable = true
        syncDetail.drawsBackground = false
        syncDetail.font = .monospacedSystemFont(ofSize: 11, weight: .regular)
        syncDetail.textContainerInset = NSSize(width: 6, height: 6)
        syncDetail.isVerticallyResizable = true
        syncDetail.autoresizingMask = [.width]
        syncDetail.string = "Select a row to see its details."
        let detailScroll = NSScrollView()
        detailScroll.documentView = syncDetail
        detailScroll.hasVerticalScroller = true
        detailScroll.borderType = .bezelBorder
        detailScroll.heightAnchor.constraint(equalToConstant: 170).isActive = true
        detailScroll.widthAnchor.constraint(equalToConstant: 760).isActive = true

        return scrolling([
            DesignKit.section("Google access", rows: [
                DesignKit.horizontal([label("Client ID"), googleClientIDField]),
                DesignKit.horizontal([label("Client secret"), googleClientSecretField]),
                DesignKit.horizontal([button("Save Client", #selector(saveGoogleClient)), button("Connect Google…", #selector(connectGoogle)), button("Disconnect", #selector(disconnectGoogle))]),
                googleStatus,
                DesignKit.caption("Read-only access to spreadsheets. You sign in once in the browser; the refresh token is kept in the Keychain and the sync runs without a browser afterwards.", width: 700)
            ]),
            DesignKit.section("Recordings sheet", rows: [
                DesignKit.horizontal([label("Spreadsheet ID"), spreadsheetField, button("Test Connection", #selector(testSheetConnection))]),
                syncEnabledButton,
                replaceZoomButton,
                syncStatus,
                DesignKit.caption("""
                Each tab is a group's LMS code; columns A File Name, B Type, C Date, D Shared Link. The sheet is never changed. \
                A link goes to the LMS only after that class's attendance is finished and its session has ended on the dashboard. \
                Exactly one session per group per day is required; a different link already on the session is never overwritten.
                """, width: 700)
            ]),
            DesignKit.section("Recordings", rows: [
                scroll,
                detailScroll,
                DesignKit.horizontal([
                    button("Sync Now", #selector(syncNowPressed)),
                    button("Retry Failed", #selector(retryFailedPressed)),
                    button("Retry Selected", #selector(retrySelectedPressed)),
                    button("Open LMS Session", #selector(openSelectedSession)),
                    button("View Logs", #selector(viewLogs))
                ])
            ])
        ])
    }

    private func refreshRecordingSync() {
        let sync = coordinator.recordingSync
        let settings = sync.settings
        if googleClientIDField.currentEditor() == nil { googleClientIDField.stringValue = settings.clientID }
        if spreadsheetField.currentEditor() == nil { spreadsheetField.stringValue = settings.spreadsheetID }
        syncEnabledButton.state = settings.enabled ? .on : .off
        replaceZoomButton.state = settings.replaceZoomRecordingLinks ? .on : .off
        if !googleStatus.stringValue.hasPrefix("…") {
            googleStatus.stringValue = settings.clientID.isEmpty
                ? "No OAuth client saved yet."
                : (sync.isGoogleConnected ? "✓ Google connected." : "Not connected. Press Connect Google.")
        }
        var lines: [String] = []
        if sync.isSyncing { lines.append("Syncing now…") }
        lines.append("Last successful sync: " + (sync.lastSuccessAt.map { DateFormatter.localizedString(from: $0, dateStyle: .medium, timeStyle: .short) } ?? "never"))
        if let next = sync.nextRun { lines.append("Next scheduled run: " + DateFormatter.localizedString(from: next, dateStyle: .medium, timeStyle: .short)) }
        if let summary = sync.lastSummary { lines.append("Last result: \(summary)") }
        if LmsSettings.load().dryRun { lines.append("Rehearse is ON (Automation → LMS): links are checked but never saved.") }
        syncStatus.stringValue = lines.joined(separator: "\n")
        let selected = Set(syncTable.selectedRowIndexes.compactMap { syncRows.indices.contains($0) ? syncRows[$0].id : nil })
        syncRows = sync.records.sorted { ($0.sessionDate, $0.groupCode) > ($1.sessionDate, $1.groupCode) }
        syncTable.reloadData()
        syncTable.selectRowIndexes(IndexSet(syncRows.indices.filter { selected.contains(syncRows[$0].id) }), byExtendingSelection: false)
        showSyncDetail()
    }

    func tableViewSelectionDidChange(_ notification: Notification) {
        guard (notification.object as? NSTableView) === syncTable else { return }
        showSyncDetail()
    }

    /// Everything known about the selected record, selectable so links can be copied.
    private func showSyncDetail() {
        guard syncRows.indices.contains(syncTable.selectedRow) else {
            syncDetail.string = syncRows.isEmpty ? "No recordings yet. Press Sync Now." : "Select a row to see its details."
            return
        }
        let record = syncRows[syncTable.selectedRow]
        func stamp(_ date: Date?) -> String { date.map { DateFormatter.localizedString(from: $0, dateStyle: .medium, timeStyle: .short) } ?? "—" }
        var lines = [
            "\(record.groupCode) · \(record.sessionDate) · \(Self.syncStatusText(record).text)",
            "",
            "Drive link:      \(record.driveURL)",
            "Sheet:           \(record.sheetTab), row \(record.sheetRow)\(record.fileName.isEmpty ? "" : " – \(record.fileName)")",
            "LMS session:     \(record.lmsSessionURL ?? "not opened yet")"
        ]
        if let replaced = record.replacedLink { lines.append("Replaced link:   \(replaced)") }
        if let conflicting = record.conflictingLink { lines.append("On the LMS now:  \(conflicting)") }
        if let message = record.lastMessage { lines.append("Last result:     \(message)") }
        if let error = record.error, error != record.lastMessage { lines.append("Reason:          \(error)") }
        lines.append("Attempts:        \(record.attempts)   Last try: \(stamp(record.lastAttemptAt))   Completed: \(stamp(record.completedAt))")
        syncDetail.string = lines.joined(separator: "\n")
    }

    /// Short, fixed-length words for the LMS column; the long story is in Details and the panel.
    static func syncStatusText(_ record: RecordingSyncRecord) -> (text: String, color: NSColor) {
        switch record.state {
        case .attached:
            switch record.lmsOutcome {
            case "replacedZoom": return ("Updated ✓ (Zoom replaced)", .systemGreen)
            case "alreadyAttached": return ("Already on LMS ✓", .systemGreen)
            default: return ("Updated ✓", .systemGreen)
            }
        case .processing: return ("Updating…", .labelColor)
        case .pending:
            switch record.issue {
            case .rehearsed: return ("Rehearsed only", .secondaryLabelColor)
            case .noSession: return ("Waiting: no session", .secondaryLabelColor)
            case .sessionNotEnded: return ("Waiting: not ended", .secondaryLabelColor)
            case .attendanceNotComplete: return ("Waiting: attendance", .secondaryLabelColor)
            default: return ("Pending", .secondaryLabelColor)
            }
        case .conflict:
            switch record.issue {
            case .multipleDriveLinksInSheet: return ("Conflict: sheet links", .systemOrange)
            case .existingLinkDiffers: return ("Conflict: other link", .systemOrange)
            case .ambiguousSessions: return ("Conflict: 2+ sessions", .systemOrange)
            case .sheetLinkChanged: return ("Conflict: link changed", .systemOrange)
            default: return ("Conflict – review", .systemOrange)
            }
        case .failed: return ("Failed (\(record.attempts))", .systemRed)
        }
    }

    private func syncCell(column: String, row: Int) -> NSView? {
        guard syncRows.indices.contains(row) else { return nil }
        let record = syncRows[row]
        let text: String
        var color = NSColor.labelColor
        switch column {
        case "sync.group": text = record.groupCode
        case "sync.date": text = record.sessionDate
        case "sync.drive": text = "Found ✓"
        case "sync.lms":
            let status = Self.syncStatusText(record)
            text = status.text
            color = status.color
        default:
            switch record.state {
            case .attached:
                text = record.replacedLink.map { "Replaced \(RecordingLinkRules.preview($0))" }
                    ?? (record.lmsOutcome == "alreadyAttached" ? "The session already had this Drive link." : "Drive link saved and read back.")
            case .conflict where record.conflictingLink != nil:
                text = "LMS has \(RecordingLinkRules.preview(record.conflictingLink!))"
            default:
                text = record.error ?? ""
            }
        }
        let field = NSTextField(labelWithString: text)
        field.textColor = color
        field.lineBreakMode = .byTruncatingTail
        field.toolTip = column == "sync.drive" ? "\(record.driveURL)\n\(record.sheetTab) row \(record.sheetRow): \(record.fileName)" : text
        return field
    }

    @objc private func saveGoogleClient() {
        let secret = googleClientSecretField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        coordinator.recordingSync.saveClient(id: googleClientIDField.stringValue, secret: secret.isEmpty ? nil : secret)
        googleClientSecretField.stringValue = ""
        googleStatus.stringValue = "Client saved."
        refresh()
    }

    @objc private func connectGoogle() {
        saveGoogleClient()
        googleStatus.stringValue = "… Finish signing in to Google in your browser."
        coordinator.recordingSync.connectGoogle { [weak self] result in
            switch result {
            case .success: self?.googleStatus.stringValue = "✓ Google connected."
            case .failure(let error): self?.googleStatus.stringValue = "✗ \(error.localizedDescription)"
            }
            self?.refresh()
        }
    }

    @objc private func disconnectGoogle() {
        coordinator.recordingSync.disconnectGoogle()
        googleStatus.stringValue = "Disconnected."
        refresh()
    }

    @objc private func syncSettingsChanged() {
        var settings = coordinator.recordingSync.settings
        let wasEnabled = settings.enabled
        settings.spreadsheetID = spreadsheetField.stringValue
        settings.enabled = syncEnabledButton.state == .on
        settings.replaceZoomRecordingLinks = replaceZoomButton.state == .on
        settings.save()
        if wasEnabled != settings.enabled { coordinator.syncLaunchAgent() }
        refresh()
    }

    @objc private func testSheetConnection() {
        syncSettingsChanged()
        syncStatus.stringValue = "Reading the sheet…"
        coordinator.recordingSync.testConnection { [weak self] message in
            self?.syncStatus.stringValue = message
        }
    }

    @objc private func syncNowPressed() {
        syncSettingsChanged()
        coordinator.recordingSync.syncNow()
    }

    @objc private func retryFailedPressed() { coordinator.recordingSync.retryFailed() }

    @objc private func retrySelectedPressed() {
        let ids = syncTable.selectedRowIndexes.compactMap { syncRows.indices.contains($0) ? syncRows[$0].id : nil }
        guard !ids.isEmpty else { return }
        coordinator.recordingSync.retry(ids: ids)
    }

    @objc private func openSelectedSession() {
        guard syncRows.indices.contains(syncTable.selectedRow) else { return }
        let record = syncRows[syncTable.selectedRow]
        let url = record.lmsSessionURL.flatMap(URL.init(string:))
            ?? URL(string: "https://dashboard.depi.eyouthbusiness.com/group_admin/sessions?date_from=\(record.sessionDate)&date_to=\(record.sessionDate)")
        if let url { NSWorkspace.shared.open(url) }
    }

    @objc private func viewLogs() {
        tabs.selectTabViewItem(withIdentifier: "Log")
    }

    private func makeMoreTab() -> NSView {
        for control in [webHeadlessButton, rolesEnabledButton, openAppButton] {
            control.target = self
            control.action = #selector(moreSettingsChanged)
        }
        nodePathField.placeholderString = "empty = find it automatically (/opt/homebrew/bin/node, …)"
        nodePathField.widthAnchor.constraint(equalToConstant: 380).isActive = true
        nodePathField.target = self
        nodePathField.action = #selector(moreSettingsChanged)
        helperStatus.preferredMaxLayoutWidth = 700

        return scrolling([
            DesignKit.section("Zoom Web meetings", rows: [
                DesignKit.caption("""
                Set an account's engine to Web or Auto in Schedules → Zoom Accounts. Auto runs a meeting in the desktop \
                app while it is free and in the Zoom Web Client when the desktop app already holds a meeting, so two \
                classes can overlap. Each account has its own browser profile; sign in to Zoom there once.
                """, width: 700),
                webHeadlessButton
            ]),
            DesignKit.section("Co-host", rows: [
                rolesEnabledButton,
                DesignKit.caption("Candidates are listed per group in Schedules → Groups. Only people on that list are ever made co-host, and Zoom's own list confirms it.", width: 700)
            ]),
            DesignKit.section("Scheduler", rows: [
                openAppButton,
                DesignKit.caption("Installs a LaunchAgent that opens the app a few minutes before each meeting in the next two weeks. It points at the app's bundle identifier, so moving the app does not break it.", width: 700)
            ]),
            DesignKit.section("Automation helper", rows: [
                DesignKit.horizontal([label("Node.js"), nodePathField]),
                DesignKit.horizontal([button("Check Helper", #selector(checkHelper))]),
                helperStatus
            ])
        ])
    }

    private func makeLogTab() -> NSView {
        logView.isEditable = false
        logView.font = .monospacedSystemFont(ofSize: 11, weight: .regular)
        logView.isVerticallyResizable = true
        logView.autoresizingMask = [.width]
        let scroll = NSScrollView()
        scroll.documentView = logView
        scroll.hasVerticalScroller = true
        return scroll
    }

    // MARK: LMS actions

    @objc private func lmsSettingsChanged() {
        LmsSettings(
            runSessionOnMeetingStart: runSessionButton.state == .on,
            takeAttendance: takeAttendanceButton.state == .on,
            correctAttendance: correctAttendanceButton.state == .on,
            showBrowser: showBrowserButton.state == .on,
            dryRun: dryRunButton.state == .on
        ).save()
        let zone = zoomTimeZoneField.stringValue.trimmingCharacters(in: .whitespaces)
        if zone.isEmpty {
            UserDefaults.standard.removeObject(forKey: "lms.zoomAccountTimeZone")
        } else if TimeZone(identifier: zone) != nil {
            UserDefaults.standard.set(zone, forKey: "lms.zoomAccountTimeZone")
        } else {
            NSSound.beep()
        }
    }

    @objc private func saveSignIn() { storeSignIn(verify: true) }
    @objc private func saveSignInWithoutTest() { storeSignIn(verify: false) }

    private func storeSignIn(verify: Bool) {
        let email = emailField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        let password = passwordField.stringValue
        guard !email.isEmpty, !password.isEmpty else {
            signInStatus.stringValue = "Enter both the email and the password."
            return
        }
        signInStatus.stringValue = verify ? "Signing in to the dashboard…" : "Saving…"
        coordinator.saveSignIn(LmsAccount(email: email, password: password), verify: verify) { [weak self] result in
            // Cleared either way, so the password does not sit in an open window.
            self?.passwordField.stringValue = ""
            self?.signInStatus.stringValue = result.success ? "✓ \(result.message)" : "✗ \(result.message)"
        }
    }

    @objc private func forgetSignIn() {
        coordinator.forgetSignIn()
        refresh()
    }

    // MARK: Follow-up actions

    @objc private func runSelectedFollowUp() {
        guard followUpRows.indices.contains(followUpTable.selectedRow) else { return }
        coordinator.runFollowUpNow(id: followUpRows[followUpTable.selectedRow].id)
        manualStatus.stringValue = "Running \(followUpRows[followUpTable.selectedRow].describe)… see the Log tab."
    }

    @objc private func removeSelectedFollowUp() {
        guard followUpRows.indices.contains(followUpTable.selectedRow) else { return }
        coordinator.followUps.remove(id: followUpRows[followUpTable.selectedRow].id)
        refresh()
    }

    @objc private func runAllDue() {
        coordinator.processDueFollowUps()
        manualStatus.stringValue = "Running everything due… see the Log tab."
    }

    @objc private func refreshPressed() { refresh() }

    private var manualGroup: String {
        let typed = manualGroupField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        return typed.isEmpty ? (manualGroupPopUp.titleOfSelectedItem ?? "") : typed
    }

    private var manualDate: String { LmsFollowUpQueue.dashboardDateAndTime(manualDatePicker.dateValue).0 }

    private var manualTime: String? {
        let text = manualTimeField.stringValue.trimmingCharacters(in: .whitespaces)
        return text.isEmpty ? nil : text
    }

    /// The Present names of that day's register for the chosen group.
    private func manualPresentNames() -> [String]? {
        let configuration = configurationProvider()
        guard let group = configuration.studentGroups.first(where: { $0.dashboardGroupName == manualGroup }) else { return nil }
        let probe = LmsFollowUp(group: manualGroup, sessionDate: manualDate, sessionStart: manualTime ?? "12:00", step: .takeAttendance, dueAt: Date(), attendanceGroupID: group.id)
        var sessions = AttendanceStore().loadAll()
        if let live = coordinator.liveAttendanceSession() {
            sessions.removeAll { $0.id == live.id }
            sessions.append(live)
        }
        return LmsPresentNames.session(for: probe, in: sessions).map(LmsPresentNames.present(in:))
    }

    private func runManual(_ action: AutomationCoordinator.ManualAction, _ title: String) {
        guard !manualGroup.isEmpty else {
            manualStatus.stringValue = "Choose or type a group first."
            return
        }
        manualStatus.stringValue = "\(title) for \(manualGroup) on \(manualDate)… see the Log tab."
        coordinator.perform(action, group: manualGroup, date: manualDate, time: manualTime) { [weak self] result in
            self?.manualStatus.stringValue = "\(result.success ? "✓" : "✗") \(result.message)"
        }
    }

    @objc private func manualRunSession() { runManual(.runSession, "Run Session") }

    @objc private func manualTakeAttendance() {
        guard let present = manualPresentNames() else {
            manualStatus.stringValue = "No attendance register was found for \(manualGroup) on \(manualDate)."
            return
        }
        runManual(.takeAttendance(present: present), "Uploading attendance (\(present.count) present)")
    }

    @objc private func manualCorrectAttendance() {
        guard let present = manualPresentNames() else {
            manualStatus.stringValue = "No attendance register was found for \(manualGroup) on \(manualDate)."
            return
        }
        runManual(.correctAttendance(present: present), "Correcting attendance (\(present.count) present)")
    }

    // MARK: More actions

    @objc private func moreSettingsChanged() {
        let defaults = UserDefaults.standard
        defaults.set(webHeadlessButton.state == .on, forKey: "web.headless")
        defaults.set(rolesEnabledButton.state == .on, forKey: "roles.enabled")
        let openApp = openAppButton.state == .on
        let changed = defaults.bool(forKey: "scheduler.openAppForMeetings") != openApp
        defaults.set(openApp, forKey: "scheduler.openAppForMeetings")
        if changed { coordinator.syncLaunchAgent() }
        let node = nodePathField.stringValue.trimmingCharacters(in: .whitespaces)
        if node.isEmpty {
            defaults.removeObject(forKey: AutomationEnvironment.nodePathDefaultsKey)
        } else {
            defaults.set(node, forKey: AutomationEnvironment.nodePathDefaultsKey)
        }
    }

    @objc private func checkHelper() {
        moreSettingsChanged()
        helperStatus.stringValue = "Checking…"
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            guard let self else { return }
            let status = self.coordinator.checkHelper()
            DispatchQueue.main.async { self.helperStatus.stringValue = status }
        }
    }

    // MARK: Table

    func numberOfRows(in tableView: NSTableView) -> Int {
        tableView === syncTable ? syncRows.count : followUpRows.count
    }

    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        if tableView === syncTable { return syncCell(column: tableColumn?.identifier.rawValue ?? "", row: row) }
        guard followUpRows.indices.contains(row), let id = tableColumn?.identifier.rawValue else { return nil }
        let item = followUpRows[row]
        let text: String
        switch id {
        case "due": text = DateFormatter.localizedString(from: item.dueAt, dateStyle: .short, timeStyle: .short)
        case "class": text = "\(item.group) · \(item.sessionDate) \(item.sessionStart)"
        case "step": text = item.step.displayName
        default:
            if item.attempts >= LmsFollowUpQueue.maximumAttempts {
                text = "Gave up: \(item.lastError ?? "")"
            } else {
                text = item.lastError.map { "Attempt \(item.attempts): \($0)" } ?? "Waiting"
            }
        }
        let field = NSTextField(labelWithString: text)
        field.lineBreakMode = .byTruncatingTail
        field.toolTip = text
        return field
    }

    // MARK: Builders

    private func tab(_ title: String, _ view: NSView) -> NSTabViewItem {
        let item = NSTabViewItem(identifier: title)
        item.label = title
        item.view = view
        return item
    }

    private func label(_ text: String) -> NSTextField { NSTextField(labelWithString: text) }

    private func button(_ title: String, _ action: Selector) -> NSButton {
        NSButton(title: title, target: self, action: action)
    }

    private func scrolling(_ sections: [NSView]) -> NSView {
        let stack = NSStackView(views: sections)
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = DesignKit.Metrics.sectionSpacing
        stack.translatesAutoresizingMaskIntoConstraints = false

        let document = TopAnchoredView()
        document.translatesAutoresizingMaskIntoConstraints = false
        document.addSubview(stack)
        let scroll = NSScrollView()
        scroll.hasVerticalScroller = true
        scroll.autohidesScrollers = true
        scroll.drawsBackground = false
        scroll.documentView = document
        NSLayoutConstraint.activate([
            document.widthAnchor.constraint(equalTo: scroll.contentView.widthAnchor),
            stack.leadingAnchor.constraint(equalTo: document.leadingAnchor, constant: 20),
            stack.trailingAnchor.constraint(lessThanOrEqualTo: document.trailingAnchor, constant: -20),
            stack.topAnchor.constraint(equalTo: document.topAnchor, constant: 20),
            stack.bottomAnchor.constraint(equalTo: document.bottomAnchor, constant: -20)
        ])
        return scroll
    }
}

private final class TopAnchoredView: NSView {
    override var isFlipped: Bool { true }
}
