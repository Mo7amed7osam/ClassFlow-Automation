import AppKit
import Foundation
import OSLog
import ZoomAXSupport
import ZoomAutoAdmitCore

/// Takes periodic attendance snapshots alongside a live meeting.
///
/// Deliberately isolated from Auto Admit: it owns its own timer and its own
/// failures. A snapshot that cannot be taken is logged and counted, and the
/// next one runs normally — admissions, the scheduler and the meeting workflow
/// never depend on any of it.
final class AttendanceCoordinator {
    /// How often the timer wakes to *decide*. Snapshots themselves are governed
    /// by `SnapshotSchedule`, which is minutes apart.
    private static let tickInterval: TimeInterval = 5

    private let logger = Logger(subsystem: "com.mohamedhosam.ZoomAutoAdmit", category: "attendance")
    private let store: AttendanceStore
    private let schedulerLog: SchedulerLog
    private let queue = DispatchQueue(label: "com.mohamedhosam.ZoomAutoAdmit.attendance", qos: .utility)

    private var timer: DispatchSourceTimer?
    private var recorder: AttendanceSnapshotRecorder?
    private var session: AttendanceSession?
    private var group: StudentGroup?
    private var coalescer = PostAdmitCoalescer()
    private var schedule = SnapshotSchedule()
    /// When a snapshot was last *attempted*, successful or not. Kept apart from
    /// the recorder's `lastSnapshotAt`, which only counts readable lists.
    private var lastAttemptAt: Date?
    private var lastAttemptFailed = false

    private(set) var liveSummary: AttendanceLiveSummary?
    var onChange: (() -> Void)?
    /// Raised once per meeting, on its first readable snapshot, with the people worth asking about.
    var onUnknownParticipants: ((AttendanceSession, [UnknownParticipant]) -> Void)?

    /// Supplied by the app delegate so a finalized register can teach the
    /// roster. Optional: the coordinator records attendance perfectly well
    /// without them, it simply learns nothing.
    private let configurationProvider: (() -> SchedulerConfiguration)?
    private let configurationWriter: ((SchedulerConfiguration) -> Void)?

    init(
        store: AttendanceStore = AttendanceStore(),
        schedulerLog: SchedulerLog = .shared,
        configurationProvider: (() -> SchedulerConfiguration)? = nil,
        configurationWriter: ((SchedulerConfiguration) -> Void)? = nil
    ) {
        self.store = store
        self.schedulerLog = schedulerLog
        self.configurationProvider = configurationProvider
        self.configurationWriter = configurationWriter
    }

    var isRecording: Bool { queue.sync { timer != nil } }
    var currentSession: AttendanceSession? { queue.sync { session } }

    /// Begins snapshotting for a verified meeting.
    func start(group: StudentGroup, schedule zoomSchedule: ZoomSchedule, at now: Date = Date()) {
        queue.sync {
            guard timer == nil else {
                schedulerLog.write(
                    "[attendance] start-ignored reason=session-already-active "
                    + "session=\(session?.id.uuidString ?? "unknown")"
                )
                return
            }

            self.group = group
            self.schedule = SchedulerDefaults.snapshotSchedule
            self.recorder = AttendanceSnapshotRecorder(group: group)
            self.coalescer.reset()
            self.lastAttemptAt = nil
            self.lastAttemptFailed = false
            self.session = AttendanceSession(
                groupID: group.id,
                groupName: group.name,
                scheduleID: zoomSchedule.id,
                meetingName: zoomSchedule.meeting.name,
                startedAt: now,
                rosterSnapshot: group.students,
                evidenceSource: .accessibilitySnapshots
            )

            guard let createdSession = self.session else { return }
            schedulerLog.write(
                "[attendance] session-created id=\(createdSession.id.uuidString) "
                + "schedule=\(zoomSchedule.id.uuidString) group=\(group.id.uuidString) "
                + "roster=\(createdSession.rosterSnapshot.count) recorder-started=yes"
            )
            schedulerLog.write(
                "[attendance] snapshot-state meeting_started=due "
                + "nextPeriodicAt=after-first-success tickInterval=\(Int(Self.tickInterval))s"
            )

            let timer = DispatchSource.makeTimerSource(queue: queue)
            timer.schedule(deadline: .now() + 3, repeating: Self.tickInterval, leeway: .seconds(1))
            timer.setEventHandler { [weak self] in self?.tick() }
            self.timer = timer
            timer.resume()

            // Persist the empty evidence session immediately. This proves the
            // lifecycle is wired even if Zoom's first participant read fails.
            persistLocked(finalizing: false, at: now)
        }
    }

