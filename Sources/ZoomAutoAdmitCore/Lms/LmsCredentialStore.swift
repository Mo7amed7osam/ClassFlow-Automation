import Foundation
import Security

/// The DEPI dashboard sign-in.
public struct LmsAccount: Equatable {
    public var email: String
    public var password: String

    public init(email: String, password: String) {
        self.email = email
        self.password = password
    }
}

/// Where the dashboard sign-in is kept: the macOS Keychain.
///
/// Never in `schedules.json`, never in a log line. The password leaves this type only to be
/// sent on the helper's stdin, which types it into the dashboard's sign-in form.
public protocol LmsCredentialStoring {
    func read() -> LmsAccount?
    @discardableResult func save(_ account: LmsAccount) -> Bool
    @discardableResult func delete() -> Bool
}

public struct LmsCredentialStore: LmsCredentialStoring {
    public static let service = "com.mohamedhosam.ZoomAutoAdmit.LMS"
    private let service: String

    public init(service: String = LmsCredentialStore.service) {
        self.service = service
    }

    public func read() -> LmsAccount? {
        var query = baseQuery
        query[kSecReturnData as String] = true
        query[kSecReturnAttributes as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var item: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess,
              let attributes = item as? [String: Any],
              let email = attributes[kSecAttrAccount as String] as? String,
              let data = attributes[kSecValueData as String] as? Data,
              let password = String(data: data, encoding: .utf8) else {
            return nil
        }
        return LmsAccount(email: email, password: password)
    }

    @discardableResult
    public func save(_ account: LmsAccount) -> Bool {
        let email = account.email.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !email.isEmpty, !account.password.isEmpty, let data = account.password.data(using: .utf8) else { return false }
        SecItemDelete(baseQuery as CFDictionary)
        var attributes = baseQuery
        attributes[kSecAttrAccount as String] = email
        attributes[kSecValueData as String] = data
        // After first unlock, so a class at 7 AM can sign in without anyone at the keyboard.
        attributes[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
        return SecItemAdd(attributes as CFDictionary, nil) == errSecSuccess
    }

    @discardableResult
    public func delete() -> Bool {
        let status = SecItemDelete(baseQuery as CFDictionary)
        return status == errSecSuccess || status == errSecItemNotFound
    }

    private var baseQuery: [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service]
    }
}
