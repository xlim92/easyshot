import AppKit
import ServiceManagement

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var statusItem: NSStatusItem?
    private let screenshotItem = NSMenuItem(title: "Take Screenshot", action: #selector(takeScreenshot), keyEquivalent: "")
    private var hotKey: HotKey?
    private var shortcut = Shortcut.load()
    private var recordedShortcut: Shortcut?
    private var overlays: [OverlayWindow] = []
    private var isCapturing = false

    func applicationDidFinishLaunching(_ notification: Notification) {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        let icon = NSImage(size: CGSize(width: 18, height: 18), flipped: false) { rect in
            Glyph.draw(in: rect.insetBy(dx: 1, dy: 1), color: .black)
            return true
        }
        icon.isTemplate = true
        icon.accessibilityDescription = "EasyShot"
        item.button?.image = icon
        let menu = NSMenu()
        screenshotItem.target = self
        menu.addItem(screenshotItem)
        menu.addItem(withTitle: "Screenshot Shortcut…", action: #selector(changeShortcut), keyEquivalent: "").target = self
        menu.addItem(.separator())
        menu.addItem(withTitle: "Quit EasyShot", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        item.menu = menu
        statusItem = item

        hotKey = HotKey { [weak self] in self?.takeScreenshot() }
        hotKey?.register(shortcut)
        showShortcutInMenu()
        if !CGPreflightScreenCaptureAccess() {
            CGRequestScreenCaptureAccess()
        }
        // Launch at login: the app adds itself to Login Items, but only if it isn't there yet,
        // so a login item the user turned off (status requiresApproval) stays off.
        if SMAppService.mainApp.status == .notRegistered {
            do {
                try SMAppService.mainApp.register()
            } catch {
                NSLog("EasyShot: не удалось добавить в Login Items: \(error)")
            }
        }
    }

    /// Records a new shortcut: the next key press with ⌘, ⌃ or ⌥ (or an F key) becomes the hotkey.
    @objc private func changeShortcut() {
        // Unregister the current hotkey while recording, otherwise pressing it would fire it instead of being recorded.
        hotKey?.unregister()
        NSApp.activate()
        let alert = NSAlert()
        alert.messageText = "Screenshot Shortcut"
        alert.informativeText = "Press a new key combination with ⌘, ⌃ or ⌥, or an F key.\n\nCurrent shortcut: \(shortcut.description)"
        alert.addButton(withTitle: "Cancel")
        alert.addButton(withTitle: "Reset to \(Shortcut.standard.description)")
        recordedShortcut = nil
        let monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            guard let recorded = Shortcut(event: event) else { return event }
            self?.recordedShortcut = recorded
            NSApp.stopModal(withCode: .OK)
            return nil
        }
        let response = alert.runModal()
        alert.window.orderOut(nil)
        if let monitor {
            NSEvent.removeMonitor(monitor)
        }

        let chosen = response == .OK ? recordedShortcut : response == .alertSecondButtonReturn ? Shortcut.standard : nil
        if let chosen, hotKey?.register(chosen) == true {
            shortcut = chosen
            shortcut.save()
            showShortcutInMenu()
        } else {
            hotKey?.register(shortcut)
            if let chosen {
                let error = NSAlert()
                error.messageText = "\(chosen.description) can’t be used"
                error.informativeText = "This key combination is already taken by macOS or another app. The shortcut stays \(shortcut.description)."
                error.runModal()
            }
        }
    }

    private func showShortcutInMenu() {
        screenshotItem.keyEquivalent = shortcut.menuKeyEquivalent ?? ""
        screenshotItem.keyEquivalentModifierMask = shortcut.modifiers
    }

    @objc private func takeScreenshot() {
        guard overlays.isEmpty, !isCapturing else { return }
        isCapturing = true
        Task {
            defer { isCapturing = false }
            do {
                let shots = try await Capture.allScreens()
                overlays = shots.map { OverlayWindow(shot: $0) { self.closeOverlays() } }
            } catch {
                showCaptureError(error)
                return
            }
            NSApp.activate()
            overlays.forEach { $0.orderFrontRegardless() }
            let mouse = NSEvent.mouseLocation
            (overlays.first { $0.frame.contains(mouse) } ?? overlays.first)?.makeKey()
        }
    }

    private func closeOverlays() {
        overlays.forEach { $0.orderOut(nil) }
        // Release the windows only after the event handler that closed them has returned.
        DispatchQueue.main.async { self.overlays.removeAll() }
    }

    private func showCaptureError(_ error: Error) {
        NSApp.activate()
        let alert = NSAlert()
        alert.messageText = "Не удалось сделать снимок экрана"
        alert.informativeText = """
            Разрешите EasyShot запись экрана: Системные настройки → Конфиденциальность и безопасность → \
            Запись экрана и системного аудио. После этого перезапустите приложение.

            \(error.localizedDescription)
            """
        alert.addButton(withTitle: "Открыть настройки")
        alert.addButton(withTitle: "Отмена")
        if alert.runModal() == .alertFirstButtonReturn {
            NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture")!)
        }
    }
}
