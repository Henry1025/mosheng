import SwiftUI
import WebKit
import AVFoundation

struct CallView: View {
    let character: Companion
    @Environment(\.dismiss) private var dismiss
    var body: some View {
        NavigationStack {
            CallSurface(character: character)
                .navigationTitle("和 \(character.name) 通话").navigationBarTitleDisplayMode(.inline)
                .toolbar { Button("完成") { dismiss() } }
        }.interactiveDismissDisabled(false)
    }
}

struct CallSurface: UIViewRepresentable {
    let character: Companion
    func makeCoordinator() -> Coordinator { Coordinator() }
    func makeUIView(context: Context) -> WKWebView {
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .nonPersistent()
        configuration.allowsInlineMediaPlayback = true
        configuration.mediaTypesRequiringUserActionForPlayback = []
        let web = WKWebView(frame: .zero, configuration: configuration)
        web.navigationDelegate = context.coordinator
        web.uiDelegate = context.coordinator
        web.isOpaque = false; web.backgroundColor = .black
        guard let token = APIClient.shared.token, let host = AppConfig.origin.host,
              let cookie = HTTPCookie(properties: [.name: "__Host-komorebi-session", .value: token, .domain: host, .path: "/", .secure: "TRUE", HTTPCookiePropertyKey("HttpOnly"): "TRUE"]) else { return web }
        configuration.websiteDataStore.httpCookieStore.setCookie(cookie) {
            var parts = URLComponents(url: AppConfig.website("mobile/call"), resolvingAgainstBaseURL: false)!
            parts.queryItems = [URLQueryItem(name: "character", value: character.rawValue)]
            web.load(URLRequest(url: parts.url!))
        }
        return web
    }
    func updateUIView(_ web: WKWebView, context: Context) {}
    static func dismantleUIView(_ web: WKWebView, coordinator: Coordinator) {
        web.evaluateJavaScript("window.KomorebiRealtime?.end()") { _, _ in web.stopLoading() }
    }
    final class Coordinator: NSObject, WKNavigationDelegate, WKUIDelegate {
        func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction, decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
            guard let url = navigationAction.request.url, url.scheme == "https", url.host == AppConfig.origin.host, url.path == "/mobile/call" else { decisionHandler(.cancel); return }
            decisionHandler(.allow)
        }
        func webView(_ webView: WKWebView, requestMediaCapturePermissionFor origin: WKSecurityOrigin, initiatedByFrame frame: WKFrameInfo, type: WKMediaCaptureType, decisionHandler: @escaping (WKPermissionDecision) -> Void) {
            decisionHandler(origin.protocol == "https" && origin.host == AppConfig.origin.host && frame.isMainFrame ? .prompt : .deny)
        }
    }
}
