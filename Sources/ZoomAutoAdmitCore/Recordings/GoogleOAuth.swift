import CryptoKit
import Foundation
import Network
import Security

/// The Google Cloud OAuth client the app signs in with ("Desktop app" type).
///
/// Google treats a desktop client's secret as not confidential, but it is still kept in the
/// Keychain rather than in defaults. Only read access to spreadsheets is requested.
public struct GoogleOAuthConfiguration: Equatable {
    public static let sheetsReadOnlyScope = "https://www.googleapis.com/auth/spreadsheets.readonly"
    public static let emailScope = "https://www.googleapis.com/auth/userinfo.email"

    public var clientID: String
    public var clientSecret: String?
    public var scopes: [String]

    public init(clientID: String, clientSecret: String?, scopes: [String] = [GoogleOAuthConfiguration.sheetsReadOnlyScope, GoogleOAuthConfiguration.emailScope]) {
        self.clientID = clientID.trimmingCharacters(in: .whitespacesAndNewlines)
        self.clientSecret = clientSecret?.trimmingCharacters(in: .whitespacesAndNewlines)
        self.scopes = scopes
    }

    public var isComplete: Bool { !clientID.isEmpty }
}

public enum GoogleOAuthError: Error, Equatable, LocalizedError {
    case notConfigured
    case notAuthorized
    case authorizationDenied(String)
    case stateMismatch
    case timedOut
    case listenerFailed(String)
    case tokenRequestFailed(Int, String)
    case invalidResponse

    public var errorDescription: String? {
        switch self {
        case .notConfigured: return "Enter the Google OAuth client ID first."
        case .notAuthorized: return "Google access is not authorized. Press Connect Google."
        case .authorizationDenied(let reason): return "Google sign-in was not completed (\(reason))."
        case .stateMismatch: return "The Google sign-in reply did not belong to this request; nothing was saved."
        case .timedOut: return "Google sign-in was not finished within 5 minutes."
        case .listenerFailed(let reason): return "The local sign-in listener could not start: \(reason)"
        case .tokenRequestFailed(let status, let reason): return "Google refused the token request (HTTP \(status)): \(reason)"
        case .invalidResponse: return "Google answered with something that is not a token."
        }
    }
}

/// PKCE (RFC 7636): the code only exchanges together with the verifier that produced its challenge.
public struct PKCE: Equatable {
    public let verifier: String
    public let challenge: String

    public init(verifier: String = PKCE.randomVerifier()) {
        self.verifier = verifier
        challenge = PKCE.base64URL(Data(SHA256.hash(data: Data(verifier.utf8))))
    }

    public static func randomVerifier() -> String {
        var bytes = [UInt8](repeating: 0, count: 48)
        _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        return base64URL(Data(bytes))
    }

    public static func base64URL(_ data: Data) -> String {
        data.base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }
}

public struct GoogleTokenResponse: Decodable, Equatable {
    public let accessToken: String
    public let expiresIn: Int
    public let refreshToken: String?
    public let scope: String?
    public let tokenType: String?

    enum CodingKeys: String, CodingKey {
        case accessToken = "access_token"
        case expiresIn = "expires_in"
        case refreshToken = "refresh_token"
        case scope
        case tokenType = "token_type"
    }
}

/// Where the refresh token and client secret live.
public protocol GoogleCredentialStoring: AnyObject {
    var refreshToken: String? { get }
    var clientSecret: String? { get }
    @discardableResult func saveRefreshToken(_ token: String) -> Bool
    @discardableResult func saveClientSecret(_ secret: String?) -> Bool
    @discardableResult func deleteRefreshToken() -> Bool
}

public final class KeychainGoogleCredentialStore: GoogleCredentialStoring {
    public static let defaultService = "com.mohamedhosam.ZoomAutoAdmit.GoogleOAuth"
    private let service: String

    public init(service: String = KeychainGoogleCredentialStore.defaultService) {
        self.service = service
    }

    public var refreshToken: String? { read(account: "refresh-token") }
    public var clientSecret: String? { read(account: "client-secret") }

    @discardableResult public func saveRefreshToken(_ token: String) -> Bool { write(token, account: "refresh-token") }

    @discardableResult public func saveClientSecret(_ secret: String?) -> Bool {
        guard let secret, !secret.isEmpty else { return delete(account: "client-secret") }
        return write(secret, account: "client-secret")
    }

    @discardableResult public func deleteRefreshToken() -> Bool { delete(account: "refresh-token") }

