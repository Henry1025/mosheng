import SwiftUI
import Combine

@main struct KomorebiApp: App {
    @StateObject private var model = CompanionModel()
    var body: some Scene {
        WindowGroup { RootView().environmentObject(model).preferredColorScheme(.dark) }
    }
}

@MainActor final class CompanionModel: ObservableObject {
    @Published var state: AppState?
    @Published var config: MobileConfig?
    @Published var loading = true
    @Published var sending = false
    @Published var error: String?
    @Published var character: Companion = .sora
    @Published var requiresConsent = true
    private let api = APIClient.shared
    var signedIn: Bool { state?.signedIn == true }
    var messages: [ChatMessage] { state?.histories[character.rawValue] ?? [] }
    func refresh() async {
        defer { loading = false }
        do {
            config = try await api.get("api/mobile/config")
            state = try await api.get("api/state")
            if state?.signedIn == true {
                struct Account: Decodable { let consented: Bool }
                let account: Account = try await api.post("api/mobile/account", [:])
                requiresConsent = !account.consented
            } else { requiresConsent = true }
        } catch { self.error = error.localizedDescription }
    }
    func createProfile(_ nickname: String) async {
        do { try await api.send("api/profile", ["nickname": nickname, "adult": true]); await refresh() }
        catch { self.error = error.localizedDescription }
    }
    func consent() async {
        do { try await api.send("api/mobile/consent", ["accepted": true, "version": AppConfig.consentVersion]); UserDefaults.standard.set(true, forKey: "ai-consent-" + AppConfig.consentVersion); requiresConsent = false }
        catch { self.error = error.localizedDescription }
    }
    func send(_ text: String) async -> Bool {
        guard !sending, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return false }
        sending = true; defer { sending = false }
        struct Reply: Decodable { var reply: String; var remaining: Int; var days: [String] }
        let selected = character
        do {
            let response: Reply = try await api.post("api/chat", ["character": selected.rawValue, "text": text])
            state?.histories[selected.rawValue, default: []].append(contentsOf: [ChatMessage(role: "user", content: text), ChatMessage(role: "assistant", content: response.reply)])
            state?.remaining = response.remaining; state?.days = response.days
            return true
        } catch { self.error = error.localizedDescription; return false }
    }
    func checkIn() async { do { try await api.send("api/checkin", [:]); await refresh() } catch { self.error = error.localizedDescription } }
    func report(_ message: ChatMessage) async {
        do { try await api.send("api/mobile/report", ["character": character.rawValue, "content": message.content, "reason": "用户反馈：不合适的回复"]); error = "反馈已收到。你也可以在角色选择中暂时屏蔽她。" }
        catch { self.error = error.localizedDescription }
    }
    func signOut() async {
        do { try await api.send("api/mobile/signout", [:]); try api.saveSession(nil); state = nil; UserDefaults.standard.removeObject(forKey: "ai-consent-" + AppConfig.consentVersion); await refresh() }
        catch { self.error = error.localizedDescription }
    }
    func deleteAccount() async {
        do { try await api.send("api/mobile/delete-account", ["confirmation": "DELETE"]); try api.saveSession(nil); state = nil; UserDefaults.standard.removeObject(forKey: "ai-consent-" + AppConfig.consentVersion); await refresh() }
        catch { self.error = error.localizedDescription }
    }
}

struct RootView: View {
    @EnvironmentObject var model: CompanionModel
    var body: some View {
        Group {
            if model.loading { ProgressView("推开今天的门…").tint(.pink) }
            else if !model.signedIn { WelcomeView() }
            else if model.state?.profile == nil { ProfileView() }
            else if model.requiresConsent { ConsentView() }
            else { MainTabs() }
        }
        .background(Color(red: 0.08, green: 0.06, blue: 0.13).ignoresSafeArea())
        .task { await model.refresh() }
        .alert("Komorebi", isPresented: Binding(get: { model.error != nil }, set: { if !$0 { model.error = nil } })) { Button("知道了") { model.error = nil } } message: { Text(model.error ?? "") }
    }
}

