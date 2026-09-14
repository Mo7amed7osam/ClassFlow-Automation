import Foundation

/// Wall-clock time of day, stored without a time zone so that "Saturday 6:00 PM"
/// keeps meaning 6:00 PM locally across DST changes.
public struct TimeOfDay: Codable, Equatable, Comparable, Hashable {
    public var hour: Int
    public var minute: Int

    public init(hour: Int, minute: Int) {
        self.hour = min(max(hour, 0), 23)
        self.minute = min(max(minute, 0), 59)
    }

    public static func < (lhs: TimeOfDay, rhs: TimeOfDay) -> Bool {
        (lhs.hour, lhs.minute) < (rhs.hour, rhs.minute)
    }

    public var minutesSinceMidnight: Int { hour * 60 + minute }

    public var displayText: String {
        var components = DateComponents()
        components.hour = hour
        components.minute = minute
        let calendar = Calendar.current
        guard let date = calendar.date(from: DateComponents(
            year: 2000, month: 1, day: 1, hour: hour, minute: minute
        )) else {
            return String(format: "%02d:%02d", hour, minute)
        }
        _ = components
        let formatter = DateFormatter()
        formatter.timeStyle = .short
        formatter.dateStyle = .none
        return formatter.string(from: date)
    }
}

/// Calendar weekday numbers, matching `Calendar.component(.weekday:)`.
public enum Weekday: Int, Codable, CaseIterable, Comparable, Hashable {
    case sunday = 1, monday, tuesday, wednesday, thursday, friday, saturday

    public static func < (lhs: Weekday, rhs: Weekday) -> Bool { lhs.rawValue < rhs.rawValue }

    public var shortName: String {
        switch self {
        case .sunday: return "Sun"
        case .monday: return "Mon"
        case .tuesday: return "Tue"
        case .wednesday: return "Wed"
        case .thursday: return "Thu"
        case .friday: return "Fri"
        case .saturday: return "Sat"
        }
    }

    public var fullName: String {
        switch self {
        case .sunday: return "Sunday"
        case .monday: return "Monday"
        case .tuesday: return "Tuesday"
        case .wednesday: return "Wednesday"
        case .thursday: return "Thursday"
        case .friday: return "Friday"
        case .saturday: return "Saturday"
        }
    }
}

public enum ScheduleRecurrence: Codable, Equatable, Hashable {
    case oneTime(year: Int, month: Int, day: Int)
    case daily
    case selectedWeekdays(Set<Weekday>)

    public var displayText: String {
        switch self {
        case .oneTime(let year, let month, let day):
            return String(format: "%04d-%02d-%02d", year, month, day)
        case .daily:
            return "Every day"
        case .selectedWeekdays(let days):
            if days.isEmpty { return "No days selected" }
            if days.count == 7 { return "Every day" }
            return days.sorted().map(\.shortName).joined(separator: ", ")
        }
    }

    // Explicit coding keys keep the on-disk JSON readable and stable.
    private enum CodingKeys: String, CodingKey {
        case kind, year, month, day, weekdays
    }

    private enum Kind: String, Codable {
        case oneTime, daily, selectedWeekdays
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        switch self {
        case .oneTime(let year, let month, let day):
            try container.encode(Kind.oneTime, forKey: .kind)
            try container.encode(year, forKey: .year)
            try container.encode(month, forKey: .month)
            try container.encode(day, forKey: .day)
        case .daily:
            try container.encode(Kind.daily, forKey: .kind)
        case .selectedWeekdays(let days):
            try container.encode(Kind.selectedWeekdays, forKey: .kind)
            try container.encode(days.sorted().map(\.rawValue), forKey: .weekdays)
        }
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        switch try container.decode(Kind.self, forKey: .kind) {
        case .oneTime:
            self = .oneTime(
                year: try container.decode(Int.self, forKey: .year),
                month: try container.decode(Int.self, forKey: .month),
                day: try container.decode(Int.self, forKey: .day)
            )
        case .daily:
            self = .daily
        case .selectedWeekdays:
            let raw = try container.decode([Int].self, forKey: .weekdays)
            self = .selectedWeekdays(Set(raw.compactMap(Weekday.init(rawValue:))))
        }
    }
}

