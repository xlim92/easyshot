using SkiaSharp;

namespace EasyShot;

/// Mosaic for hiding content. Pixels inside the area are never read: each block is filled with
/// a blend of two pixels from the border around the area, picked by a hash of the block coordinates.
/// The hidden content can't be recovered from the mosaic, and the image stays the same between redraws.
static class Pixelate
{
    /// `rect` is the area in screenshot pixels (origin at the top-left corner), `block` the mosaic block size in pixels.
    public static SKImage? Image(SKImage screenshot, SKRect rect, int block)
    {
        var bounds = SKRectI.Create(screenshot.Width, screenshot.Height);
        var area = SKRectI.Intersect(SKRectI.Ceiling(rect, true), bounds);
        if (area.IsEmpty)
            return null;
        // A 2-pixel border just outside the area, clipped to the screenshot.
        var outer = SKRectI.Intersect(SKRectI.Inflate(area, 2, 2), bounds);
        using var pixmap = screenshot.PeekPixels();
        List<(byte R, byte G, byte B)> border =
        [
            .. Pixels(pixmap, new SKRectI(outer.Left, outer.Top, outer.Right, area.Top)),
            .. Pixels(pixmap, new SKRectI(outer.Left, area.Bottom, outer.Right, outer.Bottom)),
            .. Pixels(pixmap, new SKRectI(outer.Left, area.Top, area.Left, area.Bottom)),
            .. Pixels(pixmap, new SKRectI(area.Right, area.Top, outer.Right, area.Bottom)),
        ];
        if (border.Count == 0)
            // The area covers the whole screenshot, so there is no outer border: use the area's top row.
            border = Pixels(pixmap, new SKRectI(area.Left, area.Top, area.Right, area.Top + 1));

        var side = Math.Max(block, 1);
        int columns = Math.Max(area.Width / side, 1), rows = Math.Max(area.Height / side, 1);
        var mosaic = new byte[columns * rows * 4];
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var (a, b) = (border[Hash(column, row, 1) % border.Count], border[Hash(column, row, 2) % border.Count]);
                var offset = (row * columns + column) * 4;
                mosaic[offset] = (byte)((a.R + b.R) / 2);
                mosaic[offset + 1] = (byte)((a.G + b.G) / 2);
                mosaic[offset + 2] = (byte)((a.B + b.B) / 2);
                mosaic[offset + 3] = 255;
            }
        }
        return SKImage.FromPixelCopy(new SKImageInfo(columns, rows, SKColorType.Rgba8888, SKAlphaType.Opaque), mosaic);
    }

    /// RGB pixels of a screenshot rectangle; the screenshot is stored as BGRA.
    private static List<(byte R, byte G, byte B)> Pixels(SKPixmap pixmap, SKRectI rect)
    {
        var bytes = pixmap.GetPixelSpan();
        var pixels = new List<(byte R, byte G, byte B)>();
        for (var y = rect.Top; y < rect.Bottom; y++)
        {
            for (var x = rect.Left; x < rect.Right; x++)
            {
                var i = y * pixmap.RowBytes + x * 4;
                pixels.Add((bytes[i + 2], bytes[i + 1], bytes[i]));
            }
        }
        return pixels;
    }

    /// Mixes block coordinates into a non-negative pseudo-random number (SplitMix64 finalizer).
    private static int Hash(int x, int y, ulong salt)
    {
        var z = (ulong)x * 0x9E3779B97F4A7C15 ^ (ulong)y * 0xD1B54A32D192ED03 ^ salt;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
        return (int)((z ^ (z >> 31)) >> 33);
    }
}
