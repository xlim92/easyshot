namespace EasyShot;

static unsafe class Program
{
    [STAThread]
    static void Main()
    {
        // A second copy would add a second tray icon and couldn't register the same hotkey.
        using var mutex = new Mutex(true, "EasyShot", out var isFirst);
        if (!isFirst)
            return;
        var input = new GdiplusStartupInput { GdiplusVersion = 1 };
        nuint token;
        PInvoke.GdiplusStartup(&token, &input, null);
        _ = new App();
        MSG message;
        while (PInvoke.GetMessage(&message, HWND.Null, 0, 0).Value > 0)
        {
            PInvoke.TranslateMessage(&message);
            PInvoke.DispatchMessage(&message);
        }
    }
}
