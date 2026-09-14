import AppKit
import Foundation
import ZoomAXSupport
import ZoomAutoAdmitCore

/// Editor for schedules and Zoom account profiles.
///
/// Built programmatically so the app stays a single binary with no nib
/// resources. Three rules drive the layout: every edit is validated before it
/// can be saved, saving always produces visible confirmation, and no edit is
/// ever lost silently.
final class SchedulerWindowController: NSWindowController, NSWindowDelegate {
    private let coordinator: SchedulerCoordinator
    private let state: AppState
    private var configuration: SchedulerConfiguration
    /// Runs the Excel imports and opens browser profiles. Set by the app delegate.
    var automation: AutomationCoordinator?
    private var isDirty = false
    private var savedFeedbackWorkItem: DispatchWorkItem?

    private let tabView = NSTabView()
    private let scheduleTable = NSTableView()
    private let profileTable = NSTableView()
    private let saveButton = NSButton(title: "Save", target: nil, action: nil)
    private let profileSaveButton = NSButton(title: "Save", target: nil, action: nil)
    private let runNowButton = NSButton(title: "Run Now", target: nil, action: nil)

    // Schedule editor fields
    private let nameField = NSTextField()
    private let enabledButton = NSButton(checkboxWithTitle: "Enabled", target: nil, action: nil)
    private let recurrencePopUp = NSPopUpButton()
    private let weekdayButtons: [NSButton] = Weekday.allCases.map {
        NSButton(checkboxWithTitle: $0.shortName, target: nil, action: nil)
    }
    private let oneTimeDatePicker = NSDatePicker()
    private let startTimePicker = NSDatePicker()
    private let usesEndTimeButton = NSButton(checkboxWithTitle: "Stop Auto Admit at", target: nil, action: nil)
    private let endTimePicker = NSDatePicker()
    private let accountPopUp = NSPopUpButton()
    private let meetingKindPopUp = NSPopUpButton()
    private let meetingNameField = NSTextField()
    private let meetingIDField = NSTextField()
    private let muteMicrophoneButton = NSButton(checkboxWithTitle: "Mute microphone", target: nil, action: nil)
    private let disableCameraButton = NSButton(checkboxWithTitle: "Turn camera off", target: nil, action: nil)
    private let autoAdmitButton = NSButton(checkboxWithTitle: "Enable Auto Admit", target: nil, action: nil)
    private let launchEarlyField = NSTextField()

    // Inline validation labels
    private let nameError = SchedulerWindowController.errorLabel()
    private let weekdayError = SchedulerWindowController.errorLabel()
    private let accountError = SchedulerWindowController.errorLabel()
    private let meetingNameError = SchedulerWindowController.errorLabel()
    private let meetingIDError = SchedulerWindowController.errorLabel()
    /// Shows the meeting number actually parsed out of whatever was typed or
    /// pasted, so a mistyped link is obvious before the schedule ever runs.
    private let meetingIDHint: NSTextField = {
        let field = NSTextField(labelWithString: "")
        field.font = .systemFont(ofSize: NSFont.smallSystemFontSize)
        field.textColor = .secondaryLabelColor
        return field
    }()

    // Attendance group fields
    private let attendanceGroupPopUp = NSPopUpButton()
    private let groupTable = NSTableView()
    private let groupNameField = NSTextField()
    private let groupIgnoredField = NSTextField()
    private let groupThresholdField = NSTextField()
    private let groupRosterLabel = NSTextField(labelWithString: "")
    private let groupLmsCodeField = NSTextField()
    private let groupCoHostField = NSTextField()
    /// The selected group's students, one row each, editable in place.
    private let rosterTable = NSTableView()
    private var rosterSearch = ""
    private let rosterSearchField = NSSearchField()

    /// The item each form currently shows. Forms write back to *these*, never to the table's
    /// selected row: by the time AppKit reports a selection change the selected row is already
    /// the new one, and writing the old form into it copied one group over another.
    private var loadedScheduleID: UUID?
    private var loadedProfileID: UUID?
    private var loadedGroupID: UUID?
    private let groupNameError = SchedulerWindowController.errorLabel()
    private let groupEmptyState = NSStackView()
    private let groupFormContainer = NSView()

    // Profile editor fields
    private let profileNameField = NSTextField()
    private let profileAccountPopUp = NSPopUpButton()
    private let profileManualField = NSTextField()
    private let profileNameError = SchedulerWindowController.errorLabel()
    private let profileAccountError = SchedulerWindowController.errorLabel()
    private let profileHintLabel = NSTextField(labelWithString: "")
    private let profileEnginePopUp = NSPopUpButton()
    private let profileWebProfileField = NSTextField()
    /// Emails read from Zoom's Switch account menu.
    private var detectedAccounts: [String] = []
    private static let manualEntryTitle = "Enter manually…"

    private let scheduleEmptyState = NSStackView()
    private let profileEmptyState = NSStackView()
    private let scheduleFormContainer = NSView()
    private let profileFormContainer = NSView()

    private var selectedScheduleIndex: Int? {
        scheduleTable.selectedRow >= 0 && configuration.schedules.indices.contains(scheduleTable.selectedRow)
            ? scheduleTable.selectedRow
            : nil
    }

    private var selectedProfileIndex: Int? {
        profileTable.selectedRow >= 0 && configuration.accountProfiles.indices.contains(profileTable.selectedRow)
            ? profileTable.selectedRow
            : nil
    }

