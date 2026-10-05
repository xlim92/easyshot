namespace EasyShot;

/// Drawing tools; the letter is the keyboard shortcut.
enum Tool { Pencil, Line, Arrow, Rectangle, FilledRect, Ellipse, Marker, Text, Pixelate, Counter, Invert }

/// A drawn object. Coordinates are in screen points (1/96 inch) with the y axis pointing down.
/// Size is in the tool's units: line width, font size, radius of the numbered circle, mosaic block size,
/// corner radius of the filled rectangle. Points: the whole path for the pencil, the anchor point for text, [start, end] for the rest.
sealed record Annotation(Tool Tool, uint Color, int Size, Vector2[] Points)
{
    public string Text { get; init; } = "";
    public int Number { get; init; }
    /// Rendered mosaic; the caller recomputes it whenever the geometry changes.
    public Mosaic? Pixelated { get; init; }

    public Vector2 Start => Points[0];
    public Vector2 End => Points[^1];
    public RectangleF Box => RectangleF.FromLTRB(MathF.Min(Start.X, End.X), MathF.Min(Start.Y, End.Y), MathF.Max(Start.X, End.X), MathF.Max(Start.Y, End.Y));
    public Font Font => Font.Get(Size);

    /// Empty objects (a click without dragging, empty text) are not added to the history.
    public bool IsValid => Tool switch
    {
        Tool.Text => Text.Length > 0,
        Tool.Counter => true,
        _ => Points.Length > 1 && Start != End,
    };

    /// Text frame with 4pt padding.
    public RectangleF TextRect
    {
        get
        {
            var size = Font.Measure(Text);
            return new RectangleF(Start.X, Start.Y, MathF.Ceiling(size.X) + 8, MathF.Ceiling(size.Y) + 8);
        }
    }

    /// Object area used for the selection frame.
    public RectangleF Bounds
    {
        get
        {
            switch (Tool)
            {
                case Tool.Text:
                    return TextRect;
                case Tool.Counter:
                    var r = Size + 2f;
                    return RectangleF.FromLTRB(MathF.Min(Start.X - r, End.X), MathF.Min(Start.Y - r, End.Y), MathF.Max(Start.X + r, End.X), MathF.Max(Start.Y + r, End.Y));
                case Tool.FilledRect or Tool.Pixelate or Tool.Invert:
                    return Box;
                default:
                    var pad = Size / 2f + 3;
                    float left = Start.X, top = Start.Y, right = Start.X, bottom = Start.Y;
                    foreach (var p in Points)
                    {
                        left = MathF.Min(left, p.X);
                        top = MathF.Min(top, p.Y);
                        right = MathF.Max(right, p.X);
                        bottom = MathF.Max(bottom, p.Y);
                    }
                    return RectangleF.FromLTRB(left - pad, top - pad, right + pad, bottom + pad);
            }
        }
    }

    /// Hit testing: lines and outlines are hit along the stroke with 4pt tolerance; fills, text and circles anywhere inside.
    public bool Contains(Vector2 p)
    {
        var reach = (Size + 8) / 2f;
        switch (Tool)
        {
            case Tool.Text or Tool.Counter or Tool.FilledRect or Tool.Pixelate or Tool.Invert:
                var bounds = Bounds;
                bounds.Inflate(4, 4);
                return bounds.Contains(p.X, p.Y);
            case Tool.Rectangle:
                var box = Box;
                Vector2 a = new(box.Left, box.Top), b = new(box.Right, box.Top), c = new(box.Right, box.Bottom), d = new(box.Left, box.Bottom);
                return MathF.Min(MathF.Min(Distance(p, a, b), Distance(p, b, c)), MathF.Min(Distance(p, c, d), Distance(p, d, a))) <= reach;
            case Tool.Ellipse:
                // Distance to the outline, estimated from the ellipse equation and its gradient.
                var e = Box;
                float rx = e.Width / 2, ry = e.Height / 2;
                if (rx == 0 || ry == 0)
                    return Distance(p, Start, End) <= reach;
                var q = p - new Vector2(e.X + rx, e.Y + ry);
                var f = q.X * q.X / (rx * rx) + q.Y * q.Y / (ry * ry) - 1;
                var gradient = 2 * MathF.Sqrt(q.X * q.X / (rx * rx * rx * rx) + q.Y * q.Y / (ry * ry * ry * ry));
                return gradient > 0 ? MathF.Abs(f) / gradient <= reach : MathF.Min(rx, ry) <= reach;
            default:
                for (var i = 1; i < Points.Length; i++)
                {
                    if (Distance(p, Points[i - 1], Points[i]) <= reach)
                        return true;
                }
                return false;
        }
    }

