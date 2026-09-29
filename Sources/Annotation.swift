import AppKit
import Carbon.HIToolbox

/// Drawing tools; the letter is the keyboard shortcut.
enum Tool: CaseIterable {
    case pencil, line, arrow, rectangle, filledRect, ellipse, marker, text, pixelate, counter, invert

    var symbol: String {
        switch self {
        case .pencil: "pencil"
        case .line: "line.diagonal"
        case .arrow: "arrow.up.right"
        case .rectangle: "rectangle"
        case .filledRect: "rectangle.fill"
        case .ellipse: "circle"
        case .marker: "highlighter"
        case .text: "textformat"
        case .pixelate: "checkerboard.rectangle"
        case .counter: "1.circle.fill"
        case .invert: "circle.lefthalf.filled"
        }
    }

    var title: String {
        switch self {
        case .pencil: "Карандаш"
        case .line: "Линия"
        case .arrow: "Стрелка"
        case .rectangle: "Прямоугольник"
        case .filledRect: "Заливка"
        case .ellipse: "Эллипс"
        case .marker: "Маркер"
        case .text: "Текст"
        case .pixelate: "Пикселизация"
        case .counter: "Нумерация"
        case .invert: "Инверсия"
        }
    }

    var shortcut: (key: String, code: Int)? {
        switch self {
        case .pencil: ("P", kVK_ANSI_P)
        case .line: ("D", kVK_ANSI_D)
        case .arrow: ("A", kVK_ANSI_A)
        case .rectangle: ("S", kVK_ANSI_S)
        case .filledRect: ("R", kVK_ANSI_R)
        case .ellipse: ("C", kVK_ANSI_C)
        case .marker: ("M", kVK_ANSI_M)
        case .text: ("T", kVK_ANSI_T)
        case .pixelate: ("B", kVK_ANSI_B)
        case .invert: ("I", kVK_ANSI_I)
        case .counter: nil
        }
    }

    /// Default size, in the tool's own units (see Annotation.size).
    var defaultSize: Int {
        switch self {
        case .text: 20
        case .marker: 18
        case .counter: 14
        case .pixelate: 10
        default: 4
        }
    }

    /// With Shift: lines snap to 45° steps, rectangular shapes and ellipses become squares and circles.
    func constrained(_ v: CGVector) -> CGVector {
        switch self {
        case .line, .arrow, .marker:
            let step = CGFloat.pi / 4, length = hypot(v.dx, v.dy)
            let angle = (atan2(v.dy, v.dx) / step).rounded() * step
            return CGVector(dx: length * cos(angle), dy: length * sin(angle))
        case .rectangle, .filledRect, .ellipse, .pixelate, .invert:
            let side = max(abs(v.dx), abs(v.dy))
            return CGVector(dx: v.dx < 0 ? -side : side, dy: v.dy < 0 ? -side : side)
        case .pencil, .text, .counter:
            return v
        }
    }
}

/// A drawn object. Coordinates are in screen points with the y axis pointing down.
struct Annotation {
    let tool: Tool
    var color: NSColor
    /// Size in the tool's units: line width, font size, radius of the numbered circle,
    /// mosaic block size, corner radius of the filled rectangle.
    var size: Int
    /// Pencil: the whole path; text: the anchor point; everything else: [start, end].
    var points: [CGPoint]
    var text = ""
    var number = 0
    /// Rendered mosaic; the caller recomputes it whenever the geometry changes.
    var pixelated: CGImage?

    var start: CGPoint { points[0] }
    var end: CGPoint { points[points.count - 1] }
    var box: CGRect {
        CGRect(x: min(start.x, end.x), y: min(start.y, end.y), width: abs(start.x - end.x), height: abs(start.y - end.y))
    }

    var font: NSFont { .systemFont(ofSize: CGFloat(size), weight: .medium) }

    /// Empty objects (a click without dragging, empty text) are not added to the history.
    var isValid: Bool {
        switch tool {
        case .text: !text.isEmpty
        case .counter: true
        default: points.count > 1 && start != end
        }
    }

    /// Text frame with 4pt padding.
    var textRect: CGRect {
        let size = (text as NSString).boundingRect(with: CGSize(width: CGFloat.greatestFiniteMagnitude, height: .greatestFiniteMagnitude),
                                                   options: .usesLineFragmentOrigin, attributes: [.font: font]).size
        return CGRect(origin: start, size: CGSize(width: ceil(size.width) + 8, height: ceil(size.height) + 8))
    }

    /// Object area used for the selection frame.
    var bounds: CGRect {
        switch tool {
        case .text:
            return textRect
        case .counter:
            let r = CGFloat(size) + 2
            return CGRect(x: start.x - r, y: start.y - r, width: 2 * r, height: 2 * r).union(CGRect(origin: end, size: .zero))
        case .filledRect, .pixelate, .invert:
            return box
        default:
            let xs = points.map(\.x), ys = points.map(\.y), pad = CGFloat(size) / 2 + 3
            return CGRect(x: xs.min()!, y: ys.min()!, width: xs.max()! - xs.min()!, height: ys.max()! - ys.min()!)
                .insetBy(dx: -pad, dy: -pad)
        }
    }

