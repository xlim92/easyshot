import Carbon.HIToolbox

/// Global hotkey via Carbon; unlike event taps it doesn't need the Accessibility permission.
@MainActor
final class HotKey {
    private var ref: EventHotKeyRef?
    private let handler: @MainActor () -> Void

    init(handler: @escaping @MainActor () -> Void) {
        self.handler = handler
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, _, userData in
            let hotKey = Unmanaged<HotKey>.fromOpaque(userData!).takeUnretainedValue()
            MainActor.assumeIsolated { hotKey.handler() }
            return noErr
        }, 1, &spec, Unmanaged.passUnretained(self).toOpaque(), nil)
    }

    /// Registers the shortcut in place of the previous one; returns false if the system refused it.
    @discardableResult
    func register(_ shortcut: Shortcut) -> Bool {
        unregister()
        return RegisterEventHotKey(UInt32(shortcut.keyCode), UInt32(shortcut.carbonModifiers), EventHotKeyID(signature: 0x4D59_5348, id: 1),
                                   GetApplicationEventTarget(), 0, &ref) == noErr
    }

    func unregister() {
        if let ref {
            UnregisterEventHotKey(ref)
        }
        ref = nil
    }
}
