import AppKit
import ScreenCaptureKit

struct ScreenShot {
    let screen: NSScreen
    let image: CGImage
}

enum Capture {
    /// Captures all screens up front: selecting and drawing happen on a frozen image.
    @MainActor
    static func allScreens() async throws -> [ScreenShot] {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        var shots: [ScreenShot] = []
        for screen in NSScreen.screens {
            let id = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? CGDirectDisplayID
            guard let display = content.displays.first(where: { $0.displayID == id }) else { continue }
            let config = SCStreamConfiguration()
            config.width = Int(screen.frame.width * screen.backingScaleFactor)
            config.height = Int(screen.frame.height * screen.backingScaleFactor)
            config.showsCursor = false
            let filter = SCContentFilter(display: display, excludingWindows: [])
            let image = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)
            shots.append(ScreenShot(screen: screen, image: image))
        }
        return shots
    }
}