    /// Hit testing: lines and outlines are hit along the stroke with 4pt tolerance; fills, text and circles anywhere inside.
    func contains(_ p: CGPoint) -> Bool {
        let path = CGMutablePath()
        switch tool {
        case .text, .counter, .filledRect, .pixelate, .invert:
            return bounds.insetBy(dx: -4, dy: -4).contains(p)
        case .pencil, .line, .arrow, .marker:
            path.addLines(between: points)
        case .rectangle:
            path.addRect(box)
        case .ellipse:
            path.addEllipse(in: box)
        }
        return path.copy(strokingWithWidth: CGFloat(size) + 8, lineCap: .round, lineJoin: .round, miterLimit: 10).contains(p)
    }

    mutating func move(by d: CGVector) {
        points = points.map { CGPoint(x: $0.x + d.dx, y: $0.y + d.dy) }
    }

    /// Draws the object into the current NSGraphicsContext (flipped: y grows downwards).
    func draw() {
        guard let ctx = NSGraphicsContext.current?.cgContext else { return }
        ctx.saveGState()
        defer { ctx.restoreGState() }
        let width = CGFloat(size)
        ctx.setStrokeColor(color.cgColor)
        ctx.setFillColor(color.cgColor)
        ctx.setLineWidth(width)
        ctx.setLineCap(.round)
        ctx.setLineJoin(.round)

        switch tool {
        case .pencil, .line:
            ctx.addLines(between: tool == .pencil ? points : [start, end])
            ctx.strokePath()
        case .arrow:
            drawArrow(ctx, width)
        case .rectangle:
            ctx.stroke(box)
        case .filledRect:
            let radius = min(width, box.width / 2, box.height / 2)
            ctx.addPath(CGPath(roundedRect: box, cornerWidth: radius, cornerHeight: radius, transform: nil))
            ctx.fillPath()
        case .ellipse:
            ctx.strokeEllipse(in: box)
        case .marker:
            // A semi-transparent stroke with multiply blending keeps the text underneath crisp.
            ctx.setBlendMode(.multiply)
            ctx.setAlpha(0.4)
            ctx.setLineCap(.butt)
            ctx.addLines(between: [start, end])
            ctx.strokePath()
        case .text:
            (text as NSString).draw(at: CGPoint(x: start.x + 4, y: start.y + 4), withAttributes: [.font: font, .foregroundColor: color])
        case .pixelate:
            guard let pixelated else { return }
            // The mosaic image is stored upright but the context is flipped, so flip it back.
            ctx.interpolationQuality = .none
            ctx.translateBy(x: box.minX, y: box.maxY)
            ctx.scaleBy(x: 1, y: -1)
            ctx.draw(pixelated, in: CGRect(origin: .zero, size: box.size))
        case .counter:
            drawCounter(ctx)
        case .invert:
            // Difference blending with white inverts everything under the area.
            ctx.setBlendMode(.difference)
            ctx.setFillColor(.white)
            ctx.fill(box)
        }
    }

    /// Shaft up to the base of the head plus a triangular head: length is 3 × line width + 8pt, width is 1.2 × length.
    private func drawArrow(_ ctx: CGContext, _ width: CGFloat) {
        let length = hypot(end.x - start.x, end.y - start.y)
        guard length > 0 else { return }
        let u = CGVector(dx: (end.x - start.x) / length, dy: (end.y - start.y) / length)
        let head = min(3 * width + 8, length), spread = head * 0.6
        let base = CGPoint(x: end.x - u.dx * head, y: end.y - u.dy * head)
        ctx.addLines(between: [start, base])
        ctx.strokePath()
        ctx.addLines(between: [end,
                               CGPoint(x: base.x + u.dy * spread, y: base.y - u.dx * spread),
                               CGPoint(x: base.x - u.dy * spread, y: base.y + u.dx * spread)])
        ctx.closePath()
        ctx.fillPath()
    }

    /// Numbered circle with a white outline; dragging while placing it adds a pointer to the release point.
    private func drawCounter(_ ctx: CGContext) {
        let r = CGFloat(size), outline = max(1.5, r / 8)
        let circle = CGRect(x: start.x - r, y: start.y - r, width: 2 * r, height: 2 * r)
        ctx.setFillColor(.white)
        ctx.fillEllipse(in: circle.insetBy(dx: -outline, dy: -outline))
        ctx.setFillColor(color.cgColor)
        if hypot(end.x - start.x, end.y - start.y) > r {
            let angle = atan2(end.y - start.y, end.x - start.x), spread: CGFloat = 0.45
            ctx.addLines(between: [start,
                                   CGPoint(x: start.x + r * cos(angle - spread), y: start.y + r * sin(angle - spread)),
                                   end,
                                   CGPoint(x: start.x + r * cos(angle + spread), y: start.y + r * sin(angle + spread))])
            ctx.closePath()
            ctx.fillPath()
        }
        ctx.fillEllipse(in: circle)

        let label = "\(number)" as NSString
        var fontSize = r * 1.1
        var attributes: [NSAttributedString.Key: Any]
        repeat {
            attributes = [.font: NSFont.systemFont(ofSize: fontSize, weight: .bold), .foregroundColor: color.isDark ? NSColor.white : .black]
            fontSize -= 1
        } while label.size(withAttributes: attributes).width > 1.5 * r && fontSize > 1
        let size = label.size(withAttributes: attributes)
        label.draw(at: CGPoint(x: start.x - size.width / 2, y: start.y - size.height / 2), withAttributes: attributes)
    }
}

extension NSColor {
    /// Whether the color is dark by BT.601 luma; used to pick a contrasting number color.
    var isDark: Bool {
        guard let c = usingColorSpace(.sRGB) else { return true }
        return 0.299 * c.redComponent + 0.587 * c.greenComponent + 0.114 * c.blueComponent < 0.6
    }
}
