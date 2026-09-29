import AppKit

/// The app glyph, drawn in code: a camera inside viewfinder corners.
/// Used both for the menu bar item and for the app icon.
enum Glyph {
    /// Draws the glyph into `rect` of the current context (y axis pointing up) on a 100 × 100 grid.
    static func draw(in rect: CGRect, color: NSColor) {
        guard let ctx = NSGraphicsContext.current?.cgContext else { return }
        ctx.saveGState()
        defer { ctx.restoreGState() }
        ctx.translateBy(x: rect.minX, y: rect.minY)
        ctx.scaleBy(x: rect.width / 100, y: rect.height / 100)
        // A separate layer, so the cut-outs below remove only the glyph and not what lies under it.
        ctx.beginTransparencyLayer(auxiliaryInfo: nil)
        defer { ctx.endTransparencyLayer() }
        ctx.setFillColor(color.cgColor)
        ctx.setStrokeColor(color.cgColor)

        // Viewfinder corners.
        ctx.setLineWidth(7)
        ctx.setLineCap(.round)
        ctx.setLineJoin(.round)
        for (x, y, dx, dy) in [(6.0, 6.0, 1.0, 1.0), (94, 6, -1, 1), (6, 94, 1, -1), (94, 94, -1, -1)] {
            ctx.move(to: CGPoint(x: x, y: y + 20 * dy))
            ctx.addLine(to: CGPoint(x: x, y: y))
            ctx.addLine(to: CGPoint(x: x + 20 * dx, y: y))
        }
        ctx.strokePath()

        // Camera body with the viewfinder bump on top.
        ctx.addPath(CGPath(roundedRect: CGRect(x: 24, y: 28, width: 52, height: 38), cornerWidth: 8, cornerHeight: 8, transform: nil))
        ctx.addPath(CGPath(roundedRect: CGRect(x: 39, y: 60, width: 22, height: 13), cornerWidth: 4, cornerHeight: 4, transform: nil))
        ctx.fillPath()

        // The lens ring and the flash are cut out of the body, then the lens itself is filled back in.
        ctx.setBlendMode(.destinationOut)
        ctx.fillEllipse(in: CGRect(x: 36, y: 33, width: 28, height: 28))
        ctx.fillEllipse(in: CGRect(x: 65, y: 55, width: 5, height: 5))
        ctx.setBlendMode(.normal)
        ctx.fillEllipse(in: CGRect(x: 42.5, y: 39.5, width: 15, height: 15))
    }
}