    private func query(_ account: String) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: account]
    }

    private func read(account: String) -> String? {
        var query = query(account)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var item: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess, let data = item as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    private func write(_ value: String, account: String) -> Bool {
        SecItemDelete(query(account) as CFDictionary)
        var attributes = query(account)
        attributes[kSecValueData as String] = Data(value.utf8)
        // After first unlock, so the 08:00 sync runs without anyone at the keyboard.
        attributes[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
        return SecItemAdd(attributes as CFDictionary, nil) == errSecSuccess
    }

    private func delete(account: String) -> Bool {
        let status = SecItemDelete(query(account) as CFDictionary)
        return status == errSecSuccess || status == errSecItemNotFound
    }
}

/// Signs in once in the browser (loopback redirect + PKCE), then works from the refresh token.
public final class GoogleOAuthClient {
    public typealias HTTP = (URLRequest) async throws -> (Data, HTTPURLResponse)

    public static let authorizationEndpoint = URL(string: "https://accounts.google.com/o/oauth2/v2/auth")!
    public static let tokenEndpoint = URL(string: "https://oauth2.googleapis.com/token")!

    private let configuration: () -> GoogleOAuthConfiguration
    private let store: GoogleCredentialStoring
    private let http: HTTP
    private let lock = NSLock()
    private var cachedToken: (value: String, expires: Date)?

    public init(configuration: @escaping () -> GoogleOAuthConfiguration, store: GoogleCredentialStoring, http: @escaping HTTP = GoogleOAuthClient.urlSession) {
        self.configuration = configuration
        self.store = store
        self.http = http
    }

    public static let urlSession: HTTP = { request in
        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw GoogleOAuthError.invalidResponse }
        return (data, http)
    }

    public var isAuthorized: Bool { store.refreshToken != nil }

    public static func authorizationURL(configuration: GoogleOAuthConfiguration, redirectURI: String, state: String, pkce: PKCE) -> URL {
        var components = URLComponents(url: authorizationEndpoint, resolvingAgainstBaseURL: false)!
        components.queryItems = [
            URLQueryItem(name: "client_id", value: configuration.clientID),
            URLQueryItem(name: "redirect_uri", value: redirectURI),
            URLQueryItem(name: "response_type", value: "code"),
            URLQueryItem(name: "scope", value: configuration.scopes.joined(separator: " ")),
            URLQueryItem(name: "state", value: state),
            URLQueryItem(name: "code_challenge", value: pkce.challenge),
            URLQueryItem(name: "code_challenge_method", value: "S256"),
            // A refresh token is only issued with offline access, and only reliably with consent.
            URLQueryItem(name: "access_type", value: "offline"),
            URLQueryItem(name: "prompt", value: "consent")
        ]
        return components.url!
    }

    static func formRequest(_ fields: [(String, String?)]) -> URLRequest {
        var request = URLRequest(url: tokenEndpoint)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        var allowed = CharacterSet.alphanumerics
        allowed.insert(charactersIn: "-._~")
        request.httpBody = fields
            .compactMap { key, value in value.map { "\(key)=\($0.addingPercentEncoding(withAllowedCharacters: allowed) ?? $0)" } }
            .joined(separator: "&")
            .data(using: .utf8)
        return request
    }

    /// Opens the Google sign-in page and waits for the redirect. Stores the refresh token.
    public func authorize(openURL: @escaping (URL) -> Void, timeout: TimeInterval = 300) async throws {
        var config = configuration()
        guard config.isComplete else { throw GoogleOAuthError.notConfigured }
        if config.clientSecret == nil || config.clientSecret?.isEmpty == true { config.clientSecret = store.clientSecret }

        let receiver = LoopbackRedirectReceiver()
        let port = try await receiver.start()
        defer { receiver.stop() }
        let redirectURI = "http://127.0.0.1:\(port)"
        let pkce = PKCE()
        let state = PKCE.randomVerifier()
        openURL(Self.authorizationURL(configuration: config, redirectURI: redirectURI, state: state, pkce: pkce))

        let query = try await receiver.waitForRedirect(timeout: timeout)
        if let error = query["error"] { throw GoogleOAuthError.authorizationDenied(error) }
        guard query["state"] == state else { throw GoogleOAuthError.stateMismatch }
        guard let code = query["code"] else { throw GoogleOAuthError.authorizationDenied("no code") }

        let token = try await exchange(Self.formRequest([
            ("code", code), ("client_id", config.clientID), ("client_secret", config.clientSecret),
            ("redirect_uri", redirectURI), ("grant_type", "authorization_code"), ("code_verifier", pkce.verifier)
        ]))
        guard let refresh = token.refreshToken else {
            throw GoogleOAuthError.tokenRequestFailed(200, "Google returned no refresh token. Remove the app's access at myaccount.google.com/permissions and connect again.")
        }
        store.saveRefreshToken(refresh)
        cache(token)
    }

    /// A valid access token, refreshed from the stored refresh token when needed.
    public func accessToken(now: Date = Date()) async throws -> String {
        if let cached = validCachedToken(now: now) { return cached }
        let config = configuration()
        guard config.isComplete else { throw GoogleOAuthError.notConfigured }
        guard let refresh = store.refreshToken else { throw GoogleOAuthError.notAuthorized }
        do {
            let token = try await exchange(Self.formRequest([
                ("client_id", config.clientID), ("client_secret", config.clientSecret?.isEmpty == false ? config.clientSecret : store.clientSecret),
                ("refresh_token", refresh), ("grant_type", "refresh_token")
            ]))
            if let rotated = token.refreshToken { store.saveRefreshToken(rotated) }
            cache(token, now: now)
            return token.accessToken
        } catch GoogleOAuthError.tokenRequestFailed(let status, let reason) where reason.contains("invalid_grant") {
            // Revoked or expired: the stored token is useless, so it goes and the UI asks to connect again.
            store.deleteRefreshToken()
            throw GoogleOAuthError.tokenRequestFailed(status, "Google access was revoked or expired. Connect Google again.")
        }
    }

    public func disconnect() {
        store.deleteRefreshToken()
        withLock { cachedToken = nil }
    }

    private func validCachedToken(now: Date) -> String? {
        withLock { cachedToken.flatMap { $0.expires > now.addingTimeInterval(60) ? $0.value : nil } }
    }

    private func cache(_ token: GoogleTokenResponse, now: Date = Date()) {
        withLock { cachedToken = (token.accessToken, now.addingTimeInterval(TimeInterval(token.expiresIn))) }
    }

    private func withLock<T>(_ body: () -> T) -> T {
        lock.lock()
        defer { lock.unlock() }
        return body()
    }

    private func exchange(_ request: URLRequest) async throws -> GoogleTokenResponse {
        let (data, response) = try await http(request)
        guard (200..<300).contains(response.statusCode) else {
            let text = String(data: data, encoding: .utf8) ?? ""
            throw GoogleOAuthError.tokenRequestFailed(response.statusCode, text.prefix(300).description)
        }
        guard let token = try? JSONDecoder().decode(GoogleTokenResponse.self, from: data) else { throw GoogleOAuthError.invalidResponse }
        return token
    }
}

