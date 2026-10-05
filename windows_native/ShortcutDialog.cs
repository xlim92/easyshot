namespace EasyShot;

/// Records a new shortcut: the next key press with Ctrl or Alt (or an F key) becomes the hotkey.
sealed unsafe class ShortcutDialog : Window
{
    private const int Width = 400, Height = 130, Margin = 12, ButtonHeight = 28;
    private const int CancelButton = 1, ResetButton = 2;
    private readonly string text;
    private readonly uint dpi = PInvoke.GetDpiForSystem();
    private readonly HFONT font;
    private bool closed;
    private Shortcut? chosen;

    private ShortcutDialog(Shortcut current)
    {
        text = $"Press a new key or key combination, such as PrtScn.\n\nCurrent shortcut: {current.Description}";
        // The dialog font of the system at the display's scale.
        var metrics = new NONCLIENTMETRICSW { cbSize = (uint)sizeof(NONCLIENTMETRICSW) };
        PInvoke.SystemParametersInfoForDpi((uint)SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETNONCLIENTMETRICS, metrics.cbSize, &metrics, 0, dpi);
        font = PInvoke.CreateFontIndirect(&metrics.lfMessageFont);

        const WINDOW_STYLE style = WINDOW_STYLE.WS_CAPTION | WINDOW_STYLE.WS_SYSMENU;
        const WINDOW_EX_STYLE exStyle = WINDOW_EX_STYLE.WS_EX_DLGMODALFRAME | WINDOW_EX_STYLE.WS_EX_TOPMOST;
        var frame = new RECT { right = Scale(Width), bottom = Scale(Height) };
        PInvoke.AdjustWindowRectExForDpi(&frame, style, false, exStyle, dpi);
        int width = frame.right - frame.left, height = frame.bottom - frame.top;
        Create("EasyShotShortcut", exStyle, style, "Screenshot Shortcut", (PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSCREEN) - width) / 2,
               (PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYSCREEN) - height) / 2, width, height);
        AddButton(CancelButton, "Cancel", Width - Margin - 90, 90);
        AddButton(ResetButton, $"Reset to {Shortcut.Standard.Description}", Width - Margin - 90 - 8 - 150, 150);
        PInvoke.ShowWindow(Handle, SHOW_WINDOW_CMD.SW_SHOW);
        PInvoke.SetForegroundWindow(Handle);
    }

    /// The chosen shortcut, or null if the dialog was cancelled.
    public static Shortcut? Run(Shortcut current)
    {
        var dialog = new ShortcutDialog(current);
        // A message loop of its own, as in a modal dialog: key presses are recorded before the buttons get them.
        MSG message;
        while (!dialog.closed)
        {
            if (PInvoke.GetMessage(&message, HWND.Null, 0, 0).Value <= 0)
            {
                // Keep the quit request for the main message loop.
                PInvoke.PostQuitMessage(0);
                break;
            }
            // Windows sends PrtScn to apps only as a key release.
            var isKey = message.message is PInvoke.WM_KEYDOWN or PInvoke.WM_SYSKEYDOWN
                        || (message.message is PInvoke.WM_KEYUP or PInvoke.WM_SYSKEYUP && message.wParam.Value == (nuint)VIRTUAL_KEY.VK_SNAPSHOT);
            if (isKey && (message.hwnd == dialog.Handle || PInvoke.IsChild(dialog.Handle, message.hwnd)) && dialog.Record((uint)message.wParam.Value))
                continue;
            PInvoke.TranslateMessage(&message);
            PInvoke.DispatchMessage(&message);
        }
        return dialog.chosen;
    }

    /// Esc cancels, any other key is chosen; modifier keys on their own go on to the buttons.
    private bool Record(uint key)
    {
        if (key == (uint)VIRTUAL_KEY.VK_ESCAPE)
            Finish(null);
        else if (Shortcut.From(key) is { } shortcut)
            Finish(shortcut);
        else
            return false;
        return true;
    }

    private void Finish(Shortcut? result)
    {
        chosen = result;
        closed = true;
        Close();
        PInvoke.DeleteObject(font);
    }

    protected override LRESULT OnMessage(uint message, WPARAM wParam, LPARAM lParam)
    {
        switch (message)
        {
            case PInvoke.WM_PAINT:
                var hdc = PInvoke.BeginPaint(Handle, out var paint);
                PInvoke.SelectObject(hdc, font);
                PInvoke.SetBkMode(hdc, BACKGROUND_MODE.TRANSPARENT);
                var area = new RECT { left = Scale(Margin), top = Scale(Margin), right = Scale(Width - Margin), bottom = Scale(Height - Margin - ButtonHeight) };
                fixed (char* s = text)
                    PInvoke.DrawText(hdc, s, -1, &area, DRAW_TEXT_FORMAT.DT_WORDBREAK);
                PInvoke.EndPaint(Handle, in paint);
                return default;
            case PInvoke.WM_COMMAND:
                Finish((wParam.Value & 0xFFFF) == ResetButton ? Shortcut.Standard : null);
                return default;
            case PInvoke.WM_CLOSE:
                Finish(null);
                return default;
        }
        return Default(message, wParam, lParam);
    }

    private void AddButton(int id, string label, int x, int width)
    {
        HWND button;
        fixed (char* className = "BUTTON", text = label)
        {
            button = PInvoke.CreateWindowEx(0, className, text, WINDOW_STYLE.WS_CHILD | WINDOW_STYLE.WS_VISIBLE | WINDOW_STYLE.WS_TABSTOP,
                                            Scale(x), Scale(Height - Margin - ButtonHeight), Scale(width), Scale(ButtonHeight), Handle, (HMENU)(nint)id, HINSTANCE.Null, null);
        }
        PInvoke.SendMessage(button, PInvoke.WM_SETFONT, (WPARAM)(nuint)(nint)font.Value, new LPARAM(1));
    }

    /// Dialog units are pixels at 100%.
    private int Scale(int value) => (int)(value * dpi / 96);
}
