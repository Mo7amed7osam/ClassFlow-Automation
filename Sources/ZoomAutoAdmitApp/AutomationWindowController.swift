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
    private let attachRecordingButton = NSButton(checkboxWithTitle: "Attach the Zoom recording link 4 hours after the start", target: nil, action: nil)
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
    private let manualLinkField = NSTextField()
    private let manualStatus = NSTextField(wrappingLabelWithString: "")

    // Recording API
    private let apiEnabledButton = NSButton(checkboxWithTitle: "Run the recording API while the app is open", target: nil, action: nil)
    private let apiPortField = NSTextField()
    private let apiHostField = NSTextField()
    private let apiClientsField = NSTextField()
    private let apiLockWaitField = NSTextField()
    private let apiKeyStatus = NSTextField(labelWithString: "")
    private let apiStatus = NSTextField(labelWithString: "")

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

        let tabs = NSTabView()
        tabs.addTabViewItem(tab("LMS", makeLmsTab()))
        tabs.addTabViewItem(tab("Follow-ups", makeFollowUpTab()))
        tabs.addTabViewItem(tab("Recording API", makeApiTab()))
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
        attachRecordingButton.state = settings.attachRecording ? .on : .off
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

        let api = RecordingAPISettings.load()
        apiEnabledButton.state = UserDefaults.standard.bool(forKey: RecordingAPISettings.enabledKey) ? .on : .off
        apiPortField.stringValue = String(api.port)
        apiHostField.stringValue = api.host
        apiClientsField.stringValue = api.allowedClients
        apiLockWaitField.stringValue = String(api.lockWaitSeconds)
        apiKeyStatus.stringValue = RecordingAPIKeyStore.load().map { "Key stored (…\($0.suffix(4)))" } ?? "No key yet."
        apiStatus.stringValue = coordinator.apiStatus

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
        for button in [runSessionButton, takeAttendanceButton, correctAttendanceButton, attachRecordingButton, showBrowserButton, dryRunButton] {
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
                attachRecordingButton,
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
        manualLinkField.placeholderString = "https://drive.google.com/file/d/…/view  or a Zoom share link"
        manualLinkField.widthAnchor.constraint(equalToConstant: 460).isActive = true
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
                DesignKit.horizontal([label("Recording link"), manualLinkField]),
                DesignKit.horizontal([
                    button("Attach Link", #selector(manualAttachLink)),
                    button("Find in Zoom & Attach", #selector(manualRecordingFromZoom))
                ]),
                manualStatus,
                DesignKit.caption("Attendance uses that day's register for the group. The start time picks between two sessions of one group on the same day.")
            ])
        ])
    }

    private func makeApiTab() -> NSView {
        apiEnabledButton.target = self
        apiEnabledButton.action = #selector(apiEnabledChanged)
        apiPortField.widthAnchor.constraint(equalToConstant: 80).isActive = true
        apiHostField.placeholderString = "empty = 127.0.0.1 (loopback only)"
        apiClientsField.placeholderString = "only with a private host, e.g. 100.64.0.0/10"
        apiLockWaitField.widthAnchor.constraint(equalToConstant: 80).isActive = true
        [apiHostField, apiClientsField].forEach { $0.widthAnchor.constraint(equalToConstant: 320).isActive = true }

        return scrolling([
            DesignKit.section("Recording API for n8n", rows: [
                apiEnabledButton,
                DesignKit.horizontal([label("Port"), apiPortField, label("Wait for a busy profile"), apiLockWaitField, label("seconds")]),
                DesignKit.horizontal([label("Listen on"), apiHostField]),
                DesignKit.horizontal([label("Allowed clients"), apiClientsField]),
                DesignKit.horizontal([button("Apply & Restart", #selector(applyApiSettings)), button("Stop", #selector(stopApi))]),
                apiStatus
            ]),
            DesignKit.section("API key", rows: [
                apiKeyStatus,
                DesignKit.horizontal([button("Generate New Key & Copy", #selector(generateApiKey)), button("Copy Key", #selector(copyApiKey))]),
                DesignKit.caption("n8n sends it as the X-API-Key header. It is kept in your Keychain and never written to a file or a log.")
            ]),
            DesignKit.section("Calling it", rows: [
                DesignKit.caption("""
                POST http://127.0.0.1:<port>/api/recordings/process
                { "group": "CAI5_AIS4_S7", "recordLink": "https://drive.google.com/file/d/…/view", "date": "2026-09-03", "startTime": "19:00", "replaceExisting": false }

                GET /health answers {"status":"ok"} without a key. The API listens on loopback by default; to reach it \
                from another machine, run a tunnel (cloudflared, Tailscale) on this Mac. Answers: 200 attached or already \
                there, 400 invalid request, 401 wrong key, 404 recording not found, 409 busy, 500 dashboard failure.
                """, width: 700)
            ])
        ])
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
            attachRecording: attachRecordingButton.state == .on,
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
            if result.success, self?.apiEnabledButton.state == .on { self?.coordinator.startRecordingAPI() }
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

    @objc private func manualAttachLink() {
        let link = manualLinkField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !link.isEmpty else {
            manualStatus.stringValue = "Paste the recording link first."
            return
        }
        runManual(.attachLink(link, replaceExisting: false), "Attaching the link")
    }

    @objc private func manualRecordingFromZoom() {
        let configuration = configurationProvider()
        let group = configuration.studentGroups.first { $0.dashboardGroupName == manualGroup }
        let schedule = configuration.schedules.first { $0.attendanceGroupID == group?.id }
        guard let profile = schedule.flatMap(configuration.profile(for:)) else {
            manualStatus.stringValue = "No schedule links \(manualGroup) to a Zoom account, so its recordings profile is unknown."
            return
        }
        runManual(.recordingFromZoom(profile: profile.resolvedWebProfileName), "Finding the recording in Zoom")
    }

    // MARK: API actions

    @objc private func apiEnabledChanged() {
        let enabled = apiEnabledButton.state == .on
        UserDefaults.standard.set(enabled, forKey: RecordingAPISettings.enabledKey)
        enabled ? applyApiSettings() : stopApi()
    }

    @objc private func applyApiSettings() {
        RecordingAPISettings(
            port: Int(apiPortField.stringValue) ?? 47821,
            host: apiHostField.stringValue.trimmingCharacters(in: .whitespaces),
            allowedClients: apiClientsField.stringValue.trimmingCharacters(in: .whitespaces),
            lockWaitSeconds: Int(apiLockWaitField.stringValue) ?? 120
        ).save()
        if apiEnabledButton.state == .on {
            coordinator.startRecordingAPI()
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 2) { [weak self] in self?.refresh() }
    }

    @objc private func stopApi() {
        coordinator.stopRecordingAPI()
        DispatchQueue.main.asyncAfter(deadline: .now() + 1) { [weak self] in self?.refresh() }
    }

    @objc private func generateApiKey() {
        if RecordingAPIKeyStore.load() != nil {
            let alert = NSAlert()
            alert.messageText = "Replace the API key?"
            alert.informativeText = "n8n will need the new key; requests with the old one are refused."
            alert.addButton(withTitle: "Cancel")
            alert.addButton(withTitle: "Replace")
            guard alert.runModal() == .alertSecondButtonReturn else { return }
        }
        let key = RecordingAPIKeyStore.generate()
        guard RecordingAPIKeyStore.save(key) else {
            apiKeyStatus.stringValue = "The Keychain refused the key."
            return
        }
        copyToPasteboard(key)
        apiKeyStatus.stringValue = "New key stored and copied to the clipboard."
        if coordinator.isRecordingAPIRunning { coordinator.startRecordingAPI() }
    }

    @objc private func copyApiKey() {
        guard let key = RecordingAPIKeyStore.load() else { return }
        copyToPasteboard(key)
        apiKeyStatus.stringValue = "Key copied to the clipboard."
    }

    private func copyToPasteboard(_ text: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
    }

    // MARK: More actions

    @objc private func moreSettingsChanged() {
        let defaults = UserDefaults.standard
        defaults.set(webHeadlessButton.state == .on, forKey: "web.headless")
        defaults.set(rolesEnabledButton.state == .on, forKey: "roles.enabled")
        let openApp = openAppButton.state == .on
        let changed = defaults.bool(forKey: "scheduler.openAppForMeetings") != openApp
        defaults.set(openApp, forKey: "scheduler.openAppForMeetings")
        if changed {
            if openApp { coordinator.syncLaunchAgent() } else { LaunchAgentScheduler.uninstall() }
        }
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

    func numberOfRows(in tableView: NSTableView) -> Int { followUpRows.count }

    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
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
