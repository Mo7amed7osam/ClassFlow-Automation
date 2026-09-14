import Foundation

/// Opens the app ahead of scheduled meetings, so a class still starts when the app was quit.
///
/// macOS's launchd holds one LaunchAgent with a calendar entry per upcoming start. At that
/// moment it runs `open -g -b <bundle id>`: if the app is already running nothing happens, and
/// if it is not, it starts in the background and its own scheduler takes the meeting as usual.
/// Pointing at the bundle identifier rather than a path means moving the app does not break it.
public enum LaunchAgentScheduler {
    public static let label = "com.mohamedhosam.ZoomAutoAdmit.scheduler"
    /// launchd's calendar entries carry no year, so only the near future is written; the app
    /// rewrites the agent whenever schedules change and each time it launches.
    public static let horizonDays = 14
    public static let leadMinutes = 5

    public static var plistURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/LaunchAgents/\(label).plist")
    }

    /// The wake-up moments: each upcoming start in the horizon, `leadMinutes` early.
    public static func launchDates(
        configuration: SchedulerConfiguration,
        now: Date = Date(),
        calendar: Calendar = .current
    ) -> [Date] {
        let horizon = now.addingTimeInterval(TimeInterval(horizonDays * 24 * 60 * 60))
        var dates = Set<Date>()
        for schedule in configuration.schedules where schedule.isEnabled {
            var cursor = now
            // A handful per schedule is plenty inside two weeks and keeps the plist small.
            for _ in 0..<20 {
                guard let next = ScheduleTimeline.nextOccurrence(of: schedule, after: cursor, calendar: calendar), next <= horizon else { break }
                let wake = next.addingTimeInterval(TimeInterval(-(leadMinutes + schedule.launchZoomMinutesEarly) * 60))
                if wake > now { dates.insert(wake) }
                cursor = next.addingTimeInterval(60)
            }
        }
        return dates.sorted()
    }

    public static func plist(bundleIdentifier: String, dates: [Date], calendar: Calendar = .current) -> [String: Any] {
        let intervals: [[String: Int]] = dates.map { date in
            let parts = calendar.dateComponents([.month, .day, .hour, .minute], from: date)
            return ["Month": parts.month ?? 1, "Day": parts.day ?? 1, "Hour": parts.hour ?? 0, "Minute": parts.minute ?? 0]
        }
        return [
            "Label": label,
            "ProgramArguments": ["/usr/bin/open", "-g", "-b", bundleIdentifier],
            "StartCalendarInterval": intervals,
            "RunAtLoad": false,
            "ProcessType": "Interactive"
        ]
    }

    /// Writes and (re)loads the agent. With no upcoming meetings, or when turned off, removes it.
    @discardableResult
    public static func install(
        configuration: SchedulerConfiguration,
        enabled: Bool,
        bundleIdentifier: String? = Bundle.main.bundleIdentifier,
        now: Date = Date()
    ) -> String {
        let dates = launchDates(configuration: configuration, now: now)
        guard enabled, let bundleIdentifier, !dates.isEmpty else {
            uninstall()
            return enabled ? "No upcoming meetings to wake the app for." : "Opening the app for meetings is off."
        }
        let url = plistURL
        do {
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            let data = try PropertyListSerialization.data(fromPropertyList: plist(bundleIdentifier: bundleIdentifier, dates: dates), format: .xml, options: 0)
            try data.write(to: url, options: .atomic)
        } catch {
            return "The launch agent could not be written: \(error.localizedDescription)"
        }
        let domain = "gui/\(getuid())"
        launchctl(["bootout", domain, url.path])
        let status = launchctl(["bootstrap", domain, url.path])
        return status == 0
            ? "The app will open itself before \(dates.count) upcoming meeting(s)."
            : "The launch agent was written but launchd did not load it (status \(status))."
    }

    public static func uninstall() {
        let url = plistURL
        guard FileManager.default.fileExists(atPath: url.path) else { return }
        launchctl(["bootout", "gui/\(getuid())", url.path])
        try? FileManager.default.removeItem(at: url)
    }

    @discardableResult
    private static func launchctl(_ arguments: [String]) -> Int32 {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/launchctl")
        process.arguments = arguments
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        do {
            try process.run()
            process.waitUntilExit()
            return process.terminationStatus
        } catch {
            return -1
        }
    }
}
