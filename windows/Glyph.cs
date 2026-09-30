using SkiaSharp;

namespace EasyShot;

/// The app glyph, drawn in code: a camera inside viewfinder corners. The same drawing as Sources/Glyph.swift,
/// used for the tray icon.
static class Glyph
{
    /// Draws the glyph into `rect` on a 100 × 100 grid whose y axis points up, like in the macOS version.
    public static void Draw(SKCanvas canvas, SKRect rect, SKColor color)
    {
        canvas.Save();
        canvas.Translate(rect.Left, rect.Bottom);
        canvas.Scale(rect.Width / 100, -rect.Height / 100);
        // A separate layer, so the cut-outs below remove only the glyph and not what lies under it.
        canvas.SaveLayer();
        using var paint = new SKPaint
        {
            IsAntialias = true, Color = color, IsStroke = true, StrokeWidth = 7, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
        };

        // Viewfinder corners.
        using var corners = new SKPathBuilder();
        foreach (var (x, y, dx, dy) in new[] { (6f, 6f, 1f, 1f), (94, 6, -1, 1), (6, 94, 1, -1), (94, 94, -1, -1) })
        {
            corners.MoveTo(x, y + 20 * dy);
            corners.LineTo(x, y);
            corners.LineTo(x + 20 * dx, y);
        }
        using var path = corners.Detach();
        canvas.DrawPath(path, paint);

        // Camera body with the viewfinder bump on top.
        paint.IsStroke = false;
        canvas.DrawRoundRect(SKRect.Create(24, 28, 52, 38), 8, 8, paint);
        canvas.DrawRoundRect(SKRect.Create(39, 60, 22, 13), 4, 4, paint);

        // The lens ring and the flash are cut out of the body, then the lens itself is filled back in.
        paint.BlendMode = SKBlendMode.DstOut;
        canvas.DrawOval(SKRect.Create(36, 33, 28, 28), paint);
        canvas.DrawOval(SKRect.Create(65, 55, 5, 5), paint);
        paint.BlendMode = SKBlendMode.SrcOver;
        canvas.DrawOval(SKRect.Create(42.5f, 39.5f, 15, 15), paint);
        canvas.Restore();
        canvas.Restore();
    }
}
