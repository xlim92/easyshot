using System.Globalization;

namespace EasyShot;

/// A 32-bit BGRA image in native memory, rows from top to bottom.
sealed unsafe class Pixels : IDisposable
{
    public readonly int Width, Height;

    public Pixels(int width, int height)
    {
        Width = width;
        Height = height;
        Data = (uint*)NativeMemory.AllocZeroed((nuint)width * (nuint)height, sizeof(uint));
    }

    public uint* Data { get; private set; }

    /// Header of a top-down 32-bit bitmap of this size, for GDI.
    public BITMAPINFO Header => new()
    {
        bmiHeader = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = Width, biHeight = -Height, biPlanes = 1, biBitCount = 32 },
    };

    public void Dispose()
    {
        NativeMemory.Free(Data);
        Data = null;
    }
}

/// Drawing on a Pixels image in points, which the transform maps to pixels. Shapes and text are drawn by GDI+ with antialiasing;
/// copying, inverting, the highlighter's multiplying and the mosaic work on the pixels directly, since GDI+ has no such blending.
sealed unsafe class Canvas : IDisposable
{
    private const int Argb32Premultiplied = 0xE200B, Rgb32 = 0x22009;
    private static readonly Guid PngEncoder = new("557cf406-1a04-11d3-9a73-0000f81ef32e");
    private readonly Pixels target;
    private readonly GpBitmap* bitmap;
    private readonly GpGraphics* graphics;

    public Canvas(Pixels target)
    {
        this.target = target;
        bitmap = Bitmap(target, Argb32Premultiplied);
        GpGraphics* g;
        PInvoke.GdipGetImageGraphicsContext((GpImage*)bitmap, &g);
        graphics = g;
        PInvoke.GdipSetSmoothingMode(g, SmoothingMode.SmoothingModeAntiAlias);
        // Pixel centers at half coordinates, as in CoreGraphics.
        PInvoke.GdipSetPixelOffsetMode(g, PixelOffsetMode.PixelOffsetModeHalf);
        PInvoke.GdipSetTextRenderingHint(g, TextRenderingHint.TextRenderingHintAntiAlias);
    }

    public void Dispose()
    {
        PInvoke.GdipDeleteGraphics(graphics);
        PInvoke.GdipDisposeImage((GpImage*)bitmap);
    }

    // MARK: - State

    public uint Save()
    {
        uint state;
        PInvoke.GdipSaveGraphics(graphics, &state);
        return state;
    }

    public void Restore(uint state) => PInvoke.GdipRestoreGraphics(graphics, state);

    public void Translate(float dx, float dy) => PInvoke.GdipTranslateWorldTransform(graphics, dx, dy, MatrixOrder.MatrixOrderPrepend);

    public void Scale(float sx, float sy) => PInvoke.GdipScaleWorldTransform(graphics, sx, sy, MatrixOrder.MatrixOrderPrepend);

    public void Rotate(float degrees) => PInvoke.GdipRotateWorldTransform(graphics, degrees, MatrixOrder.MatrixOrderPrepend);

    public void ClipRect(RectangleF r) => PInvoke.GdipSetClipRect(graphics, r.X, r.Y, r.Width, r.Height, CombineMode.CombineModeIntersect);

    // MARK: - Shapes and text

    public void Line(Vector2 a, Vector2 b, uint color, float width)
    {
        var pen = Pen(color, width);
        PInvoke.GdipDrawLine(graphics, pen, a.X, a.Y, b.X, b.Y);
        PInvoke.GdipDeletePen(pen);
    }

    public void Lines(ReadOnlySpan<Vector2> points, uint color, float width)
    {
        var pen = Pen(color, width);
        fixed (Vector2* p = points)
            PInvoke.GdipDrawLines(graphics, pen, (PointF*)p, points.Length);
        PInvoke.GdipDeletePen(pen);
    }