    public Annotation Moved(Vector2 offset)
    {
        var points = new Vector2[Points.Length];
        for (var i = 0; i < points.Length; i++)
            points[i] = Points[i] + offset;
        return this with { Points = points };
    }

    public void Draw(Canvas canvas)
    {
        switch (Tool)
        {
            case Tool.Pencil:
                canvas.Lines(Points, Color, Size);
                break;
            case Tool.Line:
                canvas.Line(Start, End, Color, Size);
                break;
            case Tool.Arrow:
                DrawArrow(canvas);
                break;
            case Tool.Rectangle:
                canvas.StrokeRect(Box, Color, Size);
                break;
            case Tool.FilledRect:
                var box = Box;
                canvas.FillRoundRect(box, MathF.Min(Size, MathF.Min(box.Width, box.Height) / 2), Color);
                break;
            case Tool.Ellipse:
                canvas.StrokeEllipse(Box, Color, Size);
                break;
            case Tool.Marker:
                // A semi-transparent stroke with multiply blending keeps the text underneath crisp.
                canvas.Multiply(Start, End, Size, Color, 0.4f);
                break;
            case Tool.Text:
                canvas.Text(Text, Start + new Vector2(4), Font, Color);
                break;
            case Tool.Pixelate:
                if (Pixelated != null)
                    canvas.Mosaic(Box, Pixelated);
                break;
            case Tool.Counter:
                DrawCounter(canvas);
                break;
            case Tool.Invert:
                // Everything under the area is inverted, like difference blending with white.
                canvas.Invert(Box);
                break;
        }
    }

    /// Shaft up to the base of the head plus a triangular head: length is 3 × line width + 8pt, width is 1.2 × length.
    private void DrawArrow(Canvas canvas)
    {
        var length = Vector2.Distance(Start, End);
        if (length <= 0)
            return;
        var u = (End - Start) / length;
        var head = MathF.Min(3 * Size + 8, length);
        var spread = head * 0.6f;
        var neck = End - u * head;
        canvas.Line(Start, neck, Color, Size);
        canvas.FillPolygon([End, new(neck.X + u.Y * spread, neck.Y - u.X * spread), new(neck.X - u.Y * spread, neck.Y + u.X * spread)], Color);
    }

    /// Numbered circle with a white outline; dragging while placing it adds a pointer to the release point.
    private void DrawCounter(Canvas canvas)
    {
        float r = Size, outline = MathF.Max(1.5f, r / 8);
        canvas.FillEllipse(Circle(Start, r + outline), 0xFFFFFFFF);
        if (Vector2.Distance(Start, End) > r)
        {
            var angle = MathF.Atan2(End.Y - Start.Y, End.X - Start.X);
            const float spread = 0.45f;
            canvas.FillPolygon([Start, Start + r * Direction(angle - spread), End, Start + r * Direction(angle + spread)], Color);
        }
        canvas.FillEllipse(Circle(Start, r), Color);

        var label = $"{Number}";
        var fontSize = r * 1.1f;
        while (Font.Get(fontSize, bold: true).Width(label) > 1.5f * r && fontSize > 2)
            fontSize -= 1;
        var font = Font.Get(fontSize, bold: true);
        canvas.Text(label, Start - new Vector2(font.Width(label), font.LineHeight) / 2, font, IsDark(Color) ? 0xFFFFFFFF : 0xFF000000);
    }

    public static RectangleF Circle(Vector2 center, float radius) => new(center.X - radius, center.Y - radius, 2 * radius, 2 * radius);

    /// Whether the color is dark by BT.601 luma; used to pick a contrasting number color.
    public static bool IsDark(uint color) => 0.299f * (color >> 16 & 0xFF) + 0.587f * (color >> 8 & 0xFF) + 0.114f * (color & 0xFF) < 0.6f * 255;

    private static Vector2 Direction(float angle) => new(MathF.Cos(angle), MathF.Sin(angle));

    /// Distance from p to the segment ab.
    private static float Distance(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var t = ab == Vector2.Zero ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
        return Vector2.Distance(p, a + t * ab);
    }
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
        public Vector2 Constrained(Vector2 v)
        {
            switch (tool)
            {
                case Tool.Line or Tool.Arrow or Tool.Marker:
                    var step = MathF.PI / 4;
                    var angle = MathF.Round(MathF.Atan2(v.Y, v.X) / step) * step;
                    return v.Length() * new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                case Tool.Rectangle or Tool.FilledRect or Tool.Ellipse or Tool.Pixelate or Tool.Invert:
                    var side = MathF.Max(MathF.Abs(v.X), MathF.Abs(v.Y));
                    return new Vector2(v.X < 0 ? -side : side, v.Y < 0 ? -side : side);
                default:
                    return v;
            }
        }
    }
}
