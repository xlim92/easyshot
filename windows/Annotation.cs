using SkiaSharp;

namespace EasyShot;

/// Drawing tools; the letter is the keyboard shortcut.
enum Tool { Pencil, Line, Arrow, Rectangle, FilledRect, Ellipse, Marker, Text, Pixelate, Counter, Invert }

/// A drawn object. Coordinates are in screen points (1/96 inch) with the y axis pointing down.
/// Size is in the tool's units: line width, font size, radius of the numbered circle, mosaic block size,
/// corner radius of the filled rectangle. Points: the whole path for the pencil, the anchor point for text, [start, end] for the rest.
sealed record Annotation(Tool Tool, SKColor Color, int Size, SKPoint[] Points)
{
    public static readonly SKTypeface Medium = Typeface(SKFontStyleWeight.Medium);
    public static readonly SKTypeface Bold = Typeface(SKFontStyleWeight.Bold);

    public string Text { get; init; } = "";
    public int Number { get; init; }
    /// Rendered mosaic; the caller recomputes it whenever the geometry changes.
    public SKImage? Pixelated { get; init; }

    public SKPoint Start => Points[0];
    public SKPoint End => Points[^1];
    public SKRect Box => new(Math.Min(Start.X, End.X), Math.Min(Start.Y, End.Y), Math.Max(Start.X, End.X), Math.Max(Start.Y, End.Y));

    public static SKFont TextFont(int size) => new(Medium, size);

    /// Empty objects (a click without dragging, empty text) are not added to the history.
    public bool IsValid => Tool switch
    {
        Tool.Text => Text.Length > 0,
        Tool.Counter => true,
        _ => Points.Length > 1 && Start != End,
    };

    /// Text frame with 4pt padding.
    public SKRect TextRect
    {
        get
        {
            using var font = TextFont(Size);
            var lines = Text.Split('\n');
            return SKRect.Create(Start.X, Start.Y, MathF.Ceiling(lines.Max(line => font.MeasureText(line))) + 8,
                                 MathF.Ceiling(lines.Length * font.Spacing) + 8);
        }
    }

    /// Object area used for the selection frame.
    public SKRect Bounds
    {
        get
        {
            switch (Tool)
            {
                case Tool.Text:
                    return TextRect;
                case Tool.Counter:
                    var r = Size + 2f;
                    return new SKRect(Math.Min(Start.X - r, End.X), Math.Min(Start.Y - r, End.Y), Math.Max(Start.X + r, End.X), Math.Max(Start.Y + r, End.Y));
                case Tool.FilledRect or Tool.Pixelate or Tool.Invert:
                    return Box;
                default:
                    var pad = Size / 2f + 3;
                    return new SKRect(Points.Min(p => p.X) - pad, Points.Min(p => p.Y) - pad, Points.Max(p => p.X) + pad, Points.Max(p => p.Y) + pad);
            }
        }
    }