    public void StrokeRect(RectangleF r, uint color, float width, ReadOnlySpan<float> dash = default)
    {
        var pen = Pen(color, width, dash);
        PInvoke.GdipDrawRectangle(graphics, pen, r.X, r.Y, r.Width, r.Height);
        PInvoke.GdipDeletePen(pen);
    }

    public void FillRect(RectangleF r, uint color)
    {
        var brush = Brush(color);
        PInvoke.GdipFillRectangle(graphics, brush, r.X, r.Y, r.Width, r.Height);
        PInvoke.GdipDeleteBrush(brush);
    }

    public void StrokeRoundRect(RectangleF r, float radius, uint color, float width, ReadOnlySpan<float> dash = default)
    {
        var path = RoundRect(r, radius);
        var pen = Pen(color, width, dash);
        PInvoke.GdipDrawPath(graphics, pen, path);
        PInvoke.GdipDeletePen(pen);
        PInvoke.GdipDeletePath(path);
    }

    public void FillRoundRect(RectangleF r, float radius, uint color)
    {
        var path = RoundRect(r, radius);
        var brush = Brush(color);
        PInvoke.GdipFillPath(graphics, brush, path);
        PInvoke.GdipDeleteBrush(brush);
        PInvoke.GdipDeletePath(path);
    }

    public void StrokeEllipse(RectangleF r, uint color, float width)
    {
        var pen = Pen(color, width);
        PInvoke.GdipDrawEllipse(graphics, pen, r.X, r.Y, r.Width, r.Height);
        PInvoke.GdipDeletePen(pen);
    }

    public void FillEllipse(RectangleF r, uint color)
    {
        var brush = Brush(color);
        PInvoke.GdipFillEllipse(graphics, brush, r.X, r.Y, r.Width, r.Height);
        PInvoke.GdipDeleteBrush(brush);
    }

    public void FillPolygon(ReadOnlySpan<Vector2> points, uint color)
    {
        var brush = Brush(color);
        fixed (Vector2* p = points)
            PInvoke.GdipFillPolygon(graphics, brush, (PointF*)p, points.Length, FillMode.FillModeWinding);
        PInvoke.GdipDeleteBrush(brush);
    }

    public void StrokePath(string svg, uint color, float width)
    {
        var path = Path(svg, FillMode.FillModeWinding);
        var pen = Pen(color, width);
        PInvoke.GdipDrawPath(graphics, pen, path);
        PInvoke.GdipDeletePen(pen);
        PInvoke.GdipDeletePath(path);
    }

    /// Fills an SVG path; with evenOdd, inner contours cut holes.
    public void FillPath(string svg, uint color, bool evenOdd = false)
    {
        var path = Path(svg, evenOdd ? FillMode.FillModeAlternate : FillMode.FillModeWinding);
        var brush = Brush(color);
        PInvoke.GdipFillPath(graphics, brush, path);
        PInvoke.GdipDeleteBrush(brush);
        PInvoke.GdipDeletePath(path);
    }

    /// Draws text with the top-left corner of its first line at the point; lines are separated by \n.
    public void Text(string text, Vector2 topLeft, Font font, uint color)
    {
        var brush = Brush(color);
        var layout = new RectF { X = topLeft.X, Y = topLeft.Y };
        fixed (char* s = text)
            PInvoke.GdipDrawString(graphics, s, text.Length, font.Handle, &layout, Font.Format, brush);
        PInvoke.GdipDeleteBrush(brush);
    }

    // MARK: - Pixel operations

    /// Copies the pixels of `source` under a rectangle in points: pixel (x, y) gets source pixel (x + dx, y + dy).
    public void Copy(Pixels source, RectangleF area, int dx = 0, int dy = 0)
    {
        var (x0, y0, x1, y1) = PixelBounds(area);
        x0 = Math.Max(x0, -dx);
        y0 = Math.Max(y0, -dy);
        x1 = Math.Min(x1, source.Width - dx);
        y1 = Math.Min(y1, source.Height - dy);
        if (x1 <= x0)
            return;
        for (var y = y0; y < y1; y++)
            new ReadOnlySpan<uint>(source.Data + (y + dy) * source.Width + x0 + dx, x1 - x0).CopyTo(new Span<uint>(target.Data + y * target.Width + x0, x1 - x0));
    }

