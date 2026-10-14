import AppKit
import SwiftUI

// Renders LogoMark to a square PNG: RenderIcon <output.png> <pixels>
@main
struct RenderIcon {
    @MainActor
    static func main() throws {
    let arguments = CommandLine.arguments
    let output = URL(fileURLWithPath: arguments[1])
    let pixels = CGFloat(Double(arguments[2]) ?? 1024)
    // macOS icon grid: the artwork fills about 80% of the canvas, centred, leaving room for the shadow.
    let view = ZStack {
        Color.clear
        LogoMark()
            .frame(width: pixels * 0.805, height: pixels * 0.805)
            .shadow(color: .black.opacity(0.28), radius: pixels * 0.012, y: pixels * 0.010)
    }
    .frame(width: pixels, height: pixels)
    let renderer = ImageRenderer(content: view)
    renderer.scale = 1
    guard let image = renderer.cgImage else { throw NSError(domain: "RenderIcon", code: 1) }
    let rep = NSBitmapImageRep(cgImage: image)
    guard let data = rep.representation(using: .png, properties: [:]) else { throw NSError(domain: "RenderIcon", code: 2) }
    try data.write(to: output)
    }
}
