import Foundation

/// One line the Node helper wrote: a log line, an event, or the final result.
public enum AutomationMessage: Equatable {
    case log(level: String, message: String)
    case event(name: String, data: [String: AutomationValue])
    case result([String: AutomationValue])

    /// Parses one stdout line. Anything that is not the helper's JSON is kept as a log line,
    /// so a stray print from a dependency is visible instead of lost.
    public static func parse(line: String) -> AutomationMessage? {
        let trimmed = line.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }
        guard let data = trimmed.data(using: .utf8),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let type = object["type"] as? String else {
            return .log(level: "info", message: trimmed)
        }
        let values = object.compactMapValues(AutomationValue.init(any:))
        switch type {
        case "log":
            return .log(level: (object["level"] as? String) ?? "info", message: (object["message"] as? String) ?? "")
        case "event":
            let payload = (object["data"] as? [String: Any])?.compactMapValues(AutomationValue.init(any:)) ?? [:]
            return .event(name: (object["name"] as? String) ?? "", data: payload)
        case "result":
            return .result(values.filter { $0.key != "type" })
        default:
            return .log(level: "info", message: trimmed)
        }
    }
}

/// JSON values from the helper, typed enough to read without `Any` escaping into callers.
public indirect enum AutomationValue: Equatable {
    case string(String)
    case number(Double)
    case bool(Bool)
    case array([AutomationValue])
    case object([String: AutomationValue])
    case null

    init?(any: Any) {
        switch any {
        case let value as String: self = .string(value)
        case let value as NSNumber:
            // JSONSerialization hands booleans over as NSNumber too.
            if CFGetTypeID(value) == CFBooleanGetTypeID() { self = .bool(value.boolValue) } else { self = .number(value.doubleValue) }
        case let value as [Any]: self = .array(value.compactMap(AutomationValue.init(any:)))
        case let value as [String: Any]: self = .object(value.compactMapValues(AutomationValue.init(any:)))
        case is NSNull: self = .null
        default: return nil
        }
    }

    public var string: String? { if case .string(let value) = self { return value } else { return nil } }
    public var bool: Bool? { if case .bool(let value) = self { return value } else { return nil } }
    public var number: Double? { if case .number(let value) = self { return value } else { return nil } }
    public var array: [AutomationValue]? { if case .array(let value) = self { return value } else { return nil } }
    public var object: [String: AutomationValue]? { if case .object(let value) = self { return value } else { return nil } }

    public subscript(key: String) -> AutomationValue? { object?[key] }
}

/// What a finished helper command answered.
public struct AutomationResult: Equatable {
    public var success: Bool
    public var message: String
    public var failure: String?
    public var body: [String: AutomationValue]

    public init(success: Bool, message: String, failure: String? = nil, body: [String: AutomationValue] = [:]) {
        self.success = success
        self.message = message
        self.failure = failure
        self.body = body
    }

    init(body: [String: AutomationValue]) {
        self.body = body
        success = body["success"]?.bool ?? false
        message = body["message"]?.string ?? ""
        failure = body["failure"]?.string ?? body["reason"]?.string
    }

    public static func helperUnavailable(_ reason: String) -> AutomationResult {
        AutomationResult(success: false, message: reason, failure: "helperUnavailable")
    }
}

/// Where the Node runtime and the helper script live.
///
/// The app bundle carries the helper under Resources/automation; a development build runs it
/// from the repository. Node itself is the user's: Homebrew's, the official installer's, or a
/// path set in Settings.
public struct AutomationEnvironment: Equatable {
    public var nodeURL: URL
    public var helperDirectory: URL

    public var scriptURL: URL { helperDirectory.appendingPathComponent("bin/zaa-automation.mjs") }

    public init(nodeURL: URL, helperDirectory: URL) {
        self.nodeURL = nodeURL
        self.helperDirectory = helperDirectory
    }

    public static let nodePathDefaultsKey = "automationNodePath"

    public static func nodeCandidates(override: String?, environment: [String: String]) -> [String] {
        var candidates: [String] = []
        if let override, !override.trimmingCharacters(in: .whitespaces).isEmpty { candidates.append(override) }
        if let fromEnvironment = environment["ZOOM_AUTO_ADMIT_NODE"] { candidates.append(fromEnvironment) }
        candidates += ["/opt/homebrew/bin/node", "/usr/local/bin/node"]
        // nvm and volta keep node under the home folder; a GUI app does not inherit their PATH.
        if let home = environment["HOME"] {
            candidates.append("\(home)/.volta/bin/node")
            let nvm = URL(fileURLWithPath: "\(home)/.nvm/versions/node")
            if let versions = try? FileManager.default.contentsOfDirectory(atPath: nvm.path) {
                for version in versions.sorted(by: { $0.compare($1, options: .numeric) == .orderedDescending }) {
                    candidates.append(nvm.appendingPathComponent(version).appendingPathComponent("bin/node").path)
                }
            }
        }
        for directory in (environment["PATH"] ?? "").split(separator: ":") {
            candidates.append("\(directory)/node")
        }
        return candidates
    }