    /// Inverts the colors under a rectangle, like difference blending with white.
    public void Invert(RectangleF area)
    {
        var (x0, y0, x1, y1) = PixelBounds(area);
        for (var y = y0; y < y1; y++)
        {
            for (uint* p = target.Data + y * target.Width + x0, end = p + (x1 - x0); p < end; p++)
                *p ^= 0x00FFFFFF;
        }
    }

    /// The highlighter: a butt-capped stroke from a to b whose color multiplies the pixels under it, with the given opacity.
    public void Multiply(Vector2 a, Vector2 b, float width, uint color, float opacity)
    {
        var bounds = RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        bounds.Inflate(width / 2 + 1, width / 2 + 1);
        var (x0, y0, x1, y1) = PixelBounds(bounds);
        var points = stackalloc PointF[] { new() { X = a.X, Y = a.Y }, new() { X = b.X, Y = b.Y }, new() { X = a.X + 1, Y = a.Y } };
        PInvoke.GdipTransformPoints(graphics, CoordinateSpace.CoordinateSpaceDevice, CoordinateSpace.CoordinateSpaceWorld, points, 3);
        var start = new Vector2(points[0].X, points[0].Y);
        var axis = new Vector2(points[1].X, points[1].Y) - start;
        var length = axis.Length();
        if (length == 0)
            return;
        var u = axis / length;
        var half = width * Vector2.Distance(new Vector2(points[2].X, points[2].Y), start) / 2;
        float red = (color >> 16 & 0xFF) / 255f, green = (color >> 8 & 0xFF) / 255f, blue = (color & 0xFF) / 255f;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f) - start;
                var along = Vector2.Dot(p, u);
                var across = MathF.Abs(u.X * p.Y - u.Y * p.X);
                // Distance outside the stroke's rectangle, antialiased over one pixel.
                var k = opacity * Math.Clamp(0.5f - MathF.Max(MathF.Max(-along, along - length), across - half), 0, 1);
                if (k <= 0)
                    continue;
                ref var pixel = ref target.Data[y * target.Width + x];
                pixel = (pixel & 0xFF000000) | (uint)((pixel >> 16 & 0xFF) * (1 - k + k * red)) << 16
                        | (uint)((pixel >> 8 & 0xFF) * (1 - k + k * green)) << 8 | (uint)((pixel & 0xFF) * (1 - k + k * blue));
            }
        }
    }

    /// Fills a rectangle with the mosaic's blocks, stretched over it like an image scaled with the nearest neighbor.
    public void Mosaic(RectangleF area, Mosaic mosaic)
    {
        var (fx0, fy0, fx1, fy1) = PixelBounds(area, clipped: false);
        var (x0, y0, x1, y1) = PixelBounds(area);
        if (fx1 <= fx0 || fy1 <= fy0)
            return;
        for (var y = y0; y < y1; y++)
        {
            var row = (y - fy0) * mosaic.Rows / (fy1 - fy0) * mosaic.Columns;
            for (var x = x0; x < x1; x++)
                target.Data[y * target.Width + x] = mosaic.Colors[row + (x - fx0) * mosaic.Columns / (fx1 - fx0)];
        }
    }

    /// Pixel bounds of a rectangle in points; clipped to the image and the clip rectangle unless asked otherwise.
    private (int X0, int Y0, int X1, int Y1) PixelBounds(RectangleF r, bool clipped = true)
    {
        // The pixels are about to be read or written directly: let GDI+ finish its drawing first.
        PInvoke.GdipFlush(graphics, FlushIntention.FlushIntentionSync);
        RectF clip;
        PInvoke.GdipGetClipBounds(graphics, &clip);
        var corners = stackalloc PointF[]
        {
            new() { X = r.Left, Y = r.Top }, new() { X = r.Right, Y = r.Bottom },
            new() { X = clip.X, Y = clip.Y }, new() { X = clip.X + clip.Width, Y = clip.Y + clip.Height },
        };
        PInvoke.GdipTransformPoints(graphics, CoordinateSpace.CoordinateSpaceDevice, CoordinateSpace.CoordinateSpaceWorld, corners, 4);
        float x0 = Math.Min(corners[0].X, corners[1].X), y0 = Math.Min(corners[0].Y, corners[1].Y);
        float x1 = Math.Max(corners[0].X, corners[1].X), y1 = Math.Max(corners[0].Y, corners[1].Y);
        if (clipped)
        {
            x0 = Math.Max(x0, Math.Max(Math.Min(corners[2].X, corners[3].X), 0));
            y0 = Math.Max(y0, Math.Max(Math.Min(corners[2].Y, corners[3].Y), 0));
            x1 = Math.Max(x0, Math.Min(x1, Math.Min(Math.Max(corners[2].X, corners[3].X), target.Width)));
            y1 = Math.Max(y0, Math.Min(y1, Math.Min(Math.Max(corners[2].Y, corners[3].Y), target.Height)));
        }
        return ((int)MathF.Round(x0), (int)MathF.Round(y0), (int)MathF.Round(x1), (int)MathF.Round(y1));
    }

    // MARK: - Files and icons

    /// Saves the image as PNG; false if it couldn't be written.
    public static bool SavePng(Pixels image, string path)
    {
        var bitmap = Bitmap(image, Rgb32);
        var encoder = PngEncoder;
        Status status;
        fixed (char* p = path)
            status = PInvoke.GdipSaveImageToFile((GpImage*)bitmap, p, &encoder, null);
        PInvoke.GdipDisposeImage((GpImage*)bitmap);
        return status == Status.Ok;
    }

    /// An icon of the image, transparency included.
    public static HICON ToIcon(Pixels image)
    {
        var bitmap = Bitmap(image, Argb32Premultiplied);
        HICON icon;
        PInvoke.GdipCreateHICONFromBitmap(bitmap, &icon);
        PInvoke.GdipDisposeImage((GpImage*)bitmap);
        return icon;
    }

    // MARK: - GDI+ objects

    private static GpBitmap* Bitmap(Pixels image, int format)
    {
        GpBitmap* bitmap;
        PInvoke.GdipCreateBitmapFromScan0(image.Width, image.Height, image.Width * 4, format, (byte*)image.Data, &bitmap);
        return bitmap;
    }

    /// A pen with round caps and joins; dash lengths are in points.
    private static GpPen* Pen(uint color, float width, ReadOnlySpan<float> dash = default)
    {
        GpPen* pen;
        PInvoke.GdipCreatePen1(color, width, Unit.UnitWorld, &pen);
        PInvoke.GdipSetPenLineCap197819(pen, LineCap.LineCapRound, LineCap.LineCapRound, DashCap.DashCapFlat);
        PInvoke.GdipSetPenLineJoin(pen, LineJoin.LineJoinRound);
        if (!dash.IsEmpty)
        {
            // GDI+ measures dashes in pen widths.
            var lengths = stackalloc float[dash.Length];
            for (var i = 0; i < dash.Length; i++)
                lengths[i] = dash[i] / width;
            PInvoke.GdipSetPenDashArray(pen, lengths, dash.Length);
        }
        return pen;
    }

    private static GpBrush* Brush(uint color)
    {
        GpSolidFill* brush;
        PInvoke.GdipCreateSolidFill(color, &brush);
        return (GpBrush*)brush;
    }

    private static GpPath* RoundRect(RectangleF r, float radius)
    {
        GpPath* path;
        PInvoke.GdipCreatePath(FillMode.FillModeWinding, &path);
        var d = Math.Min(2 * radius, Math.Min(r.Width, r.Height));
        if (d > 0)
        {
            PInvoke.GdipAddPathArc(path, r.Left, r.Top, d, d, 180, 90);
            PInvoke.GdipAddPathArc(path, r.Right - d, r.Top, d, d, 270, 90);
            PInvoke.GdipAddPathArc(path, r.Right - d, r.Bottom - d, d, d, 0, 90);
            PInvoke.GdipAddPathArc(path, r.Left, r.Bottom - d, d, d, 90, 90);
        }
        else
        {
            PInvoke.GdipAddPathLine(path, r.Left, r.Top, r.Right, r.Top);
            PInvoke.GdipAddPathLine(path, r.Right, r.Bottom, r.Left, r.Bottom);
        }
        PInvoke.GdipClosePathFigure(path);
        return path;
    }

    /// A path from SVG path data with the absolute commands M, L, H, V, A (circular arcs) and Z, all the icons use.
    private static GpPath* Path(string svg, FillMode fill)
    {
        GpPath* path;
        PInvoke.GdipCreatePath(fill, &path);
        var reader = new SvgReader(svg);
        Vector2 position = default, start = default;
        var command = 'M';
        while (reader.Next() is var c and not '\0')
        {
            if (char.IsLetter(c))
            {
                command = c;
                if (c == 'Z')
                {
                    PInvoke.GdipClosePathFigure(path);
                    position = start;
                }
                continue;
            }
            reader.Back();
            Vector2 to;
            switch (command)
            {
                case 'M':
                    PInvoke.GdipStartPathFigure(path);
                    position = start = new Vector2(reader.Number(), reader.Number());
                    // Further coordinate pairs after M are lines.
                    command = 'L';
                    continue;
                case 'H':
                    to = new Vector2(reader.Number(), position.Y);
                    break;
                case 'V':
                    to = new Vector2(position.X, reader.Number());
                    break;
                case 'A':
                    var radius = reader.Number();
                    reader.Number();
                    reader.Number();
                    bool large = reader.Number() != 0, sweep = reader.Number() != 0;
                    to = new Vector2(reader.Number(), reader.Number());
                    AddArc(path, position, to, radius, large, sweep);
                    position = to;
                    continue;
                default:
                    to = new Vector2(reader.Number(), reader.Number());
                    break;
            }
            PInvoke.GdipAddPathLine(path, position.X, position.Y, to.X, to.Y);
            position = to;
        }
        return path;
    }

    /// Adds an SVG arc of a circle from `from` to `to`, converted to the center form that GDI+ takes (SVG spec F.6.5).
    private static void AddArc(GpPath* path, Vector2 from, Vector2 to, float radius, bool large, bool sweep)
    {
        var half = (from - to) / 2;
        var distance = half.Length();
        if (distance == 0)
            return;
        radius = Math.Max(radius, distance);
        var factor = MathF.Sqrt(radius * radius - distance * distance) / distance * (large == sweep ? -1 : 1);
        var center = (from + to) / 2 + factor * new Vector2(half.Y, -half.X);
        var startAngle = MathF.Atan2(from.Y - center.Y, from.X - center.X);
        var sweepAngle = MathF.Atan2(to.Y - center.Y, to.X - center.X) - startAngle;
        if (sweep && sweepAngle < 0)
            sweepAngle += 2 * MathF.PI;
        else if (!sweep && sweepAngle > 0)
            sweepAngle -= 2 * MathF.PI;
        const float degrees = 180 / MathF.PI;
        PInvoke.GdipAddPathArc(path, center.X - radius, center.Y - radius, 2 * radius, 2 * radius, startAngle * degrees, sweepAngle * degrees);
    }

    /// Reads SVG path data: command letters and numbers separated by spaces or commas.
    private struct SvgReader(string svg)
    {
        private int index;

        public char Next()
        {
            while (index < svg.Length && svg[index] is ' ' or ',')
                index++;
            return index < svg.Length ? svg[index++] : '\0';
        }

        public void Back() => index--;

        public float Number()
        {
            Next();
            var begin = --index;
            if (svg[index] is '-' or '+')
                index++;
            while (index < svg.Length && (char.IsDigit(svg[index]) || svg[index] == '.'))
                index++;
            return float.Parse(svg.AsSpan(begin, index - begin), CultureInfo.InvariantCulture);
        }
    }
}

