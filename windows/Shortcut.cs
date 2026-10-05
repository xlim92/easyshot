namespace EasyShot;

/// Screenshot keyboard shortcut: a virtual-key code with the modifier bits of WinForms Keys, the same value the WinForms version
/// keeps in the registry; Ctrl+F12 by default.
readonly record struct Shortcut(uint Value)
{
    private const uint ShiftBit = 0x10000, ControlBit = 0x20000, AltBit = 0x40000;
    private const string Settings = @"Software\EasyShot";

    public static readonly Shortcut Standard = new(ControlBit | (uint)VIRTUAL_KEY.VK_F12);

    public uint Key => Value & 0xFFFF;

    public HOT_KEY_MODIFIERS Modifiers => HOT_KEY_MODIFIERS.MOD_NOREPEAT
                                          | ((Value & AltBit) != 0 ? HOT_KEY_MODIFIERS.MOD_ALT : 0)
                                          | ((Value & ControlBit) != 0 ? HOT_KEY_MODIFIERS.MOD_CONTROL : 0)
                                          | ((Value & ShiftBit) != 0 ? HOT_KEY_MODIFIERS.MOD_SHIFT : 0);

    /// Windows notation, e.g. Ctrl+Shift+F12.
    public string Description => ((Value & ControlBit) != 0 ? "Ctrl+" : "") + ((Value & AltBit) != 0 ? "Alt+" : "")
                                 + ((Value & ShiftBit) != 0 ? "Shift+" : "") + KeyName(Key);

    /// Shortcut from a key press, alone or with modifiers, such as PrtScn; null for a modifier key on its own.
    /// Alt+F4 is refused: it closes windows.
    public static Shortcut? From(uint key)
    {
        bool control = Pressed(VIRTUAL_KEY.VK_CONTROL), alt = Pressed(VIRTUAL_KEY.VK_MENU), shift = Pressed(VIRTUAL_KEY.VK_SHIFT);
        var isModifier = (VIRTUAL_KEY)key is VIRTUAL_KEY.VK_SHIFT or VIRTUAL_KEY.VK_CONTROL or VIRTUAL_KEY.VK_MENU or VIRTUAL_KEY.VK_LWIN or VIRTUAL_KEY.VK_RWIN;
        if (isModifier || (alt && !control && !shift && key == (uint)VIRTUAL_KEY.VK_F4))
            return null;
        return new Shortcut(key | (shift ? ShiftBit : 0) | (control ? ControlBit : 0) | (alt ? AltBit : 0));
    }

    public static Shortcut Load() => Registry.ReadNumber(Settings, "Shortcut") is { } value ? new Shortcut(value) : Standard;

    public void Save() => Registry.Write(Settings, "Shortcut", Value);

    private static string KeyName(uint key)
    {
        if (key is >= (uint)VIRTUAL_KEY.VK_F1 and <= (uint)VIRTUAL_KEY.VK_F24)
            return $"F{key - (uint)VIRTUAL_KEY.VK_F1 + 1}";
        if (key is >= '0' and <= '9' or >= 'A' and <= 'Z')
            return $"{(char)key}";
        // Extended keys, such as PrtScn, the arrows and Home, are named by their extended scan code;
        // without the extended flag PrtScn would read as Num *.
        var scanCode = PInvoke.MapVirtualKey(key, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC_EX);
        Span<char> name = stackalloc char[32];
        var length = PInvoke.GetKeyNameText((int)((scanCode & 0xFF) << 16 | ((scanCode & 0xFF00) == 0xE000 ? 1u << 24 : 0)), name);
        return length > 0 ? name[..length].ToString() : $"#{key}";
    }

    private static bool Pressed(VIRTUAL_KEY key) => PInvoke.GetKeyState((int)key) < 0;
}