    /// How long a register with no end time may stay open across a relaunch.
    private static let maximumResumeAge: TimeInterval = 4 * 60 * 60

    /// Picks a register back up after the app restarted mid-meeting.
    ///
    /// The session is already on disk — only the timer and the recorder live in
    /// memory — so a relaunch would otherwise silently stop collecting evidence
    /// while the class is still running, and empty the menu.
    @discardableResult
    func resumeOpenSession(
        configuration: SchedulerConfiguration,
        at now: Date = Date(),
        calendar: Calendar = .current
    ) -> Bool {
        queue.sync {
            guard timer == nil else { return false }
            guard let open = store.loadAll().first(where: { $0.endedAt == nil }) else { return false }

            guard let group = configuration.studentGroups.first(where: { $0.id == open.groupID }),
                  let schedule = configuration.schedules.first(where: { $0.id == open.scheduleID }) else {
                schedulerLog.write(
                    "[attendance] resume-skipped id=\(open.id.uuidString) reason=schedule-or-group-missing"
                )
                return false
            }

            // Past its own end time it is not a live register any more. The
            // scheduler's persisted deadline finalizes it on the next tick.
            let deadline = ScheduleTimeline.endDate(for: schedule, startedAt: open.startedAt, calendar: calendar)
                ?? open.startedAt.addingTimeInterval(Self.maximumResumeAge)
            guard now < deadline else {
                schedulerLog.write(
                    "[attendance] resume-skipped id=\(open.id.uuidString) reason=past-end-time "
                    + "deadline=\(Self.iso8601(deadline))"
                )
                return false
            }

            self.group = group
            self.schedule = SchedulerDefaults.snapshotSchedule
            self.session = open
            self.coalescer.reset()
            self.recorder = AttendanceSnapshotRecorder(
                group: group,
                existing: open.observations,
                existingSnapshots: open.snapshots,
                missedSnapshots: open.missedSnapshotCount
            )
            // A snapshot taken by the previous process still counts as the last
            // attempt, so the menu does not claim the register is brand new.
            self.lastAttemptAt = open.snapshots.map(\.capturedAt).max()
            self.lastAttemptFailed = false

            let timer = DispatchSource.makeTimerSource(queue: queue)
            timer.schedule(deadline: .now() + 3, repeating: Self.tickInterval, leeway: .seconds(1))
            timer.setEventHandler { [weak self] in self?.tick() }
            self.timer = timer
            timer.resume()

            schedulerLog.write(
                "[attendance] session-resumed id=\(open.id.uuidString) "
                + "schedule=\(schedule.id.uuidString) group=\(group.id.uuidString) "
                + "snapshots=\(open.snapshots.count) union=\(open.observations.count) "
                + "missed=\(open.missedSnapshotCount) deadline=\(Self.iso8601(deadline))"
            )
            persistLocked(finalizing: false, at: now)
            return true
        }
    }

    /// Closes registers a quit or relaunch left open past their class's end time.
    ///
    /// Such a register is never resumed and nothing else ever finalizes it, so it would stay
    /// "in progress" for good - and the recording sync waits for a finalized register.
    @discardableResult
    func finalizeAbandonedSessions(
        configuration: SchedulerConfiguration,
        at now: Date = Date(),
        calendar: Calendar = .current
    ) -> Int {
        queue.sync {
            var closed = 0
            for var open in store.loadAll() where open.endedAt == nil && open.id != session?.id {
                let schedule = configuration.schedules.first(where: { $0.id == open.scheduleID })
                let deadline = schedule.flatMap { ScheduleTimeline.endDate(for: $0, startedAt: open.startedAt, calendar: calendar) }
                    ?? open.startedAt.addingTimeInterval(Self.maximumResumeAge)
                guard now >= deadline else { continue }
                open.endedAt = deadline
                let group = configuration.studentGroups.first(where: { $0.id == open.groupID })
                let finalized = AttendanceReconciler.reconcile(
                    session: open,
                    autoAcceptConfidence: group?.autoAcceptConfidence ?? 0.9,
                    finalizing: true,
                    at: deadline
                )
                if store.save(finalized) {
                    closed += 1
                    schedulerLog.write(
                        "[attendance] abandoned-session-finalized id=\(open.id.uuidString) "
                        + "group=\(open.groupName) endedAt=\(Self.iso8601(deadline)) present=\(finalized.presentCount)"
                    )
                }
            }
            return closed
        }
    }