/// A GDI+ font whose size is in points of the canvas; fonts are kept for reuse.
sealed unsafe class Font
{
    private static readonly Dictionary<(float Size, bool Bold), Font> Cache = [];
    private static readonly GpGraphics* Measuring = CreateMeasuring();
    public static readonly GpStringFormat* Format = CreateFormat();

    public readonly GpFont* Handle;
    public readonly float LineHeight;

    private Font(float size, bool bold)
    {
        // Semibold for text, close to the medium system font of macOS; bold for numbers.
        var family = Family(bold ? "Segoe UI" : "Segoe UI Semibold");
        if (family == null)
            family = Family("Segoe UI");
        GpFont* font;
        PInvoke.GdipCreateFont(family, size, (int)(bold ? FontStyle.FontStyleBold : FontStyle.FontStyleRegular), Unit.UnitPixel, &font);
        PInvoke.GdipDeleteFontFamily(family);
        Handle = font;
        float height;
        PInvoke.GdipGetFontHeight(font, Measuring, &height);
        LineHeight = height;
    }

    public static Font Get(float size, bool bold = false)
    {
        if (!Cache.TryGetValue((size, bold), out var font))
            Cache[(size, bold)] = font = new Font(size, bold);
        return font;
    }

    /// Width of the widest line and height of all lines, in points.
    public Vector2 Measure(string text)
    {
        var lines = text.Split('\n');
        var width = 0f;
        foreach (var line in lines)
            width = Math.Max(width, Width(line));
        return new Vector2(width, lines.Length * LineHeight);
    }

