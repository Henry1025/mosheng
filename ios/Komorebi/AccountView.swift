import SwiftUI

struct AccountView: View {
    @EnvironmentObject var model: CompanionModel
    @State private var confirmDelete = false
    @State private var deleting = false
    @State private var memory = ""
    @State private var memoryLoaded = false
    var body: some View {
        NavigationStack {
            Form {
                Section { Text(model.state?.profile?.nickname ?? "你").font(.title2.bold()); Text("免费版").foregroundStyle(.secondary); Text("今天还可发送 \(model.state?.remaining ?? 0) 条文字消息").font(.footnote) }
                Section("留给 \(model.character.name) 的记忆笔记") {
                    TextEditor(text: $memory).frame(minHeight: 110)
                    Text("只保存你愿意告诉她的内容，最多 600 字符。").font(.footnote).foregroundStyle(.secondary)
                    Button("保存笔记") { Task { do { try await APIClient.shared.send("api/memory", ["character": model.character.rawValue, "note": memory]); await model.refresh() } catch { model.error = error.localizedDescription } } }.disabled(memory.count > 600)
                }
                Section("帮助与隐私") {
                    Link("联系客服 / 举报问题", destination: URL(string: "mailto:" + AppConfig.supportEmail)!)
                    Link("隐私与第三方 AI 服务", destination: AppConfig.website("privacy"))
                    Link("服务条款", destination: AppConfig.website("terms"))
                    Button("重新查看 AI 数据处理说明") { model.requiresConsent = true }
                }
                Section("账号") {
                    Button("退出登录") { Task { await model.signOut() } }
                    Button("删除账号及聊天数据", role: .destructive) { confirmDelete = true }.disabled(deleting)
                }
                Section { Text("Komorebi 1.0 · Sora 和 Rin 都是虚构的成年 AI 角色。\nAI 陪伴不能代替紧急援助或专业服务。").font(.caption).foregroundStyle(.secondary) }
            }.navigationTitle("我的小角落")
                .task { if !memoryLoaded { memory = model.state?.memories[model.character.rawValue] ?? ""; memoryLoaded = true } }
                .onChange(of: model.character) { memory = model.state?.memories[model.character.rawValue] ?? "" }
                .alert("删除账号？", isPresented: $confirmDelete) { Button("永久删除", role: .destructive) { deleting = true; Task { await model.deleteAccount(); deleting = false } }; Button("取消", role: .cancel) {} } message: { Text("这会删除登录账号、聊天、记忆和打卡，无法恢复。") }
        }
    }
}
