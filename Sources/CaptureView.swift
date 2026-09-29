import AppKit
import Carbon.HIToolbox
import UniformTypeIdentifiers

/// Capture screen: dimmed screenshot, area selection with a resizable frame, toolbars next to the frame
/// and drawing on top of the screenshot: tools, Shift constraints, mouse wheel sizing, color ring, object editing.
final class CaptureView: NSView, NSTextViewDelegate {
    private enum Drag {
        case none
        case selecting(CGPoint)
        case movingSelection(CGPoint, CGRect)
        case resizing(Int, Int, CGRect)
        case drawing
        case movingObject(CGPoint, [Annotation])
    }

    private static let handles = [(-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1)]
    private static let palette: [NSColor] = [
        0xFF3B30, 0xFF9500, 0xFFCC00, 0x34C759, 0x00C7BE, 0x007AFF, 0xAF52DE, 0xFF2D55, 0x000000, 0xFFFFFF,
    ].map { NSColor(srgbRed: CGFloat($0 >> 16) / 255, green: CGFloat($0 >> 8 & 0xFF) / 255, blue: CGFloat($0 & 0xFF) / 255, alpha: 1) }
    /// Palette swatches sit on a ring with a gap between neighbors.
    private static let ringRadius = max(44, CGFloat(palette.count) * 34 / (2 * .pi))
    private static let sizeRange = 1...72
    private static let undoLimit = 200
    /// Color and tool sizes are remembered between screenshots while the app is running.
    private static var color = palette[0]
    private static var sizes: [Tool: Int] = [:]

    private let screenshot: CGImage
    private let image: NSImage
    private let onClose: () -> Void
    private var scale: CGFloat { CGFloat(screenshot.width) / bounds.width }

    private var selection: CGRect?
    private var drag = Drag.none
    private var annotations: [Annotation] = []
    private var undoStack: [[Annotation]] = []
    private var redoStack: [[Annotation]] = []
    private var current: Annotation?
    private var selected: Int?
    private var resized: Int?
    private var tool: Tool?
    private var counter = 1
    private var editor: NSTextView?
    private var editing: Int?
    private var editorSize = 8
    private var picker: (center: CGPoint, hovered: Int?)? {
        didSet {
            if (oldValue == nil) != (picker == nil) {
                updateBars()
            }
        }
    }
    private var mouse: CGPoint?
    private var wheel: CGFloat = 0
    private var sizeHintUntil = Date.distantPast

