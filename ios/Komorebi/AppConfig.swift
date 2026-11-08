import Foundation

enum AppConfig {
    static let origin = URL(string: Bundle.main.object(forInfoDictionaryKey: "KomorebiAPIOrigin") as? String ?? "https://komorebichat.com")!
    static let callbackScheme = "komorebichat"
    static let supportEmail = "komorebi-support@googlegroups.com"
    static let consentVersion = "2026-10-25"
    static func website(_ path: String) -> URL { origin.appendingPathComponent(path) }
}

enum Companion: String, CaseIterable, Identifiable, Codable {
    case sora, rin
    var id: String { rawValue }
    var name: String { self == .sora ? "Sora" : "Rin" }
    var age: Int { self == .sora ? 24 : 25 }
    var subtitle: String { self == .sora ? "把今天，慢慢说给我听。" : "想我了？那就多待一会儿。" }
    var background: String { self == .sora ? "sora-room" : "rin-room" }
    var greeting: String { self == .sora ? "你来了。刚好，我也在等一个能聊聊天的人。" : "终于出现了。今天想让我陪你聊什么？" }
}

struct ChatMessage: Codable, Identifiable {
    var role: String
    var content: String
    var id: String { role + ":" + content }
}
struct Profile: Codable { var nickname: String; var created_at: String }
struct AppState: Codable {
    var signedIn: Bool
    var profile: Profile?
    var remaining: Int
    var limit: Int?
    var histories: [String: [ChatMessage]]
    var memories: [String: String]
    var days: [String]
}
struct MobileConfig: Decodable {
    struct Providers: Decodable { var google: Bool; var apple: Bool }
    var enabled: Bool
    var providers: Providers
}