    public static func helperCandidates(bundle: Bundle, environment: [String: String], sourceFile: String = #filePath) -> [URL] {
        var candidates: [URL] = []
        if let override = environment["ZOOM_AUTO_ADMIT_AUTOMATION_DIR"] {
            candidates.append(URL(fileURLWithPath: override, isDirectory: true))
        }
        if let resources = bundle.resourceURL {
            candidates.append(resources.appendingPathComponent("automation", isDirectory: true))
        }
        // Development: Sources/ZoomAutoAdmitCore/Automation/<this file> → repository/automation.
        let repository = URL(fileURLWithPath: sourceFile)
            .deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent()
        candidates.append(repository.appendingPathComponent("automation", isDirectory: true))
        return candidates
    }

    /// The first usable node and helper, or a sentence saying what is missing.
    public static func locate(
        nodeOverride: String? = UserDefaults.standard.string(forKey: nodePathDefaultsKey),
        bundle: Bundle = .main,
        environment: [String: String] = ProcessInfo.processInfo.environment,
        fileManager: FileManager = .default
    ) -> Result<AutomationEnvironment, AutomationSetupError> {
        guard let node = nodeCandidates(override: nodeOverride, environment: environment)
            .first(where: { fileManager.isExecutableFile(atPath: $0) }) else {
            return .failure(.nodeNotFound)
        }
        guard let helper = helperCandidates(bundle: bundle, environment: environment).first(where: {
            fileManager.fileExists(atPath: $0.appendingPathComponent("bin/zaa-automation.mjs").path)
        }) else {
            return .failure(.helperNotFound)
        }
        guard fileManager.fileExists(atPath: helper.appendingPathComponent("node_modules/playwright").path) else {
            return .failure(.dependenciesMissing(helper.path))
        }
        return .success(AutomationEnvironment(nodeURL: URL(fileURLWithPath: node), helperDirectory: helper))
    }
}

public enum AutomationSetupError: Error, Equatable, LocalizedError {
    case nodeNotFound
    case helperNotFound
    case dependenciesMissing(String)

    public var errorDescription: String? {
        switch self {
        case .nodeNotFound:
            return "Node.js was not found. Install it (brew install node) or set its path in Settings → Automation."
        case .helperNotFound:
            return "The automation helper is missing from the app. Rebuild the app with Scripts/build-app.sh."
        case .dependenciesMissing(let path):
            return "The automation helper's packages are not installed. Run `npm install` in \(path)."
        }
    }
}

/// Runs helper commands. Every call is synchronous and must be made off the main thread.
public final class AutomationHelper {
    public typealias LineHandler = (AutomationMessage) -> Void

    private let environmentProvider: () -> Result<AutomationEnvironment, AutomationSetupError>

    public init(environmentProvider: @escaping () -> Result<AutomationEnvironment, AutomationSetupError> = { AutomationEnvironment.locate() }) {
        self.environmentProvider = environmentProvider
    }

    /// Runs one command to completion. `request` is sent on stdin, so a password never appears
    /// in the process list the way an argument would.
    public func run(
        _ command: String,
        request: [String: Any],
        timeout: TimeInterval = 600,
        onMessage: LineHandler? = nil
    ) -> AutomationResult {
        let environment: AutomationEnvironment
        switch environmentProvider() {
        case .success(let located): environment = located
        case .failure(let error): return .helperUnavailable(error.localizedDescription)
        }
        guard JSONSerialization.isValidJSONObject(request),
              let payload = try? JSONSerialization.data(withJSONObject: request) else {
            return AutomationResult(success: false, message: "The request could not be encoded.", failure: "invalidRequest")
        }

        let process = Process()
        process.executableURL = environment.nodeURL
        process.arguments = [environment.scriptURL.path, command]
        process.currentDirectoryURL = environment.helperDirectory
        process.environment = Self.childEnvironment()
        let input = Pipe()
        let output = Pipe()
        process.standardInput = input
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice

        var finalResult: AutomationResult?
        let lines = LineBuffer { line in
            guard let message = AutomationMessage.parse(line: line) else { return }
            if case .result(let body) = message { finalResult = AutomationResult(body: body) }
            onMessage?(message)
        }
        let finished = DispatchSemaphore(value: 0)
        output.fileHandleForReading.readabilityHandler = { handle in
            let data = handle.availableData
            if data.isEmpty {
                handle.readabilityHandler = nil
                lines.flush()
                finished.signal()
            } else {
                lines.append(data)
            }
        }

        do {
            try process.run()
        } catch {
            output.fileHandleForReading.readabilityHandler = nil
            return .helperUnavailable("Node.js could not be started: \(error.localizedDescription)")
        }
        input.fileHandleForWriting.write(payload)
        try? input.fileHandleForWriting.close()

        if finished.wait(timeout: .now() + timeout) == .timedOut {
            process.terminate()
            _ = finished.wait(timeout: .now() + 5)
            return AutomationResult(success: false, message: "The helper did not finish within \(Int(timeout / 60)) minutes and was stopped.", failure: "timeout")
        }
        process.waitUntilExit()
        return finalResult ?? AutomationResult(
            success: false,
            message: "The helper exited (status \(process.terminationStatus)) without an answer.",
            failure: "noResult"
        )
    }

