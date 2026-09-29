// Renders the app icon (the app glyph on a light rounded plate) in every iconset size.
// Built together with Sources/Glyph.swift, see build.sh. Usage: make-icon <.iconset folder>
import AppKit

@main
enum MakeIcon {
    @MainActor
    static func main() throws {
        let folder = URL(fileURLWithPath: CommandLine.arguments[1])
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)

        for size in [16, 32, 128, 256, 512] {
            for scale in [1, 2] {
                let pixels = size * scale
                let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: pixels, pixelsHigh: pixels, bitsPerSample: 8, samplesPerPixel: 4,
                                           hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
                NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
                // Drawn on the 1024 × 1024 macOS icon grid: an 824 × 824 plate with a 185 corner radius and a shadow.
                let k = CGFloat(pixels) / 1024
                let plate = NSBezierPath(roundedRect: CGRect(x: 100 * k, y: 100 * k, width: 824 * k, height: 824 * k), xRadius: 185 * k, yRadius: 185 * k)
                NSGraphicsContext.saveGraphicsState()
                let shadow = NSShadow()
                shadow.shadowColor = NSColor(white: 0, alpha: 0.3)
                shadow.shadowOffset = CGSize(width: 0, height: -10 * k)
                shadow.shadowBlurRadius = 20 * k
                shadow.set()
                NSColor.white.setFill()
                plate.fill()
                NSGraphicsContext.restoreGraphicsState()
                NSGradient(starting: .white, ending: NSColor(white: 0.85, alpha: 1))!.draw(in: plate, angle: -90)

                Glyph.draw(in: CGRect(x: 232 * k, y: 232 * k, width: 560 * k, height: 560 * k), color: NSColor(white: 0.12, alpha: 1))
                NSGraphicsContext.current = nil

                let name = scale == 1 ? "icon_\(size)x\(size).png" : "icon_\(size)x\(size)@2x.png"
                try rep.representation(using: .png, properties: [:])!.write(to: folder.appendingPathComponent(name))
            }
        }
    }
}
