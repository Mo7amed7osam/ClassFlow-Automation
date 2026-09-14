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
    ///
    /// Zoom's answers to Accessibility actions are not trustworthy on their own: a press on a
    /// row's More button can report an error (-25205) and still open the menu, or do nothing
    /// until the row is hovered. So every step is judged by what Zoom shows next, not by the
    /// error code: the menu is looked for after the press whatever it returned, the row's own
    /// context menu (AXShowMenu) is tried when the More button opens nothing, and the result
    /// is read back from the participants list.
    static func makeCoHost(displayName: String, pid: pid_t, menuWait: TimeInterval = 0.6, verifyWait: TimeInterval = 1.0) -> CoHostOutcome {
        guard let located = locateParticipantCell(displayName: displayName, pid: pid) else {
            return participantsReadout(pid: pid).listAvailable ? .participantNotFound : .participantsListUnavailable
        }
        if located.row.roles.contains(.coHost) || located.row.roles.contains(.host) { return .alreadyCoHost }

        let button = firstPath(in: located.cellSnapshot, where: { isMoreOptionsButton($0, for: displayName) })
            .flatMap { resolveElement(at: $0, from: located.cellElement) }

        var lastError: AXError = .success
        var item: AXUIElement?
        // 1. The row's More options button.
        if let button {
            if actionNames(of: button).contains(pressAction) {
                lastError = press(button)
                Thread.sleep(forTimeInterval: menuWait)
                item = findMenuItem(pid: pid, titles: makeCoHostTitles)
            }
            // 2. The same button's context menu.
            if item == nil, !menuIsOpen(pid: pid), actionNames(of: button).contains(kAXShowMenuAction as String) {
                let shown = AXUIElementPerformAction(button, kAXShowMenuAction as CFString)
                if shown != .success { lastError = shown }
                Thread.sleep(forTimeInterval: menuWait)
                item = findMenuItem(pid: pid, titles: makeCoHostTitles)
            }
        }
        // 3. The row cell's own context menu, as a right-click would open it.
        if item == nil, !menuIsOpen(pid: pid), actionNames(of: located.cellElement).contains(kAXShowMenuAction as String) {
            let shown = AXUIElementPerformAction(located.cellElement, kAXShowMenuAction as CFString)
            if shown != .success { lastError = shown }
            Thread.sleep(forTimeInterval: menuWait)
            item = findMenuItem(pid: pid, titles: makeCoHostTitles)
        }

        guard let item else {
            closeMenu(pid: pid, button: button)
            if button == nil { return .moreButtonUnavailable }
            return lastError == .success ? .menuItemUnavailable : .axError(lastError)
        }
        let pressed = press(item)
        Thread.sleep(forTimeInterval: verifyWait)

        let readout = participantsReadout(pid: pid)
        let confirmed = readout.admitted.contains {
            normalized($0.displayName) == normalized(displayName) && $0.roles.contains(.coHost)
        }
        if confirmed { return .assigned }
        closeMenu(pid: pid, button: button)
        return pressed == .success ? .notConfirmed : .axError(pressed)
    }

    /// Leaves Zoom as it was found when a menu is still open.
    private static func closeMenu(pid: pid_t, button: AXUIElement?) {
        guard menuIsOpen(pid: pid) else { return }
        let application = freshZoomApplicationElement(pid: pid, messagingTimeout: 2)
        for menu in children(of: application) where copyStringAttribute(menu, kAXRoleAttribute) == "AXMenu" {
            _ = AXUIElementPerformAction(menu, kAXCancelAction as CFString)
        }
        if menuIsOpen(pid: pid), let button { _ = press(button) }
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