    private let toolsBar = Bar()
    private let actionsBar = Bar()
    private lazy var toolButtons = Tool.allCases.map { tool in
        makeButton(tool.symbol, tool.title + (tool.shortcut.map { " (\($0.key))" } ?? ""), #selector(selectTool(_:)))
    }
    private lazy var colorButton = makeButton("circle.fill", "Цвет (или правая кнопка мыши)", #selector(pickColor(_:)))
    private lazy var moveButton = makeButton("cursorarrow", "Выбор и перемещение объектов (V, Esc)", #selector(selectMoveMode))

    init(screenshot: CGImage, size: NSSize, onClose: @escaping () -> Void) {
        self.screenshot = screenshot
        image = NSImage(cgImage: screenshot, size: size)
        self.onClose = onClose
        super.init(frame: CGRect(origin: .zero, size: size))
        addTrackingArea(NSTrackingArea(rect: .zero, options: [.mouseMoved, .mouseEnteredAndExited, .activeAlways, .inVisibleRect], owner: self))

        colorButton.image = Self.swatch(Self.color)
        setup(toolsBar, .vertical, [moveButton] + toolButtons + [
            colorButton,
            makeButton("arrow.uturn.backward", "Отменить (⌘Z)", #selector(undo)),
            makeButton("arrow.uturn.forward", "Повторить (⇧⌘Z)", #selector(redo)),
        ])
        setup(actionsBar, .horizontal, [
            makeButton("doc.on.doc", "Копировать (⌘C, Enter)", #selector(copyImage)),
            makeButton("square.and.arrow.down", "Сохранить (⌘S)", #selector(saveImage)),
            makeButton("xmark", "Закрыть (Esc)", #selector(close)),
        ])
        updateToolButtons()
        updateBars()
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) не используется") }

    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func resetCursorRects() { addCursorRect(bounds, cursor: .crosshair) }

    // MARK: - Drawing

    override func draw(_ dirtyRect: NSRect) {
        image.draw(in: bounds, from: .zero, operation: .copy, fraction: 1, respectFlipped: true, hints: nil)
        let shade = NSBezierPath(rect: bounds)
        NSColor(white: 0, alpha: 0.45).setFill()
        guard let sel = selection else {
            shade.fill()
            return
        }

        NSGraphicsContext.saveGraphicsState()
        NSBezierPath(rect: sel).addClip()
        for (i, annotation) in annotations.enumerated() where i != editing {
            annotation.draw()
        }
        current?.draw()
        NSGraphicsContext.restoreGraphicsState()

        shade.append(NSBezierPath(rect: sel))
        shade.windingRule = .evenOdd
        shade.fill()

        drawFrame(sel)
        if let selected {
            // Selected object: an accent-colored dashed frame over a white underlay.
            let path = NSBezierPath(roundedRect: annotations[selected].bounds.insetBy(dx: -3, dy: -3), xRadius: 3, yRadius: 3)
            path.lineWidth = 2
            NSColor.white.setStroke()
            path.stroke()
            path.setLineDash([5, 3], count: 2, phase: 0)
            NSColor.controlAccentColor.setStroke()
            path.stroke()
        }
        drawMousePreview()
        drawSizeHint()
        drawPicker()
    }

    private func drawFrame(_ sel: CGRect) {
        let border = NSBezierPath(rect: sel.insetBy(dx: -0.5, dy: -0.5))
        NSColor.black.setStroke()
        border.stroke()
        border.setLineDash([4, 4], count: 2, phase: 0)
        NSColor.white.setStroke()
        border.stroke()
        for (dx, dy) in Self.handles {
            let p = handlePoint(dx, dy, sel)
            let handle = NSBezierPath(rect: CGRect(x: p.x - 3, y: p.y - 3, width: 6, height: 6))
            NSColor.white.setFill()
            handle.fill()
            NSColor.black.setStroke()
            handle.stroke()
        }

        let label = "\(Int((sel.width * scale).rounded())) × \(Int((sel.height * scale).rounded()))" as NSString
        let attributes: [NSAttributedString.Key: Any] = [
            .font: NSFont.monospacedDigitSystemFont(ofSize: 11, weight: .medium), .foregroundColor: NSColor.white,
        ]
        let size = label.size(withAttributes: attributes)
        var box = CGRect(x: sel.minX, y: sel.minY - size.height - 8, width: size.width + 8, height: size.height + 4)
        if box.minY < 0 {
            box.origin = CGPoint(x: sel.minX + 4, y: sel.minY + 4)
        }
        NSColor(white: 0, alpha: 0.7).setFill()
        NSBezierPath(roundedRect: box, xRadius: 3, yRadius: 3).fill()
        label.draw(at: CGPoint(x: box.minX + 4, y: box.minY + 2), withAttributes: attributes)
    }

    /// Brush preview under the cursor: tool color and width; for numbering, a circle with the next number.
    private func drawMousePreview() {
        guard let tool, let mouse, case .none = drag, picker == nil, editor == nil, selection?.contains(mouse) == true else { return }
        let size = CGFloat(size(of: tool))
        let diameter: CGFloat = switch tool {
        case .text, .pixelate, .invert, .filledRect: 0
        case .counter: 2 * size
        default: max(size, 3)
        }
        guard diameter > 0 else { return }
        let circle = CGRect(x: mouse.x - diameter / 2, y: mouse.y - diameter / 2, width: diameter, height: diameter)
        Self.color.withAlphaComponent(tool == .marker || tool == .counter ? 0.4 : 1).setFill()
        NSBezierPath(ovalIn: circle).fill()
        if tool == .counter {
            let label = "\(counter)" as NSString
            let attributes: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: size, weight: .bold), .foregroundColor: NSColor.white]
            let labelSize = label.size(withAttributes: attributes)
            label.draw(at: CGPoint(x: circle.midX - labelSize.width / 2, y: circle.midY - labelSize.height / 2), withAttributes: attributes)
        }
    }

    /// After scrolling the wheel, the current size is briefly shown next to the cursor.
    private func drawSizeHint() {
        guard Date.now < sizeHintUntil, let mouse,
              let value = editor != nil ? editorSize : selected.map({ annotations[$0].size }) ?? tool.map(size(of:)) else { return }
        let label = "\(value)" as NSString
        let attributes: [NSAttributedString.Key: Any] = [.font: NSFont.boldSystemFont(ofSize: 13), .foregroundColor: NSColor.white]
        let size = label.size(withAttributes: attributes)
        let badge = CGRect(x: mouse.x + 16, y: mouse.y + 16, width: size.width + 14, height: size.height + 6)
        NSColor(white: 0.15, alpha: 0.75).setFill()
        NSBezierPath(roundedRect: badge, xRadius: badge.height / 2, yRadius: badge.height / 2).fill()
        label.draw(at: CGPoint(x: badge.midX - size.width / 2, y: badge.midY - size.height / 2), withAttributes: attributes)
    }

    /// Color ring around the point where it was opened; the highlighted color is enlarged and outlined in white.
    private func drawPicker() {
        guard let picker else { return }
        for (i, swatchColor) in Self.palette.enumerated() {
            let hovered = i == picker.hovered
            let path = NSBezierPath(ovalIn: swatchRect(i, around: picker.center).insetBy(dx: hovered ? -3 : 0, dy: hovered ? -3 : 0))
            swatchColor.setFill()
            path.fill()
            path.lineWidth = hovered ? 3 : 1
            (hovered ? NSColor.white : NSColor(white: 0, alpha: 0.5)).setStroke()
            path.stroke()
        }
    }

    /// Frame of swatch i, going clockwise from the top of the ring.
    private func swatchRect(_ i: Int, around center: CGPoint) -> CGRect {
        let angle = 2 * .pi * CGFloat(i) / CGFloat(Self.palette.count) - .pi / 2, r: CGFloat = 13
        return CGRect(x: center.x + Self.ringRadius * cos(angle) - r, y: center.y + Self.ringRadius * sin(angle) - r, width: 2 * r, height: 2 * r)
    }

    // MARK: - Mouse

    override func mouseDown(with event: NSEvent) {
        window?.makeKey()
        let p = point(event)
        mouse = p
        defer {
            updateBars()
            updateCursor()
            needsDisplay = true
        }
        if let picker {
            // A left click while the ring is open picks the highlighted color.
            if let i = picker.hovered {
                apply(color: Self.palette[i])
            }
            self.picker = nil
            return
        }
        if editor != nil {
            // The first click only finishes text editing.
            commitText()
            return
        }
        if event.clickCount == 2, tool == nil, let i = selected, annotations[i].tool == .text, annotations[i].contains(p) {
            beginText(at: annotations[i].start, editing: i)
            return
        }
        resized = nil
        guard let sel = selection else {
            drag = .selecting(p)
            return
        }
        if let (dx, dy) = Self.handles.first(where: { hypot(handlePoint($0.0, $0.1, sel).x - p.x, handlePoint($0.0, $0.1, sel).y - p.y) <= 6 }) {
            selected = nil
            drag = .resizing(dx, dy, sel)
        } else if sel.contains(p) {
            if let tool {
                // With a tool active, a click always starts a new object.
                startDrawing(tool, at: p)
            } else if let i = selected.flatMap({ annotations[$0].bounds.contains(p) ? $0 : nil }) ?? hit(p) {
                select(object: i)
                drag = .movingObject(p, annotations)
            } else {
                selected = nil
                // Move the frame only while nothing is drawn; otherwise drawings would drift away from what they point at.
                if annotations.isEmpty {
                    drag = .movingSelection(p, sel)
                }
            }
        } else if annotations.isEmpty {
            // Start a new selection only while nothing is drawn, so a stray click can't lose the drawing.
            selection = nil
            drag = .selecting(p)
        }
    }

    override func mouseDragged(with event: NSEvent) {
        let p = point(event)
        mouse = p
        switch drag {
        case .none:
            return
        case .selecting(let start):
            selection = Self.rect(start, p)
        case .movingSelection(let start, let original):
            var r = original.offsetBy(dx: (p.x - start.x).rounded(), dy: (p.y - start.y).rounded())
            r.origin.x = min(max(r.minX, 0), bounds.maxX - r.width)
            r.origin.y = min(max(r.minY, 0), bounds.maxY - r.height)
            selection = r
        case .resizing(let dx, let dy, let r):
            var a = CGPoint(x: r.minX, y: r.minY), b = CGPoint(x: r.maxX, y: r.maxY)
            if dx < 0 { a.x = p.x } else if dx > 0 { b.x = p.x }
            if dy < 0 { a.y = p.y } else if dy > 0 { b.y = p.y }
            selection = Self.rect(a, b)
        case .drawing:
            guard var a = current else { return }
            if a.tool == .pencil {
                a.points.append(p)
            } else {
                let v = CGVector(dx: p.x - a.start.x, dy: p.y - a.start.y)
                let d = event.modifierFlags.contains(.shift) ? a.tool.constrained(v) : v
                a.points[1] = CGPoint(x: a.start.x + d.dx, y: a.start.y + d.dy)
            }
            current = refreshed(a)
        case .movingObject(let start, let before):
            guard let i = selected else { return }
            var a = before[i]
            a.move(by: CGVector(dx: p.x - start.x, dy: p.y - start.y))
            annotations[i] = refreshed(a)
        }
        updateBars()
        needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        switch drag {
        case .drawing:
            if let a = current, a.isValid {
                record { annotations.append(a) }
                if a.tool == .counter {
                    counter = min(counter + 1, 999)
                }
            }
            current = nil
        case .movingObject(let start, let before) where point(event) != start:
            push(before)
        case .selecting, .resizing:
            if let sel = selection, sel.width < 2 || sel.height < 2 {
                selection = nil
            }
        default:
            break
        }
        drag = .none
        updateBars()
        updateCursor()
        needsDisplay = true
    }

    override func mouseMoved(with event: NSEvent) {
        mouse = point(event)
        updateCursor()
        if let center = picker?.center, let i = Self.palette.indices.first(where: { swatchRect($0, around: center).contains(mouse!) }) {
            picker?.hovered = i
        }
        if tool != nil || picker != nil {
            needsDisplay = true
        }
    }

    override func mouseExited(with event: NSEvent) {
        mouse = nil
        needsDisplay = true
    }

    /// Right click opens the color ring; with no tool active it first selects the object under the cursor so it can be recolored.
    override func rightMouseDown(with event: NSEvent) {
        guard editor == nil else { return }
        let p = point(event)
        if tool == nil, let i = hit(p) {
            select(object: i)
        }
        picker = (p, Self.palette.firstIndex(of: Self.color))
        needsDisplay = true
    }

    override func rightMouseDragged(with event: NSEvent) {
        mouseMoved(with: event)
    }

    /// Besides clicking a color, you can press the right button, drag to a color and release.
    override func rightMouseUp(with event: NSEvent) {
        let p = point(event)
        if let center = picker?.center, let i = Self.palette.indices.first(where: { swatchRect($0, around: center).contains(p) }) {
            apply(color: Self.palette[i])
            picker = nil
        }
        needsDisplay = true
    }

    /// The mouse wheel changes the size of the current tool, the selected object or the text being typed, within 1...72;
    /// Shift + wheel with the numbering tool changes the next number.
    override func scrollWheel(with event: NSEvent) {
        var delta = event.scrollingDeltaY != 0 ? event.scrollingDeltaY : event.scrollingDeltaX
        if event.isDirectionInvertedFromDevice {
            delta = -delta
        }
        let step: Int
        if event.hasPreciseScrollingDeltas {
            // Trackpads send a stream of small deltas: accumulate them, one size step per 6pt of scrolling.
            wheel += delta
            step = Int(wheel / 6)
            wheel -= CGFloat(step) * 6
        } else {
            step = delta > 0 ? 1 : delta < 0 ? -1 : 0
        }
        guard step != 0 else { return }

        if tool == .counter, event.modifierFlags.contains(.shift) {
            counter = min(max(counter + step, 1), 999)
        } else if let editor {
            editorSize = clamp(editorSize + step)
            editor.font = .systemFont(ofSize: CGFloat(editorSize), weight: .medium)
            editor.sizeToFit()
        } else if let i = selected {
            if resized != i {
                push(annotations)
                resized = i
            }
            annotations[i].size = clamp(annotations[i].size + step)
            annotations[i] = refreshed(annotations[i])
            Self.sizes[annotations[i].tool] = annotations[i].size
        } else if let tool {
            Self.sizes[tool] = clamp(size(of: tool) + step)
        } else {
            return
        }
        sizeHintUntil = .now + 0.8
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.85) { [weak self] in self?.needsDisplay = true }
        needsDisplay = true
    }

    // MARK: - Keyboard

    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        guard event.modifierFlags.contains(.command) else { return super.performKeyEquivalent(with: event) }
        switch Int(event.keyCode) {
        case kVK_ANSI_C: copyImage()
        case kVK_ANSI_S: saveImage()
        case kVK_ANSI_Z: event.modifierFlags.contains(.shift) ? redo() : undo()
        case kVK_Return where editor != nil: commitText()
        default: return super.performKeyEquivalent(with: event)
        }
        return true
    }

    override func keyDown(with event: NSEvent) {
        switch Int(event.keyCode) {
        case kVK_Escape:
            // Esc works in steps: close the color ring, drop the tool, deselect the object, and only then quit.
            if picker != nil {
                picker = nil
            } else if let tool {
                select(tool)
            } else if selected != nil {
                selected = nil
            } else {
                close()
            }
            needsDisplay = true
        case kVK_Return, kVK_ANSI_KeypadEnter:
            copyImage()
        case kVK_Delete, kVK_ForwardDelete:
            deleteSelected()
        case kVK_ANSI_V:
            selectMoveMode()
        case let code:
            if let tool = Tool.allCases.first(where: { $0.shortcut?.code == code }) {
                select(tool)
            } else {
                super.keyDown(with: event)
            }
        }
    }

    // MARK: - Text

    /// Text is typed right on the screenshot: Enter adds a line; Esc, ⌘Enter or a click elsewhere finishes.
    private func beginText(at p: CGPoint, editing index: Int? = nil) {
        let source = index.map { annotations[$0] }
        editorSize = source?.size ?? size(of: .text)
        let textView = NSTextView(frame: CGRect(origin: p, size: CGSize(width: 20, height: 20)))
        textView.font = .systemFont(ofSize: CGFloat(editorSize), weight: .medium)
        textView.textColor = source?.color ?? Self.color
        textView.insertionPointColor = textView.textColor ?? Self.color
        textView.drawsBackground = false
        textView.isRichText = false
        textView.allowsUndo = true
        textView.textContainerInset = CGSize(width: 4, height: 4)
        textView.textContainer?.lineFragmentPadding = 0
        textView.textContainer?.widthTracksTextView = false
        textView.textContainer?.containerSize = CGSize(width: CGFloat.greatestFiniteMagnitude, height: .greatestFiniteMagnitude)
        textView.isHorizontallyResizable = true
        textView.maxSize = CGSize(width: CGFloat.greatestFiniteMagnitude, height: .greatestFiniteMagnitude)
        textView.string = source?.text ?? ""
        textView.delegate = self
        textView.sizeToFit()
        addSubview(textView)
        window?.makeFirstResponder(textView)
        textView.selectAll(nil)
        editor = textView
        editing = index
        selected = nil
        needsDisplay = true
    }

    func textView(_ textView: NSTextView, doCommandBy selector: Selector) -> Bool {
        guard selector == #selector(cancelOperation(_:)) else { return false }
        commitText()
        return true
    }

    private func commitText() {
        guard let textView = editor else { return }
        editor = nil
        textView.removeFromSuperview()
        window?.makeFirstResponder(self)
        let a = Annotation(tool: .text, color: textView.textColor ?? Self.color, size: editorSize,
                           points: [textView.frame.origin], text: textView.string)
        if let i = editing {
            editing = nil
            let old = annotations[i]
            if !a.isValid {
                record { annotations.remove(at: i) }
            } else if a.text != old.text || a.size != old.size || a.color != old.color {
                record { annotations[i] = a }
            }
        } else if a.isValid {
            record { annotations.append(a) }
            Self.sizes[.text] = editorSize
        }
        needsDisplay = true
    }

    // MARK: - Actions

    @objc private func selectTool(_ sender: NSButton) {
        select(Tool.allCases[toolButtons.firstIndex(of: sender)!])
    }

    /// Choosing the active tool again deselects it.
    private func select(_ newTool: Tool) {
        commitText()
        tool = tool == newTool ? nil : newTool
        selected = nil
        updateToolButtons()
        needsDisplay = true
    }

    /// Selecting an object drops the active tool and makes the object's size current.
    private func select(object i: Int) {
        selected = i
        tool = nil
        Self.sizes[annotations[i].tool] = annotations[i].size
        updateToolButtons()
    }

    /// Selection mode: clicking an object selects it, dragging moves it.
    @objc private func selectMoveMode() {
        if let tool {
            select(tool)
        }
    }

    private func updateToolButtons() {
        moveButton.contentTintColor = tool == nil ? .controlAccentColor : .darkGray
        for (button, t) in zip(toolButtons, Tool.allCases) {
            button.contentTintColor = t == tool ? .controlAccentColor : .darkGray
        }
    }

    /// The color button opens the same ring as a right click, around the button and fully on screen.
    @objc private func pickColor(_ sender: NSButton) {
        let button = convert(sender.bounds, from: sender), margin = Self.ringRadius + 20
        picker = (CGPoint(x: min(max(button.midX, margin), bounds.maxX - margin), y: min(max(button.midY, margin), bounds.maxY - margin)),
                  Self.palette.firstIndex(of: Self.color))
        needsDisplay = true
    }

    /// The new color applies to new objects, to the selected object (undoable) and to the text being typed.
    private func apply(color: NSColor) {
        Self.color = color
        colorButton.image = Self.swatch(color)
        if let editor {
            editor.textColor = color
            editor.insertionPointColor = color
        } else if let i = selected, annotations[i].color != color {
            record { annotations[i].color = color }
        }
        needsDisplay = true
    }

    @objc private func undo() {
        commitText()
        guard let previous = undoStack.popLast() else { return }
        redoStack.append(annotations)
        restore(previous)
    }

    @objc private func redo() {
        commitText()
        guard let next = redoStack.popLast() else { return }
        undoStack.append(annotations)
        restore(next)
    }

    /// After undo or redo, the next number is the highest remaining one + 1.
    private func restore(_ state: [Annotation]) {
        annotations = state
        selected = nil
        counter = (annotations.filter { $0.tool == .counter }.map(\.number).max() ?? 0) + 1
        needsDisplay = true
    }

    /// Deleting a numbered circle shifts the following numbers down so the sequence has no gaps.
    private func deleteSelected() {
        guard let i = selected else { return }
        let removed = annotations[i]
        record {
            annotations.remove(at: i)
            if removed.tool == .counter {
                for j in annotations.indices where annotations[j].tool == .counter && annotations[j].number > removed.number {
                    annotations[j].number -= 1
                }
                counter = max(counter - 1, 1)
            }
        }
        selected = nil
        needsDisplay = true
    }

    @objc private func copyImage() {
        guard let image = renderSelection() else { return }
        let rep = NSBitmapImageRep(cgImage: image)
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.setData(rep.representation(using: .png, properties: [:]), forType: .png)
        pasteboard.setData(rep.tiffRepresentation, forType: .tiff)
        onClose()
    }

    @objc private func saveImage() {
        guard let image = renderSelection(),
              let data = NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:]) else { return }
        onClose()
        // Show the dialog after the overlays close, otherwise it would end up behind them.
        DispatchQueue.main.async { Self.save(data) }
    }

    private static func save(_ data: Data) {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.png]
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyy-MM-dd HH.mm.ss"
        panel.nameFieldStringValue = "Screenshot \(formatter.string(from: .now)).png"
        NSApp.activate()
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            try data.write(to: url)
        } catch {
            NSAlert(error: error).runModal()
        }
    }

