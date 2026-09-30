namespace EasyShot;

/// Toolbar icons; the tools come first, in the order of Tool.
enum Icon { Pencil, Line, Arrow, Rectangle, FilledRect, Ellipse, Marker, Text, Pixelate, Counter, Invert, Move, Undo, Redo, Copy, Save, Close }

/// Toolbar icons, drawn in code on a 24 × 24 grid: the macOS version uses SF Symbols, which may be used only on Apple platforms.
static class Icons
{
    /// `background` is the color under the icon, for the digit cut out of the numbered circle.
    public static void Draw(Canvas canvas, Icon icon, RectangleF rect, uint color, uint background)
    {
        var state = canvas.Save();
        canvas.Translate(rect.Left, rect.Top);
        canvas.Scale(rect.Width / 24, rect.Height / 24);
        switch (icon)
        {
            case Icon.Move:
                Stroke("M6 3.5V18.5L10 14.8L12.7 20.5L15.1 19.4L12.4 13.8H18Z");
                break;
            case Icon.Pencil:
                Stroke("M4 20L5 15.5L15.5 5A2.475 2.475 0 0 1 19 8.5L8.5 19ZM5 15.5L8.5 19M13.2 7.3L16.7 10.8");
                break;
            case Icon.Line:
                Stroke("M5 19L19 5");
                break;
            case Icon.Arrow:
                Stroke("M6 18L18 6M9 6H18V15");
                break;
            case Icon.Rectangle:
                canvas.StrokeRoundRect(new RectangleF(3.5f, 6, 17, 12), 2, color, 2);
                break;
            case Icon.FilledRect:
                canvas.FillRoundRect(new RectangleF(2.5f, 5, 19, 14), 2.5f, color);
                break;
            case Icon.Ellipse:
                canvas.StrokeEllipse(new RectangleF(3.5f, 3.5f, 17, 17), color, 2);
                break;
            case Icon.Marker:
                // A chisel-tip marker tilted towards the bottom left, over the stripe it leaves.
                Stroke("M3.5 20.5H11");
                canvas.Translate(-1.5f + 12, 1.5f + 12);
                canvas.Rotate(45);
                canvas.Translate(-12, -12);
                Stroke("M8.5 3A2 2 0 0 1 10.5 1H13.5A2 2 0 0 1 15.5 3V12H8.5ZM9.5 12V17L14.5 14.5V12");
                break;
            case Icon.Text:
                Stroke("M5 5.5H19M12 5.5V19.5M9.5 19.5H14.5");
                break;
            case Icon.Pixelate:
                var frame = new RectangleF(3.5f, 5.5f, 17, 13);
                for (var row = 0; row < 3; row++)
                {
                    for (var column = row % 2; column < 4; column += 2)
                        canvas.FillRect(new RectangleF(frame.X + column * frame.Width / 4, frame.Y + row * frame.Height / 3, frame.Width / 4, frame.Height / 3), color);
                }
                canvas.StrokeRoundRect(frame, 2, color, 2);
                break;
            case Icon.Counter:
                // A filled circle with the digit drawn in the color under the icon, as if cut out.
                canvas.FillEllipse(new RectangleF(2.5f, 2.5f, 19, 19), color);
                canvas.StrokePath("M9.8 9L12.6 7V17", background, 2);
                break;
            case Icon.Invert:
                canvas.StrokeEllipse(new RectangleF(3.5f, 3.5f, 17, 17), color, 2);
                canvas.FillPath("M12 3.5A8.5 8.5 0 0 0 12 20.5Z", color);
                break;
            case Icon.Undo:
                Stroke("M9 14L4 9L9 4M4 9H14.5A5.5 5.5 0 0 1 14.5 20H11");
                break;
            case Icon.Redo:
                Stroke("M15 14L20 9L15 4M20 9H9.5A5.5 5.5 0 0 0 9.5 20H13");
                break;
            case Icon.Copy:
                Stroke("M15.5 7.5V5A1.5 1.5 0 0 0 14 3.5H6A1.5 1.5 0 0 0 4.5 5V15A1.5 1.5 0 0 0 6 16.5H8.5");
                canvas.StrokeRoundRect(new RectangleF(8.5f, 7.5f, 11, 13), 1.5f, color, 2);
                break;
            case Icon.Save:
                Stroke("M12 3.5V14.5M7.5 10L12 14.5L16.5 10M4.5 13.5V18.5A2 2 0 0 0 6.5 20.5H17.5A2 2 0 0 0 19.5 18.5V13.5");
                break;
            case Icon.Close:
                Stroke("M6 6L18 18M18 6L6 18");
                break;
        }
        canvas.Restore(state);

        void Stroke(string svg) => canvas.StrokePath(svg, color, 2);
    }
}