    func stop(at now: Date = Date()) {
        queue.sync {
            guard let timer else { return }
            timer.setEventHandler {}
            timer.cancel()
            self.timer = nil
            persistLocked(finalizing: false, at: now)
            schedulerLog.write("Attendance snapshots stopped")
        }
    }

    /// Takes a final snapshot and closes the register.
    @discardableResult
    func finalize(at now: Date = Date()) -> AttendanceSession? {
        queue.sync {
            if let timer {
                timer.setEventHandler {}
                timer.cancel()
                self.timer = nil
            }
            takeSnapshotLocked(reason: .final, at: now)
            guard let finalized = persistLocked(finalizing: true, at: now) else { return nil }
            schedulerLog.write(
                "Attendance finalized for \(finalized.groupName): "
                + "present=\(finalized.presentCount) absent=\(finalized.absentCount) "
                + "review=\(finalized.needsReviewCount) snapshots=\(finalized.snapshots.count) "
                + "missed=\(finalized.missedSnapshotCount)"
            )
            learnConfirmedAliasesLocked(from: finalized)
            return finalized
        }
    }

    /// Writes the names this class settled back onto the roster.
    ///
    /// A Zoom name that had to be worked out once should never have to be worked
    /// out again — that is the difference between a register that gets easier
    /// every week and one that asks the same question forever. Only Present
    /// records are learned; a Needs Review row is an open question, and teaching
    /// the roster from it would turn a guess into a permanent fact.
    private func learnConfirmedAliasesLocked(from session: AttendanceSession) {
        guard let configurationProvider, let configurationWriter else { return }
        var configuration = configurationProvider()
        guard let index = configuration.studentGroups.firstIndex(where: { $0.id == session.groupID }) else {
            return
        }

        let outcome = AliasLearning.learnConfirmedMatches(
            in: session,
            group: configuration.studentGroups[index]
        )
        for skipped in outcome.skipped {
            schedulerLog.write("[attendance] alias-not-learned \(skipped)")
        }
        guard outcome.didChangeGroup else { return }

        configuration.studentGroups[index] = outcome.group
        configurationWriter(configuration)
        let learned = outcome.learned.map { "\($0.alias)->\($0.studentName)" }.joined(separator: " | ")
        schedulerLog.write("[attendance] aliases-learned count=\(outcome.learned.count) [\(learned)]")
    }

    /// Called when Auto Admit lets somebody in. Bursts coalesce into one snapshot.
    func noteAdmit(at now: Date = Date()) {
        queue.async { [weak self] in
            guard let self else { return }
            self.schedulerLog.write("[attendance] admit-observed at=\(Self.iso8601(now))")
            guard self.timer != nil, self.schedule.postAdmitEnabled else {
                self.schedulerLog.write("[attendance] post-admit-snapshot skipped=no-active-session")
                return
            }
            let dueAt = self.coalescer.noteAdmit(at: now, delay: self.schedule.postAdmitDelay)
            self.schedulerLog.write(
                "[attendance] post-admit-snapshot scheduled=\(Self.iso8601(dueAt)) "
                + "burst=\(self.coalescer.admitsInBurst)"
            )
        }
    }

    /// Takes a snapshot immediately, for the menu command.
    func snapshotNow() {
        queue.async { [weak self] in
            guard let self else { return }
            guard self.timer != nil else {
                self.schedulerLog.write("[attendance] snapshot-request reason=manual rejected=no-active-session")
                return
            }
            self.takeSnapshotLocked(reason: .manual, at: Date())
            self.persistLocked(finalizing: false, at: Date())
        }
    }

    // MARK: Timer

    // MARK: Ignore list and unknown participants

    /// The first readable snapshot of a meeting is when a trainer or coordinator is most likely
    /// already there; ask about them once, then never again for this meeting.
    private func checkUnknownParticipantsLocked() {
        guard var current = session, !current.unknownParticipantsReviewed,
              let recorder, !recorder.snapshots.isEmpty else { return }
        current.unknownParticipantsReviewed = true
        session = current
        let unknown = UnknownParticipantDetector.detect(session: current, history: store.sessions(forGroup: current.groupID))
        persistLocked(finalizing: false, at: Date())
        schedulerLog.write("[attendance] unknown-participants checked found=\(unknown.count) [\(unknown.map(\.name).joined(separator: " | "))]")
        guard !unknown.isEmpty, let snapshot = session else { return }
        DispatchQueue.main.async { [weak self] in self?.onUnknownParticipants?(snapshot, unknown) }
    }