/// How a scheduled meeting is started.
///
/// Live inspection settled this: Zoom registers the public `zoommtg` URL scheme,
/// and its documented `start` action targets a meeting by number using whichever
/// account is signed in. That is far more robust than navigating the meeting
/// list, which lives inside a window and therefore disappears from Accessibility
/// whenever Zoom sits on another Space — the exact state Zoom was found in
/// during discovery.
public struct MeetingReference: Codable, Equatable, Hashable {
    public enum Kind: Codable, Equatable, Hashable {
        /// Start a specific meeting by its Zoom meeting number.
        case meetingID(String)
        /// Zoom's `Start meeting` application menu entry (personal meeting).
        case instantMeeting

        private enum CodingKeys: String, CodingKey { case kind, meetingID }
        private enum Tag: String, Codable { case meetingID, instantMeeting }

        public func encode(to encoder: Encoder) throws {
            var container = encoder.container(keyedBy: CodingKeys.self)
            switch self {
            case .meetingID(let identifier):
                try container.encode(Tag.meetingID, forKey: .kind)
                try container.encode(identifier, forKey: .meetingID)
            case .instantMeeting:
                try container.encode(Tag.instantMeeting, forKey: .kind)
            }
        }

        public init(from decoder: Decoder) throws {
            let container = try decoder.container(keyedBy: CodingKeys.self)
            switch try container.decode(Tag.self, forKey: .kind) {
            case .meetingID:
                self = .meetingID(try container.decode(String.self, forKey: .meetingID))
            case .instantMeeting:
                self = .instantMeeting
            }
        }
    }

    public var name: String
    public var kind: Kind
    /// Optional meeting passcode, taken from a pasted invite link.
    public var passcode: String?

    public init(name: String, kind: Kind, passcode: String? = nil) {
        self.name = name
        self.kind = kind
        self.passcode = passcode
    }

    private enum CodingKeys: String, CodingKey { case name, kind, passcode }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        name = try container.decode(String.self, forKey: .name)
        kind = try container.decode(Kind.self, forKey: .kind)
        passcode = try container.decodeIfPresent(String.self, forKey: .passcode)
    }

    /// Extracts a Zoom meeting number from a plain number *or* a full invite
    /// link.
    ///
    /// Stripping every non-digit is wrong for a link: the host name carries
    /// digits of its own, so `https://us05web.zoom.us/j/94698416251` would
    /// become `0594698416251` and silently open nothing. Links are therefore
    /// parsed structurally, and only a bare entry falls back to digit filtering.
    public static func normalizedMeetingID(_ raw: String) -> String {
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard looksLikeLink(trimmed) else { return trimmed.filter(\.isNumber) }

        // https://<anything>.zoom.us/j/<digits> and /s/<digits> and /w/<digits>
        for marker in ["/j/", "/s/", "/w/", "/my/"] {
            if let range = trimmed.range(of: marker) {
                let tail = trimmed[range.upperBound...]
                let digits = tail.prefix { $0.isNumber }
                if !digits.isEmpty { return String(digits) }
            }
        }
        // zoommtg://zoom.us/join?confno=<digits>
        if let range = trimmed.range(of: "confno=") {
            let digits = trimmed[range.upperBound...].prefix { $0.isNumber }
            if !digits.isEmpty { return String(digits) }
        }
        return ""
    }

    /// Pulls `pwd=` out of a pasted invite link, when present.
    public static func passcode(from raw: String) -> String? {
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let range = trimmed.range(of: "pwd=") else { return nil }
        let value = trimmed[range.upperBound...].prefix { $0.isLetter || $0.isNumber || $0 == "." || $0 == "_" || $0 == "-" }
        return value.isEmpty ? nil : String(value)
    }

    public static func looksLikeLink(_ raw: String) -> Bool {
        let value = raw.lowercased()
        return value.contains("://") || value.contains("zoom.us") || value.contains("/j/")
    }

    public var displayText: String {
        switch kind {
        case .meetingID(let identifier):
            let digits = Self.normalizedMeetingID(identifier)
            return digits.isEmpty ? name : "\(name) (\(digits))"
        case .instantMeeting:
            return "\(name) (personal meeting)"
        }
    }

    /// Shorter form for list rows, where the schedule name is already shown
    /// above and repeating it just pushes the useful part out of view.
    public var summaryText: String {
        switch kind {
        case .meetingID(let identifier):
            let digits = Self.normalizedMeetingID(identifier)
            return digits.isEmpty ? name : "\(name) · \(Self.groupedMeetingID(digits))"
        case .instantMeeting:
            return "\(name) · Personal meeting"
        }
    }

    /// Zoom writes meeting numbers in 3-4-4 groups.
    public static func groupedMeetingID(_ digits: String) -> String {
        guard digits.count > 6 else { return digits }
        var remaining = Substring(digits)
        var groups: [String] = []
        for size in [3, 4, 4] {
            guard !remaining.isEmpty else { break }
            let chunk = remaining.prefix(size)
            groups.append(String(chunk))
            remaining = remaining.dropFirst(chunk.count)
        }
        if !remaining.isEmpty { groups.append(String(remaining)) }
        return groups.joined(separator: " ")
    }
}

