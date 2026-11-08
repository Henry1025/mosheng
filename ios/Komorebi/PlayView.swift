import SwiftUI

struct ConnectFour {
    var cells = Array(repeating: 0, count: 42)
    var turn = 1
    var winner = 0
    mutating func drop(_ column: Int) {
        guard winner == 0, (0..<7).contains(column), let row = (0..<6).reversed().first(where: { cells[$0 * 7 + column] == 0 }) else { return }
        cells[row * 7 + column] = turn
        for (dr, dc) in [(0, 1), (1, 0), (1, 1), (1, -1)] {
            for r in 0..<6 { for c in 0..<7 {
                if (0..<6).contains(r + dr * 3), (0..<7).contains(c + dc * 3), (0..<4).allSatisfy({ cells[(r + dr * $0) * 7 + c + dc * $0] == turn }) { winner = turn }
            } }
        }
        if winner == 0 { turn = turn == 1 ? 2 : 1 }
    }
}
struct PlayView: View {
    @EnvironmentObject var model: CompanionModel
    @State private var game = ConnectFour()
    @State private var endDate: Date?
    @State private var question = "今天有没有一件让你偷偷开心的小事？"
    let questions = ["今天有没有一件让你偷偷开心的小事？", "如果现在能一起散步，你想去哪里？", "你最容易被哪种小细节打动？", "下次见面，你想让我先说什么？"]
    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: 28) {
                    VStack(alignment: .leading, spacing: 12) {
                        Text("今天也见面了").font(.title2.bold())
                        Text("已经留下 \(model.state?.days.count ?? 0) 天的足迹。").foregroundStyle(.secondary)
                        Button("记下今天的见面") { Task { await model.checkIn() } }.buttonStyle(.borderedProminent).tint(.pink)
                    }.padding(20).frame(maxWidth: .infinity, alignment: .leading).background(.white.opacity(0.07), in: RoundedRectangle(cornerRadius: 24))
                    VStack(alignment: .leading, spacing: 15) {
                        Text("四子棋 · 双人小棋盘").font(.title2.bold())
                        Text(game.winner == 0 ? "轮到\(game.turn == 1 ? "粉色" : "蓝色")，点击想落子的列。" : "\(game.winner == 1 ? "粉色" : "蓝色")赢了，再来一局？").font(.subheadline).foregroundStyle(.secondary)
                        HStack(spacing: 6) { ForEach(0..<7) { column in
                            Button { game.drop(column) } label: { VStack(spacing: 7) { ForEach(0..<6) { row in Circle().fill(game.cells[row * 7 + column] == 1 ? Color.pink : game.cells[row * 7 + column] == 2 ? Color.cyan : Color.white.opacity(0.10)).aspectRatio(1, contentMode: .fit) } }.accessibilityLabel("第 \(column + 1) 列落子").accessibilityIdentifier("connect-column-\(column)")
                        } }.padding(12).background(.indigo.opacity(0.3), in: RoundedRectangle(cornerRadius: 18))
                        Button("重新开始") { game = ConnectFour() }
                    }
                    VStack(alignment: .leading, spacing: 14) {
                        Text("靠近一点的小问题").font(.title2.bold())
                        Text(question).font(.title3).padding(.vertical, 8)
                        Button("再抽一个") { question = questions.filter { $0 != question }.randomElement() ?? questions[0] }
                    }.padding(20).frame(maxWidth: .infinity, alignment: .leading).background(.pink.opacity(0.12), in: RoundedRectangle(cornerRadius: 24))
                    VStack(alignment: .leading, spacing: 14) {
                        Text("安静陪你 25 分钟").font(.title2.bold())
                        if let endDate { Text(endDate, style: .timer).font(.system(size: 40, weight: .light, design: .monospaced)); Button("结束专注") { self.endDate = nil } }
                        else { Text("不说话也可以，我陪你把手上的事做完。").foregroundStyle(.secondary); Button("开始专注") { endDate = Date().addingTimeInterval(25 * 60) } }
                    }
                }.padding(22)
            }.navigationTitle("一起消磨时间")
        }
    }
}