    /// Hit testing: lines and outlines are hit along the stroke with 4pt tolerance; fills, text and circles anywhere inside.
    public bool Contains(SKPoint p)
    {
        if (Tool is Tool.Text or Tool.Counter or Tool.FilledRect or Tool.Pixelate or Tool.Invert)
            return SKRect.Inflate(Bounds, 4, 4).Contains(p);
        using var builder = new SKPathBuilder();
        switch (Tool)
        {
            case Tool.Rectangle:
                builder.AddRect(Box);
                break;
            case Tool.Ellipse:
                builder.AddOval(Box);
                break;
            default:
                builder.AddPoly(Points, false);
                break;
        }
        using var path = builder.Detach();
        using var stroke = new SKPaint { IsStroke = true, StrokeWidth = Size + 8, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        using var outline = stroke.GetFillPath(path);
        return outline.Contains(p.X, p.Y);
    }

    public Annotation Moved(SKPoint d) => this with { Points = [.. Points.Select(p => p + d)] };

    /// Draws the object onto the canvas (y grows downwards).
    public void Draw(SKCanvas canvas)
    {
        using var paint = new SKPaint
        {
            IsAntialias = true, Color = Color, IsStroke = true, StrokeWidth = Size, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
        };
        switch (Tool)
        {
            case Tool.Pencil:
                using (var path = Polyline(Points))
                    canvas.DrawPath(path, paint);
                break;
            case Tool.Line:
                canvas.DrawLine(Start, End, paint);
                break;
            case Tool.Arrow:
                DrawArrow(canvas, paint);
                break;
            case Tool.Rectangle:
                canvas.DrawRect(Box, paint);
                break;
            case Tool.FilledRect:
                var radius = Math.Min(Size, Math.Min(Box.Width / 2, Box.Height / 2));
                paint.IsStroke = false;
                canvas.DrawRoundRect(Box, radius, radius, paint);
                break;
            case Tool.Ellipse:
                canvas.DrawOval(Box, paint);
                break;
            case Tool.Marker:
                // A semi-transparent stroke with multiply blending keeps the text underneath crisp.
                paint.BlendMode = SKBlendMode.Multiply;
                paint.Color = Color.WithAlpha(102);
                paint.StrokeCap = SKStrokeCap.Butt;
                canvas.DrawLine(Start, End, paint);
                break;
            case Tool.Text:
                paint.IsStroke = false;
                using (var font = TextFont(Size))
                {
                    var lines = Text.Split('\n');
                    for (var i = 0; i < lines.Length; i++)
                        canvas.DrawTextAt(lines[i], new SKPoint(Start.X + 4, Start.Y + 4 + i * font.Spacing), font, paint);
                }
                break;
            case Tool.Pixelate:
                if (Pixelated != null)
                    canvas.DrawImage(Pixelated, Box, new SKSamplingOptions(SKFilterMode.Nearest));
                break;
            case Tool.Counter:
                DrawCounter(canvas, paint);
                break;
            case Tool.Invert:
                // Difference blending with white inverts everything under the area.
                paint.IsStroke = false;
                paint.BlendMode = SKBlendMode.Difference;
                paint.Color = SKColors.White;
                canvas.DrawRect(Box, paint);
                break;
        }
    }

    /// Shaft up to the base of the head plus a triangular head: length is 3 × line width + 8pt, width is 1.2 × length.
    private void DrawArrow(SKCanvas canvas, SKPaint paint)
    {
        var length = SKPoint.Distance(Start, End);
        if (length <= 0)
            return;
        var u = new SKPoint((End.X - Start.X) / length, (End.Y - Start.Y) / length);
        var head = Math.Min(3 * Size + 8, length);
        var spread = head * 0.6f;
        var neck = new SKPoint(End.X - u.X * head, End.Y - u.Y * head);
        canvas.DrawLine(Start, neck, paint);
        paint.IsStroke = false;
        using var tip = Polyline([End, new(neck.X + u.Y * spread, neck.Y - u.X * spread), new(neck.X - u.Y * spread, neck.Y + u.X * spread)], true);
        canvas.DrawPath(tip, paint);
    }

    /// Numbered circle with a white outline; dragging while placing it adds a pointer to the release point.
    private void DrawCounter(SKCanvas canvas, SKPaint paint)
    {
        float r = Size, outline = MathF.Max(1.5f, r / 8);
        paint.IsStroke = false;
        paint.Color = SKColors.White;
        canvas.DrawCircle(Start, r + outline, paint);
        paint.Color = Color;
        if (SKPoint.Distance(Start, End) > r)
        {
            var angle = MathF.Atan2(End.Y - Start.Y, End.X - Start.X);
            const float spread = 0.45f;
            using var pointer = Polyline([
                Start,
                new(Start.X + r * MathF.Cos(angle - spread), Start.Y + r * MathF.Sin(angle - spread)),
                End,
                new(Start.X + r * MathF.Cos(angle + spread), Start.Y + r * MathF.Sin(angle + spread)),
            ], true);
            canvas.DrawPath(pointer, paint);
        }
        canvas.DrawCircle(Start, r, paint);

        var label = $"{Number}";
        using var font = new SKFont(Bold, r * 1.1f);
        while (font.MeasureText(label) > 1.5f * r && font.Size > 2)
            font.Size -= 1;
        paint.Color = Color.IsDark ? SKColors.White : SKColors.Black;
        canvas.DrawCenteredText(label, Start, font, paint);
    }

    private static SKPath Polyline(ReadOnlySpan<SKPoint> points, bool close = false)
    {
        using var builder = new SKPathBuilder();
        builder.AddPoly(points, close);
        return builder.Detach();
    }

    private static SKTypeface Typeface(SKFontStyleWeight weight) =>
        SKTypeface.FromFamilyName("Segoe UI", weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
}

static class Extensions
{
    extension(Tool tool)
    {
        public string Title => tool switch
        {
            Tool.Pencil => "Карандаш",
            Tool.Line => "Линия",
            Tool.Arrow => "Стрелка",
            Tool.Rectangle => "Прямоугольник",
            Tool.FilledRect => "Заливка",
            Tool.Ellipse => "Эллипс",
            Tool.Marker => "Маркер",
            Tool.Text => "Текст",
            Tool.Pixelate => "Пикселизация",
            Tool.Counter => "Нумерация",
            Tool.Invert => "Инверсия",
            _ => throw new ArgumentOutOfRangeException(nameof(tool)),
        };

        public char? Key => tool switch
        {
            Tool.Pencil => 'P',
            Tool.Line => 'D',
            Tool.Arrow => 'A',
            Tool.Rectangle => 'S',
            Tool.FilledRect => 'R',
            Tool.Ellipse => 'C',
            Tool.Marker => 'M',
            Tool.Text => 'T',
            Tool.Pixelate => 'B',
            Tool.Invert => 'I',
            _ => null,
        };

        /// Default size, in the tool's own units (see Annotation).
        public int DefaultSize => tool switch
        {
            Tool.Text => 20,
            Tool.Marker => 18,
            Tool.Counter => 14,
            Tool.Pixelate => 10,
            _ => 4,
        };

        /// With Shift: lines snap to 45° steps, rectangular shapes and ellipses become squares and circles.
        public SKPoint Constrained(SKPoint v)
        {
            switch (tool)
            {
                case Tool.Line or Tool.Arrow or Tool.Marker:
                    var step = MathF.PI / 4;
                    var angle = MathF.Round(MathF.Atan2(v.Y, v.X) / step) * step;
                    return new SKPoint(v.Length * MathF.Cos(angle), v.Length * MathF.Sin(angle));
                case Tool.Rectangle or Tool.FilledRect or Tool.Ellipse or Tool.Pixelate or Tool.Invert:
                    var side = Math.Max(Math.Abs(v.X), Math.Abs(v.Y));
                    return new SKPoint(v.X < 0 ? -side : side, v.Y < 0 ? -side : side);
                default:
                    return v;
            }
        }
    }

    extension(SKColor color)
    {
        /// Whether the color is dark by BT.601 luma; used to pick a contrasting number color.
        public bool IsDark => 0.299 * color.Red + 0.587 * color.Green + 0.114 * color.Blue < 0.6 * 255;
    }

    extension(SKCanvas canvas)
    {
        /// Draws a line of text with its top-left corner at the point.
        public void DrawTextAt(string text, SKPoint topLeft, SKFont font, SKPaint paint) =>
            canvas.DrawText(text, topLeft.X, topLeft.Y - font.Metrics.Ascent, SKTextAlign.Left, font, paint);

        /// Draws text centered on the point by its glyphs, so digits sit in the middle of a circle.
        public void DrawCenteredText(string text, SKPoint center, SKFont font, SKPaint paint)
        {
            font.MeasureText(text, out var bounds);
            canvas.DrawText(text, center.X - bounds.MidX, center.Y - bounds.MidY, SKTextAlign.Left, font, paint);
        }
    }
}
