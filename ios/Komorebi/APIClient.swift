import Foundation
import Security

enum APIError: LocalizedError {
    case server(String), invalidResponse
    var errorDescription: String? {
        switch self { case .server(let text): return text; case .invalidResponse: return "连接暂时不可用，请稍后重试。" }
    }
}

enum SessionVault {
    private static let service = "com.komorebichat.session"
    static func read() -> String? {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: "session", kSecReturnData as String: true, kSecMatchLimit as String: kSecMatchLimitOne]
        var item: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess, let data = item as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }
    static func write(_ token: String?) throws {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: "session"]
        SecItemDelete(query as CFDictionary)
        guard let token else { return }
        var value = query
        value[kSecValueData as String] = Data(token.utf8)
        value[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlockedThisDeviceOnly
        guard SecItemAdd(value as CFDictionary, nil) == errSecSuccess else { throw APIError.server("无法安全保存登录状态，请重试。") }
    }
}

private final class RejectRedirects: NSObject, URLSessionTaskDelegate {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) { completionHandler(nil) }
}
@MainActor final class APIClient {
    static let shared = APIClient()
    private let session: URLSession
    var token: String? = SessionVault.read()
    init() {
        let config = URLSessionConfiguration.ephemeral
        config.httpCookieStorage = nil
        config.requestCachePolicy = .reloadIgnoringLocalCacheData
        config.timeoutIntervalForRequest = 45
        session = URLSession(configuration: config, delegate: RejectRedirects(), delegateQueue: nil)
    }
    func saveSession(_ token: String?) throws { try SessionVault.write(token); self.token = token }
    func data(_ path: String, body: [String: Any]? = nil) async throws -> Data {
        guard AppConfig.origin.scheme == "https", AppConfig.origin.host == "komorebichat.com" else { throw APIError.invalidResponse }
        var request = URLRequest(url: AppConfig.website(path))
        request.httpMethod = body == nil ? "GET" : "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("Komorebi-iOS/1.0", forHTTPHeaderField: "User-Agent")
        if let token { request.setValue("Bearer " + token, forHTTPHeaderField: "Authorization") }
        if let body { request.httpBody = try JSONSerialization.data(withJSONObject: body) }
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, http.url?.host == AppConfig.origin.host else { throw APIError.invalidResponse }
        if !(200..<300).contains(http.statusCode) {
            let error = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
            throw APIError.server(error?["error"] as? String ?? "暂时无法完成，请重试。")
        }
        return data
    }
    func get<T: Decodable>(_ path: String, as type: T.Type = T.self) async throws -> T { try JSONDecoder().decode(type, from: await data(path)) }
    func post<T: Decodable>(_ path: String, _ body: [String: Any], as type: T.Type = T.self) async throws -> T { try JSONDecoder().decode(type, from: await data(path, body: body)) }
    func send(_ path: String, _ body: [String: Any]) async throws { _ = try await data(path, body: body) }
}
