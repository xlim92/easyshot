using System.Drawing.Imaging;
using SkiaSharp;

namespace EasyShot;

record ScreenShot(Screen Screen, SKImage Image);

static class Capture
{
    /// Captures all screens up front: selecting and drawing happen on a frozen image.
    public static List<ScreenShot> AllScreens() => [.. Screen.AllScreens.Select(screen => new ScreenShot(screen, Grab(screen.Bounds)))];

    private static SKImage Grab(Rectangle bounds)
    {
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        // BitBlt leaves the 4th byte of each pixel at 0, and blend modes such as invert read it as alpha.
        // Reading the pixels as ARGB makes GDI+ convert them with alpha 255.
        var data = bitmap.LockBits(new Rectangle(Point.Empty, bounds.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            return SKImage.FromPixelCopy(new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Bgra8888, SKAlphaType.Opaque), data.Scan0, data.Stride);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
