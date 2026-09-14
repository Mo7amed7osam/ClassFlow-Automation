import AppKit
import ZoomAutoAdmitCore

/// "Unknown participants detected": one row per name, each with its own decision.
final class UnknownParticipantsWindowController: NSWindowController {
    enum Decision: Int, CaseIterable {
        case later, ignorePermanently, ignoreThisMeeting, addAsStudent

        var title: String {
            switch self {
            case .later: return "Decide later"
            case .ignorePermanently: return "Ignore permanently"
            case .ignoreThisMeeting: return "Ignore this meeting only"
            case .addAsStudent: return "Add as student"
            }
        }
    }

    /// Called with the names chosen for each decision when Apply is pressed.
    var onApply: ((_ permanent: [String], _ thisMeeting: [String], _ students: [String]) -> Void)?

    private let unknown: [UnknownParticipant]
    private var popUps: [NSPopUpButton] = []

    init(session: AttendanceSession, unknown: [UnknownParticipant]) {
        self.unknown = unknown
        let window = NSPanel(
            contentRect: NSRect(x: 0, y: 0, width: 620, height: 160 + CGFloat(unknown.count) * 44),
            styleMask: [.titled, .closable, .utilityWindow],
            backing: .buffered,
            defer: false
        )
        window.title = "Unknown participants detected"
        window.isFloatingPanel = true
        // Shown over Zoom without taking focus from the meeting.
        window.becomesKeyOnlyIfNeeded = true
        window.hidesOnDeactivate = false
        window.center()
        super.init(window: window)

        let heading = NSTextField(wrappingLabelWithString: "\(session.groupName): these people are in the meeting but not on the roster, and look like staff or keep showing up. Nothing is counted for them until you decide.")
        heading.preferredMaxLayoutWidth = 580

        var rows: [NSView] = [heading]
        for participant in unknown {
            let name = NSTextField(labelWithString: participant.name)
            name.font = .boldSystemFont(ofSize: NSFont.systemFontSize)
            let why = NSTextField(labelWithString: participant.reasons.map(\.description).joined(separator: " · "))
            why.font = .systemFont(ofSize: NSFont.smallSystemFontSize)
            why.textColor = .secondaryLabelColor
            let text = NSStackView(views: [name, why])
            text.orientation = .vertical
            text.alignment = .leading
            text.spacing = 2
            text.widthAnchor.constraint(equalToConstant: 360).isActive = true

            let popUp = NSPopUpButton()
            popUp.addItems(withTitles: Decision.allCases.map(\.title))
            // A staff word or host role is almost always staff; a recurring stranger is left to decide.
            let looksLikeStaff = participant.reasons.contains { if case .recurring = $0 { return false } else { return true } }
            popUp.selectItem(at: looksLikeStaff ? Decision.ignorePermanently.rawValue : Decision.later.rawValue)
            popUps.append(popUp)

            let row = NSStackView(views: [text, popUp])
            row.orientation = .horizontal
            row.spacing = 12
            rows.append(row)
        }

        let apply = NSButton(title: "Apply", target: self, action: #selector(applyPressed))
        apply.keyEquivalent = "\r"
        let cancel = NSButton(title: "Decide Later", target: self, action: #selector(cancelPressed))
        let buttons = NSStackView(views: [NSView(), cancel, apply])
        buttons.orientation = .horizontal
        rows.append(buttons)

        let stack = NSStackView(views: rows)
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 12
        stack.edgeInsets = NSEdgeInsets(top: 18, left: 20, bottom: 18, right: 20)
        stack.translatesAutoresizingMaskIntoConstraints = false
        buttons.widthAnchor.constraint(equalTo: stack.widthAnchor, constant: -40).isActive = true
        let content = NSView()
        content.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: content.trailingAnchor),
            stack.topAnchor.constraint(equalTo: content.topAnchor),
            stack.bottomAnchor.constraint(lessThanOrEqualTo: content.bottomAnchor)
        ])
        window.contentView = content
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) is not used") }

    func present() {
        window?.orderFrontRegardless()
    }

    @objc private func applyPressed() {
        var permanent: [String] = []
        var thisMeeting: [String] = []
        var students: [String] = []
        for (participant, popUp) in zip(unknown, popUps) {
            switch Decision(rawValue: popUp.indexOfSelectedItem) ?? .later {
            case .ignorePermanently: permanent.append(participant.name)
            case .ignoreThisMeeting: thisMeeting.append(participant.name)
            case .addAsStudent: students.append(participant.name)
            case .later: break
            }
        }
        onApply?(permanent, thisMeeting, students)
        close()
    }

    @objc private func cancelPressed() {
        close()
    }
}
