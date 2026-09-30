using Microsoft.Win32;

namespace EasyShot;

/// Screenshot keyboard shortcut: a Keys value with its modifiers, stored in the registry; Ctrl+F12 by default.
static class Shortcut
{
    public const Keys Standard = Keys.Control | Keys.F12;
    private const string Settings = @"HKEY_CURRENT_USER\Software\EasyShot";

    /// Shortcut from a key press; null without Ctrl or Alt (F keys excepted), since such a hotkey would swallow normal typing.
    /// Alt+F4 is refused too: it closes windows.
    public static Keys? From(Keys keys)
    {
        var key = keys & Keys.KeyCode;
        var isFunctionKey = key is >= Keys.F1 and <= Keys.F24;
        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin || ((keys & (Keys.Control | Keys.Alt)) == 0 && !isFunctionKey)
            || keys == (Keys.Alt | Keys.F4))
            return null;
        return keys;
    }

    public static Keys Load() => Registry.GetValue(Settings, "Shortcut", null) is int value ? (Keys)value : Standard;

    public static void Save(Keys keys) => Registry.SetValue(Settings, "Shortcut", (int)keys);

    /// Windows notation, e.g. Ctrl+Shift+F12.
    public static string Describe(Keys keys) => new KeysConverter().ConvertToString(keys) ?? "";
}
