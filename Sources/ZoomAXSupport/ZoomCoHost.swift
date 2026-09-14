import ApplicationServices
import Foundation

/// Making a participant co-host through Zoom's own Accessibility tree.
///
/// Grounded in the live hierarchy of the participants list:
///
/// ```
/// AXOutline description="Participants list"
///   AXRow
///     AXCell identifier="ZMHCTableItemType_PANELIST"
///       AXStaticText value="Mohab Mohamed"
///       AXMenuButton description="More options for Mohab Mohamed, collapsed"   actions=[AXPress]
/// ```
///
/// The row's More button opens a menu holding "Make co-host". Nothing else in that menu is
/// ever pressed, a Waiting Room row is never eligible, and success is read back from the list
/// ("(Co-host)") rather than assumed. No coordinates, no keystrokes, no pointer.
public extension ZoomAXSupport {
    enum CoHostOutcome: Equatable {
        case assigned
        case alreadyCoHost
        case participantsListUnavailable
        case participantNotFound
        case moreButtonUnavailable
        case menuItemUnavailable
        case notConfirmed
        case axError(AXError)

        public var isSuccess: Bool { self == .assigned || self == .alreadyCoHost }

        public var message: String {
            switch self {
            case .assigned: return "made co-host"
            case .alreadyCoHost: return "already a co-host"
            case .participantsListUnavailable: return "the Participants list is not open"
            case .participantNotFound: return "not in the joined list right now"
            case .moreButtonUnavailable: return "the row's More options button is not exposed"
            case .menuItemUnavailable: return "Make co-host was not offered in the row menu"
            case .notConfirmed: return "Zoom did not confirm the change"
            case .axError(let error): return "Accessibility error \(error.rawValue)"
            }
        }
    }

    static let makeCoHostTitles: Set<String> = ["make co-host", "make cohost", "assign as co-host", "تعيين كمضيف مشارك", "جعله مضيفًا مشاركًا"]

    /// The name of the menu button a row carries: "More options for <name>, collapsed".
    static func isMoreOptionsButton(_ node: SnapshotNode, for displayName: String) -> Bool {
        guard node.role == "AXMenuButton" || node.role == "AXButton" || node.role == "AXPopUpButton" else { return false }
        let label = normalized(node.description ?? node.title ?? "")
        guard label.hasPrefix("more options for ") else { return false }
        let target = normalized(displayName)
        let rest = label.dropFirst("more options for ".count)
        return rest == target || rest.hasPrefix(target + ",")
    }

    /// Assigns co-host to the admitted participant shown as `displayName`.
    static func makeCoHost(displayName: String, pid: pid_t, menuWait: TimeInterval = 0.6, verifyWait: TimeInterval = 1.0) -> CoHostOutcome {
        guard let located = locateParticipantCell(displayName: displayName, pid: pid) else {
            return participantsReadout(pid: pid).listAvailable ? .participantNotFound : .participantsListUnavailable
        }
        if located.row.roles.contains(.coHost) || located.row.roles.contains(.host) { return .alreadyCoHost }

        guard let buttonPath = firstPath(in: located.cellSnapshot, where: { isMoreOptionsButton($0, for: displayName) }),
              let button = resolveElement(at: buttonPath, from: located.cellElement),
              actionNames(of: button).contains(pressAction) else {
            return .moreButtonUnavailable
        }

        let opened = press(button)
        guard opened == .success else { return .axError(opened) }
        Thread.sleep(forTimeInterval: menuWait)

        guard let item = findMenuItem(pid: pid, titles: makeCoHostTitles) else {
            // Leave Zoom as it was found: the same button closes the menu it opened.
            _ = AXUIElementPerformAction(button, kAXCancelAction as CFString)
            if menuIsOpen(pid: pid) { _ = press(button) }
            return .menuItemUnavailable
        }
        let pressed = press(item)
        guard pressed == .success else { return .axError(pressed) }
        Thread.sleep(forTimeInterval: verifyWait)

        let readout = participantsReadout(pid: pid)
        let confirmed = readout.admitted.contains {
            normalized($0.displayName) == normalized(displayName) && $0.roles.contains(.coHost)
        }
        return confirmed ? .assigned : .notConfirmed
    }

    private struct LocatedCell {
        let row: ParticipantRow
        let cellElement: AXUIElement
        let cellSnapshot: SnapshotNode
    }

    private static func locateParticipantCell(displayName: String, pid: pid_t) -> LocatedCell? {
        let application = freshZoomApplicationElement(pid: pid, messagingTimeout: 5)
        let windows = windowsResult(of: application)
        guard windows.error == .success else { return nil }
        let target = normalized(displayName)

        for window in windows.windows where normalized(windowTitle(window)) != "zoom workplace" {
            let tree = snapshot(from: buildTree(from: window, maxDepth: 12, maxChildren: 400, maxNodes: 20_000))
            let readout = participantsReadout(inWindow: tree)
            guard readout.listAvailable else { continue }
            // Admitted rows only: a Waiting Room entry is never made co-host.
            guard let row = readout.admitted.first(where: { normalized($0.displayName) == target }),
                  let element = resolveElement(at: row.indexPath, from: window),
                  let cell = node(at: row.indexPath, in: tree) else { continue }
            return LocatedCell(row: row, cellElement: element, cellSnapshot: cell)
        }
        return nil
    }

    private static func node(at indexPath: [Int], in root: SnapshotNode) -> SnapshotNode? {
        var current = root
        for index in indexPath {
            guard current.children.indices.contains(index) else { return nil }
            current = current.children[index]
        }
        return current
    }

    private static func firstPath(in node: SnapshotNode, path: [Int] = [], where matches: (SnapshotNode) -> Bool) -> [Int]? {
        if matches(node) { return path }
        for (index, child) in node.children.enumerated() {
            if let found = firstPath(in: child, path: path + [index], where: matches) { return found }
        }
        return nil
    }

    /// Menus Zoom opens from a row hang off the application, not the window.
    private static func findMenuItem(pid: pid_t, titles: Set<String>) -> AXUIElement? {
        let application = freshZoomApplicationElement(pid: pid, messagingTimeout: 3)
        var queue: [(AXUIElement, Int)] = children(of: application).map { ($0, 0) }
        var visited = 0
        while !queue.isEmpty, visited < 4000 {
            let (element, depth) = queue.removeFirst()
            visited += 1
            let role = copyStringAttribute(element, kAXRoleAttribute)
            if role == "AXMenuItem" {
                let title = normalized(copyStringAttribute(element, kAXTitleAttribute) ?? copyStringAttribute(element, kAXDescriptionAttribute) ?? "")
                if titles.contains(title), isEnabled(element) { return element }
            }
            // The menu bar holds hundreds of items that are never the row menu.
            if role == "AXMenuBar" || depth >= 8 { continue }
            queue.append(contentsOf: children(of: element).map { ($0, depth + 1) })
        }
        return nil
    }

    private static func menuIsOpen(pid: pid_t) -> Bool {
        let application = freshZoomApplicationElement(pid: pid, messagingTimeout: 2)
        for window in children(of: application) where copyStringAttribute(window, kAXRoleAttribute) == "AXMenu" {
            _ = window
            return true
        }
        return false
    }
}