    @objc private func close() {
        onClose()
    }

    /// Final image: pixels of the selected area in the screenshot's original color space, plus the drawn objects.
    func renderSelection() -> CGImage? {
        commitText()
        guard let sel = selection else { return nil }
        let pixels = CGRect(x: sel.minX * scale, y: sel.minY * scale, width: sel.width * scale, height: sel.height * scale).integral
        guard let crop = screenshot.cropping(to: pixels),
              let ctx = CGContext(data: nil, width: crop.width, height: crop.height, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: screenshot.colorSpace ?? CGColorSpaceCreateDeviceRGB(),
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        ctx.draw(crop, in: CGRect(x: 0, y: 0, width: crop.width, height: crop.height))
        // Objects are in points with y pointing down; map them to the area's pixels.
        ctx.translateBy(x: 0, y: CGFloat(crop.height))
        ctx.scaleBy(x: scale, y: -scale)
        ctx.translateBy(x: -sel.minX, y: -sel.minY)
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(cgContext: ctx, flipped: true)
        annotations.forEach { $0.draw() }
        NSGraphicsContext.restoreGraphicsState()
        return ctx.makeImage()
    }

    // MARK: - Helpers

    private func startDrawing(_ tool: Tool, at p: CGPoint) {
        if tool == .text {
            beginText(at: p)
            return
        }
        current = Annotation(tool: tool, color: Self.color, size: size(of: tool), points: tool == .pencil ? [p] : [p, p], number: counter)
        drag = .drawing
    }

    private func size(of tool: Tool) -> Int {
        Self.sizes[tool] ?? tool.defaultSize
    }

    private func record(_ change: () -> Void) {
        push(annotations)
        change()
    }

    private func push(_ state: [Annotation]) {
        undoStack.append(state)
        if undoStack.count > Self.undoLimit {
            undoStack.removeFirst()
        }
        redoStack.removeAll()
    }

    /// Topmost object under the point.
    private func hit(_ p: CGPoint) -> Int? {
        annotations.indices.last { annotations[$0].contains(p) }
    }

    /// The mosaic depends on where it sits on the screenshot, so recompute it on every geometry change.
    private func refreshed(_ annotation: Annotation) -> Annotation {
        guard annotation.tool == .pixelate else { return annotation }
        var a = annotation
        let box = a.box
        a.pixelated = Pixelate.image(from: screenshot,
                                     rect: CGRect(x: box.minX * scale, y: box.minY * scale, width: box.width * scale, height: box.height * scale),
                                     block: Int(CGFloat(a.size) * scale))
        return a
    }

    /// In selection mode, a hand cursor over an object shows that it can be dragged.
    private func updateCursor() {
        guard let mouse, hitTest(convert(mouse, to: superview)) === self else { return }
        if case .movingObject = drag {
            NSCursor.closedHand.set()
        } else if tool == nil, picker == nil, editor == nil, hit(mouse) != nil {
            NSCursor.openHand.set()
        } else {
            NSCursor.crosshair.set()
        }
    }

    private func clamp(_ size: Int) -> Int {
        min(max(size, Self.sizeRange.lowerBound), Self.sizeRange.upperBound)
    }

    private func point(_ event: NSEvent) -> CGPoint {
        let p = convert(event.locationInWindow, from: nil)
        return CGPoint(x: min(max(p.x, 0), bounds.maxX), y: min(max(p.y, 0), bounds.maxY))
    }

    private func handlePoint(_ dx: Int, _ dy: Int, _ r: CGRect) -> CGPoint {
        CGPoint(x: [r.minX, r.midX, r.maxX][dx + 1], y: [r.minY, r.midY, r.maxY][dy + 1])
    }

    private static func rect(_ a: CGPoint, _ b: CGPoint) -> CGRect {
        CGRect(x: min(a.x, b.x), y: min(a.y, b.y), width: abs(a.x - b.x), height: abs(a.y - b.y)).integral
    }

    private static func swatch(_ color: NSColor) -> NSImage {
        NSImage(size: CGSize(width: 18, height: 18), flipped: false) { rect in
            let path = NSBezierPath(ovalIn: rect.insetBy(dx: 2, dy: 2))
            color.setFill()
            path.fill()
            NSColor.gray.setStroke()
            path.stroke()
            return true
        }
    }

    // MARK: - Toolbars

    private func makeButton(_ symbol: String, _ tip: String, _ action: Selector) -> NSButton {
        let image = NSImage(systemSymbolName: symbol, accessibilityDescription: tip)!
            .withSymbolConfiguration(.init(pointSize: 15, weight: .regular))!
        let button = NSButton(image: image, target: self, action: action)
        button.isBordered = false
        button.toolTip = tip
        button.contentTintColor = .darkGray
        button.widthAnchor.constraint(equalToConstant: 30).isActive = true
        button.heightAnchor.constraint(equalToConstant: 30).isActive = true
        return button
    }

    private func setup(_ bar: NSStackView, _ orientation: NSUserInterfaceLayoutOrientation, _ buttons: [NSButton]) {
        buttons.forEach(bar.addArrangedSubview)
        bar.orientation = orientation
        bar.spacing = 0
        bar.edgeInsets = NSEdgeInsets(top: 3, left: 3, bottom: 3, right: 3)
        bar.appearance = NSAppearance(named: .aqua)
        bar.wantsLayer = true
        bar.layer?.backgroundColor = NSColor(white: 0.97, alpha: 1).cgColor
        bar.layer?.cornerRadius = 6
        addSubview(bar)
    }

    /// Tools go to the right of the frame and actions below it; near screen edges the toolbars move inside.
    /// Toolbars are hidden while selecting or drawing so they don't get in the way.
    private func updateBars() {
        // Hide the toolbars while the color ring is open so they don't cover it.
        var visible = selection != nil && picker == nil
        switch drag {
        case .selecting, .drawing: visible = false
        default: break
        }
        toolsBar.isHidden = !visible
        actionsBar.isHidden = !visible
        guard visible, let sel = selection else { return }

        let toolsSize = toolsBar.fittingSize, actionsSize = actionsBar.fittingSize
        var x = sel.maxX + 6
        if x + toolsSize.width > bounds.maxX {
            x = sel.minX - 6 - toolsSize.width
        }
        if x < 0 {
            x = sel.maxX - toolsSize.width - 6
        }
        let y = min(max(sel.maxY - toolsSize.height, 0), bounds.maxY - toolsSize.height)
        toolsBar.frame = CGRect(origin: CGPoint(x: x, y: y), size: toolsSize)

        var actions = CGRect(origin: CGPoint(x: max(sel.maxX - actionsSize.width, 0), y: sel.maxY + 6), size: actionsSize)
        if actions.maxY > bounds.maxY {
            actions.origin.y = sel.maxY - actionsSize.height - 6
        }
        if actions.intersects(toolsBar.frame) {
            actions.origin.x = toolsBar.frame.minX - actionsSize.width - 6
        }
        actionsBar.frame = actions
    }
}

/// Toolbar next to the frame: clicks on its background must not start a selection or a drawing.
private final class Bar: NSStackView {
    override func mouseDown(with event: NSEvent) {}
    override func resetCursorRects() { addCursorRect(bounds, cursor: .arrow) }
}