/// A logical Zoom account.
///
/// Holds no password, token or any other credential: only the identifier needed
/// to recognise an account that is *already* signed in inside Zoom's own
/// Switch account menu.
public struct ZoomAccountProfile: Codable, Equatable, Identifiable, Hashable {
    public var id: UUID
    /// Friendly label, e.g. "DEPI".
    public var name: String
    /// Matched against Zoom's saved accounts. An email is strongly preferred:
    /// several saved accounts can share a display name.
    public var accountIdentifier: String
    /// Which Zoom runs this account's meetings. Auto uses the desktop app while it is free and
    /// the Web Client when it is already in a meeting.
    public var preferredEngine: ZoomEnginePreference
    /// The app-owned browser profile the Web Client signs in with. Empty means one is named
    /// after the account.
    public var webProfileName: String?

    public init(
        id: UUID = UUID(),
        name: String,
        accountIdentifier: String,
        preferredEngine: ZoomEnginePreference = .desktop,
        webProfileName: String? = nil
    ) {
        self.id = id
        self.name = name
        self.accountIdentifier = accountIdentifier
        self.preferredEngine = preferredEngine
        self.webProfileName = webProfileName
    }

    private enum CodingKeys: String, CodingKey {
        case id, name, accountIdentifier, preferredEngine, webProfileName
    }

    // Profiles saved before engines existed keep running on the desktop app, as they did.
    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decode(UUID.self, forKey: .id)
        name = try container.decode(String.self, forKey: .name)
        accountIdentifier = try container.decode(String.self, forKey: .accountIdentifier)
        preferredEngine = try container.decodeIfPresent(ZoomEnginePreference.self, forKey: .preferredEngine) ?? .desktop
        webProfileName = try container.decodeIfPresent(String.self, forKey: .webProfileName)
    }

    /// A browser profile folder name: letters, digits, '.', '_' and '-' only.
    public var resolvedWebProfileName: String {
        let source = (webProfileName?.isEmpty == false ? webProfileName! : name)
        let allowed = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "._-"))
        var cleaned = String(source.unicodeScalars.map { allowed.contains($0) && $0.isASCII ? Character($0) : "-" })
        while cleaned.first.map({ !$0.isLetter && !$0.isNumber }) == true { cleaned.removeFirst() }
        cleaned = String(cleaned.prefix(64))
        return cleaned.isEmpty ? "zoom-\(id.uuidString.prefix(8).lowercased())" : cleaned
    }
}

public enum ZoomEnginePreference: String, Codable, CaseIterable, Equatable, Hashable {
    case desktop
    case web
    case auto

    public var displayName: String {
        switch self {
        case .desktop: return "Zoom desktop app"
        case .web: return "Zoom Web (browser)"
        case .auto: return "Auto (desktop, Web when busy)"
        }
    }
}

public struct ZoomSchedule: Codable, Equatable, Identifiable, Hashable {
    public var id: UUID
    public var name: String
    public var isEnabled: Bool
    public var recurrence: ScheduleRecurrence
    public var startTime: TimeOfDay
    /// When set, Auto Admit monitoring is stopped at this time. The Zoom meeting
    /// itself is never ended automatically.
    public var endTime: TimeOfDay?
    public var accountProfileID: UUID
    public var meeting: MeetingReference
    public var enablesAutoAdmit: Bool
    /// The class group whose attendance this meeting records.
    ///
    /// Optional because attendance is additive: a schedule without a group runs
    /// exactly as before and simply records nothing.
    public var attendanceGroupID: UUID?
    /// Mute the microphone in Zoom's pre-join preview before starting.
    /// The workflow still reads Zoom's real state and only presses when the
    /// microphone is actually live.
    public var mutesMicrophoneBeforeJoining: Bool
    /// Turn the camera off in Zoom's pre-join preview before starting.
    public var disablesCameraBeforeJoining: Bool
    /// Zoom is launched this many minutes before the start time so its UI is
    /// ready when the workflow runs.
    public var launchZoomMinutesEarly: Int

