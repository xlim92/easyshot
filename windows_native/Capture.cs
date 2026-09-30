namespace EasyShot;

/// Captures all screens up front: selecting and drawing happen on a frozen image.
static unsafe class Capture
{
    private static readonly List<RECT> Displays = [];

    public static List<(RECT Bounds, Pixels Image)> AllScreens()
    {
        Displays.Clear();
        PInvoke.EnumDisplayMonitors(HDC.Null, (RECT*)null, &AddDisplay, default);
        var shots = new List<(RECT, Pixels)>();
        foreach (var bounds in Displays)
            shots.Add((bounds, Grab(bounds)));
        return shots;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL AddDisplay(HMONITOR monitor, HDC hdc, RECT* bounds, LPARAM data)
    {
        Displays.Add(*bounds);
        return true;
    }

    private static Pixels Grab(RECT bounds)
    {
        int width = bounds.right - bounds.left, height = bounds.bottom - bounds.top;
        var image = new Pixels(width, height);
        var screen = PInvoke.GetDC(HWND.Null);
        var memory = PInvoke.CreateCompatibleDC(screen);
        var header = image.Header;
        void* bits;
        var bitmap = PInvoke.CreateDIBSection(memory, &header, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
        var old = PInvoke.SelectObject(memory, bitmap);
        // CAPTUREBLT also takes layered windows, such as menus and tooltips.
        var captured = PInvoke.BitBlt(memory, 0, 0, width, height, screen, bounds.left, bounds.top, ROP_CODE.SRCCOPY | ROP_CODE.CAPTUREBLT);
        // BitBlt leaves the 4th byte of each pixel at 0: the screenshot is opaque.
        for (var i = 0; i < width * height; i++)
            image.Data[i] = ((uint*)bits)[i] | 0xFF000000;
        PInvoke.SelectObject(memory, old);
        PInvoke.DeleteObject(bitmap);
        PInvoke.DeleteDC(memory);
        PInvoke.ReleaseDC(HWND.Null, screen);
        if (!captured)
        {
            image.Dispose();
            throw new InvalidOperationException("Не удалось сделать снимок экрана");
        }
        return image;
    }
}
