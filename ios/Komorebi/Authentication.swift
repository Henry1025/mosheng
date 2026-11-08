import AuthenticationServices
import Combine
import CryptoKit
import Foundation
import Security
import UIKit

@MainActor final class Authentication: NSObject, ObservableObject, ASWebAuthenticationPresentationContextProviding {
    @Published var busy = false
    @Published var nonce: String?
    @Published var error: String?
    private var webSession: ASWebAuthenticationSession?
    private let api = APIClient.shared
    struct NonceResponse: Decodable { let nonce: String }
    struct SessionResponse: Decodable { let sessionToken: String }
    static func random() -> String {
        var bytes = [UInt8](repeating: 0, count: 32)
        precondition(SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes) == errSecSuccess)
        return Data(bytes).base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
    }
    static func sha256(_ value: String) -> String { SHA256.hash(data: Data(value.utf8)).map { String(format: "%02x", $0) }.joined() }
    static func challenge(_ value: String) -> String { Data(SHA256.hash(data: Data(value.utf8))).base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "") }
    func prepareApple() async {
        do { let response: NonceResponse = try await api.post("api/mobile/auth/nonce", [:]); nonce = response.nonce }
        catch { self.error = error.localizedDescription; nonce = nil }
    }
    func appleRequest(_ request: ASAuthorizationAppleIDRequest) {
        request.requestedScopes = [.email]
        request.nonce = nonce.map(Self.sha256)
    }
    func finishApple(_ result: Result<ASAuthorization, Error>, onComplete: @escaping () async -> Void) {
        guard !busy else { return }
        switch result {
        case .failure(let failure):
            if (failure as? ASAuthorizationError)?.code != .canceled { error = failure.localizedDescription }
        case .success(let authorization):
            guard let credential = authorization.credential as? ASAuthorizationAppleIDCredential, let data = credential.identityToken, let identityToken = String(data: data, encoding: .utf8), let code = credential.authorizationCode, let authorizationCode = String(data: code, encoding: .utf8), let nonce else { error = "Apple 登录没有完成，请重试。"; return }
            busy = true
            Task {
                defer { busy = false; self.nonce = nil }
                do { let response: SessionResponse = try await api.post("api/mobile/auth/apple", ["identityToken": identityToken, "authorizationCode": authorizationCode, "nonce": nonce]); try api.saveSession(response.sessionToken); await onComplete() }
                catch { self.error = error.localizedDescription }
            }
        }
    }
    func google(onComplete: @escaping () async -> Void) {
        guard !busy else { return }
        busy = true; error = nil
        let verifier = Self.random(), state = Self.random()
        var url = URLComponents(url: AppConfig.website("api/mobile/auth/start"), resolvingAgainstBaseURL: false)!
        url.queryItems = [URLQueryItem(name: "challenge", value: Self.challenge(verifier)), URLQueryItem(name: "state", value: state)]
        webSession = ASWebAuthenticationSession(url: url.url!, callbackURLScheme: AppConfig.callbackScheme) { [weak self] callback, failure in
            Task { @MainActor in
                guard let self else { return }
                defer { self.busy = false; self.webSession = nil }
                if let failure {
                    if (failure as? ASWebAuthenticationSessionError)?.code != .canceledLogin { self.error = failure.localizedDescription }; return
                }
                guard let callback, callback.scheme == AppConfig.callbackScheme, callback.host == "auth", let parts = URLComponents(url: callback, resolvingAgainstBaseURL: false), parts.queryItems?.first(where: { $0.name == "state" })?.value == state, let code = parts.queryItems?.first(where: { $0.name == "code" })?.value else { self.error = "登录校验失败，请重试。"; return }
                do { let response: SessionResponse = try await self.api.post("api/mobile/auth/exchange", ["code": code, "verifier": verifier]); try self.api.saveSession(response.sessionToken); await onComplete() }
                catch { self.error = error.localizedDescription }
            }
        }
        webSession?.presentationContextProvider = self
        webSession?.prefersEphemeralWebBrowserSession = true
        if webSession?.start() != true { busy = false; error = "无法打开登录窗口。"; webSession = nil }
    }
    func presentationAnchor(for session: ASWebAuthenticationSession) -> ASPresentationAnchor {
        UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.flatMap(\.windows).first(where: \.isKeyWindow) ?? ASPresentationAnchor()
    }
}