    /// Re-reconciles the live register after the global ignore list changed.
    func ignoreListChanged() {
        queue.async { [weak self] in
            guard let self, self.session != nil else { return }
            self.persistLocked(finalizing: false, at: Date())
        }
    }

    /// "Ignore this meeting only": kept on the session, gone with it.
    func ignoreForThisMeeting(_ names: [String], sessionID: UUID) {
        queue.async { [weak self] in
            guard let self else { return }
            if var current = self.session, current.id == sessionID {
                current.meetingIgnoredNames = Array(Set(current.meetingIgnoredNames + names))
                self.session = current
                self.persistLocked(finalizing: false, at: Date())
            } else if var stored = self.store.load(id: sessionID) {
                stored.meetingIgnoredNames = Array(Set(stored.meetingIgnoredNames + names))
                let group = self.configurationProvider?().studentGroups.first { $0.id == stored.groupID }
                self.store.save(AttendanceReconciler.reconcile(session: stored, autoAcceptConfidence: group?.autoAcceptConfidence ?? 0.9, finalizing: stored.isFinalized))
            }
            self.schedulerLog.write("[attendance] ignored-for-meeting session=\(sessionID.uuidString) count=\(names.count)")
        }
    }

    /// "Add as student": onto the group's roster for good, and onto this meeting's roster so the
    /// name is matched now rather than next week.
    func addStudents(_ names: [String], sessionID: UUID) {
        queue.async { [weak self] in
            guard let self, let provider = self.configurationProvider, let writer = self.configurationWriter else { return }
            var configuration = provider()
            let groupID = self.session?.id == sessionID ? self.session?.groupID : self.store.load(id: sessionID)?.groupID
            guard let groupID, let index = configuration.studentGroups.firstIndex(where: { $0.id == groupID }) else { return }
            let known = Set(configuration.studentGroups[index].students.map(\.normalizedOfficialName))
            let new = names.filter { !known.contains(NameNormalizer.normalize($0)) }.map { Student(officialName: $0) }
            guard !new.isEmpty else { return }
            configuration.studentGroups[index].students.append(contentsOf: new)
            writer(configuration)
            if var current = self.session, current.id == sessionID {
                current.rosterSnapshot.append(contentsOf: new)
                self.session = current
                self.group = configuration.studentGroups[index]
                self.persistLocked(finalizing: false, at: Date())
            }
            self.schedulerLog.write("[attendance] students-added group=\(groupID.uuidString) count=\(new.count)")
        }
    }

    private func tick() {
        guard let recorder, session != nil else { return }
        let now = Date()

        // The very first snapshot happens as soon as the meeting is verified.
        if recorder.snapshots.isEmpty {
            schedulerLog.write("[attendance] meeting_started snapshot firing")
            takeSnapshotLocked(reason: .meetingStarted, at: now)
            persistLocked(finalizing: false, at: now)
            checkUnknownParticipantsLocked()
            return
        }
        // The first snapshot may have missed (panel closed); ask on the first one that read.
        checkUnknownParticipantsLocked()

        if coalescer.isPending, coalescer.isDue(at: now) {
            schedulerLog.write("[attendance] post-admit snapshot firing")
            coalescer.reset()
            takeSnapshotLocked(reason: .postAdmit, at: now)
            persistLocked(finalizing: false, at: now)
            return
        }

        if schedule.isPeriodicDue(now: now, lastSnapshotAt: recorder.lastSnapshotAt) {
            takeSnapshotLocked(reason: .periodic, at: now)
            persistLocked(finalizing: false, at: now)
        }
    }