    /// Width of one line of text, in points.
    public float Width(string line)
    {
        if (line.Length == 0)
            return 0;
        var layout = new RectF();
        RectF box;
        fixed (char* s = line)
            PInvoke.GdipMeasureString(Measuring, s, line.Length, Handle, &layout, Format, &box, null, null);
        return box.Width;
    }

    private static GpFontFamily* Family(string name)
    {
        GpFontFamily* family;
        fixed (char* n = name)
            return PInvoke.GdipCreateFontFamilyFromName(n, null, &family) == Status.Ok ? family : null;
    }

    /// Text is measured on a graphics of its own, without the scale of a canvas.
    private static GpGraphics* CreateMeasuring()
    {
        GpBitmap* bitmap;
        PInvoke.GdipCreateBitmapFromScan0(1, 1, 0, 0xE200B, null, &bitmap);
        GpGraphics* graphics;
        PInvoke.GdipGetImageGraphicsContext((GpImage*)bitmap, &graphics);
        PInvoke.GdipSetTextRenderingHint(graphics, TextRenderingHint.TextRenderingHintAntiAlias);
        return graphics;
    }

    private static GpStringFormat* CreateFormat()
    {
        GpStringFormat* generic, format;
        PInvoke.GdipStringFormatGetGenericTypographic(&generic);
        PInvoke.GdipCloneStringFormat(generic, &format);
        // Typographic layout without the padding GDI+ adds by default; trailing spaces count, for the caret.
        PInvoke.GdipSetStringFormatFlags(format, (int)(StringFormatFlags.StringFormatFlagsMeasureTrailingSpaces
                                                       | StringFormatFlags.StringFormatFlagsNoWrap | StringFormatFlags.StringFormatFlagsNoClip));
        return format;
    }
}
