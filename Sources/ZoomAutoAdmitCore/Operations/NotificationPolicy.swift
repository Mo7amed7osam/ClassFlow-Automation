import Foundation

/// A notification the app would like to show.
public struct OperationsNotice: Equatable {
    /// Repeats of one problem share a key: the same class, the same kind, the same reason.
    public var key: String
    public var title: String
    public var body: String
    public var severity: OperationsSeverity
    public var sessionKey: String?
    public var scheduleID: UUID?

    public init(key: String, title: String, body: String, severity: OperationsSeverity, sessionKey: String? = nil, scheduleID: UUID? = nil) {
        self.key = key
        self.title = title
        self.body = body
        self.severity = severity
        self.sessionKey = sessionKey
        self.scheduleID = scheduleID
    }
}

/// What to do with a notice.
public struct NoticeDecision: Equatable {
    public var post: Bool
    /// The notification identifier: repeats reuse it, so the newer one replaces the older in
    /// Notification Center instead of stacking up.
    public var identifier: String
    public var title: String
    public var body: String
    /// How many times this key has come up in the current window, this one included.
    public var occurrences: Int
}

/// Decides which notices become notifications, and groups repeats.
///
/// - Info never notifies; it is history only.
/// - The first notice for a key is shown.
/// - A repeat within the quiet period replaces the earlier notification at most once every
///   `repeatInterval`, with the count in its title, unless it is more severe than before.
/// - After the quiet period a key starts over.
public final class NotificationThrottle {
    public static let quietPeriod: TimeInterval = 2 * 60 * 60
    public static let repeatInterval: TimeInterval = 30 * 60

    private struct Entry {
        var firstAt: Date
        var lastPostedAt: Date
        var count: Int
        var severity: OperationsSeverity
    }

    private var entries: [String: Entry] = [:]
    private let lock = NSLock()

    public init() {}

    public func decide(_ notice: OperationsNotice, now: Date = Date()) -> NoticeDecision {
        lock.lock()
        defer { lock.unlock() }
        let identifier = "ops|\(notice.key)"
        guard notice.severity != .info else {
            return NoticeDecision(post: false, identifier: identifier, title: notice.title, body: notice.body, occurrences: 1)
        }

        guard var entry = entries[notice.key], now.timeIntervalSince(entry.firstAt) < Self.quietPeriod else {
            entries[notice.key] = Entry(firstAt: now, lastPostedAt: now, count: 1, severity: notice.severity)
            return NoticeDecision(post: true, identifier: identifier, title: notice.title, body: notice.body, occurrences: 1)
        }

        entry.count += 1
        let escalated = notice.severity > entry.severity
        let post = escalated || now.timeIntervalSince(entry.lastPostedAt) >= Self.repeatInterval
        if post { entry.lastPostedAt = now }
        entry.severity = max(entry.severity, notice.severity)
        entries[notice.key] = entry
        return NoticeDecision(
            post: post,
            identifier: identifier,
            title: "\(notice.title) (×\(entry.count))",
            body: notice.body,
            occurrences: entry.count
        )
    }

    /// A success for a class clears its failures, so the next failure is news again.
    public func clear(prefix: String) {
        lock.lock()
        defer { lock.unlock() }
        entries = entries.filter { !$0.key.hasPrefix(prefix) }
    }
}
