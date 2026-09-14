import Foundation

/// Refuses a dashboard step whose group mapping could put one class's attendance on another.
///
/// Found in real data: a group was renamed to its sibling's name, so two rosters answered to the
/// same dashboard group. Name matching alone would then write G2's students onto G1's session.
/// Every dashboard step checks the mapping first and stops when it is ambiguous or has changed.
public enum LmsGroupMapping {
    public enum Problem: Equatable {
        /// More than one roster resolves to this dashboard group.
        case sharedDashboardGroup(code: String, groupNames: [String])
        /// The follow-up was written for one dashboard group, but its register's group now maps
        /// to another (renamed or re-coded since).
        case changedSinceScheduled(expected: String, now: String)
        /// The register's group no longer exists.
        case groupMissing

        public var message: String {
            switch self {
            case .sharedDashboardGroup(let code, let names):
                return "\(names.count) groups (\(names.joined(separator: ", "))) all map to dashboard group \(code). Give each its own LMS group code before any dashboard step runs."
            case .changedSinceScheduled(let expected, let now):
                return "This step was scheduled for \(expected), but the group now maps to \(now). Nothing was sent; check the group's LMS code."
            case .groupMissing:
                return "The attendance group for this step no longer exists."
            }
        }
    }

    public static func key(_ code: String) -> String {
        code.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
    }

    /// Dashboard groups claimed by more than one roster.
    public static func sharedDashboardGroups(in configuration: SchedulerConfiguration) -> [Problem] {
        let byCode = Dictionary(grouping: configuration.studentGroups) { key($0.dashboardGroupName) }
        return byCode.values
            .filter { $0.count > 1 }
            .map { groups in .sharedDashboardGroup(code: groups[0].dashboardGroupName, groupNames: groups.map { "\($0.name) (\($0.students.count))" }) }
            .sorted { $0.message < $1.message }
    }

    /// Whether a group may be used for a dashboard step right now.
    public static func problem(for group: StudentGroup, in configuration: SchedulerConfiguration) -> Problem? {
        let code = key(group.dashboardGroupName)
        let sharing = configuration.studentGroups.filter { key($0.dashboardGroupName) == code }
        guard sharing.count <= 1 else {
            return .sharedDashboardGroup(code: group.dashboardGroupName, groupNames: sharing.map { "\($0.name) (\($0.students.count))" })
        }
        return nil
    }

    /// Whether a queued follow-up still points at the dashboard group its register belongs to.
    public static func problem(for followUp: LmsFollowUp, in configuration: SchedulerConfiguration) -> Problem? {
        guard let groupID = followUp.attendanceGroupID else { return nil }
        guard let group = configuration.studentGroups.first(where: { $0.id == groupID }) else { return .groupMissing }
        if let shared = problem(for: group, in: configuration) { return shared }
        guard key(group.dashboardGroupName) == key(followUp.group) else {
            return .changedSinceScheduled(expected: followUp.group, now: group.dashboardGroupName)
        }
        return nil
    }
}
