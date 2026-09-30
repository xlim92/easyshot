using System.Runtime.InteropServices;

namespace EasyShot;

/// Global hotkey via RegisterHotKey; WM_HOTKEY arrives at a hidden window of this class.
sealed partial class HotKey : NativeWindow
{
    private readonly Action handler;
    private bool registered;

    public HotKey(Action handler)
    {
        this.handler = handler;
        CreateHandle(new CreateParams());
    }

    /// Registers the shortcut in place of the previous one; returns false if the system refused it.
    public bool Register(Keys shortcut)
    {
        Unregister();
        const uint alt = 1, control = 2, shift = 4, noRepeat = 0x4000;
        var modifiers = noRepeat | (shortcut.HasFlag(Keys.Alt) ? alt : 0) | (shortcut.HasFlag(Keys.Control) ? control : 0)
                        | (shortcut.HasFlag(Keys.Shift) ? shift : 0);
        registered = RegisterHotKey(Handle, 1, modifiers, (uint)(shortcut & Keys.KeyCode));
        return registered;
    }

    public void Unregister()
    {
        if (registered)
            UnregisterHotKey(Handle, 1);
        registered = false;
    }

    protected override void WndProc(ref Message m)
    {
        const int hotKeyMessage = 0x0312;
        if (m.Msg == hotKeyMessage)
            handler();
        base.WndProc(ref m);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint window, int id);
}
