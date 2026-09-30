using SkiaSharp;

namespace EasyShot;

/// Toolbar icons, drawn in code on a 24 × 24 grid: the macOS version uses SF Symbols, which may be used only on Apple platforms.
static class Icons
{
    public static void Draw(SKCanvas canvas, string name, SKRect rect, SKColor color)
    {
        canvas.Save();
        canvas.Translate(rect.Left, rect.Top);
        canvas.Scale(rect.Width / 24, rect.Height / 24);
        using var paint = new SKPaint
        {
            IsAntialias = true, Color = color, IsStroke = true, StrokeWidth = 2, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
        };
        switch (name)
        {
            case "Move":
                DrawSvg("M6 3.5V18.5L10 14.8L12.7 20.5L15.1 19.4L12.4 13.8H18Z");
                break;
            case nameof(Tool.Pencil):
                DrawSvg("M4 20L5 15.5L15.5 5A2.475 2.475 0 0 1 19 8.5L8.5 19ZM5 15.5L8.5 19M13.2 7.3L16.7 10.8");
                break;
            case nameof(Tool.Line):
                DrawSvg("M5 19L19 5");
                break;
            case nameof(Tool.Arrow):
                DrawSvg("M6 18L18 6M9 6H18V15");
                break;
            case nameof(Tool.Rectangle):
                canvas.DrawRoundRect(SKRect.Create(3.5f, 6, 17, 12), 2, 2, paint);
                break;
            case nameof(Tool.FilledRect):
                paint.IsStroke = false;
                canvas.DrawRoundRect(SKRect.Create(2.5f, 5, 19, 14), 2.5f, 2.5f, paint);
                break;
            case nameof(Tool.Ellipse):
                canvas.DrawCircle(12, 12, 8.5f, paint);
                break;
            case nameof(Tool.Marker):
                // A chisel-tip marker tilted towards the bottom left, over the stripe it leaves.
                DrawSvg("M3.5 20.5H11");
                canvas.Translate(-1.5f, 1.5f);
                canvas.RotateDegrees(45, 12, 12);
                DrawSvg("M8.5 3A2 2 0 0 1 10.5 1H13.5A2 2 0 0 1 15.5 3V12H8.5ZM9.5 12V17L14.5 14.5V12");
                break;
            case nameof(Tool.Text):
                DrawSvg("M5 5.5H19M12 5.5V19.5M9.5 19.5H14.5");
                break;
            case nameof(Tool.Pixelate):
                var frame = SKRect.Create(3.5f, 5.5f, 17, 13);
                canvas.Save();
                canvas.ClipRect(frame, antialias: true);
                paint.IsStroke = false;
                for (var row = 0; row < 3; row++)
                    for (var column = row % 2; column < 4; column += 2)
                        canvas.DrawRect(SKRect.Create(frame.Left + column * frame.Width / 4, frame.Top + row * frame.Height / 3, frame.Width / 4, frame.Height / 3), paint);
                canvas.Restore();
                paint.IsStroke = true;
                canvas.DrawRoundRect(frame, 2, 2, paint);
                break;
            case nameof(Tool.Counter):
                // A filled circle with the digit cut out of it.
                canvas.SaveLayer();
                paint.IsStroke = false;
                canvas.DrawCircle(12, 12, 9.5f, paint);
                paint.IsStroke = true;
                paint.BlendMode = SKBlendMode.DstOut;
                DrawSvg("M9.8 9L12.6 7V17");
                canvas.Restore();
                break;
            case nameof(Tool.Invert):
                canvas.DrawCircle(12, 12, 8.5f, paint);
                paint.IsStroke = false;
                DrawSvg("M12 3.5A8.5 8.5 0 0 0 12 20.5Z");
                break;
            case "Undo":
                DrawSvg("M9 14L4 9L9 4M4 9H14.5A5.5 5.5 0 0 1 14.5 20H11");
                break;
            case "Redo":
                DrawSvg("M15 14L20 9L15 4M20 9H9.5A5.5 5.5 0 0 0 9.5 20H13");
                break;
            case "Copy":
                DrawSvg("M15.5 7.5V5A1.5 1.5 0 0 0 14 3.5H6A1.5 1.5 0 0 0 4.5 5V15A1.5 1.5 0 0 0 6 16.5H8.5");
                canvas.DrawRoundRect(SKRect.Create(8.5f, 7.5f, 11, 13), 1.5f, 1.5f, paint);
                break;
            case "Save":
                DrawSvg("M12 3.5V14.5M7.5 10L12 14.5L16.5 10M4.5 13.5V18.5A2 2 0 0 0 6.5 20.5H17.5A2 2 0 0 0 19.5 18.5V13.5");
                break;
            case "Close":
                DrawSvg("M6 6L18 18M18 6L6 18");
                break;
        }
        canvas.Restore();

        void DrawSvg(string svg)
        {
            using var path = SKPath.ParseSvgPathData(svg);
            canvas.DrawPath(path, paint);
        }
    }
}
