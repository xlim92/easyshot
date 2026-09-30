namespace EasyShot;

/// A Win32 window whose messages go to OnMessage of its object.
abstract unsafe class Window
{
    private static readonly Dictionary<HWND, Window> Windows = [];
    private static Window? creating;

    public HWND Handle { get; private set; }

    protected void Create(string className, WINDOW_EX_STYLE exStyle, WINDOW_STYLE style, string title, int x, int y, int width, int height)
    {
        // Windows finds a class of the app by its name and module, so the window is created with the same module.
        var module = (HINSTANCE)PInvoke.GetModuleHandle(default(PCWSTR)).Value;
        fixed (char* name = className, text = title)
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                style = WNDCLASS_STYLES.CS_DBLCLKS,
                lpfnWndProc = &WndProc,
                hInstance = module,
                hCursor = PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_ARROW),
                hbrBackground = (HBRUSH)(nint)(SYS_COLOR_INDEX.COLOR_WINDOW + 1),
                lpszClassName = name,
            };
            // Registering the class of a second window of the same kind fails harmlessly.
            PInvoke.RegisterClassEx(&windowClass);
            creating = this;
            Handle = PInvoke.CreateWindowEx(exStyle, name, text, style, x, y, width, height, HWND.Null, HMENU.Null, module, null);
            creating = null;
        }
    }

    public void Close() => PInvoke.DestroyWindow(Handle);

    protected void Invalidate() => PInvoke.InvalidateRect(Handle, (RECT*)null, false);

    protected abstract LRESULT OnMessage(uint message, WPARAM wParam, LPARAM lParam);

    protected LRESULT Default(uint message, WPARAM wParam, LPARAM lParam) => PInvoke.DefWindowProc(Handle, message, wParam, lParam);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WndProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        if (!Windows.TryGetValue(hwnd, out var window))
        {
            if (creating == null)
                return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
            // The first messages arrive while CreateWindowEx is still running.
            window = creating;
            window.Handle = hwnd;
            Windows[hwnd] = window;
        }
        try
        {
            return window.OnMessage(message, wParam, lParam);
        }
        catch (Exception error)
        {
            // An exception can't cross into Windows code: it would end the process.
            PInvoke.MessageBox(HWND.Null, error.Message, "EasyShot", MESSAGEBOX_STYLE.MB_ICONERROR);
            return default;
        }
        finally
        {
            if (message == PInvoke.WM_NCDESTROY)
                Windows.Remove(hwnd);
        }
    }
}
