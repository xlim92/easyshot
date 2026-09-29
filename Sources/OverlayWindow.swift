import AppKit

/// Full-screen overlay window; each display gets its own, and a selection can start on any of them.
final class OverlayWindow: NSWindow {
    init(shot: ScreenShot, onClose: @escaping () -> Void) {
        super.init(contentRect: shot.screen.frame, styleMask: .borderless, backing: .buffered, defer: false)
        // Above the Dock and the menu bar, but below pop-up menus.
        level = NSWindow.Level(rawValue: NSWindow.Level.popUpMenu.rawValue - 1)
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        isReleasedWhenClosed = false
        hasShadow = false
        let view = CaptureView(screenshot: shot.image, size: shot.screen.frame.size, onClose: onClose)
        contentView = view
        makeFirstResponder(view)
    }

    override var canBecomeKey: Bool { true }
}