    init(coordinator: SchedulerCoordinator, state: AppState) {
        self.coordinator = coordinator
        self.state = state
        self.configuration = coordinator.currentConfiguration

        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 940, height: 680),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        window.title = "Zoom Auto Admit"
        window.center()
        window.setFrameAutosaveName("SchedulerWindow")
        window.minSize = NSSize(width: 880, height: 640)
        // A large title reads as an app rather than a preferences sheet.
        window.titleVisibility = .visible
        window.toolbarStyle = .unified
        super.init(window: window)
        window.delegate = self
        window.contentView = makeContentView()
        reloadFromConfiguration()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) is not used")
    }

    func present() {
        if !isDirty {
            configuration = coordinator.currentConfiguration
            reloadFromConfiguration()
        }
        // A saved frame can leave the window on a display that is no longer
        // there, or one the user is not looking at.
        if let window, let screen = NSScreen.main, !screen.visibleFrame.intersects(window.frame) {
            window.center()
        }
        refreshDetectedAccounts(announce: false)
        NSApp.activate(ignoringOtherApps: true)
        showWindow(nil)
        window?.makeKeyAndOrderFront(nil)
    }

    /// Nothing is lost silently.
    func windowShouldClose(_ sender: NSWindow) -> Bool {
        guard isDirty else { return true }
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = "Discard unsaved changes?"
        alert.informativeText = "Your edits to schedules or account profiles haven't been saved."
        alert.addButton(withTitle: "Cancel")
        alert.addButton(withTitle: "Discard")
        guard alert.runModal() == .alertSecondButtonReturn else { return false }
        configuration = coordinator.currentConfiguration
        isDirty = false
        reloadFromConfiguration()
        return true
    }

    // MARK: Layout

    private func makeContentView() -> NSView {
        tabView.translatesAutoresizingMaskIntoConstraints = false

        let schedulesItem = NSTabViewItem(identifier: "schedules")
        schedulesItem.label = "Schedules"
        schedulesItem.view = makeSchedulesTab()
        tabView.addTabViewItem(schedulesItem)

        let profilesItem = NSTabViewItem(identifier: "profiles")
        profilesItem.label = "Zoom Accounts"
        profilesItem.view = makeProfilesTab()
        tabView.addTabViewItem(profilesItem)

        let groupsItem = NSTabViewItem(identifier: "groups")
        groupsItem.label = "Groups"
        groupsItem.view = makeGroupsTab()
        tabView.addTabViewItem(groupsItem)

        let container = NSView()
        container.addSubview(tabView)
        NSLayoutConstraint.activate([
            tabView.leadingAnchor.constraint(equalTo: container.leadingAnchor, constant: 12),
            tabView.trailingAnchor.constraint(equalTo: container.trailingAnchor, constant: -12),
            tabView.topAnchor.constraint(equalTo: container.topAnchor, constant: 12),
            tabView.bottomAnchor.constraint(equalTo: container.bottomAnchor, constant: -12)
        ])
        return container
    }

    private func makeSchedulesTab() -> NSView {
        let view = NSView()

        configure(table: scheduleTable, columnTitle: "Schedules")
        scheduleTable.identifier = NSUserInterfaceItemIdentifier("schedulesTable")
        attendanceGroupPopUp.identifier = NSUserInterfaceItemIdentifier("scheduleGroupPopUp")
        accountPopUp.identifier = NSUserInterfaceItemIdentifier("scheduleAccountPopUp")
        scheduleTable.delegate = self
        scheduleTable.dataSource = self
        let scroll = scrollView(for: scheduleTable)

        let addButton = iconButton("plus", action: #selector(addSchedule), tooltip: "Add a schedule")
        let removeButton = iconButton("minus", action: #selector(removeSchedule), tooltip: "Delete the selected schedule")
        let importButton = NSButton(title: "Import Excel…", target: self, action: #selector(importTimetable))
        importButton.toolTip = "Add the online sessions of a DEPI timetable (.xlsx) as one-time schedules"
        importButton.controlSize = .small
        let listButtons = NSStackView(views: [addButton, removeButton, importButton])
        listButtons.orientation = .horizontal
        listButtons.spacing = 6

        buildEmptyState(
            scheduleEmptyState,
            title: "No scheduled meetings",
            message: "Create a schedule and Zoom Auto Admit can prepare your meetings automatically.",
            buttonTitle: "Create Schedule",
            action: #selector(addSchedule),
            symbol: "calendar.badge.clock"
        )

        recurrencePopUp.addItems(withTitles: ["One time", "Every day", "Selected weekdays"])
        recurrencePopUp.target = self
        recurrencePopUp.action = #selector(recurrenceChanged)

        meetingKindPopUp.addItems(withTitles: ["Meeting ID", "Personal meeting"])
        meetingKindPopUp.target = self
        meetingKindPopUp.action = #selector(meetingKindChanged)

        configure(datePicker: oneTimeDatePicker, elements: .yearMonthDay)
        configure(datePicker: startTimePicker, elements: .hourMinute)
        configure(datePicker: endTimePicker, elements: .hourMinute)

        meetingIDField.placeholderString = "123 4567 8901  or  https://zoom.us/j/1234567890"
        launchEarlyField.placeholderString = "2"
        launchEarlyField.formatter = integerFormatter()

        [nameField, meetingNameField, meetingIDField, launchEarlyField].forEach {
            $0.delegate = self
        }
        [enabledButton, usesEndTimeButton, muteMicrophoneButton, disableCameraButton, autoAdmitButton]
            .forEach { button in
                button.target = self
                button.action = #selector(fieldChanged)
            }
        weekdayButtons.forEach {
            $0.target = self
            $0.action = #selector(fieldChanged)
        }
        [oneTimeDatePicker, startTimePicker, endTimePicker].forEach {
            $0.target = self
            $0.action = #selector(fieldChanged)
        }
        accountPopUp.target = self
        accountPopUp.action = #selector(fieldChanged)
        attendanceGroupPopUp.target = self
        attendanceGroupPopUp.action = #selector(fieldChanged)

        let weekdayRow = NSStackView(views: weekdayButtons)
        weekdayRow.orientation = .horizontal
        weekdayRow.spacing = 2

        let endTimeRow = NSStackView(views: [usesEndTimeButton, endTimePicker])
        endTimeRow.orientation = .horizontal
        endTimeRow.spacing = 8

        let form = NSStackView(views: [
            section("General", rows: [
                labelled("Name", nameField, error: nameError),
                labelled("", enabledButton)
            ]),
            section("When", rows: [
                labelled("Repeat", recurrencePopUp),
                labelled("Days", weekdayRow, error: weekdayError),
                labelled("Date", oneTimeDatePicker),
                labelled("Start time", startTimePicker),
                labelled("End time", endTimeRow)
            ]),
            section("Zoom", rows: [
                labelled("Account", accountPopUp, error: accountError),
                labelled("Meeting type", meetingKindPopUp),
                labelled("Meeting name", meetingNameField, error: meetingNameError),
                labelled("Meeting ID or link", meetingIDField, error: meetingIDError),
                labelled("", meetingIDHint)
            ]),
            section("Attendance", rows: [
                labelled("Group", attendanceGroupPopUp),
                labelled("", caption("Attendance is recorded only for schedules linked to a group."))
            ]),
            section("Before joining", rows: [
                labelled("", muteMicrophoneButton),
                labelled("", disableCameraButton),
                labelled("", caption("Zoom's real state is checked first; controls are only pressed when needed."))
            ]),
            section("After the meeting starts", rows: [
                labelled("", autoAdmitButton),
                labelled("Open Zoom early", launchEarlyField, suffix: "minutes")
            ])
        ])
        form.orientation = .vertical
        form.alignment = .leading
        form.spacing = DesignKit.Metrics.sectionSpacing

        let formScroll = NSScrollView()
        formScroll.hasVerticalScroller = true
        formScroll.drawsBackground = false
        formScroll.documentView = form
        form.translatesAutoresizingMaskIntoConstraints = false
        formScroll.translatesAutoresizingMaskIntoConstraints = false

        scheduleFormContainer.translatesAutoresizingMaskIntoConstraints = false
        scheduleFormContainer.addSubview(formScroll)
        NSLayoutConstraint.activate([
            formScroll.leadingAnchor.constraint(equalTo: scheduleFormContainer.leadingAnchor),
            formScroll.trailingAnchor.constraint(equalTo: scheduleFormContainer.trailingAnchor),
            formScroll.topAnchor.constraint(equalTo: scheduleFormContainer.topAnchor),
            formScroll.bottomAnchor.constraint(equalTo: scheduleFormContainer.bottomAnchor),
            form.topAnchor.constraint(equalTo: formScroll.contentView.topAnchor),
            form.leadingAnchor.constraint(equalTo: formScroll.contentView.leadingAnchor)
        ])

        runNowButton.target = self
        runNowButton.action = #selector(runSelectedScheduleNow)
        runNowButton.toolTip = "Run this schedule immediately, exactly as the scheduler would."

        saveButton.target = self
        saveButton.action = #selector(save)
        saveButton.keyEquivalent = "\r"

        let actionRow = NSStackView(views: [runNowButton, NSView(), saveButton])
        actionRow.orientation = .horizontal
        actionRow.spacing = 10

        [scroll, listButtons, scheduleEmptyState, scheduleFormContainer, actionRow].forEach {
            $0.translatesAutoresizingMaskIntoConstraints = false
            view.addSubview($0)
        }

        NSLayoutConstraint.activate([
            scroll.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 10),
            scroll.topAnchor.constraint(equalTo: view.topAnchor, constant: 14),
            scroll.widthAnchor.constraint(equalToConstant: 260),
            scroll.bottomAnchor.constraint(equalTo: listButtons.topAnchor, constant: -8),

            listButtons.leadingAnchor.constraint(equalTo: scroll.leadingAnchor),
            listButtons.bottomAnchor.constraint(equalTo: view.bottomAnchor, constant: -14),

            scheduleFormContainer.leadingAnchor.constraint(equalTo: scroll.trailingAnchor, constant: 20),
            scheduleFormContainer.topAnchor.constraint(equalTo: view.topAnchor, constant: 14),
            scheduleFormContainer.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -10),
            scheduleFormContainer.bottomAnchor.constraint(equalTo: actionRow.topAnchor, constant: -12),

            scheduleEmptyState.centerXAnchor.constraint(equalTo: scheduleFormContainer.centerXAnchor),
            scheduleEmptyState.centerYAnchor.constraint(equalTo: scheduleFormContainer.centerYAnchor),
            scheduleEmptyState.widthAnchor.constraint(lessThanOrEqualToConstant: 340),

            actionRow.leadingAnchor.constraint(equalTo: scheduleFormContainer.leadingAnchor),
            actionRow.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -10),
            actionRow.bottomAnchor.constraint(equalTo: view.bottomAnchor, constant: -14),

            nameField.widthAnchor.constraint(equalToConstant: 300),
            meetingNameField.widthAnchor.constraint(equalToConstant: 300),
            meetingIDField.widthAnchor.constraint(equalToConstant: 200),
            launchEarlyField.widthAnchor.constraint(equalToConstant: 64),
            accountPopUp.widthAnchor.constraint(equalToConstant: 300),
            attendanceGroupPopUp.widthAnchor.constraint(equalToConstant: 300),
            recurrencePopUp.widthAnchor.constraint(equalToConstant: 200),
            meetingKindPopUp.widthAnchor.constraint(equalToConstant: 200)
        ])
        return view
    }

    private func makeProfilesTab() -> NSView {
        let view = NSView()

        configure(table: profileTable, columnTitle: "Zoom Accounts")
        profileTable.delegate = self
        profileTable.dataSource = self
        let scroll = scrollView(for: profileTable)

        let addButton = iconButton("plus", action: #selector(addProfile), tooltip: "Add an account profile")
        let removeButton = iconButton("minus", action: #selector(removeProfile), tooltip: "Delete the selected profile")
        let listButtons = NSStackView(views: [addButton, removeButton])
        listButtons.orientation = .horizontal
        listButtons.spacing = 6

        buildEmptyState(
            profileEmptyState,
            title: "No Zoom accounts configured",
            message: "Add a saved Zoom account to use scheduled meetings.",
            buttonTitle: "Read Accounts from Zoom",
            action: #selector(addProfileFromZoom),
            symbol: "person.crop.circle.badge.questionmark"
        )

        profileNameField.delegate = self
        profileManualField.delegate = self
        profileManualField.placeholderString = "name@example.com"
        profileAccountPopUp.target = self
        profileAccountPopUp.action = #selector(profileAccountChanged)

        let refreshButton = NSButton(
            title: "Refresh accounts from Zoom",
            target: self,
            action: #selector(refreshAccountsPressed)
        )
        profileHintLabel.font = .systemFont(ofSize: NSFont.smallSystemFontSize)
        profileHintLabel.textColor = .secondaryLabelColor

        profileEnginePopUp.addItems(withTitles: ZoomEnginePreference.allCases.map(\.displayName))
        profileEnginePopUp.target = self
        profileEnginePopUp.action = #selector(fieldChanged)
        profileWebProfileField.delegate = self
        profileWebProfileField.placeholderString = "named after the profile"
        let webSignInButton = NSButton(title: "Sign in to Zoom Web…", target: self, action: #selector(openWebProfile))

        let form = NSStackView(views: [
            section("Account profile", rows: [
                labelled("Profile name", profileNameField, error: profileNameError),
                labelled("Zoom account", profileAccountPopUp, error: profileAccountError),
                labelled("", profileManualField),
                labelled("", refreshButton),
                labelled("", profileHintLabel)
            ]),
            section("Runs meetings in", rows: [
                labelled("Engine", profileEnginePopUp),
                labelled("Browser profile", profileWebProfileField),
                labelled("", webSignInButton),
                labelled("", caption("""
                Auto uses the desktop app while it is free and the Zoom Web Client when it already holds a meeting. \
                Sign in to Zoom once in this account's browser profile; the Web Client reuses that sign-in.
                """))
            ]),
            caption("""
            A profile only points at an account that is already signed in to Zoom. \
            No password or token is stored. The email address is used because \
            several Zoom accounts can share the same display name.
            """)
        ])
        form.orientation = .vertical
        form.alignment = .leading
        form.spacing = DesignKit.Metrics.sectionSpacing

        profileSaveButton.target = self
        profileSaveButton.action = #selector(save)
        let actionRow = NSStackView(views: [NSView(), profileSaveButton])
        actionRow.orientation = .horizontal

        profileFormContainer.translatesAutoresizingMaskIntoConstraints = false
        form.translatesAutoresizingMaskIntoConstraints = false
        profileFormContainer.addSubview(form)
        NSLayoutConstraint.activate([
            form.leadingAnchor.constraint(equalTo: profileFormContainer.leadingAnchor),
            form.topAnchor.constraint(equalTo: profileFormContainer.topAnchor),
            form.trailingAnchor.constraint(lessThanOrEqualTo: profileFormContainer.trailingAnchor),
            form.bottomAnchor.constraint(lessThanOrEqualTo: profileFormContainer.bottomAnchor)
        ])

        [scroll, listButtons, profileEmptyState, profileFormContainer, actionRow].forEach {
            $0.translatesAutoresizingMaskIntoConstraints = false
            view.addSubview($0)
        }

        NSLayoutConstraint.activate([
            scroll.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 10),
            scroll.topAnchor.constraint(equalTo: view.topAnchor, constant: 14),
            scroll.widthAnchor.constraint(equalToConstant: 260),
            scroll.bottomAnchor.constraint(equalTo: listButtons.topAnchor, constant: -8),

            listButtons.leadingAnchor.constraint(equalTo: scroll.leadingAnchor),
            listButtons.bottomAnchor.constraint(equalTo: view.bottomAnchor, constant: -14),

            profileFormContainer.leadingAnchor.constraint(equalTo: scroll.trailingAnchor, constant: 20),
            profileFormContainer.topAnchor.constraint(equalTo: view.topAnchor, constant: 14),
            profileFormContainer.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -10),
            // Without a bottom anchor this container has no height at all.
            profileFormContainer.bottomAnchor.constraint(equalTo: actionRow.topAnchor, constant: -12),

            profileEmptyState.centerXAnchor.constraint(equalTo: profileFormContainer.centerXAnchor),
            profileEmptyState.centerYAnchor.constraint(equalTo: profileFormContainer.centerYAnchor),
            profileEmptyState.widthAnchor.constraint(lessThanOrEqualToConstant: 340),

            actionRow.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -10),
            actionRow.bottomAnchor.constraint(equalTo: view.bottomAnchor, constant: -14),

            profileNameField.widthAnchor.constraint(equalToConstant: 300),
            profileManualField.widthAnchor.constraint(equalToConstant: 300),
            profileAccountPopUp.widthAnchor.constraint(equalToConstant: 300),
            profileEnginePopUp.widthAnchor.constraint(equalToConstant: 300),
            profileWebProfileField.widthAnchor.constraint(equalToConstant: 300)
        ])
        return view
    }

    private func makeGroupsTab() -> NSView {
        let view = NSView()

        configure(table: groupTable, columnTitle: "Groups")
        groupTable.identifier = NSUserInterfaceItemIdentifier("groupsTable")
        groupNameField.identifier = NSUserInterfaceItemIdentifier("groupNameField")
        groupLmsCodeField.identifier = NSUserInterfaceItemIdentifier("groupLmsCodeField")
        groupTable.delegate = self
        groupTable.dataSource = self
        let scroll = scrollView(for: groupTable)

        let addButton = iconButton("plus", action: #selector(addGroup), tooltip: "Add a group")
        let removeButton = iconButton("minus", action: #selector(removeGroup), tooltip: "Delete the selected group")
        let listButtons = NSStackView(views: [addButton, removeButton])
        listButtons.orientation = .horizontal
        listButtons.spacing = 6

        buildEmptyState(
            groupEmptyState,
            title: "No groups yet",
            message: "A group holds one class's official student list. Link a schedule to it and attendance is recorded automatically.",
            buttonTitle: "Create Group",
            action: #selector(addGroup),
            symbol: "person.3"
        )

        groupNameField.delegate = self
        groupIgnoredField.delegate = self
        groupIgnoredField.placeholderString = "eyouth coordinator, Mohamed Hosam"
        groupThresholdField.delegate = self
        groupThresholdField.placeholderString = "90"

        for (id, title, width, editable) in [("order", "#", 34.0, false), ("name", "Official name", 250.0, true), ("aliases", "Zoom names (comma-separated)", 220.0, true), ("email", "Email", 150.0, true)] {
            let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("roster.\(id)"))
            column.title = title
            column.width = width
            column.isEditable = editable
            rosterTable.addTableColumn(column)
        }
        rosterTable.identifier = NSUserInterfaceItemIdentifier("groupRosterTable")
        rosterTable.usesAlternatingRowBackgroundColors = true
        rosterTable.allowsMultipleSelection = true
        rosterTable.rowHeight = 22
        rosterTable.dataSource = self
        rosterTable.delegate = self
        let rosterScroll = NSScrollView()
        rosterScroll.documentView = rosterTable
        rosterScroll.hasVerticalScroller = true
        rosterScroll.hasHorizontalScroller = true
        rosterScroll.borderType = .bezelBorder
        rosterScroll.translatesAutoresizingMaskIntoConstraints = false
        rosterScroll.heightAnchor.constraint(equalToConstant: 320).isActive = true
        rosterScroll.widthAnchor.constraint(equalToConstant: 620).isActive = true
        rosterSearchField.placeholderString = "Search students"
        rosterSearchField.target = self
        rosterSearchField.action = #selector(rosterSearchChanged)
        rosterSearchField.widthAnchor.constraint(equalToConstant: 220).isActive = true
        let addStudentButton = NSButton(title: "Add Student", target: self, action: #selector(addStudent))
        let removeStudentsButton = NSButton(title: "Remove Selected", target: self, action: #selector(removeSelectedStudents))

        let importButton = NSButton(title: "Import CSV…", target: self, action: #selector(importRoster))
        let importExcelButton = NSButton(title: "Import Excel…", target: self, action: #selector(importRosterWorkbook))
        groupLmsCodeField.delegate = self
        groupLmsCodeField.placeholderString = "e.g. CAI5_AIS4_S7 (empty: the group name)"
        groupCoHostField.delegate = self
        groupCoHostField.placeholderString = "Mohab Mohamed, Sara Ali | Sara A."
        let pasteButton = NSButton(title: "Paste Names…", target: self, action: #selector(pasteRoster))
        let clearRosterButton = NSButton(title: "Clear Roster", target: self, action: #selector(clearRoster))

        let form = NSStackView(views: [
            section("Group", rows: [
                labelled("Name", groupNameField, error: groupNameError),
                labelled("Ignore names", groupIgnoredField),
                labelled("", caption("Comma-separated. The host account is already excluded automatically.")),
                labelled("Auto-accept above", groupThresholdField, suffix: "%")
            ]),
            section("DEPI dashboard & co-host", rows: [
                labelled("LMS group code", groupLmsCodeField),
                labelled("Co-host candidates", groupCoHostField),
                labelled("", caption("Comma-separated people. Add Zoom display names a person uses after '|'. Only these people are ever made co-host."))
            ]),
            section("Official student list", rows: [
                // The import controls sit directly under the heading. Putting
                // them below a 180pt list pushed them to the bottom of the
                // window, where they were easy to miss entirely.
                labelled("", horizontal([importButton, importExcelButton, pasteButton, clearRosterButton])),
                labelled("", horizontal([groupRosterLabel, rosterSearchField])),
                labelled("", rosterScroll),
                labelled("", horizontal([addStudentButton, removeStudentsButton])),
                labelled("", caption("Double-click a name, its Zoom names or email to edit. Names must match the DEPI dashboard exactly for the attendance upload."))
            ])
        ])
        form.orientation = .vertical
        form.alignment = .leading
        form.spacing = DesignKit.Metrics.sectionSpacing

        let saveRow = NSStackView(views: [NSView(), NSButton(title: "Save", target: self, action: #selector(save))])
        saveRow.orientation = .horizontal

        let formScroll = NSScrollView()
        formScroll.hasVerticalScroller = true
        formScroll.drawsBackground = false
        formScroll.documentView = form
        formScroll.translatesAutoresizingMaskIntoConstraints = false
        form.translatesAutoresizingMaskIntoConstraints = false

        groupFormContainer.translatesAutoresizingMaskIntoConstraints = false
        groupFormContainer.addSubview(formScroll)
        NSLayoutConstraint.activate([
            formScroll.leadingAnchor.constraint(equalTo: groupFormContainer.leadingAnchor),
            formScroll.trailingAnchor.constraint(equalTo: groupFormContainer.trailingAnchor),
            formScroll.topAnchor.constraint(equalTo: groupFormContainer.topAnchor),
            formScroll.bottomAnchor.constraint(equalTo: groupFormContainer.bottomAnchor),
            form.topAnchor.constraint(equalTo: formScroll.contentView.topAnchor),
            form.leadingAnchor.constraint(equalTo: formScroll.contentView.leadingAnchor)
        ])

        [scroll, listButtons, groupEmptyState, groupFormContainer, saveRow].forEach {
            $0.translatesAutoresizingMaskIntoConstraints = false
            view.addSubview($0)
        }
        NSLayoutConstraint.activate([
            scroll.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 10),
            scroll.topAnchor.constraint(equalTo: view.topAnchor, constant: 14),
            scroll.widthAnchor.constraint(equalToConstant: 220),
            scroll.bottomAnchor.constraint(equalTo: listButtons.topAnchor, constant: -8),

            listButtons.leadingAnchor.constraint(equalTo: scroll.leadingAnchor),
            listButtons.bottomAnchor.constraint(equalTo: view.bottomAnchor, constant: -14),

            groupFormContainer.leadingAnchor.constraint(equalTo: scroll.trailingAnchor, constant: 20),
            groupFormContainer.topAnchor.constraint(equalTo: view.topAnchor, constant: 14),
            groupFormContainer.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -10),
            groupFormContainer.bottomAnchor.constraint(equalTo: saveRow.topAnchor, constant: -12),

            groupEmptyState.centerXAnchor.constraint(equalTo: groupFormContainer.centerXAnchor),
            groupEmptyState.centerYAnchor.constraint(equalTo: groupFormContainer.centerYAnchor),
            groupEmptyState.widthAnchor.constraint(lessThanOrEqualToConstant: 360),

            saveRow.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -10),
            saveRow.bottomAnchor.constraint(equalTo: view.bottomAnchor, constant: -14),

            groupNameField.widthAnchor.constraint(equalToConstant: 300),
            groupIgnoredField.widthAnchor.constraint(equalToConstant: 300),
            groupThresholdField.widthAnchor.constraint(equalToConstant: 70),
            groupLmsCodeField.widthAnchor.constraint(equalToConstant: 300),
            groupCoHostField.widthAnchor.constraint(equalToConstant: 380)
        ])
        return view
    }

    private var selectedGroupIndex: Int? {
        groupTable.selectedRow >= 0 && configuration.studentGroups.indices.contains(groupTable.selectedRow)
            ? groupTable.selectedRow
            : nil
    }

    private func loadSelectedGroup() {
        guard let index = selectedGroupIndex else {
            loadedGroupID = nil
            rosterTable.reloadData()
            return
        }
        let group = configuration.studentGroups[index]
        loadedGroupID = group.id
        groupNameField.stringValue = group.name
        groupIgnoredField.stringValue = group.ignoredParticipantNames.joined(separator: ", ")
        groupThresholdField.stringValue = String(Int(group.autoAcceptConfidence * 100))
        groupLmsCodeField.stringValue = group.lmsGroupCode ?? ""
        groupCoHostField.stringValue = Self.coHostText(group.coHostCandidates)
        rosterSearch = ""
        rosterSearchField.stringValue = ""
        reloadRoster()
        updateChrome()
    }

    private func storeSelectedGroup() {
        guard let loadedGroupID, let index = configuration.studentGroups.firstIndex(where: { $0.id == loadedGroupID }) else { return }
        configuration.studentGroups[index].name = groupNameField.stringValue
        configuration.studentGroups[index].ignoredParticipantNames = groupIgnoredField.stringValue
            .split(separator: ",")
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }
        let percent = Double(groupThresholdField.stringValue) ?? 90
        configuration.studentGroups[index].autoAcceptConfidence = min(max(percent / 100, 0.5), 1.0)
        let code = groupLmsCodeField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        configuration.studentGroups[index].lmsGroupCode = code.isEmpty ? nil : code
        configuration.studentGroups[index].coHostCandidates = Self.coHostCandidates(
            from: groupCoHostField.stringValue,
            keeping: configuration.studentGroups[index].coHostCandidates
        )
    }

    // MARK: Student roster table

    private var loadedGroupIndex: Int? {
        loadedGroupID.flatMap { id in configuration.studentGroups.firstIndex { $0.id == id } }
    }

    /// Roster positions (into the group's students) of the rows currently shown.
    private var visibleRoster: [Int] {
        guard let index = loadedGroupIndex else { return [] }
        let students = configuration.studentGroups[index].students
        let query = rosterSearch.trimmingCharacters(in: .whitespaces).lowercased()
        return students.indices.filter { position in
            guard !query.isEmpty else { return true }
            let student = students[position]
            return ([student.officialName, student.email ?? ""] + student.aliases).contains { $0.lowercased().contains(query) }
        }
    }

    private func reloadRoster() {
        rosterTable.reloadData()
        guard let index = loadedGroupIndex else {
            groupRosterLabel.stringValue = ""
            return
        }
        let count = configuration.studentGroups[index].students.count
        let shown = visibleRoster.count
        groupRosterLabel.stringValue = shown == count ? "\(count) student(s)" : "\(shown) of \(count) student(s)"
    }

    private func rosterCell(column: String, row: Int) -> NSView? {
        let positions = visibleRoster
        guard let groupIndex = loadedGroupIndex, positions.indices.contains(row) else { return nil }
        let position = positions[row]
        let student = configuration.studentGroups[groupIndex].students[position]
        let text: String
        switch column {
        case "roster.order": text = String(position + 1)
        case "roster.name": text = student.officialName
        case "roster.aliases": text = student.aliases.joined(separator: ", ")
        default: text = student.email ?? ""
        }
        let field = NSTextField(string: text)
        field.isBordered = false
        field.drawsBackground = false
        field.lineBreakMode = .byTruncatingTail
        field.toolTip = text
        field.isEditable = column != "roster.order"
        field.textColor = column == "roster.order" ? .secondaryLabelColor : .labelColor
        // The student's id, not the row, identifies what an edit changes: search can reorder rows.
        field.identifier = NSUserInterfaceItemIdentifier("\(column)|\(student.id.uuidString)")
        field.target = self
        field.action = #selector(rosterCellEdited(_:))
        field.cell?.sendsActionOnEndEditing = true
        return field
    }

    @objc private func rosterCellEdited(_ sender: NSTextField) {
        guard let raw = sender.identifier?.rawValue, let groupIndex = loadedGroupIndex else { return }
        let parts = raw.split(separator: "|")
        guard parts.count == 2, let studentID = UUID(uuidString: String(parts[1])),
              let position = configuration.studentGroups[groupIndex].students.firstIndex(where: { $0.id == studentID }) else { return }
        let value = sender.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        var student = configuration.studentGroups[groupIndex].students[position]
        switch String(parts[0]) {
        case "roster.name":
            guard !value.isEmpty else {
                sender.stringValue = student.officialName
                NSSound.beep()
                return
            }
            guard value != student.officialName else { return }
            student.officialName = value
        case "roster.aliases":
            let aliases = value.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
            guard aliases != student.aliases else { return }
            student.aliases = aliases
        case "roster.email":
            let email = value.isEmpty ? nil : value
            guard email != student.email else { return }
            student.email = email
        default:
            return
        }
        configuration.studentGroups[groupIndex].students[position] = student
        groupTable.reloadData(forRowIndexes: IndexSet(integer: groupIndex), columnIndexes: IndexSet(integer: 0))
        markDirty()
    }

    @objc private func rosterSearchChanged() {
        rosterSearch = rosterSearchField.stringValue
        reloadRoster()
    }

    @objc private func addStudent() {
        guard let groupIndex = loadedGroupIndex else { return }
        let alert = NSAlert()
        alert.messageText = "Add a student to \(configuration.studentGroups[groupIndex].name)"
        alert.informativeText = "Use the name exactly as the DEPI dashboard lists it."
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 320, height: 24))
        field.placeholderString = "Official name"
        alert.accessoryView = field
        alert.addButton(withTitle: "Add")
        alert.addButton(withTitle: "Cancel")
        alert.window.initialFirstResponder = field
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        let name = field.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !name.isEmpty else { return }
        if configuration.studentGroups[groupIndex].students.contains(where: { $0.normalizedOfficialName == NameNormalizer.normalize(name) }) {
            showAlert("Already on the roster", "“\(name)” is already in this group.")
            return
        }
        configuration.studentGroups[groupIndex].students.append(Student(officialName: name))
        rosterSearch = ""
        rosterSearchField.stringValue = ""
        reloadRoster()
        rosterTable.scrollRowToVisible(configuration.studentGroups[groupIndex].students.count - 1)
        groupTable.reloadData(forRowIndexes: IndexSet(integer: groupIndex), columnIndexes: IndexSet(integer: 0))
        markDirty()
    }

    @objc private func removeSelectedStudents() {
        guard let groupIndex = loadedGroupIndex else { return }
        let positions = visibleRoster
        let chosen = rosterTable.selectedRowIndexes.compactMap { positions.indices.contains($0) ? positions[$0] : nil }
        guard !chosen.isEmpty else { return }
        let names = chosen.map { configuration.studentGroups[groupIndex].students[$0].officialName }
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = "Remove \(names.count) student(s)?"
        alert.informativeText = names.prefix(8).joined(separator: "\n") + (names.count > 8 ? "\n…" : "") + "\n\nPast attendance keeps them; their learned Zoom names go."
        alert.addButton(withTitle: "Remove")
        alert.addButton(withTitle: "Cancel")
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        for position in chosen.sorted(by: >) {
            configuration.studentGroups[groupIndex].students.remove(at: position)
        }
        reloadRoster()
        groupTable.reloadData(forRowIndexes: IndexSet(integer: groupIndex), columnIndexes: IndexSet(integer: 0))
        markDirty()
    }

    /// "Mohab Mohamed, Sara Ali | Sara A." — people separated by commas, a person's Zoom names after "|".
    static func coHostText(_ candidates: [CoHostCandidate]) -> String {
        candidates
            .map { ([$0.name] + $0.aliases).joined(separator: " | ") }
            .joined(separator: ", ")
    }

    static func coHostCandidates(from text: String, keeping existing: [CoHostCandidate]) -> [CoHostCandidate] {
        text.split(separator: ",").compactMap { entry in
            let parts = entry.split(separator: "|").map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }.filter { !$0.isEmpty }
            guard let name = parts.first else { return nil }
            // Keep a person's identity across edits so remembered assignments still apply.
            let id = existing.first { NameNormalizer.normalize($0.name) == NameNormalizer.normalize(name) }?.id ?? UUID()
            return CoHostCandidate(id: id, name: name, aliases: Array(parts.dropFirst()))
        }
    }

    @objc private func addGroup() {
        storeSelectedGroup()
        configuration.studentGroups.append(StudentGroup(name: "New group"))
        groupTable.reloadData()
        groupTable.selectRowIndexes([configuration.studentGroups.count - 1], byExtendingSelection: false)
        loadSelectedGroup()
        markDirty()
        window?.makeFirstResponder(groupNameField)
    }

    @objc private func removeGroup() {
        guard let index = selectedGroupIndex else { return }
        let group = configuration.studentGroups[index]
        let usedBy = configuration.schedules.filter { $0.attendanceGroupID == group.id }

        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = "Delete “\(group.name)”?"
        alert.informativeText = usedBy.isEmpty
            ? "Its roster and learned aliases will be removed. Past attendance history is kept."
            : "\(usedBy.count) schedule(s) use this group and will stop recording attendance."
        alert.addButton(withTitle: "Cancel")
        alert.addButton(withTitle: "Delete")
        guard alert.runModal() == .alertSecondButtonReturn else { return }

        configuration.studentGroups.remove(at: index)
        groupTable.reloadData()
        loadSelectedGroup()
        markDirty()
    }

    @objc private func importRoster() {
        guard let index = selectedGroupIndex else { return }
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.commaSeparatedText, .plainText]
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let url = panel.url,
              let text = try? String(contentsOf: url, encoding: .utf8) else {
            return
        }
        applyRoster(text, to: index)
    }

    @objc private func pasteRoster() {
        guard let index = selectedGroupIndex else { return }

        let alert = NSAlert()
        alert.messageText = "Paste the student list"
        alert.informativeText = "One name per line, or CSV with a Name column."
        let text = NSTextView(frame: NSRect(x: 0, y: 0, width: 380, height: 200))
        text.isEditable = true
        text.font = .systemFont(ofSize: NSFont.systemFontSize)
        let scroll = NSScrollView(frame: NSRect(x: 0, y: 0, width: 380, height: 200))
        scroll.documentView = text
        scroll.hasVerticalScroller = true
        scroll.borderType = .bezelBorder
        alert.accessoryView = scroll
        alert.addButton(withTitle: "Cancel")
        alert.addButton(withTitle: "Import")
        alert.window.initialFirstResponder = text

        guard alert.runModal() == .alertSecondButtonReturn else { return }
        applyRoster(text.string, to: index)
    }

    /// Merging rather than replacing is what preserves learned aliases.
    private func applyRoster(_ text: String, to index: Int) {
        let result = RosterImporter.merge(text, into: configuration.studentGroups[index].students)
        configuration.studentGroups[index].students = result.students
        loadSelectedGroup()
        markDirty()

        var detail = "\(result.addedCount) added, \(result.updatedCount) updated."
        if !result.skippedLines.isEmpty {
            detail += "\n\(result.skippedLines.count) line(s) had no usable name and were skipped."
        }
        let alert = NSAlert()
        alert.messageText = "Roster imported"
        alert.informativeText = detail
        alert.runModal()
    }

    @objc private func clearRoster() {
        guard let index = selectedGroupIndex else { return }
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = "Clear the roster for “\(configuration.studentGroups[index].name)”?"
        alert.informativeText = "Learned aliases for these students are removed too."
        alert.addButton(withTitle: "Cancel")
        alert.addButton(withTitle: "Clear")
        guard alert.runModal() == .alertSecondButtonReturn else { return }

        configuration.studentGroups[index].students = []
        loadSelectedGroup()
        markDirty()
    }

    // MARK: Binding

    private func reloadFromConfiguration() {
        rebuildAccountPopUp()
        rebuildAttendanceGroupPopUp()
        scheduleTable.reloadData()
        profileTable.reloadData()
        groupTable.reloadData()

        if scheduleTable.selectedRow < 0, !configuration.schedules.isEmpty {
            scheduleTable.selectRowIndexes([0], byExtendingSelection: false)
        }
        if profileTable.selectedRow < 0, !configuration.accountProfiles.isEmpty {
            profileTable.selectRowIndexes([0], byExtendingSelection: false)
        }
        if groupTable.selectedRow < 0, !configuration.studentGroups.isEmpty {
            groupTable.selectRowIndexes([0], byExtendingSelection: false)
        }

        loadSelectedSchedule()
        loadSelectedProfile()
        loadSelectedGroup()
        updateChrome()
    }

    private func rebuildAccountPopUp() {
        accountPopUp.removeAllItems()
        for profile in configuration.accountProfiles {
            // Menu items, not addItem(withTitle:): that silently drops a duplicate title and shifts
            // every later position off its profile.
            accountPopUp.menu?.addItem(withTitle: "\(profile.name) — \(profile.accountIdentifier)", action: nil, keyEquivalent: "")
        }
        if configuration.accountProfiles.isEmpty {
            accountPopUp.menu?.addItem(withTitle: "No Zoom accounts yet", action: nil, keyEquivalent: "")
        }
        reselectScheduleMenus()
    }

    /// After a menu is rebuilt it points at its first item. The loaded schedule's own account and
    /// group are selected again from the model; otherwise the next store would write the first item
    /// into the schedule (which is how a Save once unlinked a schedule from its group).
    private func reselectScheduleMenus() {
        guard let loadedScheduleID, let schedule = configuration.schedules.first(where: { $0.id == loadedScheduleID }) else { return }
        if let account = configuration.accountProfiles.firstIndex(where: { $0.id == schedule.accountProfileID }) {
            accountPopUp.selectItem(at: account)
        }
        let group = schedule.attendanceGroupID.flatMap { id in configuration.studentGroups.firstIndex { $0.id == id } }
        attendanceGroupPopUp.selectItem(at: group.map { $0 + 1 } ?? 0)
    }

    private func loadSelectedSchedule() {
        guard let index = selectedScheduleIndex else {
            loadedScheduleID = nil
            return
        }
        let schedule = configuration.schedules[index]
        loadedScheduleID = schedule.id

        nameField.stringValue = schedule.name
        enabledButton.state = schedule.isEnabled ? .on : .off
        autoAdmitButton.state = schedule.enablesAutoAdmit ? .on : .off
        muteMicrophoneButton.state = schedule.mutesMicrophoneBeforeJoining ? .on : .off
        disableCameraButton.state = schedule.disablesCameraBeforeJoining ? .on : .off
        launchEarlyField.stringValue = String(schedule.launchZoomMinutesEarly)
        startTimePicker.dateValue = date(from: schedule.startTime)

        if let endTime = schedule.endTime {
            usesEndTimeButton.state = .on
            endTimePicker.dateValue = date(from: endTime)
        } else {
            usesEndTimeButton.state = .off
            endTimePicker.dateValue = date(from: TimeOfDay(hour: 20, minute: 0))
        }

        switch schedule.recurrence {
        case .oneTime(let year, let month, let day):
            recurrencePopUp.selectItem(at: 0)
            oneTimeDatePicker.dateValue = Calendar.current.date(
                from: DateComponents(year: year, month: month, day: day)
            ) ?? Date()
            setWeekdays([])
        case .daily:
            recurrencePopUp.selectItem(at: 1)
            setWeekdays([])
        case .selectedWeekdays(let days):
            recurrencePopUp.selectItem(at: 2)
            setWeekdays(days)
        }

        switch schedule.meeting.kind {
        case .meetingID(let identifier):
            meetingKindPopUp.selectItem(at: 0)
            meetingIDField.stringValue = identifier
        case .instantMeeting:
            meetingKindPopUp.selectItem(at: 1)
            meetingIDField.stringValue = ""
        }
        meetingNameField.stringValue = schedule.meeting.name

        if let profileIndex = configuration.accountProfiles.firstIndex(where: { $0.id == schedule.accountProfileID }) {
            accountPopUp.selectItem(at: profileIndex)
        }
        if let groupID = schedule.attendanceGroupID,
           let groupIndex = configuration.studentGroups.firstIndex(where: { $0.id == groupID }) {
            attendanceGroupPopUp.selectItem(at: groupIndex + 1)
        } else {
            attendanceGroupPopUp.selectItem(at: 0)
        }
        updateChrome()
    }

    /// Reads the form back into the model without touching disk.
    private func storeSelectedSchedule() {
        guard let loadedScheduleID, let index = configuration.schedules.firstIndex(where: { $0.id == loadedScheduleID }) else { return }
        var schedule = configuration.schedules[index]

        schedule.name = nameField.stringValue
        schedule.isEnabled = enabledButton.state == .on
        schedule.enablesAutoAdmit = autoAdmitButton.state == .on
        schedule.mutesMicrophoneBeforeJoining = muteMicrophoneButton.state == .on
        schedule.disablesCameraBeforeJoining = disableCameraButton.state == .on
        schedule.launchZoomMinutesEarly = Int(launchEarlyField.stringValue) ?? 2
        schedule.startTime = timeOfDay(from: startTimePicker.dateValue)
        schedule.endTime = usesEndTimeButton.state == .on ? timeOfDay(from: endTimePicker.dateValue) : nil

        switch recurrencePopUp.indexOfSelectedItem {
        case 0:
            let components = Calendar.current.dateComponents(
                [.year, .month, .day],
                from: oneTimeDatePicker.dateValue
            )
            schedule.recurrence = .oneTime(
                year: components.year ?? 2026,
                month: components.month ?? 1,
                day: components.day ?? 1
            )
        case 1:
            schedule.recurrence = .daily
        default:
            schedule.recurrence = .selectedWeekdays(currentWeekdays())
        }

        schedule.meeting = MeetingReference(
            name: meetingNameField.stringValue,
            kind: meetingKindPopUp.indexOfSelectedItem == 0
                ? .meetingID(meetingIDField.stringValue)
                : .instantMeeting,
            // A pasted invite link often carries the passcode with it.
            passcode: MeetingReference.passcode(from: meetingIDField.stringValue)
        )

        let profileIndex = accountPopUp.indexOfSelectedItem
        if configuration.accountProfiles.indices.contains(profileIndex) {
            schedule.accountProfileID = configuration.accountProfiles[profileIndex].id
        }

        // Index 0 is "None"; the rest map onto the groups in order.
        let groupIndex = attendanceGroupPopUp.indexOfSelectedItem - 1
        schedule.attendanceGroupID = configuration.studentGroups.indices.contains(groupIndex)
            ? configuration.studentGroups[groupIndex].id
            : nil

        configuration.schedules[index] = schedule
    }

    /// "None" first, so a schedule without attendance stays the default.
    private func rebuildAttendanceGroupPopUp() {
        attendanceGroupPopUp.removeAllItems()
        attendanceGroupPopUp.menu?.addItem(withTitle: "None — don't record attendance", action: nil, keyEquivalent: "")
        for group in configuration.studentGroups {
            attendanceGroupPopUp.menu?.addItem(withTitle: "\(group.name) (\(group.students.count) students)", action: nil, keyEquivalent: "")
        }
        reselectScheduleMenus()
    }

    private func loadSelectedProfile() {
        guard let index = selectedProfileIndex else {
            loadedProfileID = nil
            return
        }
        let profile = configuration.accountProfiles[index]
        loadedProfileID = profile.id
        profileNameField.stringValue = profile.name
        selectAccount(profile.accountIdentifier)
        profileEnginePopUp.selectItem(at: ZoomEnginePreference.allCases.firstIndex(of: profile.preferredEngine) ?? 0)
        profileWebProfileField.stringValue = profile.webProfileName ?? ""
        updateChrome()
    }

    private func storeSelectedProfile() {
        guard let loadedProfileID, let index = configuration.accountProfiles.firstIndex(where: { $0.id == loadedProfileID }) else { return }
        configuration.accountProfiles[index].name = profileNameField.stringValue
        configuration.accountProfiles[index].accountIdentifier = currentProfileAccountIdentifier()
        let engines = ZoomEnginePreference.allCases
        if engines.indices.contains(profileEnginePopUp.indexOfSelectedItem) {
            configuration.accountProfiles[index].preferredEngine = engines[profileEnginePopUp.indexOfSelectedItem]
        }
        let webProfile = profileWebProfileField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        configuration.accountProfiles[index].webProfileName = webProfile.isEmpty ? nil : webProfile
    }

    /// The single source of truth for the stored email, whether it came from the
    /// detected-accounts popup or from manual entry. The previous version kept
    /// these as two independent fields, so choosing a detected account left the
    /// stored value empty and profiles were saved as `personal <>`.
    private func currentProfileAccountIdentifier() -> String {
        let title = profileAccountPopUp.titleOfSelectedItem ?? ""
        if title == Self.manualEntryTitle || title.isEmpty {
            return profileManualField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        }
        return title.replacingOccurrences(of: "● ", with: "")
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private func selectAccount(_ identifier: String) {
        let trimmed = identifier.trimmingCharacters(in: .whitespacesAndNewlines)
        rebuildProfileAccountPopUp(selecting: trimmed)
        let isDetected = detectedAccounts.contains { $0.caseInsensitiveCompare(trimmed) == .orderedSame }
        profileManualField.stringValue = isDetected ? "" : trimmed
        profileManualField.isHidden = isDetected && !trimmed.isEmpty
    }

    private func rebuildProfileAccountPopUp(selecting identifier: String) {
        profileAccountPopUp.removeAllItems()
        for account in detectedAccounts {
            profileAccountPopUp.addItem(withTitle: account)
        }
        if !identifier.isEmpty,
           !detectedAccounts.contains(where: { $0.caseInsensitiveCompare(identifier) == .orderedSame }) {
            profileAccountPopUp.addItem(withTitle: identifier)
        }
        profileAccountPopUp.addItem(withTitle: Self.manualEntryTitle)

        if identifier.isEmpty {
            profileAccountPopUp.selectItem(withTitle: Self.manualEntryTitle)
        } else {
            profileAccountPopUp.selectItem(withTitle: identifier)
            if profileAccountPopUp.indexOfSelectedItem < 0 {
                profileAccountPopUp.selectItem(withTitle: Self.manualEntryTitle)
            }
        }
    }

    private func refreshDetectedAccounts(announce: Bool) {
        guard let snapshot = LiveZoomAutomation().readAccountMenu(), !snapshot.entries.isEmpty else {
            detectedAccounts = []
            profileHintLabel.stringValue = ZoomAXSupport.zoomApplication() == nil
                ? "Zoom isn't open. Open Zoom so its saved accounts can be read."
                : "Zoom's account list couldn't be read."
            if announce { presentZoomNotRunningIfNeeded() }
            rebuildProfileAccountPopUp(selecting: currentProfileAccountIdentifier())
            return
        }

        detectedAccounts = snapshot.entries.compactMap { $0.email ?? nil }
        let active = snapshot.activeAccount?.email
        profileHintLabel.stringValue = active.map { "Signed in to Zoom right now: \($0)" }
            ?? "\(detectedAccounts.count) accounts found in Zoom."
        rebuildProfileAccountPopUp(selecting: currentProfileAccountIdentifier())
    }

    private func presentZoomNotRunningIfNeeded() {
        guard ZoomAXSupport.zoomApplication() == nil else { return }
        let alert = NSAlert()
        alert.messageText = "Zoom isn't open"
        alert.informativeText = "Open Zoom first so Zoom Auto Admit can read your saved accounts."
        alert.addButton(withTitle: "Open Zoom")
        alert.addButton(withTitle: "Cancel")
        if alert.runModal() == .alertFirstButtonReturn {
            _ = LiveZoomAutomation().launchZoom()
        }
    }

    // MARK: Validation and chrome

    private func updateChrome() {
        storeSelectedSchedule()
        storeSelectedProfile()
        storeSelectedGroup()

        let scheduleIssues = selectedScheduleIndex.map {
            ScheduleValidation.validate(configuration.schedules[$0], in: configuration)
        } ?? []
        show(scheduleIssues, .name, on: nameError)
        show(scheduleIssues, .weekdays, on: weekdayError)
        show(scheduleIssues, .account, on: accountError)
        show(scheduleIssues, .meetingName, on: meetingNameError)
        show(scheduleIssues, .meetingID, on: meetingIDError)

        let profileIssues = selectedProfileIndex.map {
            ScheduleValidation.validate(configuration.accountProfiles[$0])
        } ?? []
        show(profileIssues, .name, on: profileNameError)
        show(profileIssues, .accountIdentifier, on: profileAccountError)

        let groupNameMissing = selectedGroupIndex.map {
            configuration.studentGroups[$0].name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        } ?? false
        groupNameError.stringValue = groupNameMissing ? "⚠ Enter a name" : ""
        groupNameError.isHidden = !groupNameMissing

        let hasGroups = !configuration.studentGroups.isEmpty
        groupEmptyState.isHidden = hasGroups
        groupFormContainer.isHidden = !hasGroups

        let everythingValid = ScheduleValidation.isValid(configuration)
            && !configuration.studentGroups.contains {
                $0.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            }
        saveButton.isEnabled = isDirty && everythingValid
        profileSaveButton.isEnabled = isDirty && everythingValid
        runNowButton.isEnabled = selectedScheduleIndex != nil
            && scheduleIssues.isEmpty
            && !coordinator.isWorkflowActive

        updateMeetingIDHint()

        let isOneTime = recurrencePopUp.indexOfSelectedItem == 0
        let isWeekdays = recurrencePopUp.indexOfSelectedItem == 2
        oneTimeDatePicker.isEnabled = isOneTime
        weekdayButtons.forEach { $0.isEnabled = isWeekdays }
        meetingIDField.isEnabled = meetingKindPopUp.indexOfSelectedItem == 0
        endTimePicker.isEnabled = usesEndTimeButton.state == .on

        let hasSchedules = !configuration.schedules.isEmpty
        scheduleEmptyState.isHidden = hasSchedules
        scheduleFormContainer.isHidden = !hasSchedules
        let hasProfiles = !configuration.accountProfiles.isEmpty
        profileEmptyState.isHidden = hasProfiles
        profileFormContainer.isHidden = !hasProfiles
    }

    private func show<Field: Equatable>(
        _ issues: [ValidationIssue<Field>],
        _ field: Field,
        on label: NSTextField
    ) {
        if let issue = issues.first(where: { $0.field == field }) {
            label.stringValue = "⚠ \(issue.message)"
            label.isHidden = false
        } else {
            label.stringValue = ""
            label.isHidden = true
        }
    }

    private func updateMeetingIDHint() {
        guard meetingKindPopUp.indexOfSelectedItem == 0 else {
            meetingIDHint.stringValue = "Zoom's own Start meeting command is used."
            return
        }
        let raw = meetingIDField.stringValue
        let digits = MeetingReference.normalizedMeetingID(raw)
        guard !digits.isEmpty else {
            meetingIDHint.stringValue = ""
            return
        }
        var text = "Will open meeting \(MeetingReference.groupedMeetingID(digits))"
        if MeetingReference.passcode(from: raw) != nil {
            text += " with its passcode"
        }
        meetingIDHint.stringValue = text
    }

    private func markDirty() {
        isDirty = true
        updateChrome()
    }

    // MARK: Actions

    @objc private func save() {
        storeSelectedSchedule()
        storeSelectedProfile()
        storeSelectedGroup()

        guard ScheduleValidation.isValid(configuration) else {
            updateChrome()
            NSSound.beep()
            return
        }

        coordinator.update(configuration: configuration)
        isDirty = false
        scheduleTable.reloadData()
        profileTable.reloadData()
        groupTable.reloadData()
        rebuildAccountPopUp()
        rebuildAttendanceGroupPopUp()
        updateChrome()
        showSavedFeedback()
    }

    /// Subtle inline confirmation rather than a modal alert.
    private func showSavedFeedback() {
        savedFeedbackWorkItem?.cancel()
        for button in [saveButton, profileSaveButton] {
            button.title = "✓ Saved"
            button.isEnabled = false
        }
        let work = DispatchWorkItem { [weak self] in
            guard let self else { return }
            for button in [self.saveButton, self.profileSaveButton] {
                button.title = "Save"
            }
            self.updateChrome()
        }
        savedFeedbackWorkItem = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.8, execute: work)
    }

    @objc private func addSchedule() {
        storeSelectedSchedule()
        let schedule = ZoomSchedule(
            name: "New schedule",
            recurrence: .selectedWeekdays([.saturday]),
            startTime: TimeOfDay(hour: 18, minute: 0),
            accountProfileID: configuration.accountProfiles.first?.id ?? UUID(),
            meeting: MeetingReference(name: "New meeting", kind: .meetingID("")),
            enablesAutoAdmit: SchedulerDefaults.enablesAutoAdmit,
            mutesMicrophoneBeforeJoining: SchedulerDefaults.mutesMicrophone,
            disablesCameraBeforeJoining: SchedulerDefaults.disablesCamera
        )
        configuration.schedules.append(schedule)
        scheduleTable.reloadData()
        scheduleTable.selectRowIndexes([configuration.schedules.count - 1], byExtendingSelection: false)
        loadSelectedSchedule()
        markDirty()
        window?.makeFirstResponder(nameField)
    }

    @objc private func removeSchedule() {
        guard let index = selectedScheduleIndex else { return }
        let schedule = configuration.schedules[index]

        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = "Delete “\(schedule.name)”?"
        alert.informativeText = "This schedule will no longer run."
        alert.addButton(withTitle: "Cancel")
        alert.addButton(withTitle: "Delete")
        guard alert.runModal() == .alertSecondButtonReturn else { return }

        configuration.schedules.remove(at: index)
        scheduleTable.reloadData()
        loadSelectedSchedule()
        markDirty()
    }

    @objc private func runSelectedScheduleNow() {
        storeSelectedSchedule()
        guard let index = selectedScheduleIndex else { return }
        let schedule = configuration.schedules[index]
        guard ScheduleValidation.isValid(schedule, in: configuration) else {
            updateChrome()
            NSSound.beep()
            return
        }

        if isDirty { save() }
        // Disabled immediately so it cannot be double-pressed while the
        // workflow spins up.
        runNowButton.isEnabled = false
        if !coordinator.runNow(schedule) {
            updateChrome()
        }
    }

    @objc private func addProfile() {
        storeSelectedProfile()
        configuration.accountProfiles.append(
            ZoomAccountProfile(name: "New profile", accountIdentifier: "")
        )
        profileTable.reloadData()
        profileTable.selectRowIndexes([configuration.accountProfiles.count - 1], byExtendingSelection: false)
        refreshDetectedAccounts(announce: false)
        loadSelectedProfile()
        markDirty()
        window?.makeFirstResponder(profileNameField)
    }

    @objc private func addProfileFromZoom() {
        refreshDetectedAccounts(announce: true)
        guard let first = detectedAccounts.first else { return }
        configuration.accountProfiles.append(
            ZoomAccountProfile(name: suggestedProfileName(for: first), accountIdentifier: first)
        )
        profileTable.reloadData()
        profileTable.selectRowIndexes([configuration.accountProfiles.count - 1], byExtendingSelection: false)
        loadSelectedProfile()
        markDirty()
    }

    private func suggestedProfileName(for email: String) -> String {
        String(email.split(separator: "@").first ?? "Zoom account").capitalized
    }

    @objc private func removeProfile() {
        guard let index = selectedProfileIndex else { return }
        let profile = configuration.accountProfiles[index]
        let usedBy = configuration.schedules.filter { $0.accountProfileID == profile.id }

        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = "Delete “\(profile.name)”?"
        alert.informativeText = usedBy.isEmpty
            ? "This account profile will be removed."
            : "\(usedBy.count) schedule(s) use this account and will need a new one."
        alert.addButton(withTitle: "Cancel")
        alert.addButton(withTitle: "Delete")
        guard alert.runModal() == .alertSecondButtonReturn else { return }

        configuration.accountProfiles.remove(at: index)
        profileTable.reloadData()
        rebuildAccountPopUp()
        loadSelectedProfile()
        markDirty()
    }

    @objc private func refreshAccountsPressed() {
        refreshDetectedAccounts(announce: true)
        updateChrome()
    }

    @objc private func profileAccountChanged() {
        let isManual = profileAccountPopUp.titleOfSelectedItem == Self.manualEntryTitle
        profileManualField.isHidden = !isManual
        if isManual { window?.makeFirstResponder(profileManualField) }
        markDirty()
    }

    // MARK: Excel imports and Zoom Web

    @objc private func openWebProfile() {
        guard let index = selectedProfileIndex, let automation else { return }
        storeSelectedProfile()
        let profile = configuration.accountProfiles[index]
        automation.openBrowserProfile(profile.resolvedWebProfileName, url: "https://zoom.us/signin")
    }

    /// Reads an .xlsx through the helper off the main thread, then hands the result back here.
    private func readWorkbook(command: String, completion: @escaping (AutomationResult) -> Void) {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.init(filenameExtension: "xlsx")].compactMap { $0 }
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let url = panel.url else { return }
        guard let automation else { return }
        DispatchQueue.global(qos: .userInitiated).async {
            let result = automation.readWorkbook(command: command, path: url.path)
            DispatchQueue.main.async { completion(result) }
        }
    }

    @objc private func importRosterWorkbook() {
        guard let index = selectedGroupIndex else { return }
        let groupID = configuration.studentGroups[index].id
        readWorkbook(command: "read-roster") { [weak self] result in
            guard let self, let index = self.configuration.studentGroups.firstIndex(where: { $0.id == groupID }) else { return }
            guard result.success else {
                self.showAlert("The roster could not be read", result.message)
                return
            }
            let rows = WorkbookRosterRow.rows(from: result)
            let merged = WorkbookRosterRow.merge(rows, into: self.configuration.studentGroups[index].students)
            self.configuration.studentGroups[index].students = merged.students
            self.loadSelectedGroup()
            self.markDirty()
            var detail = "\(merged.added) added in the sheet's order."
            if !merged.skipped.isEmpty { detail += "\n\(merged.skipped.count) already on the roster were skipped." }
            let problems = result.body["problems"]?.array?.compactMap(\.string) ?? []
            if !problems.isEmpty { detail += "\n" + problems.prefix(5).joined(separator: "\n") }
            self.showAlert("Roster imported", detail)
        }
    }

    @objc private func importTimetable() {
        guard !configuration.accountProfiles.isEmpty else {
            showAlert("Add a Zoom account first", "Imported sessions need an account and a meeting link.")
            return
        }
        readWorkbook(command: "read-timetable") { [weak self] result in
            guard let self else { return }
            guard let timetable = Timetable(result: result) else {
                self.showAlert("The timetable could not be read", result.message)
                return
            }
            self.presentTimetableImport(timetable)
        }
    }

    private func presentTimetableImport(_ timetable: Timetable) {
        let accountPopUp = NSPopUpButton()
        accountPopUp.addItems(withTitles: configuration.accountProfiles.map { "\($0.name) — \($0.accountIdentifier)" })
        let groupPopUp = NSPopUpButton()
        groupPopUp.addItem(withTitle: "None — don't record attendance")
        groupPopUp.addItems(withTitles: configuration.studentGroups.map(\.name))
        // The Round Code names the group; preselect a group whose code or name is exactly that.
        if let match = configuration.studentGroups.firstIndex(where: {
            $0.dashboardGroupName.caseInsensitiveCompare(timetable.groupCode) == .orderedSame
        }) {
            groupPopUp.selectItem(at: match + 1)
        }
        let linkField = NSTextField()
        linkField.placeholderString = "Meeting link or ID for these sessions"
        if let existing = configuration.schedules.first(where: { schedule in
            configuration.group(for: schedule)?.dashboardGroupName.caseInsensitiveCompare(timetable.groupCode) == .orderedSame
        }), case .meetingID(let raw) = existing.meeting.kind {
            linkField.stringValue = raw
            if let profileIndex = configuration.accountProfiles.firstIndex(where: { $0.id == existing.accountProfileID }) {
                accountPopUp.selectItem(at: profileIndex)
            }
        }
        let enableButton = NSButton(checkboxWithTitle: "Enable imported meetings (they start real meetings)", target: nil, action: nil)

        let online = timetable.rows.filter { $0.issue.isEmpty }.count
        let preview = NSTextView(frame: NSRect(x: 0, y: 0, width: 520, height: 180))
        preview.isEditable = false
        preview.font = .monospacedSystemFont(ofSize: 10, weight: .regular)
        preview.string = timetable.rows.map { row in
            let when = "\(row.date ?? "????-??-??") \(row.startTime ?? "--:--")"
            return "\(row.issue.isEmpty ? "✓" : "–") \(when)  #\(row.sessionNumber)  \(row.topic)\(row.issue.isEmpty ? "" : "   [\(row.issue)]")"
        }.joined(separator: "\n")
        let previewScroll = NSScrollView(frame: NSRect(x: 0, y: 0, width: 520, height: 180))
        previewScroll.documentView = preview
        previewScroll.hasVerticalScroller = true
        previewScroll.borderType = .bezelBorder

        let form = NSStackView(views: [
            DesignKit.row("Account", accountPopUp),
            DesignKit.row("Meeting link", linkField),
            DesignKit.row("Attendance group", groupPopUp),
            DesignKit.fullWidthRow(enableButton),
            previewScroll
        ])
        form.orientation = .vertical
        form.alignment = .leading
        form.spacing = 8
        form.frame = NSRect(x: 0, y: 0, width: 540, height: 330)
        linkField.widthAnchor.constraint(equalToConstant: 320).isActive = true
        previewScroll.widthAnchor.constraint(equalToConstant: 520).isActive = true
        previewScroll.heightAnchor.constraint(equalToConstant: 180).isActive = true

        let alert = NSAlert()
        alert.messageText = "Import \(timetable.groupCode.isEmpty ? "timetable" : timetable.groupCode)"
        alert.informativeText = "\(timetable.rows.count) rows, \(online) online. Physical sessions, No Session rows, past dates and existing meetings at the same time are skipped. Existing schedules are not changed."
        alert.accessoryView = form
        alert.addButton(withTitle: "Import")
        alert.addButton(withTitle: "Cancel")
        guard alert.runModal() == .alertFirstButtonReturn else { return }

        let raw = linkField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !MeetingReference.normalizedMeetingID(raw).isEmpty else {
            showAlert("No meeting link", "Enter the meeting link or ID the imported sessions should open.")
            return
        }
        let account = configuration.accountProfiles[max(accountPopUp.indexOfSelectedItem, 0)]
        let groupIndex = groupPopUp.indexOfSelectedItem - 1
        let group = configuration.studentGroups.indices.contains(groupIndex) ? configuration.studentGroups[groupIndex] : nil
        let meeting = MeetingReference(
            name: timetable.groupCode.isEmpty ? (group?.name ?? "Class") : timetable.groupCode,
            kind: .meetingID(raw),
            passcode: MeetingReference.passcode(from: raw)
        )
        let plan = TimetableImporter.plan(
            timetable: timetable,
            account: account,
            meeting: meeting,
            attendanceGroupID: group?.id,
            existing: configuration.schedules,
            enable: enableButton.state == .on,
            template: selectedScheduleIndex.map { configuration.schedules[$0] }
        )
        storeSelectedSchedule()
        configuration.schedules.append(contentsOf: plan.schedules)
        scheduleTable.reloadData()
        markDirty()

        var reasons: [String: Int] = [:]
        for decision in plan.skipped { reasons[decision.skipReason ?? "", default: 0] += 1 }
        let skippedText = reasons.sorted { $0.key < $1.key }.map { "\($0.value) × \($0.key)" }.joined(separator: "\n")
        showAlert(
            "\(plan.schedules.count) session(s) added",
            (plan.schedules.isEmpty ? "" : "Press Save to keep them\(enableButton.state == .on ? "" : " (they are disabled until you enable them)").\n") + skippedText
        )
    }

    private func showAlert(_ title: String, _ detail: String) {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = detail
        alert.runModal()
    }

    @objc private func recurrenceChanged() { markDirty() }
    @objc private func meetingKindChanged() { markDirty() }
    @objc private func fieldChanged() { markDirty() }

    // MARK: Small builders

    private static func errorLabel() -> NSTextField {
        DesignKit.errorLabel()
    }

    private func section(_ title: String, rows: [NSView]) -> NSView {
        DesignKit.section(title, rows: rows)
    }

    private func labelled(
        _ title: String,
        _ control: NSView,
        suffix: String? = nil,
        error: NSTextField? = nil
    ) -> NSView {
        // An empty title means the control spans the row, indented to the same
        // axis as every other control.
        guard !title.isEmpty else {
            guard let error else { return DesignKit.fullWidthRow(control) }
            let stack = NSStackView(views: [DesignKit.fullWidthRow(control), DesignKit.fullWidthRow(error)])
            stack.orientation = .vertical
            stack.alignment = .leading
            stack.spacing = 4
            return stack
        }
        return DesignKit.row(title, control, suffix: suffix, error: error)
    }

    private func horizontal(_ views: [NSView]) -> NSStackView {
        DesignKit.horizontal(views)
    }

    private func caption(_ text: String) -> NSTextField {
        DesignKit.caption(text)
    }

    private func buildEmptyState(
        _ stack: NSStackView,
        title: String,
        message: String,
        buttonTitle: String,
        action: Selector,
        symbol: String = "tray"
    ) {
        let icon = NSImageView()
        icon.image = NSImage(systemSymbolName: symbol, accessibilityDescription: nil)
        icon.symbolConfiguration = NSImage.SymbolConfiguration(pointSize: 34, weight: .light)
        icon.contentTintColor = .tertiaryLabelColor

        let titleLabel = NSTextField(labelWithString: title)
        titleLabel.font = .systemFont(ofSize: 15, weight: .semibold)
        titleLabel.alignment = .center

        let messageLabel = NSTextField(wrappingLabelWithString: message)
        messageLabel.textColor = .secondaryLabelColor
        messageLabel.alignment = .center
        messageLabel.preferredMaxLayoutWidth = 320

        let button = NSButton(title: buttonTitle, target: self, action: action)
        button.bezelStyle = .rounded
        button.controlSize = .large

        stack.orientation = .vertical
        stack.alignment = .centerX
        stack.spacing = 12
        [icon, titleLabel, messageLabel, button].forEach { stack.addArrangedSubview($0) }
        stack.setCustomSpacing(16, after: icon)
    }

    private func iconButton(_ symbol: String, action: Selector, tooltip: String) -> NSButton {
        let button = NSButton(
            image: NSImage(systemSymbolName: symbol, accessibilityDescription: tooltip)
                ?? NSImage(size: NSSize(width: 12, height: 12)),
            target: self,
            action: action
        )
        button.bezelStyle = .smallSquare
        button.toolTip = tooltip
        return button
    }

    private func currentWeekdays() -> Set<Weekday> {
        var days: Set<Weekday> = []
        for (index, button) in weekdayButtons.enumerated() where button.state == .on {
            if let weekday = Weekday(rawValue: index + 1) { days.insert(weekday) }
        }
        return days
    }

    private func setWeekdays(_ days: Set<Weekday>) {
        for (index, button) in weekdayButtons.enumerated() {
            let weekday = Weekday(rawValue: index + 1)
            button.state = weekday.map(days.contains) == true ? .on : .off
        }
    }

    private func date(from time: TimeOfDay) -> Date {
        Calendar.current.date(
            from: DateComponents(year: 2000, month: 1, day: 1, hour: time.hour, minute: time.minute)
        ) ?? Date()
    }

    private func timeOfDay(from date: Date) -> TimeOfDay {
        let components = Calendar.current.dateComponents([.hour, .minute], from: date)
        return TimeOfDay(hour: components.hour ?? 0, minute: components.minute ?? 0)
    }

    private func integerFormatter() -> NumberFormatter {
        let formatter = NumberFormatter()
        formatter.allowsFloats = false
        formatter.minimum = 0
        formatter.maximum = 60
        return formatter
    }

    private func configure(datePicker: NSDatePicker, elements: NSDatePicker.ElementFlags) {
        datePicker.datePickerStyle = .textFieldAndStepper
        datePicker.datePickerElements = elements
        datePicker.dateValue = Date()
    }

    private func configure(table: NSTableView, columnTitle: String) {
        let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("main"))
        column.title = columnTitle
        column.width = 240
        table.addTableColumn(column)
        table.headerView = nil
        table.style = .inset
        table.usesAlternatingRowBackgroundColors = false
    }

    /// Lists are presented as a sidebar, which is what a list-plus-detail
    /// window looks like everywhere else on the system.
    private func scrollView(for table: NSTableView) -> NSView {
        let scroll = NSScrollView()
        scroll.documentView = table
        scroll.hasVerticalScroller = true
        scroll.drawsBackground = false
        scroll.borderType = .noBorder
        scroll.automaticallyAdjustsContentInsets = false
        scroll.contentInsets = NSEdgeInsets(top: 6, left: 0, bottom: 6, right: 0)
        scroll.translatesAutoresizingMaskIntoConstraints = false

        let backdrop = NSVisualEffectView()
        backdrop.material = .sidebar
        backdrop.blendingMode = .behindWindow
        backdrop.state = .followsWindowActiveState
        backdrop.wantsLayer = true
        backdrop.layer?.cornerRadius = 8
        backdrop.addSubview(scroll)

        NSLayoutConstraint.activate([
            scroll.leadingAnchor.constraint(equalTo: backdrop.leadingAnchor),
            scroll.trailingAnchor.constraint(equalTo: backdrop.trailingAnchor),
            scroll.topAnchor.constraint(equalTo: backdrop.topAnchor),
            scroll.bottomAnchor.constraint(equalTo: backdrop.bottomAnchor)
        ])
        return backdrop
    }
}

