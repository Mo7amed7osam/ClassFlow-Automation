import Foundation

public enum ZoomEngine: String, Codable, Equatable {
    case desktop
    case web
}

/// Decides which Zoom runs a scheduled meeting.
///
/// The desktop app can only hold one meeting at a time. An account set to Web always uses the
/// browser; Auto uses the desktop app while it is free and moves to the Web Client when it is
/// already busy - which is what lets two classes overlap. A personal meeting has no link a
/// browser could open, so it always stays on the desktop app.
public final class ZoomEngineAllocator {
    private let lock = NSLock()
    private var desktopReservation: UUID?

    public init() {}

    public func allocate(
        scheduleID: UUID,
        preference: ZoomEnginePreference,
        hasWebLink: Bool,
        desktopHasActiveMeeting: Bool
    ) -> ZoomEngine {
        lock.lock()
        defer { lock.unlock() }
        guard hasWebLink else { return reserveDesktop(scheduleID) }
        switch preference {
        case .web:
            return .web
        case .desktop:
            return reserveDesktop(scheduleID)
        case .auto:
            if desktopHasActiveMeeting || (desktopReservation != nil && desktopReservation != scheduleID) { return .web }
            return reserveDesktop(scheduleID)
        }
    }

    public func release(scheduleID: UUID) {
        lock.lock()
        if desktopReservation == scheduleID { desktopReservation = nil }
        lock.unlock()
    }

    public var isDesktopReserved: Bool {
        lock.lock()
        defer { lock.unlock() }
        return desktopReservation != nil
    }

    private func reserveDesktop(_ scheduleID: UUID) -> ZoomEngine {
        desktopReservation = scheduleID
        return .desktop
    }

    /// The link a browser opens for a schedule's meeting, with the passcode when one was pasted.
    public static func webLink(for meeting: MeetingReference) -> URL? {
        guard case .meetingID(let raw) = meeting.kind else { return nil }
        let digits = MeetingReference.normalizedMeetingID(raw)
        guard !digits.isEmpty else { return nil }
        var components = URLComponents(string: "https://zoom.us/j/\(digits)")
        if let passcode = meeting.passcode ?? MeetingReference.passcode(from: raw) {
            components?.queryItems = [URLQueryItem(name: "pwd", value: passcode)]
        }
        return components?.url
    }
}

/// Attendance for a meeting held in the Web Client, fed by the names the page reports.
///
/// It writes the same AttendanceSession the desktop recorder writes, so the register, the
/// review window, the CSV export and the LMS upload all work the same for both.
public final class WebAttendanceRecorder {
    private let lock = NSLock()
    private let store: AttendanceStore
    private let group: StudentGroup
    private let recorder: AttendanceSnapshotRecorder
    private var session: AttendanceSession

    public init(group: StudentGroup, schedule: ZoomSchedule, store: AttendanceStore = AttendanceStore(), startedAt: Date = Date()) {
        self.group = group
        self.store = store
        self.recorder = AttendanceSnapshotRecorder(group: group)
        self.session = AttendanceSession(
            groupID: group.id,
            groupName: group.name,
            scheduleID: schedule.id,
            meetingName: schedule.meeting.name,
            startedAt: startedAt,
            rosterSnapshot: group.students,
            evidenceSource: .accessibilitySnapshots
        )
        _ = store.save(session)
    }

    public var current: AttendanceSession {
        lock.lock()
        defer { lock.unlock() }
        return session
    }

    /// Folds in one list of names the page showed as in the meeting.
    @discardableResult
    public func record(names: [String], at now: Date = Date(), reason: SnapshotReason = .periodic) -> AttendanceSession {
        lock.lock()
        defer { lock.unlock() }
        let participants = names.compactMap { name -> SnapshotParticipant? in
            let parsed = Self.stripRole(name)
            guard !parsed.isHost, !group.isIgnored(parsed.name), !AttendanceIgnoreRules.current.matches(parsed.name) else { return nil }
            let normalized = NameNormalizer.normalize(parsed.name)
            return normalized.isEmpty ? nil : SnapshotParticipant(rawZoomName: parsed.name, normalizedZoomName: normalized)
        }
        let reasonForThis: SnapshotReason = recorder.snapshots.isEmpty ? .meetingStarted : reason
        recorder.merge(AttendanceSnapshot(capturedAt: now, reason: reasonForThis, participants: participants))
        return persistLocked(finalizing: false, at: now)
    }

    @discardableResult
    public func finalize(at now: Date = Date()) -> AttendanceSession {
        lock.lock()
        defer { lock.unlock() }
        return persistLocked(finalizing: true, at: now)
    }

    private func persistLocked(finalizing: Bool, at now: Date) -> AttendanceSession {
        session.observations = recorder.observations
        session.snapshots = recorder.snapshots
        if finalizing { session.endedAt = now }
        session = AttendanceReconciler.reconcile(
            session: session,
            autoAcceptConfidence: group.autoAcceptConfidence,
            finalizing: finalizing,
            at: now
        )
        _ = store.save(session)
        return session
    }

    /// The Web Client shows "Name (Host, Me)"; the host running the class is not a student.
    static func stripRole(_ raw: String) -> (name: String, isHost: Bool) {
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.hasSuffix(")"), let open = trimmed.lastIndex(of: "(") else { return (trimmed, false) }
        let inside = trimmed[trimmed.index(after: open)..<trimmed.index(before: trimmed.endIndex)].lowercased()
        let tokens = inside.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }
        let known: Set<String> = ["host", "co-host", "cohost", "me", "you", "guest"]
        guard !tokens.isEmpty, tokens.allSatisfy(known.contains) else { return (trimmed, false) }
        let name = trimmed[..<open].trimmingCharacters(in: .whitespaces)
        let isHost = tokens.contains("host") || tokens.contains("me") || tokens.contains("you") || tokens.contains("co-host") || tokens.contains("cohost")
        return (name.isEmpty ? trimmed : name, isHost)
    }
}