    private func takeSnapshotLocked(reason: SnapshotReason, at now: Date) {
        schedulerLog.write(
            "[attendance] snapshot-request reason=\(reason.rawValue) at=\(Self.iso8601(now))"
        )
        lastAttemptAt = now
        lastAttemptFailed = true
        guard let recorder else {
            schedulerLog.write("[attendance] snapshot-failed reason=recorder-not-started")
            return
        }
        guard let process = ZoomAXSupport.zoomApplication() else {
            schedulerLog.write("[attendance] snapshot-failed reason=zoom-not-running")
            return
        }

        var diagnostics = ZoomAXSupport.participantsReadoutDiagnostics(pid: process.pid)
        var panelState: ZoomAXSupport.ParticipantsPanelState = diagnostics.readout.listAvailable ? .open : .unknown

        if !diagnostics.readout.listAvailable {
            let reading = ZoomAXSupport.participantsReading(pid: process.pid)
            panelState = reading.state
            schedulerLog.write("[attendance] participants-panel state=\(reading.state.rawValue)")
            if reading.state != .open {
                let outcome = openParticipantsPanel(reading: reading)
                schedulerLog.write("[attendance] participants-panel open-outcome=\(outcome)")
            }

            // Whether the panel was opened or Zoom merely returned a transient
            // partial tree, discard every old AX reference and retry once.
            Thread.sleep(forTimeInterval: 0.4)
            diagnostics = ZoomAXSupport.participantsReadoutDiagnostics(pid: process.pid)
            if diagnostics.readout.listAvailable { panelState = .open }
        }

        let readout = diagnostics.readout
        schedulerLog.write(
            "[attendance] zoom-pid=\(process.pid) windows=\(diagnostics.windowsCount) "
            + "windowsError=\(diagnostics.windowsError.diagnosticDescription) panel=\(panelState.rawValue)"
        )
        let rawPanelists = readout.admitted.map { row in
            "\(row.rawText){roles=\(row.roles.map(\.rawValue).sorted().joined(separator: ","))}"
        }.joined(separator: " | ")
        schedulerLog.write(
            "[attendance] parser available=\(readout.listAvailable) "
            + "reported=\(readout.reportedCount.map(String.init) ?? "n/a") "
            + "panelists=[\(rawPanelists)] waiting=\(readout.waiting.count)"
        )

        if let snapshot = recorder.capture(readout, reason: reason, at: now) {
            lastAttemptFailed = false
            schedulerLog.write(
                "[attendance] filtered=[\(snapshot.participants.map(\.rawZoomName).joined(separator: " | "))]"
            )
            schedulerLog.write(
                "[attendance] snapshot-captured id=\(snapshot.id.uuidString) "
                + "reason=\(reason.rawValue) participants=\(snapshot.participants.count) "
                + "union=\(recorder.observedIdentityCount)"
            )
        } else {
            // A missed snapshot removes nobody: evidence only ever accumulates.
            schedulerLog.write(
                "[attendance] snapshot-missed reason=\(reason.rawValue) "
                + "missed=\(recorder.missedSnapshots) union=\(recorder.observedIdentityCount)"
            )
        }
    }

    private func openParticipantsPanel(reading: ZoomAXSupport.ParticipantsReading) -> String {
        switch reading.state {
        case .open:
            return "already-open"
        case .closed:
            if let command = reading.menuCommand {
                return String(describing: ZoomAXSupport.pressParticipantsMenuCommand(command, in: reading))
            }
            if let toggle = reading.toggle {
                return String(describing: ZoomAXSupport.pressParticipantsToggle(toggle, in: reading))
            }
            return "control-unavailable"
        case .unknown:
            return "state-unknown-refused-toggle"
        }
    }

    @discardableResult
    private func persistLocked(finalizing: Bool, at now: Date) -> AttendanceSession? {
        guard var current = session, let recorder, let group else { return nil }

        current.observations = recorder.observations
        current.snapshots = recorder.snapshots
        current.missedSnapshotCount = recorder.missedSnapshots
        if finalizing { current.endedAt = now }

        let reconciled = AttendanceReconciler.reconcile(
            session: current,
            autoAcceptConfidence: group.autoAcceptConfidence,
            finalizing: finalizing,
            at: now
        )
        session = reconciled

        let saved = store.save(reconciled)
        if !saved {
            logger.error("Could not write the attendance session to disk")
        }
        let matched = reconciled.records.filter { $0.status == .present }.count
        schedulerLog.write(
            "[attendance] reconcile matched=\(matched) "
            + "unresolved=\(reconciled.unmatchedZoomNames.count) review=\(reconciled.needsReviewCount)"
        )
        schedulerLog.write(
            "[attendance] session-saved=\(saved) id=\(reconciled.id.uuidString) "
            + "snapshots=\(reconciled.snapshots.count) union=\(reconciled.observations.count)"
        )
        let next = recorder.lastSnapshotAt.flatMap(schedule.nextPeriodicDate(after:))
        schedulerLog.write("[attendance] next-snapshot=\(next.map(Self.iso8601) ?? "meeting_started pending")")
        publishSummaryLocked(reconciled, recorder: recorder)
        return reconciled
    }

