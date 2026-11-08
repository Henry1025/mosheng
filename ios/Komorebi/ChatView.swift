import SwiftUI
import AVFoundation

struct ChatView: View {
    @EnvironmentObject var model: CompanionModel
    @State private var text = ""
    @State private var showCharacters = false
    @State private var showCall = false
    @State private var audio: AVAudioPlayer?
    @State private var audioBusy = false
    @AppStorage("blocked-sora") private var blockedSora = false
    @AppStorage("blocked-rin") private var blockedRin = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    var blocked: Bool { model.character == .sora ? blockedSora : blockedRin }
    var body: some View {
        NavigationStack {
            ZStack {
                Image(model.character.background).resizable().scaledToFill().containerRelativeFrame([.horizontal, .vertical]).clipped().ignoresSafeArea().overlay(.black.opacity(0.30))
                LinearGradient(colors: [.black.opacity(0.10), .black.opacity(0.8)], startPoint: .top, endPoint: .bottom).ignoresSafeArea()
                VStack(spacing: 0) {
                    HStack {
                        Button { showCharacters = true } label: { HStack { Text(model.character.name).font(.title2.bold()); Image(systemName: "chevron.down").font(.caption) } }
                        Text("AI 角色").font(.caption2).foregroundStyle(.white.opacity(0.7))
                        Spacer()
                        Button { showCall = true } label: { Image(systemName: "phone.fill") }.padding(.leading, 16).accessibilityLabel("和她通话")
                    }.padding(20)
                    Spacer(minLength: 30)
                    if blocked {
                        ContentUnavailableView("角色已屏蔽", systemImage: "hand.raised", description: Text("你可以选择另一位角色，或在角色选择里解除屏蔽。"))
                    } else {
                        Text(model.character.subtitle).font(.headline).shadow(radius: 10).padding(.bottom, 16)
                        ScrollViewReader { proxy in
                            ScrollView {
                                LazyVStack(alignment: .leading, spacing: 14) {
                                    if model.messages.isEmpty { bubble(ChatMessage(role: "assistant", content: model.character.greeting)) }
                                    ForEach(Array(model.messages.enumerated()), id: \.offset) { index, message in bubble(message).id(index) }
                                    if model.sending { ProgressView("她正在想怎么回答…").font(.caption).tint(.pink) }
                                    Color.clear.frame(height: 1).id("bottom")
                                }.padding(18)
                            }.frame(maxHeight: 420).background(.ultraThinMaterial).clipShape(RoundedRectangle(cornerRadius: 28))
                                .onChange(of: model.messages.count) { if reduceMotion { proxy.scrollTo("bottom") } else { withAnimation { proxy.scrollTo("bottom", anchor: .bottom) } } }
                        }
                        ScrollView(.horizontal, showsIndicators: false) { HStack(spacing: 9) { ForEach(["今天有点想你", "陪我聊一会儿", "想抱抱", "说说你的今天"], id: \.self) { prompt in Button(prompt) { text = prompt }.buttonStyle(.bordered).tint(.white) } }.padding(.horizontal, 18) }.padding(.vertical, 12)
                        HStack(alignment: .bottom, spacing: 12) {
                            TextField("把想说的话留给她…", text: $text, axis: .vertical).lineLimit(1...4).padding(13).background(.white.opacity(0.14), in: RoundedRectangle(cornerRadius: 18)).accessibilityIdentifier("chat-input")
                            Button { let value = text; Task { if await model.send(value) { text = "" } } } label: { Image(systemName: "arrow.up").font(.headline).frame(width: 46, height: 46).background(.pink, in: Circle()) }.disabled(model.sending || text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || text.count > 2000).accessibilityLabel("发送消息")
                        }.padding(.horizontal, 18)
                        Text("今日剩余 \(model.state?.remaining ?? 0) 条 · AI 生成的陪伴，请保留自己的判断").font(.caption2).foregroundStyle(.white.opacity(0.55)).padding(.vertical, 10)
                    }
                }
            }.toolbar(.hidden, for: .navigationBar).clipped()
                .sheet(isPresented: $showCharacters) { characterSheet.presentationDetents([.medium]) }
                .sheet(isPresented: $showCall, onDismiss: { Task { await model.refresh() } }) { CallView(character: model.character) }
        }
    }
    func bubble(_ message: ChatMessage) -> some View {
        HStack {
            if message.role == "user" { Spacer(minLength: 40) }
            VStack(alignment: .leading, spacing: 9) {
                Text(message.content).textSelection(.enabled).font(.system(size: 16)).lineSpacing(5)
                if message.role == "assistant" { Button { Task { await play(message.content) } } label: { Label("听她说", systemImage: "waveform").font(.caption) }.disabled(audioBusy) }
            }.padding(14).background(message.role == "user" ? Color.pink.opacity(0.64) : Color.white.opacity(0.14), in: RoundedRectangle(cornerRadius: 20))
                .contextMenu { if message.role == "assistant" { Button("举报这条回复", systemImage: "flag") { Task { await model.report(message) } } }; ShareLink(item: message.content) { Label("分享文字", systemImage: "square.and.arrow.up") } }
            if message.role != "user" { Spacer(minLength: 40) }
        }
    }
    var characterSheet: some View {
        NavigationStack {
            List {
                ForEach(Companion.allCases) { companion in
                    Button { model.character = companion; showCharacters = false } label: { VStack(alignment: .leading, spacing: 8) { Text("\(companion.name) · \(companion.age)").font(.headline); Text(companion.subtitle).font(.subheadline).foregroundStyle(.secondary) }.padding(.vertical, 8) }
                }
                Section("角色偏好") { Toggle("屏蔽 Sora", isOn: $blockedSora); Toggle("屏蔽 Rin", isOn: $blockedRin) }
            }.navigationTitle("今天想见谁？").navigationBarTitleDisplayMode(.inline)
        }
    }
    func play(_ content: String) async {
        guard content.count <= 500 else { model.error = "这条回复较长，暂时无法整段播放。"; return }
        audioBusy = true; defer { audioBusy = false }
        do { audio?.stop(); let data = try await APIClient.shared.data("api/voice", body: ["character": model.character.rawValue, "text": content]); try AVAudioSession.sharedInstance().setCategory(.playback, mode: .spokenAudio); audio = try AVAudioPlayer(data: data); audio?.play() }
        catch { model.error = error.localizedDescription }
    }
}
