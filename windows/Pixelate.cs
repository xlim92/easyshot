namespace EasyShot;

/// A mosaic of Columns × Rows colors, row by row.
sealed record Mosaic(uint[] Colors, int Columns, int Rows);

/// Mosaic for hiding content. Pixels inside the area are never read: each block is filled with
/// a blend of two pixels from the border around the area, picked by a hash of the block coordinates.
/// The hidden content can't be recovered from the mosaic, and the image stays the same between redraws.
static unsafe class Pixelate
{
    /// `rect` is the area in screenshot pixels, `block` the mosaic block size in pixels.
    public static Mosaic? Image(Pixels screenshot, RectangleF rect, int block)
    {
        int x0 = Math.Max((int)MathF.Floor(rect.Left), 0), y0 = Math.Max((int)MathF.Floor(rect.Top), 0);
        int x1 = Math.Min((int)MathF.Ceiling(rect.Right), screenshot.Width), y1 = Math.Min((int)MathF.Ceiling(rect.Bottom), screenshot.Height);
        if (x0 >= x1 || y0 >= y1)
            return null;
        // A 2-pixel border just outside the area, clipped to the screenshot.
        int ox0 = Math.Max(x0 - 2, 0), oy0 = Math.Max(y0 - 2, 0), ox1 = Math.Min(x1 + 2, screenshot.Width), oy1 = Math.Min(y1 + 2, screenshot.Height);
        var border = new List<uint>();
        Add(border, screenshot, ox0, oy0, ox1, y0);
        Add(border, screenshot, ox0, y1, ox1, oy1);
        Add(border, screenshot, ox0, y0, x0, y1);
        Add(border, screenshot, x1, y0, ox1, y1);
        if (border.Count == 0)
            // The area covers the whole screenshot, so there is no outer border: use the area's top row.
            Add(border, screenshot, x0, y0, x1, y0 + 1);

        var side = Math.Max(block, 1);
        int columns = Math.Max((x1 - x0) / side, 1), rows = Math.Max((y1 - y0) / side, 1);
        var colors = new uint[columns * rows];
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                uint a = border[Hash(column, row, 1) % border.Count], b = border[Hash(column, row, 2) % border.Count];
                colors[row * columns + column] = 0xFF000000 | Average(a, b, 16) | Average(a, b, 8) | Average(a, b, 0);
            }
        }
        return new Mosaic(colors, columns, rows);
    }

    /// Average of one color channel of two pixels.
    private static uint Average(uint a, uint b, int shift) => ((a >> shift & 0xFF) + (b >> shift & 0xFF)) / 2 << shift;

    private static void Add(List<uint> pixels, Pixels image, int x0, int y0, int x1, int y1)
    {
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
                pixels.Add(image.Data[y * image.Width + x]);
        }
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
