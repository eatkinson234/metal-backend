import SwiftUI

struct HeroView: View {
    @ObservedObject var model: LobbyStore
    let compact: Bool
    let short: Bool
    let height: CGFloat
    var body: some View {
        ZStack(alignment: .leading) {
            GeometryReader { geometry in
                if let image = CouchTheme.arenaImage {
                    Image(nsImage: image).resizable().scaledToFill()
                        .frame(width: geometry.size.width, height: geometry.size.height).clipped()
                }
            }
            LinearGradient(stops: [
                .init(color: .white, location: 0),
                .init(color: .white, location: 0.32),
                .init(color: .white.opacity(0.90), location: 0.48),
                .init(color: .white.opacity(0.12), location: 0.76)
            ], startPoint: .leading, endPoint: .trailing)
            VStack(alignment: .leading, spacing: 0) {
                Text("Super Smash Bros.\nUltimate")
                    .font(.system(size: short ? 33 : (compact ? 43 : 48), weight: .heavy))
                    .tracking(-1.6).lineSpacing(-2).foregroundStyle(CouchTheme.text)
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 20)
                HStack(spacing: 18) {
                    CouchActionButton(id: "play") { model.requestPlay() } label: {
                        Label(model.starting ? "Starting…" : (model.running ? "Game running" : "Play Smash"), systemImage: "play.fill")
                    }.buttonStyle(CouchButtonStyle(prominent: true, compact: short)).disabled(model.running || model.starting)
                    Text(model.starting ? "Starting game" : (model.running ? "Keep the launcher open" : (model.readiness.canPlay ? "Ready" : "Connect a controller")))
                        .font(.system(size: 13, weight: .medium))
                        .foregroundStyle(model.readiness.canPlay ? CouchTheme.mint : CouchTheme.secondary)
                }
            }.padding(short ? 24 : 34)
        }
        .frame(height: height).clipShape(RoundedRectangle(cornerRadius: 20))
        .overlay(RoundedRectangle(cornerRadius: 20).strokeBorder(CouchTheme.line))
        .accessibilityElement(children: .contain)
    }
}