    public init(
        id: UUID = UUID(),
        name: String,
        isEnabled: Bool = true,
        recurrence: ScheduleRecurrence,
        startTime: TimeOfDay,
        endTime: TimeOfDay? = nil,
        accountProfileID: UUID,
        meeting: MeetingReference,
        enablesAutoAdmit: Bool = true,
        attendanceGroupID: UUID? = nil,
        mutesMicrophoneBeforeJoining: Bool = true,
        disablesCameraBeforeJoining: Bool = true,
        launchZoomMinutesEarly: Int = 2
    ) {
        self.id = id
        self.name = name
        self.isEnabled = isEnabled
        self.recurrence = recurrence
        self.startTime = startTime
        self.endTime = endTime
        self.accountProfileID = accountProfileID
        self.meeting = meeting
        self.enablesAutoAdmit = enablesAutoAdmit
        self.attendanceGroupID = attendanceGroupID
        self.mutesMicrophoneBeforeJoining = mutesMicrophoneBeforeJoining
        self.disablesCameraBeforeJoining = disablesCameraBeforeJoining
        self.launchZoomMinutesEarly = min(max(launchZoomMinutesEarly, 0), 60)
    }

    // Hand-written decoding so schedules saved before the pre-join settings
    // existed keep loading, defaulting to the safe choice: both devices off.
    private enum CodingKeys: String, CodingKey {
        case id, name, isEnabled, recurrence, startTime, endTime, accountProfileID
        case meeting, enablesAutoAdmit, attendanceGroupID, mutesMicrophoneBeforeJoining
        case disablesCameraBeforeJoining, launchZoomMinutesEarly
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decode(UUID.self, forKey: .id)
        name = try container.decode(String.self, forKey: .name)
        isEnabled = try container.decode(Bool.self, forKey: .isEnabled)
        recurrence = try container.decode(ScheduleRecurrence.self, forKey: .recurrence)
        startTime = try container.decode(TimeOfDay.self, forKey: .startTime)
        endTime = try container.decodeIfPresent(TimeOfDay.self, forKey: .endTime)
        accountProfileID = try container.decode(UUID.self, forKey: .accountProfileID)
        meeting = try container.decode(MeetingReference.self, forKey: .meeting)
        enablesAutoAdmit = try container.decodeIfPresent(Bool.self, forKey: .enablesAutoAdmit) ?? true
        attendanceGroupID = try container.decodeIfPresent(UUID.self, forKey: .attendanceGroupID)
        mutesMicrophoneBeforeJoining =
            try container.decodeIfPresent(Bool.self, forKey: .mutesMicrophoneBeforeJoining) ?? true
        disablesCameraBeforeJoining =
            try container.decodeIfPresent(Bool.self, forKey: .disablesCameraBeforeJoining) ?? true
        launchZoomMinutesEarly =
            try container.decodeIfPresent(Int.self, forKey: .launchZoomMinutesEarly) ?? 2
    }
}

/// Everything the scheduler persists.
public struct SchedulerConfiguration: Codable, Equatable {
    public var accountProfiles: [ZoomAccountProfile]
    public var schedules: [ZoomSchedule]
    /// Class groups and their rosters. Kept beside the schedules so a single
    /// file still describes everything the scheduler needs.
    public var studentGroups: [StudentGroup]

    public init(
        accountProfiles: [ZoomAccountProfile] = [],
        schedules: [ZoomSchedule] = [],
        studentGroups: [StudentGroup] = []
    ) {
        self.accountProfiles = accountProfiles
        self.schedules = schedules
        self.studentGroups = studentGroups
    }

    private enum CodingKeys: String, CodingKey {
        case accountProfiles, schedules, studentGroups
    }

    // Files written before groups existed must keep loading.
    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        accountProfiles = try container.decodeIfPresent([ZoomAccountProfile].self, forKey: .accountProfiles) ?? []
        schedules = try container.decodeIfPresent([ZoomSchedule].self, forKey: .schedules) ?? []
        studentGroups = try container.decodeIfPresent([StudentGroup].self, forKey: .studentGroups) ?? []
    }

    public func group(for schedule: ZoomSchedule) -> StudentGroup? {
        guard let id = schedule.attendanceGroupID else { return nil }
        return studentGroups.first { $0.id == id }
    }

    public func profile(for schedule: ZoomSchedule) -> ZoomAccountProfile? {
        accountProfiles.first { $0.id == schedule.accountProfileID }
    }
}
