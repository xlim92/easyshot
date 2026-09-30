// Renders the app icon (the app glyph on a light rounded plate): every iconset size for macOS or an .ico for Windows.
// Built together with Sources/Glyph.swift, see build.sh and windows/build.sh. Usage: make-icon <.iconset folder | .ico file>
import AppKit

@main
enum MakeIcon {
    @MainActor
    static func main() throws {
        let output = URL(fileURLWithPath: CommandLine.arguments[1])
        if output.pathExtension == "ico" {
            try ico(sizes: [16, 24, 32, 48, 64, 256]).write(to: output)
            return
        }
        try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)

        for size in [16, 32, 128, 256, 512] {
            for scale in [1, 2] {
                let name = scale == 1 ? "icon_\(size)x\(size).png" : "icon_\(size)x\(size)@2x.png"
                try png(pixels: size * scale).write(to: output.appendingPathComponent(name))
            }
        }
    }

    /// The icon as a PNG image, `pixels` wide and high.
    @MainActor
    static func png(pixels: Int) -> Data {
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
        return rep.representation(using: .png, properties: [:])!
    }

    /// A Windows .ico with PNG images of the given sizes; Windows reads PNG images in icons since Vista.
    @MainActor
    static func ico(sizes: [Int]) -> Data {
        let images = sizes.map { png(pixels: $0) }
        var ico = Data([0, 0, 1, 0, UInt8(sizes.count), 0])
        var offset = ico.count + 16 * sizes.count
        for (size, image) in zip(sizes, images) {
            // Directory entry: width and height (0 stands for 256), no palette, 1 plane, 32 bits per pixel, image length and offset.
            ico += [UInt8(size % 256), UInt8(size % 256), 0, 0, 1, 0, 32, 0]
            ico += withUnsafeBytes(of: UInt32(image.count).littleEndian, Array.init)
            ico += withUnsafeBytes(of: UInt32(offset).littleEndian, Array.init)
            offset += image.count
        }
        return images.reduce(ico, +)
    }
}