// MARK: - Table data

extension SchedulerWindowController: NSTableViewDataSource, NSTableViewDelegate {
    func numberOfRows(in tableView: NSTableView) -> Int {
        if tableView === rosterTable { return visibleRoster.count }
        if tableView === scheduleTable { return configuration.schedules.count }
        if tableView === groupTable { return configuration.studentGroups.count }
        return configuration.accountProfiles.count
    }

    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        if tableView === rosterTable {
            return rosterCell(column: tableColumn?.identifier.rawValue ?? "", row: row)
        }
        if tableView === scheduleTable {
            guard configuration.schedules.indices.contains(row) else { return nil }
            let schedule = configuration.schedules[row]
            let account = configuration.profile(for: schedule)?.name ?? "No account"
            let level: StatusLevel = schedule.isEnabled ? .normal : .neutral
            return card(
                title: schedule.name,
                subtitle: "\(schedule.recurrence.displayText) · \(schedule.startTime.displayText)",
                detail: "\(account) · \(schedule.meeting.summaryText)",
                badge: badgeText(for: schedule),
                dotColor: level.color
            )
        }

        if tableView === groupTable {
            guard configuration.studentGroups.indices.contains(row) else { return nil }
            let group = configuration.studentGroups[row]
            let linked = configuration.schedules.filter { $0.attendanceGroupID == group.id }.count
            return card(
                title: group.name,
                subtitle: "\(group.students.count) student(s)",
                detail: linked == 0 ? "Not linked to a schedule" : "\(linked) schedule(s)",
                badge: nil,
                // Amber when nothing will ever record into it.
                dotColor: group.students.isEmpty || linked == 0
                    ? StatusLevel.warning.color
                    : StatusLevel.normal.color
            )
        }