    /// Starts a long-running command (the recording API, a Web meeting). The first request line
    /// is written at once; `stop()` asks it to finish cleanly.
    public func start(_ command: String, request: [String: Any], onMessage: @escaping LineHandler, onExit: @escaping (AutomationResult?) -> Void) -> Result<RunningAutomation, AutomationSetupError> {
        switch environmentProvider() {
        case .failure(let error):
            return .failure(error)
        case .success(let environment):
            return .success(RunningAutomation(environment: environment, command: command, request: request, onMessage: onMessage, onExit: onExit))
        }
    }

    static func childEnvironment() -> [String: String] {
        var environment = ProcessInfo.processInfo.environment
        // A GUI app starts with a bare PATH; Playwright looks for browsers through it.
        let extra = "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin"
        environment["PATH"] = [environment["PATH"], extra].compactMap { $0 }.joined(separator: ":")
        environment["PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD"] = "1"
        return environment
    }
}

/// A helper process that keeps running until it is stopped.
public final class RunningAutomation {
    private let process = Process()
    private let input = Pipe()
    private let output = Pipe()
    private var lines: LineBuffer!
    private var result: AutomationResult?

    init(environment: AutomationEnvironment, command: String, request: [String: Any], onMessage: @escaping AutomationHelper.LineHandler, onExit: @escaping (AutomationResult?) -> Void) {
        process.executableURL = environment.nodeURL
        process.arguments = [environment.scriptURL.path, command]
        process.currentDirectoryURL = environment.helperDirectory
        process.environment = AutomationHelper.childEnvironment()
        process.standardInput = input
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice

        lines = LineBuffer { [weak self] line in
            guard let message = AutomationMessage.parse(line: line) else { return }
            if case .result(let body) = message { self?.result = AutomationResult(body: body) }
            onMessage(message)
        }
        output.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            if data.isEmpty {
                handle.readabilityHandler = nil
                self?.lines.flush()
            } else {
                self?.lines.append(data)
            }
        }
        process.terminationHandler = { [weak self] _ in
            // Let the reader drain what the process wrote last before reporting.
            DispatchQueue.global().asyncAfter(deadline: .now() + 0.3) { onExit(self?.result) }
        }

        do {
            try process.run()
            if let payload = try? JSONSerialization.data(withJSONObject: request) {
                input.fileHandleForWriting.write(payload)
                input.fileHandleForWriting.write(Data("\n".utf8))
            }
        } catch {
            onMessage(.log(level: "error", message: "Node.js could not be started: \(error.localizedDescription)"))
            onExit(AutomationResult.helperUnavailable(error.localizedDescription))
        }
    }

    public var isRunning: Bool { process.isRunning }

    /// Closing stdin is the stop signal; a helper that ignores it is terminated after a grace period.
    public func stop(grace: TimeInterval = 100) {
        guard process.isRunning else { return }
        input.fileHandleForWriting.write(Data("{\"type\":\"stop\"}\n".utf8))
        try? input.fileHandleForWriting.close()
        let process = self.process
        DispatchQueue.global().asyncAfter(deadline: .now() + grace) {
            if process.isRunning { process.terminate() }
        }
    }
}

/// Splits a byte stream into UTF-8 lines, holding a partial line until it completes.
final class LineBuffer {
    private var pending = Data()
    private let lock = NSLock()
    private let handler: (String) -> Void

    init(handler: @escaping (String) -> Void) {
        self.handler = handler
    }

    func append(_ data: Data) {
        lock.lock()
        pending.append(data)
        var completed: [String] = []
        while let newline = pending.firstIndex(of: 0x0A) {
            let lineData = pending[pending.startIndex..<newline]
            completed.append(String(decoding: lineData, as: UTF8.self))
            pending.removeSubrange(pending.startIndex...newline)
        }
        lock.unlock()
        completed.forEach(handler)
    }

    func flush() {
        lock.lock()
        let rest = pending
        pending = Data()
        lock.unlock()
        if !rest.isEmpty { handler(String(decoding: rest, as: UTF8.self)) }
    }
}