struct WelcomeView: View {
    @EnvironmentObject var model: CompanionModel
    @StateObject private var auth = Authentication()
    var body: some View {
        ZStack {
            Image("sora-room").resizable().scaledToFill().containerRelativeFrame([.horizontal, .vertical]).clipped().ignoresSafeArea().overlay(LinearGradient(colors: [.clear, .black.opacity(0.94)], startPoint: .top, endPoint: .bottom))
            VStack(alignment: .leading, spacing: 20) {
                Spacer()
                Text("KOMOREBI").font(.caption).tracking(6).foregroundStyle(.pink.opacity(0.85))
                Text("有些话，\n只想说给你听。").font(.system(size: 38, weight: .semibold, design: .rounded))
                Text("认识 Sora 和 Rin，让日常多一点心动。\nAI 角色聊天 · 声音 · 两个人的小游乐场").font(.subheadline).foregroundStyle(.white.opacity(0.75)).lineSpacing(7)
                if model.config?.providers.apple == true {
                    SignInWithAppleButton(.continue, onRequest: auth.appleRequest) { result in auth.finishApple(result) { await model.refresh() } }
                        .signInWithAppleButtonStyle(.white).frame(height: 52).clipShape(RoundedRectangle(cornerRadius: 15))
                        .disabled(auth.busy || auth.nonce == nil)
                }
                Button { auth.google { await model.refresh() } } label: { Label("使用 Google 继续", systemImage: "person.crop.circle.badge.checkmark").frame(maxWidth: .infinity).frame(height: 50) }.buttonStyle(.borderedProminent).tint(.pink.opacity(0.8)).disabled(auth.busy || model.config?.providers.google != true)
                if auth.busy { ProgressView().frame(maxWidth: .infinity) }
                if model.config?.enabled != true { Text("App 登录正在配置。你仍可在官网体验现有服务。").font(.footnote).foregroundStyle(.secondary) }
                if let error = auth.error { Text(error).font(.footnote).foregroundStyle(.orange); Button("重试登录准备") { Task { await auth.prepareApple() } } }
                HStack { Link("条款", destination: AppConfig.website("terms")); Text("·"); Link("隐私", destination: AppConfig.website("privacy")); Text("· 仅限 18 岁以上") }.font(.caption).foregroundStyle(.white.opacity(0.6))
            }.padding(28).padding(.bottom, 20)
        }.clipped().task { if model.config?.providers.apple == true { await auth.prepareApple() } }
    }
}

import AuthenticationServices

struct ProfileView: View {
    @EnvironmentObject var model: CompanionModel
    @State private var nickname = ""
    @State private var adult = false
    @State private var busy = false
    var body: some View {
        VStack(alignment: .leading, spacing: 24) {
            Text("我该怎么叫你？").font(.largeTitle.bold())
            Text("用你喜欢的昵称就好。她会记得。").foregroundStyle(.secondary)
            TextField("你的昵称", text: $nickname).textFieldStyle(.roundedBorder).accessibilityIdentifier("nickname")
            Toggle("我已满 18 岁，并同意服务条款", isOn: $adult)
            Link("查看条款", destination: AppConfig.website("terms"))
            Button("去见她") { busy = true; Task { await model.createProfile(nickname); busy = false } }.buttonStyle(.borderedProminent).tint(.pink).disabled(busy || !adult || nickname.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || nickname.count > 24)
            Button("退出登录") { Task { await model.signOut() } }.font(.footnote)
        }.padding(28)
    }
}

struct ConsentView: View {
    @EnvironmentObject var model: CompanionModel
    var body: some View {
        VStack(alignment: .leading, spacing: 24) {
            Image(systemName: "bubble.left.and.text.bubble.right").font(.system(size: 44)).foregroundStyle(.pink)
            Text("开始之前，\n把选择交给你。").font(.largeTitle.bold())
            Text("Sora 和 Rin 是虚构的 AI 角色，不是真人。聊天会将你的昵称、消息、记忆笔记和近期对话发送给第三方 AI 服务处理。")
            Text("当你播放声音或拨打电话时，语音服务会处理相关文本或麦克风音频。通话不保留原始录音；可用的通话文字会保存到聊天。")
            Text("你可以举报回复、屏蔽角色，也可以在设置里删除账号。AI 可能出错，不能替代真人关系或专业帮助。").foregroundStyle(.secondary)
            Link("查看服务商及完整隐私说明", destination: AppConfig.website("privacy"))
            Button("同意并开始") { Task { await model.consent() } }.buttonStyle(.borderedProminent).tint(.pink)
            Button("暂不使用，退出登录") { Task { await model.signOut() } }.font(.footnote)
        }.padding(28)
    }
}

struct MainTabs: View {
    var body: some View {
        TabView {
            ChatView().tabItem { Label("陪伴", systemImage: "bubble.left.and.bubble.right.fill") }
            PlayView().tabItem { Label("一起玩", systemImage: "gamecontroller.fill") }
            AccountView().tabItem { Label("我的", systemImage: "person.crop.circle") }
        }.tint(.pink)
    }
}