/// A one-shot HTTP listener on 127.0.0.1 that catches Google's redirect.
/// All mutable state is touched only on `queue`.
final class LoopbackRedirectReceiver: @unchecked Sendable {
    private var listener: NWListener?
    private let queue = DispatchQueue(label: "com.mohamedhosam.ZoomAutoAdmit.oauth-loopback")
    private var continuation: CheckedContinuation<[String: String], Error>?
    private var finished = false

    func start() async throws -> UInt16 {
        let parameters = NWParameters.tcp
        parameters.requiredLocalEndpoint = NWEndpoint.hostPort(host: .ipv4(.loopback), port: .any)
        let listener: NWListener
        do {
            listener = try NWListener(using: parameters)
        } catch {
            throw GoogleOAuthError.listenerFailed(error.localizedDescription)
        }
        self.listener = listener
        listener.newConnectionHandler = { [weak self] connection in self?.handle(connection) }
        return try await withCheckedThrowingContinuation { (ready: CheckedContinuation<UInt16, Error>) in
            // State updates arrive on this receiver's serial queue; the flag only guards a second resume.
            final class Once: @unchecked Sendable { var done = false }
            let once = Once()
            listener.stateUpdateHandler = { state in
                guard !once.done else { return }
                switch state {
                case .ready:
                    once.done = true
                    ready.resume(returning: listener.port?.rawValue ?? 0)
                case .failed(let error):
                    once.done = true
                    ready.resume(throwing: GoogleOAuthError.listenerFailed(error.localizedDescription))
                default:
                    break
                }
            }
            listener.start(queue: queue)
        }
    }

    func waitForRedirect(timeout: TimeInterval) async throws -> [String: String] {
        try await withCheckedThrowingContinuation { continuation in
            queue.async {
                self.continuation = continuation
                self.queue.asyncAfter(deadline: .now() + timeout) { self.finish(.failure(GoogleOAuthError.timedOut)) }
            }
        }
    }

    func stop() {
        listener?.cancel()
        listener = nil
    }

    private func handle(_ connection: NWConnection) {
        connection.start(queue: queue)
        connection.receive(minimumIncompleteLength: 1, maximumLength: 16_384) { [weak self] data, _, _, _ in
            guard let self else { return }
            let request = data.flatMap { String(data: $0, encoding: .utf8) } ?? ""
            let query = Self.parseRequestLine(request)
            let body = "<html><body style=\"font-family:-apple-system;padding:40px\"><h2>Zoom Auto Admit</h2><p>\(query["error"] == nil ? "Google access was granted. You can close this tab." : "Google sign-in was not completed.")</p></body></html>"
            let response = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: \(body.utf8.count)\r\nConnection: close\r\n\r\n\(body)"
            connection.send(content: Data(response.utf8), completion: .contentProcessed { _ in connection.cancel() })
            // Browsers also ask for /favicon.ico; only a request carrying code or error ends the wait.
            if query["code"] != nil || query["error"] != nil { self.finish(.success(query)) }
        }
    }

    private func finish(_ result: Result<[String: String], Error>) {
        guard !finished, let continuation else { return }
        finished = true
        self.continuation = nil
        continuation.resume(with: result)
    }

    static func parseRequestLine(_ request: String) -> [String: String] {
        guard let line = request.split(separator: "\r\n").first ?? request.split(separator: "\n").first else { return [:] }
        let parts = line.split(separator: " ")
        guard parts.count >= 2, let components = URLComponents(string: "http://127.0.0.1\(parts[1])") else { return [:] }
        var values: [String: String] = [:]
        for item in components.queryItems ?? [] { values[item.name] = item.value ?? "" }
        return values
    }
}
