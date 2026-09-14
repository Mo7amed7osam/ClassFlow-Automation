import AppKit
import XCTest
@testable import ZoomAutoAdmitApp
import ZoomAutoAdmitCore

/// The Groups tab once copied the group being edited over the group that was clicked, so two
/// groups ended up with one name and switching looked like it did nothing. These tests drive the
/// real window.
final class GroupsEditorTests: XCTestCase {
    private var directory: URL!

    override func setUp() {
        directory = FileManager.default.temporaryDirectory.appendingPathComponent("groups-editor-\(UUID().uuidString)")
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    }

    override func tearDown() {
        try? FileManager.default.removeItem(at: directory)
    }

    private func makeWindow(groups: [StudentGroup], accounts: [ZoomAccountProfile] = [], schedules: [ZoomSchedule] = []) -> (SchedulerWindowController, SchedulerCoordinator) {
        let store = ScheduleStore(fileURL: directory.appendingPathComponent("schedules.json"))
        XCTAssertTrue(store.save(SchedulerConfiguration(accountProfiles: accounts, schedules: schedules, studentGroups: groups)))
        let coordinator = SchedulerCoordinator(
            state: AppState(monitoringEnabled: false),
            store: store,
            schedulerLog: SchedulerLog(fileURL: directory.appendingPathComponent("scheduler.log")),
            startAutoAdmit: {},
            stopAutoAdmit: {},
            startAttendance: { _, _ in },
            stopAttendance: { _ in }
        )
        return (SchedulerWindowController(coordinator: coordinator, state: AppState(monitoringEnabled: false)), coordinator)
    }

    private func find<T: NSView>(_ identifier: String, in view: NSView?, as type: T.Type = T.self) -> T? {
        guard let view else { return nil }
        if view.identifier?.rawValue == identifier, let match = view as? T { return match }
        var children = view.subviews
        // A tab view only installs its visible tab; the other tabs' views hang off their items.
        if let tabs = view as? NSTabView { children += tabs.tabViewItems.compactMap(\.view) }
        if let scroll = view as? NSScrollView, let document = scroll.documentView { children.append(document) }
        for child in children {
            if let found = find(identifier, in: child, as: type) { return found }
        }
        return nil
    }

    func testSwitchingGroupsShowsEachGroupAndNeverCopiesOneOverAnother() throws {
        let g1 = StudentGroup(name: "CAI5_IND1_G1", students: (1...25).map { Student(officialName: "G1 Student \($0)") }, lmsGroupCode: "CAI5_IND1_G1")
        let g2 = StudentGroup(name: "CAI5_IND1_G2", students: (1...20).map { Student(officialName: "G2 Student \($0)") }, lmsGroupCode: "CAI5_IND1_G2")
        let (controller, coordinator) = makeWindow(groups: [g1, g2])
        let content = controller.window?.contentView
        let groups = try XCTUnwrap(find("groupsTable", in: content, as: NSTableView.self))
        let nameField = try XCTUnwrap(find("groupNameField", in: content, as: NSTextField.self))
        let codeField = try XCTUnwrap(find("groupLmsCodeField", in: content, as: NSTextField.self))
        let roster = try XCTUnwrap(find("groupRosterTable", in: content, as: NSTableView.self))

        groups.selectRowIndexes([0], byExtendingSelection: false)
        XCTAssertEqual(nameField.stringValue, "CAI5_IND1_G1")
        XCTAssertEqual(roster.numberOfRows, 25, "every student is listed, not just a count")

        // Edit G1's form, then click G2: the edit belongs to G1 only.
        nameField.stringValue = "CAI5_IND1_G1 edited"
        groups.selectRowIndexes([1], byExtendingSelection: false)
        XCTAssertEqual(nameField.stringValue, "CAI5_IND1_G2", "the form now shows the clicked group")
        XCTAssertEqual(codeField.stringValue, "CAI5_IND1_G2")
        XCTAssertEqual(roster.numberOfRows, 20)
        let studentCell = try XCTUnwrap(roster.view(atColumn: 1, row: 0, makeIfNecessary: true) as? NSTextField)
        XCTAssertEqual(studentCell.stringValue, "G2 Student 1")

        // Back and forth a few times, then save: both groups keep their own data.
        groups.selectRowIndexes([0], byExtendingSelection: false)
        XCTAssertEqual(nameField.stringValue, "CAI5_IND1_G1 edited")
        groups.selectRowIndexes([1], byExtendingSelection: false)
        groups.selectRowIndexes([0], byExtendingSelection: false)
        controller.perform(Selector(("save")))

        let saved = coordinator.currentConfiguration.studentGroups
        XCTAssertEqual(saved.map(\.name), ["CAI5_IND1_G1 edited", "CAI5_IND1_G2"])
        XCTAssertEqual(saved.map(\.lmsGroupCode), ["CAI5_IND1_G1", "CAI5_IND1_G2"])
        XCTAssertEqual(saved.map(\.students.count), [25, 20])
        XCTAssertEqual(saved[1].students.first?.officialName, "G2 Student 1")
    }

    /// A Save rebuilt the account and group menus, which reset to their first item, and the form then
    /// wrote that into the schedule: the G1 Saturday class lost its attendance group this way.
    func testSavingNeverUnlinksAScheduleFromItsGroupOrAccount() throws {
        let a1 = ZoomAccountProfile(name: "CAI5_IND1_G1", accountIdentifier: "g1@example.com")
        let a2 = ZoomAccountProfile(name: "CAI5_IND1_G2", accountIdentifier: "g2@example.com")
        // Same names on purpose: duplicate menu titles used to vanish and shift the positions.
        let g1 = StudentGroup(name: "Same", students: [Student(officialName: "A")], lmsGroupCode: "CAI5_IND1_G1")
        let g2 = StudentGroup(name: "Same", students: [Student(officialName: "A")], lmsGroupCode: "CAI5_IND1_G2")
        func schedule(_ name: String, _ account: ZoomAccountProfile, _ group: StudentGroup) -> ZoomSchedule {
            ZoomSchedule(name: name, recurrence: .daily, startTime: TimeOfDay(hour: 17, minute: 55), accountProfileID: account.id, meeting: MeetingReference(name: "m", kind: .meetingID("94698416251")), attendanceGroupID: group.id)
        }
        let s1 = schedule("G1 class", a1, g1)
        let s2 = schedule("G2 class", a2, g2)
        let (controller, coordinator) = makeWindow(groups: [g1, g2], accounts: [a1, a2], schedules: [s1, s2])
        let content = controller.window?.contentView
        let table = try XCTUnwrap(find("schedulesTable", in: content, as: NSTableView.self))
        let groupMenu = try XCTUnwrap(find("scheduleGroupPopUp", in: content, as: NSPopUpButton.self))
        XCTAssertEqual(groupMenu.numberOfItems, 3, "None + both groups, even with equal names")

        table.selectRowIndexes([1], byExtendingSelection: false)
        XCTAssertEqual(groupMenu.indexOfSelectedItem, 2)
        for _ in 0..<3 {
            controller.perform(Selector(("fieldChanged")))
            controller.perform(Selector(("save")))
        }
        table.selectRowIndexes([0], byExtendingSelection: false)
        controller.perform(Selector(("fieldChanged")))
        controller.perform(Selector(("save")))

        let saved = coordinator.currentConfiguration.schedules
        XCTAssertEqual(saved.map(\.attendanceGroupID), [g1.id, g2.id])
        XCTAssertEqual(saved.map(\.accountProfileID), [a1.id, a2.id])
    }
}