        guard configuration.accountProfiles.indices.contains(row) else { return nil }
        let profile = configuration.accountProfiles[row]
        let valid = ScheduleValidation.isValid(profile)
        return card(
            title: profile.name,
            subtitle: profile.accountIdentifier.isEmpty ? "No account selected" : profile.accountIdentifier,
            detail: nil,
            badge: nil,
            dotColor: valid ? StatusLevel.normal.color : StatusLevel.error.color
        )
    }

    /// Row height follows the number of lines the row actually shows.
    ///
    /// These were fixed constants, guessed per table, and the group rows were
    /// given 44pt for three lines of content — which clipped their titles in
    /// half. Deriving it from the line count keeps every list honest.
    func tableView(_ tableView: NSTableView, heightOfRow row: Int) -> CGFloat {
        if tableView === rosterTable { return 22 }
        let lines: Int
        if tableView === scheduleTable {
            lines = 4          // name · schedule · account+meeting · auto admit
        } else if tableView === groupTable {
            lines = 3          // name · student count · linked schedules
        } else {
            lines = 2          // profile name · zoom account
        }
        let titleLine = ceil(NSFont.systemFont(ofSize: NSFont.systemFontSize).boundingRectForFont.height)
        let detailLine = ceil(NSFont.systemFont(ofSize: NSFont.smallSystemFontSize).boundingRectForFont.height)
        return 14 + titleLine + CGFloat(lines - 1) * (detailLine + 3)
    }

    private func card(
        title: String,
        subtitle: String,
        detail: String?,
        badge: String?,
        dotColor: NSColor
    ) -> NSView {
        let dot = NSTextField(labelWithString: "●")
        dot.textColor = dotColor
        dot.font = .systemFont(ofSize: 9)
        dot.setContentHuggingPriority(.required, for: .horizontal)

        let titleLabel = rowLabel(title, size: NSFont.systemFontSize, weight: .semibold, color: .labelColor)

        let titleRow = NSStackView(views: [dot, titleLabel])
        titleRow.orientation = .horizontal
        titleRow.alignment = .centerY
        titleRow.spacing = 6

        var views: [NSView] = [
            titleRow,
            rowLabel(subtitle, size: NSFont.smallSystemFontSize, weight: .regular, color: .secondaryLabelColor)
        ]
        if let detail {
            views.append(
                rowLabel(detail, size: NSFont.smallSystemFontSize, weight: .regular, color: .secondaryLabelColor)
            )
        }
        if let badge {
            views.append(
                rowLabel(badge, size: NSFont.smallSystemFontSize, weight: .regular, color: .tertiaryLabelColor)
            )
        }

        let stack = NSStackView(views: views)
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 3
        stack.edgeInsets = NSEdgeInsets(top: 7, left: 6, bottom: 7, right: 6)
        return stack
    }

    /// Row labels must shrink and truncate rather than push the row wider,
    /// otherwise long meeting names get clipped mid-word.
    private func rowLabel(
        _ text: String,
        size: CGFloat,
        weight: NSFont.Weight,
        color: NSColor
    ) -> NSTextField {
        let label = NSTextField(labelWithString: text)
        label.font = .systemFont(ofSize: size, weight: weight)
        label.textColor = color
        label.lineBreakMode = .byTruncatingTail
        label.cell?.truncatesLastVisibleLine = true
        label.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        label.setContentHuggingPriority(.defaultLow, for: .horizontal)
        return label
    }

    private func badgeText(for schedule: ZoomSchedule) -> String {
        var parts = [schedule.enablesAutoAdmit ? "Auto Admit On" : "Auto Admit Off"]
        if schedule.mutesMicrophoneBeforeJoining { parts.append("Mic off") }
        if schedule.disablesCameraBeforeJoining { parts.append("Camera off") }
        if !schedule.isEnabled { parts.append("Disabled") }
        return parts.joined(separator: " · ")
    }

    func tableViewSelectionDidChange(_ notification: Notification) {
        guard let table = notification.object as? NSTableView else { return }
        // Save the form into the item it was showing, then show the newly selected one.
        if table === scheduleTable {
            storeSelectedSchedule()
            loadSelectedSchedule()
        } else if table === groupTable {
            storeSelectedGroup()
            loadSelectedGroup()
        } else if table === profileTable {
            storeSelectedProfile()
            loadSelectedProfile()
        }
    }
}

extension SchedulerWindowController: NSTextFieldDelegate {
    func controlTextDidChange(_ notification: Notification) {
        markDirty()
    }
}
