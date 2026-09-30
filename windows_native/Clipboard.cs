using Windows.Win32.System.Memory;
using Windows.Win32.System.Ole;

namespace EasyShot;

/// The clipboard: images go there as device-independent bitmaps, text comes from it as Unicode text.
static unsafe class Clipboard
{
    /// False while another app holds the clipboard.
    public static bool SetImage(Pixels image, HWND owner)
    {
        if (!Open(owner))
            return false;
        PInvoke.EmptyClipboard();
        var rowBytes = image.Width * 4;
        var memory = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, (nuint)(sizeof(BITMAPINFOHEADER) + rowBytes * image.Height));
        var data = (byte*)PInvoke.GlobalLock(memory);
        // A positive height: rows from bottom to top, the form all apps read.
        *(BITMAPINFOHEADER*)data = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = image.Width, biHeight = image.Height, biPlanes = 1, biBitCount = 32,
        };
        for (var y = 0; y < image.Height; y++)
            Buffer.MemoryCopy(image.Data + (image.Height - 1 - y) * image.Width, data + sizeof(BITMAPINFOHEADER) + y * rowBytes, rowBytes, rowBytes);
        PInvoke.GlobalUnlock(memory);
        PInvoke.SetClipboardData((uint)CLIPBOARD_FORMAT.CF_DIB, (HANDLE)(nint)memory.Value);
        PInvoke.CloseClipboard();
        return true;
    }

    /// Null when there is no text or another app holds the clipboard.
    public static string? GetText(HWND owner)
    {
        if (!Open(owner))
            return null;
        string? text = null;
        var memory = (HGLOBAL)(nint)PInvoke.GetClipboardData((uint)CLIPBOARD_FORMAT.CF_UNICODETEXT).Value;
        var chars = (char*)PInvoke.GlobalLock(memory);
        if (chars != null)
        {
            text = new string(chars);
            PInvoke.GlobalUnlock(memory);
        }
        PInvoke.CloseClipboard();
        return text;
    }

    /// Another app may hold the clipboard for a moment.
    private static bool Open(HWND owner)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (PInvoke.OpenClipboard(owner))
                return true;
            Thread.Sleep(20);
        }
        return false;
    }
}
