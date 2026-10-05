namespace EasyShot;

/// The app glyph, drawn in code: a camera inside viewfinder corners. The same drawing as Sources/Glyph.swift,
/// used for the tray icon.
static class Glyph
{
    /// Viewfinder corners: the corner point and the directions of its two arms.
    private static readonly (float X, float Y, float Dx, float Dy)[] Corners = [(6, 6, 1, 1), (94, 6, -1, 1), (6, 94, 1, -1), (94, 94, -1, -1)];

    /// The camera body with the viewfinder bump on top as one outline; the lens ring and the flash are holes in it.
    private const string Body = "M32 28H68A8 8 0 0 1 76 36V58A8 8 0 0 1 68 66H61V69A4 4 0 0 1 57 73H43A4 4 0 0 1 39 69V66H32A8 8 0 0 1 24 58V36A8 8 0 0 1 32 28Z"
                                + "M64 47A14 14 0 0 1 36 47A14 14 0 0 1 64 47ZM70 57.5A2.5 2.5 0 0 1 65 57.5A2.5 2.5 0 0 1 70 57.5Z";

    /// Draws the glyph into `rect` on a 100 × 100 grid whose y axis points up, like in the macOS version.
    public static void Draw(Canvas canvas, RectangleF rect, uint color)
    {
        var state = canvas.Save();
        canvas.Translate(rect.Left, rect.Bottom);
        canvas.Scale(rect.Width / 100, -rect.Height / 100);
        foreach (var (x, y, dx, dy) in Corners)
            canvas.Lines([new(x, y + 20 * dy), new(x, y), new(x + 20 * dx, y)], color, 7);
        canvas.FillPath(Body, color, evenOdd: true);
        // The lens inside the ring.
        canvas.FillEllipse(new RectangleF(42.5f, 39.5f, 15, 15), color);
        canvas.Restore(state);
    }

    /// Tray icon of the given size in pixels.
    public static HICON TrayIcon(int size, uint color)
    {
        using var image = new Pixels(size, size);
        using (var canvas = new Canvas(image))
            Draw(canvas, RectangleF.Inflate(new RectangleF(0, 0, size, size), -size / 16f, -size / 16f), color);
        return Canvas.ToIcon(image);
    }
}
