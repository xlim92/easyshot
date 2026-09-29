import AppKit
import Carbon.HIToolbox

/// Screenshot keyboard shortcut, stored in UserDefaults; ⌘F12 by default.
struct Shortcut: Equatable {
    static let standard = Shortcut(keyCode: kVK_F12, modifiers: .command)

    let keyCode: Int
    let modifiers: NSEvent.ModifierFlags

    init(keyCode: Int, modifiers: NSEvent.ModifierFlags) {
        self.keyCode = keyCode
        self.modifiers = modifiers.intersection([.command, .control, .option, .shift])
    }

    /// Shortcut from a key press; nil without ⌘, ⌃ or ⌥ (F keys excepted), since such a hotkey would swallow normal typing.
    init?(event: NSEvent) {
        self.init(keyCode: Int(event.keyCode), modifiers: event.modifierFlags)
        guard !modifiers.isDisjoint(with: [.command, .control, .option]) || Self.functionKeys[keyCode] != nil else { return nil }
    }

    static func load() -> Shortcut {
        let defaults = UserDefaults.standard
        guard defaults.object(forKey: "shortcutKeyCode") != nil else { return .standard }
        return Shortcut(keyCode: defaults.integer(forKey: "shortcutKeyCode"),
                        modifiers: NSEvent.ModifierFlags(rawValue: UInt(defaults.integer(forKey: "shortcutModifiers"))))
    }

    func save() {
        UserDefaults.standard.set(keyCode, forKey: "shortcutKeyCode")
        UserDefaults.standard.set(Int(modifiers.rawValue), forKey: "shortcutModifiers")
    }

    var carbonModifiers: Int {
        (modifiers.contains(.command) ? cmdKey : 0) | (modifiers.contains(.control) ? controlKey : 0)
            | (modifiers.contains(.option) ? optionKey : 0) | (modifiers.contains(.shift) ? shiftKey : 0)
    }

    /// Same notation as macOS menus: ⌃⌥⇧⌘ followed by the key, e.g. ⌘F12.
    var description: String {
        [(NSEvent.ModifierFlags.control, "⌃"), (.option, "⌥"), (.shift, "⇧"), (.command, "⌘")]
            .filter { modifiers.contains($0.0) }.map(\.1).joined() + keyName
    }

    /// Key equivalent for showing the shortcut in a menu: F keys and plain characters; other keys aren't shown.
    var menuKeyEquivalent: String? {
        if let name = Self.functionKeys[keyCode], let number = Int(name.dropFirst()) {
            return String(UnicodeScalar(NSF1FunctionKey + number - 1)!)
        }
        guard Self.specialKeys[keyCode] == nil, let character = Self.character(for: keyCode), character.count == 1 else { return nil }
        return character.lowercased()
    }

    private var keyName: String {
        Self.functionKeys[keyCode] ?? Self.specialKeys[keyCode] ?? Self.character(for: keyCode)?.uppercased() ?? "#\(keyCode)"
    }

    private static let functionKeys = [
        kVK_F1: "F1", kVK_F2: "F2", kVK_F3: "F3", kVK_F4: "F4", kVK_F5: "F5", kVK_F6: "F6", kVK_F7: "F7",
        kVK_F8: "F8", kVK_F9: "F9", kVK_F10: "F10", kVK_F11: "F11", kVK_F12: "F12", kVK_F13: "F13", kVK_F14: "F14",
        kVK_F15: "F15", kVK_F16: "F16", kVK_F17: "F17", kVK_F18: "F18", kVK_F19: "F19", kVK_F20: "F20",
    ]

    private static let specialKeys = [
        kVK_Space: "Space", kVK_Return: "↩", kVK_Tab: "⇥", kVK_Delete: "⌫", kVK_ForwardDelete: "⌦", kVK_Escape: "⎋",
        kVK_LeftArrow: "←", kVK_RightArrow: "→", kVK_UpArrow: "↑", kVK_DownArrow: "↓",
        kVK_Home: "↖", kVK_End: "↘", kVK_PageUp: "⇞", kVK_PageDown: "⇟",
    ]

    /// Key character from the ASCII-capable layout, so a Russian layout shows ⌘S rather than ⌘Ы.
    private static func character(for keyCode: Int) -> String? {
        guard let source = TISCopyCurrentASCIICapableKeyboardLayoutInputSource()?.takeRetainedValue(),
              let data = TISGetInputSourceProperty(source, kTISPropertyUnicodeKeyLayoutData) else { return nil }
        let layout = Unmanaged<CFData>.fromOpaque(data).takeUnretainedValue()
        var deadKeyState: UInt32 = 0
        var chars = [UniChar](repeating: 0, count: 4)
        var length = 0
        let status = CFDataGetBytePtr(layout).withMemoryRebound(to: UCKeyboardLayout.self, capacity: 1) {
            UCKeyTranslate($0, UInt16(keyCode), UInt16(kUCKeyActionDisplay), 0, UInt32(LMGetKbdType()),
                           OptionBits(kUCKeyTranslateNoDeadKeysBit), &deadKeyState, chars.count, &length, &chars)
        }
        return status == noErr && length > 0 ? String(utf16CodeUnits: chars, count: length) : nil
    }
}