    private func publishSummaryLocked(_ session: AttendanceSession, recorder: AttendanceSnapshotRecorder) {
        let summary = AttendanceLiveSummary(
            groupName: session.groupName,
            observedIdentities: recorder.observedIdentityCount,
            matchedStudents: session.records.filter { $0.status == .present }.count,
            totalStudents: session.rosterSnapshot.count,
            startedAt: session.startedAt,
            lastSnapshotAt: recorder.lastSnapshotAt,
            lastAttemptAt: lastAttemptAt,
            lastAttemptFailed: lastAttemptFailed,
            nextSnapshotAt: recorder.lastSnapshotAt.flatMap(schedule.nextPeriodicDate(after:)),
            periodicEnabled: schedule.periodicEnabled,
            missedSnapshots: recorder.missedSnapshots
        )
        DispatchQueue.main.async { [weak self] in
            self?.liveSummary = summary
            self?.onChange?()
        }
    }

    private static func iso8601(_ date: Date) -> String {
        ISO8601DateFormatter().string(from: date)
    }
}

/// What the menu shows while a class is running. Phrased as evidence, not as
/// live presence: nothing here claims anybody is in the meeting right now.
struct AttendanceLiveSummary {
    let groupName: String
    let observedIdentities: Int
    let matchedStudents: Int
    let totalStudents: Int
    let startedAt: Date
    /// Last snapshot that actually read the participants list.
    let lastSnapshotAt: Date?
    /// Last attempt, readable or not, so a stuck run is visible rather than silent.
    let lastAttemptAt: Date?
    let lastAttemptFailed: Bool
    let nextSnapshotAt: Date?
    let periodicEnabled: Bool
    let missedSnapshots: Int

    /// Recomputed on every menu open, so the relative times stay honest.
    var lines: [String] {
        lines(now: Date())
    }

    func lines(now: Date) -> [String] {
        var result = [
            "Observed identities: \(observedIdentities)",
            "Matched students: \(matchedStudents) / \(totalStudents)",
            "Last attendance check: \(lastCheckText(now: now))",
            "Next attendance check: \(nextCheckText(now: now))"
        ]
        if missedSnapshots > 0 {
            result.append("Missed snapshots: \(missedSnapshots)")
        }
        return result
    }

    /// Always says something. A run that has captured nothing yet is a state the
    /// user needs to see, not a reason to hide the row.
    func lastCheckText(now: Date) -> String {
        guard let lastSnapshotAt else {
            guard let lastAttemptAt else {
                return "None yet — class started \(Self.time(startedAt))"
            }
            return "\(Self.time(lastAttemptAt)) — list unreadable"
        }
        var text = "\(Self.time(lastSnapshotAt)) (\(Self.relative(lastSnapshotAt, now: now)))"
        if lastAttemptFailed, let lastAttemptAt, lastAttemptAt > lastSnapshotAt {
            text += " · retry \(Self.time(lastAttemptAt)) failed"
        }
        return text
    }

    func nextCheckText(now: Date) -> String {
        guard periodicEnabled else { return "Periodic checks off" }
        guard let nextSnapshotAt else {
            // No readable list yet: the recorder retries on its own tick rather
            // than waiting out a full interval.
            return "Retrying until the list is readable"
        }
        if nextSnapshotAt <= now { return "Due now" }
        return "\(Self.time(nextSnapshotAt)) (\(Self.relative(nextSnapshotAt, now: now)))"
    }

    private static func time(_ date: Date) -> String {
        formatter.string(from: date)
    }

    private static func relative(_ date: Date, now: Date) -> String {
        relativeFormatter.localizedString(for: date, relativeTo: now)
    }

    private static let formatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.timeStyle = .short
        formatter.dateStyle = .none
        return formatter
    }()

    private static let relativeFormatter: RelativeDateTimeFormatter = {
        let formatter = RelativeDateTimeFormatter()
        formatter.unitsStyle = .short
        return formatter
    }()
}
