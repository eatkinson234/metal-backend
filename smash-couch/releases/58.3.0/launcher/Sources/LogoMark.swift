import SwiftUI

// The Smash Couch mark: a chunky couch in front of a smash burst on the launcher's
// flame gradient. Pure SwiftUI so the header and the app icon share one drawing.
struct LogoMark: View {
    /// Corner radius as a fraction of the side (macOS icons use about 0.225).
    var cornerFraction: CGFloat = 0.225

    private static let flameA = Color(red: 1.00, green: 0.42, blue: 0.24)
    private static let flameC = Color(red: 0.88, green: 0.13, blue: 0.40)
    private static let burst = Color(red: 1.00, green: 0.80, blue: 0.22)
    private static let burstCore = Color(red: 1.00, green: 0.95, blue: 0.70)
    private static let cushion = Color(red: 1.00, green: 0.97, blue: 0.92)
    private static let ink = Color(red: 0.17, green: 0.09, blue: 0.22)

    var body: some View {
        GeometryReader { proxy in
            let s = min(proxy.size.width, proxy.size.height)
            ZStack {
                RoundedRectangle(cornerRadius: s * cornerFraction, style: .continuous)
                    .fill(LinearGradient(colors: [Self.flameA, Self.flameC], startPoint: .topLeading, endPoint: .bottomTrailing))
                RoundedRectangle(cornerRadius: s * cornerFraction, style: .continuous)
                    .fill(RadialGradient(colors: [.white.opacity(0.28), .clear], center: UnitPoint(x: 0.25, y: 0.18),
                                         startRadius: 0, endRadius: s * 0.75))
                Burst(points: 12, inner: 0.62)
                    .fill(Self.burst)
                    .frame(width: s * 0.88, height: s * 0.88)
                    .rotationEffect(.degrees(-6))
                    .offset(y: -s * 0.07)
                Burst(points: 12, inner: 0.60)
                    .fill(Self.burstCore)
                    .frame(width: s * 0.54, height: s * 0.54)
                    .rotationEffect(.degrees(12))
                    .offset(y: -s * 0.09)
                Ellipse()
                    .fill(Self.ink.opacity(0.28))
                    .frame(width: s * 0.66, height: s * 0.07)
                    .offset(y: s * 0.27)
                CouchFigure(fill: Self.cushion, ink: Self.ink, line: s * 0.026)
                    .frame(width: s * 0.70, height: s * 0.44)
                    .offset(y: s * 0.09)
            }
            .frame(width: s, height: s)
            .clipShape(RoundedRectangle(cornerRadius: s * cornerFraction, style: .continuous))
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .aspectRatio(1, contentMode: .fit)
    }
}

/// A star burst with alternating outer and inner radii.
private struct Burst: Shape {
    let points: Int
    let inner: CGFloat

    func path(in rect: CGRect) -> Path {
        let center = CGPoint(x: rect.midX, y: rect.midY)
        let outer = min(rect.width, rect.height) / 2
        var path = Path()
        // Uneven spike lengths read as an impact rather than a sticker star.
        let spikes: [CGFloat] = [1.00, 0.80, 0.94, 0.76, 1.00, 0.84, 0.90, 0.78, 0.98, 0.82, 0.92, 0.80]
        for index in 0..<(points * 2) {
            let radius = index.isMultiple(of: 2) ? outer * spikes[(index / 2) % spikes.count] : outer * inner
            let angle = Double(index) * .pi / Double(points) - .pi / 2
            let point = CGPoint(x: center.x + radius * CGFloat(cos(angle)), y: center.y + radius * CGFloat(sin(angle)))
            index == 0 ? path.move(to: point) : path.addLine(to: point)
        }
        path.closeSubpath()
        return path
    }
}

/// Couch drawn back to front (legs, backrest, seat, arms) so each part's outline
/// is hidden where a nearer part covers it, like a cartoon cel.
private struct CouchFigure: View {
    let fill: Color
    let ink: Color
    let line: CGFloat

    var body: some View {
        GeometryReader { proxy in
            let w = proxy.size.width, h = proxy.size.height, u = min(w, h)
            ZStack(alignment: .topLeading) {
                part(x: 0.07, y: 0.80, w: 0.08, h: 0.20, radius: 0.03, w, h, u)
                part(x: 0.85, y: 0.80, w: 0.08, h: 0.20, radius: 0.03, w, h, u)
                part(x: 0.12, y: 0.00, w: 0.76, h: 0.60, radius: 0.17, w, h, u)
                Path { path in
                    path.move(to: CGPoint(x: w * 0.50, y: h * 0.12))
                    path.addLine(to: CGPoint(x: w * 0.50, y: h * 0.48))
                }
                .stroke(ink, style: StrokeStyle(lineWidth: line * 0.7, lineCap: .round))
                part(x: 0.14, y: 0.50, w: 0.72, h: 0.34, radius: 0.08, w, h, u)
                Path { path in
                    path.move(to: CGPoint(x: w * 0.50, y: h * 0.56))
                    path.addLine(to: CGPoint(x: w * 0.50, y: h * 0.78))
                }
                .stroke(ink, style: StrokeStyle(lineWidth: line * 0.7, lineCap: .round))
                part(x: 0.00, y: 0.30, w: 0.20, h: 0.56, radius: 0.10, w, h, u)
                part(x: 0.80, y: 0.30, w: 0.20, h: 0.56, radius: 0.10, w, h, u)
            }
        }
    }

    private func part(x: CGFloat, y: CGFloat, w pw: CGFloat, h ph: CGFloat, radius: CGFloat,
                      _ w: CGFloat, _ h: CGFloat, _ u: CGFloat) -> some View {
        let shape = RoundedRectangle(cornerRadius: radius * u, style: .continuous)
        return shape.fill(fill)
            .overlay(shape.stroke(ink, style: StrokeStyle(lineWidth: line, lineJoin: .round)))
            .frame(width: pw * w, height: ph * h)
            .offset(x: x * w, y: y * h)
    }
}
