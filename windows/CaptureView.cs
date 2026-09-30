using System.Runtime.InteropServices;
using SkiaSharp;

namespace EasyShot;

/// Capture screen: dimmed screenshot, area selection with a resizable frame, toolbars next to the frame
/// and drawing on top of the screenshot: tools, Shift constraints, mouse wheel sizing, color ring, object editing.
/// Everything, toolbars included, is drawn with SkiaSharp in points (1/96 inch) at the display's scale.
sealed partial class CaptureView : Control
{
    private abstract record Drag;
    private sealed record Selecting(SKPoint Start) : Drag;
    private sealed record MovingSelection(SKPoint Start, SKRect Original) : Drag;
    private sealed record Resizing(int Dx, int Dy, SKRect Original) : Drag;
    private sealed record Drawing : Drag;
    private sealed record MovingObject(SKPoint Start, List<Annotation> Before) : Drag;
    private sealed record Pressing(BarButton Button) : Drag;

    /// Toolbar button; its icon is drawn in the accent color while Active returns true.
    private sealed record BarButton(string Icon, string Tip, Action Run, Func<bool>? Active = null);

    /// Text being typed right on the screenshot. Text taken for editing starts fully selected, so typing replaces it.
    private sealed class TextEditor(SKPoint origin, string text, SKColor color)
    {
        public readonly SKPoint Origin = origin;
        public string Text = text;
        public int Caret = text.Length;
        public bool AllSelected = text.Length > 0;
        public SKColor Color = color;
    }

    private static readonly (int Dx, int Dy)[] Handles = [(-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1)];
    private static readonly SKColor[] Palette =
    [
        .. new uint[] { 0xFF3B30, 0xFF9500, 0xFFCC00, 0x34C759, 0x00C7BE, 0x007AFF, 0xAF52DE, 0xFF2D55, 0x000000, 0xFFFFFF }
            .Select(rgb => new SKColor(0xFF000000 | rgb)),
    ];
    /// Palette swatches sit on a ring with a gap between neighbors.
    private static readonly float RingRadius = Math.Max(44, Palette.Length * 34 / (2 * MathF.PI));
    private const int MinSize = 1, MaxSize = 72;
    private const int UndoLimit = 200;
    private const float ButtonSize = 30, BarInset = 3;
    private static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest);
    private static readonly SKPathEffect FrameDash = SKPathEffect.CreateDash([4, 4], 0), ObjectDash = SKPathEffect.CreateDash([5, 3], 0);
    private static readonly SKColor Gray = new(85, 85, 85);
    /// Color and tool sizes are remembered between screenshots while the app is running.
    private static SKColor color = Palette[0];
    private static readonly Dictionary<Tool, int> Sizes = [];

    private readonly SKImage screenshot;
    /// The screenshot with the dimming baked in, shown outside the selection.
    private readonly SKImage dimmed;
    private readonly Action onClose;
    private readonly BarButton[] toolsBar, actionsBar;
    private readonly ToolTip tip = new();
    private SKBitmap? frame;

    private SKRect? selection;
    private Drag? drag;
    private List<Annotation> annotations = [];
    private readonly List<List<Annotation>> undoStack = [], redoStack = [];
    private Annotation? current;
    private int? selected;
    private int? resized;
    private Tool? tool;
    private int counter = 1;
    private TextEditor? editor;
    private int? editing;
    private int editorSize = 8;
    private (SKPoint Center, int? Hovered)? picker;
    private SKPoint? mouse;
    private BarButton? hovered;
    private int wheel;
    private DateTime sizeHintUntil;

    public CaptureView(SKImage screenshot, Action onClose)
    {
        this.screenshot = screenshot;
        this.onClose = onClose;
        using (var surface = SKSurface.Create(screenshot.Info))
        {
            using var shade = new SKPaint { Color = new SKColor(0, 0, 0, 115) };
            surface.Canvas.DrawImage(screenshot, 0, 0, Nearest);
            surface.Canvas.DrawRect(SKRect.Create(screenshot.Width, screenshot.Height), shade);
            dimmed = surface.Snapshot();
        }
        // The view paints every pixel itself, with no background erase in between.
        SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        Cursor = Cursors.Cross;

        toolsBar =
        [
            new("Move", "Выбор и перемещение объектов (V, Esc)", SelectMoveMode, () => tool == null),
            .. Enum.GetValues<Tool>().Select(t => new BarButton(t.ToString(), t.Title + (t.Key is { } key ? $" ({key})" : ""), () => SelectTool(t), () => tool == t)),
            new("Color", "Цвет (или правая кнопка мыши)", PickColor),
            new("Undo", "Отменить (Ctrl+Z)", Undo),
            new("Redo", "Повторить (Ctrl+Shift+Z)", Redo),
        ];
        actionsBar =
        [
            new("Copy", "Копировать (Ctrl+C, Enter)", CopyImage),
            new("Save", "Сохранить (Ctrl+S)", SaveImage),
            new("Close", "Закрыть (Esc)", onClose),
        ];
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            screenshot.Dispose();
            dimmed.Dispose();
            frame?.Dispose();
            tip.Dispose();
        }
        base.Dispose(disposing);
    }

    /// Pixels per point on this display.
    private float PixelScale => DeviceDpi / 96f;

    /// The view's bounds in points.
    private SKRect Area => SKRect.Create(ClientSize.Width / PixelScale, ClientSize.Height / PixelScale);

    private static SKColor Accent => new(SystemColors.Highlight.R, SystemColors.Highlight.G, SystemColors.Highlight.B);

    // MARK: - Drawing

    protected override void OnPaint(PaintEventArgs e)
    {
        if (frame?.Width != ClientSize.Width || frame.Height != ClientSize.Height)
        {
            frame?.Dispose();
            frame = new SKBitmap(new SKImageInfo(ClientSize.Width, ClientSize.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        }
        using (var canvas = new SKCanvas(frame))
        {
            var clip = e.ClipRectangle;
            canvas.ClipRect(SKRect.Create(clip.X, clip.Y, clip.Width, clip.Height));
            canvas.Scale(PixelScale);
            Draw(canvas);
        }
        // Copy the frame to the window as a top-down 32-bit bitmap.
        var header = new BitmapInfoHeader { Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = frame.Width, Height = -frame.Height, Planes = 1, BitCount = 32 };
        var hdc = e.Graphics.GetHdc();
        try
        {
            StretchDIBits(hdc, 0, 0, frame.Width, frame.Height, 0, 0, frame.Width, frame.Height, frame.GetPixels(), header, 0, 0x00CC0020);
        }
        finally
        {
            e.Graphics.ReleaseHdc(hdc);
        }
    }

    private void Draw(SKCanvas canvas)
    {
        canvas.DrawImage(dimmed, Area, Nearest);
        if (selection is not { } sel)
            return;

        canvas.Save();
        canvas.ClipRect(sel);
        canvas.DrawImage(screenshot, Area, Nearest);
        for (var i = 0; i < annotations.Count; i++)
        {
            if (i != editing)
                annotations[i].Draw(canvas);
        }
        current?.Draw(canvas);
        canvas.Restore();

        DrawFrame(canvas, sel);
        if (selected is { } s)
        {
            // Selected object: an accent-colored dashed frame over a white underlay.
            var box = SKRect.Inflate(annotations[s].Bounds, 3, 3);
            using var paint = new SKPaint { IsAntialias = true, IsStroke = true, StrokeWidth = 2, Color = SKColors.White };
            canvas.DrawRoundRect(box, 3, 3, paint);
            paint.PathEffect = ObjectDash;
            paint.Color = Accent;
            canvas.DrawRoundRect(box, 3, 3, paint);
        }
        DrawMousePreview(canvas);
        DrawSizeHint(canvas);
        DrawPicker(canvas);
        DrawBars(canvas);
        DrawEditor(canvas);
    }

    private void DrawFrame(SKCanvas canvas, SKRect sel)
    {
        // A whole number of pixels wide, so the frame stays crisp at any display scale.
        var line = MathF.Max(1, MathF.Round(PixelScale)) / PixelScale;
        var border = SKRect.Inflate(sel, line / 2, line / 2);
        using var paint = new SKPaint { IsStroke = true, StrokeWidth = line, Color = SKColors.Black };
        canvas.DrawRect(border, paint);
        paint.PathEffect = FrameDash;
        paint.Color = SKColors.White;
        canvas.DrawRect(border, paint);
        paint.PathEffect = null;
        foreach (var (dx, dy) in Handles)
        {
            var p = HandlePoint(dx, dy, sel);
            var handle = SKRect.Create(p.X - 3, p.Y - 3, 6, 6);
            paint.IsStroke = false;
            paint.Color = SKColors.White;
            canvas.DrawRect(handle, paint);
            paint.IsStroke = true;
            paint.Color = SKColors.Black;
            canvas.DrawRect(handle, paint);
        }

        var label = $"{MathF.Round(sel.Width * PixelScale)} × {MathF.Round(sel.Height * PixelScale)}";
        using var font = new SKFont(Annotation.Medium, 11);
        var size = new SKSize(font.MeasureText(label), font.Spacing);
        var box = SKRect.Create(sel.Left, sel.Top - size.Height - 8, size.Width + 8, size.Height + 4);
        if (box.Top < 0)
            box.Location = new SKPoint(sel.Left + 4, sel.Top + 4);
        paint.IsStroke = false;
        paint.IsAntialias = true;
        paint.Color = new SKColor(0, 0, 0, 179);
        canvas.DrawRoundRect(box, 3, 3, paint);
        paint.Color = SKColors.White;
        canvas.DrawTextAt(label, new SKPoint(box.Left + 4, box.Top + 2), font, paint);
    }

    /// Brush preview under the cursor: tool color and width; for numbering, a circle with the next number.
    private void DrawMousePreview(SKCanvas canvas)
    {
        if (tool is not { } t || mouse is not { } m || drag != null || picker != null || editor != null || selection?.Contains(m) != true)
            return;
        float size = SizeOf(t);
        var diameter = t switch
        {
            Tool.Text or Tool.Pixelate or Tool.Invert or Tool.FilledRect => 0,
            Tool.Counter => 2 * size,
            _ => Math.Max(size, 3),
        };
        if (diameter <= 0)
            return;
        using var paint = new SKPaint { IsAntialias = true, Color = t is Tool.Marker or Tool.Counter ? color.WithAlpha(102) : color };
        canvas.DrawCircle(m, diameter / 2, paint);
        if (t == Tool.Counter)
        {
            using var font = new SKFont(Annotation.Bold, size);
            paint.Color = SKColors.White;
            canvas.DrawCenteredText($"{counter}", m, font, paint);
        }
    }

    /// After scrolling the wheel, the current size is briefly shown next to the cursor.
    private void DrawSizeHint(SKCanvas canvas)
    {
        if (DateTime.Now >= sizeHintUntil || mouse is not { } m)
            return;
        int? value = editor != null ? editorSize : selected is { } i ? annotations[i].Size : tool is { } t ? SizeOf(t) : null;
        if (value == null)
            return;
        var label = $"{value}";
        using var font = new SKFont(Annotation.Bold, 13);
        var size = new SKSize(font.MeasureText(label), font.Spacing);
        var badge = SKRect.Create(m.X + 16, m.Y + 16, size.Width + 14, size.Height + 6);
        using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(38, 38, 38, 191) };
        canvas.DrawRoundRect(badge, badge.Height / 2, badge.Height / 2, paint);
        paint.Color = SKColors.White;
        canvas.DrawTextAt(label, new SKPoint(badge.MidX - size.Width / 2, badge.MidY - size.Height / 2), font, paint);
    }

    /// Color ring around the point where it was opened; the highlighted color is enlarged and outlined in white.
    private void DrawPicker(SKCanvas canvas)
    {
        if (picker is not { } open)
            return;
        using var paint = new SKPaint { IsAntialias = true };
        for (var i = 0; i < Palette.Length; i++)
        {
            var isHovered = i == open.Hovered;
            var swatch = SKRect.Inflate(SwatchRect(i, open.Center), isHovered ? 3 : 0, isHovered ? 3 : 0);
            paint.IsStroke = false;
            paint.Color = Palette[i];
            canvas.DrawOval(swatch, paint);
            paint.IsStroke = true;
            paint.StrokeWidth = isHovered ? 3 : 1;
            paint.Color = isHovered ? SKColors.White : new SKColor(0, 0, 0, 128);
            canvas.DrawOval(swatch, paint);
        }
    }

    /// Frame of swatch i, going clockwise from the top of the ring.
    private static SKRect SwatchRect(int i, SKPoint center)
    {
        var angle = 2 * MathF.PI * i / Palette.Length - MathF.PI / 2;
        const float r = 13;
        return SKRect.Create(center.X + RingRadius * MathF.Cos(angle) - r, center.Y + RingRadius * MathF.Sin(angle) - r, 2 * r, 2 * r);
    }

    private static int? SwatchAt(SKPoint center, SKPoint p)
    {
        for (var i = 0; i < Palette.Length; i++)
        {
            if (SwatchRect(i, center).Contains(p))
                return i;
        }
        return null;
    }

    /// The text being typed with its caret, or with a highlight while all of it is selected.
    private void DrawEditor(SKCanvas canvas)
    {
        if (editor is not { } e)
            return;
        using var font = Annotation.TextFont(editorSize);
        using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(0xB3, 0xD7, 0xFF) };
        var lines = e.Text.Split('\n');
        if (e.AllSelected)
        {
            for (var i = 0; i < lines.Length; i++)
                canvas.DrawRect(SKRect.Create(e.Origin.X + 4, e.Origin.Y + 4 + i * font.Spacing, font.MeasureText(lines[i]), font.Spacing), paint);
        }
        new Annotation(Tool.Text, e.Color, editorSize, [e.Origin]) { Text = e.Text }.Draw(canvas);
        if (!e.AllSelected)
        {
            var before = e.Text[..e.Caret];
            var x = e.Origin.X + 4 + font.MeasureText(before[(before.LastIndexOf('\n') + 1)..]);
            var y = e.Origin.Y + 4 + before.Count(c => c == '\n') * font.Spacing;
            paint.Color = e.Color;
            paint.StrokeWidth = 1;
            canvas.DrawLine(x, y, x, y + font.Spacing, paint);
        }
    }

    // MARK: - Mouse

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        var p = PointOf(e);
        mouse = p;
        HideTip();
        if (e.Button == MouseButtons.Left)
            LeftMouseDown(p, e.Clicks);
        else if (e.Button == MouseButtons.Right)
            RightMouseDown(p);
        UpdateCursor();
        Invalidate();
    }

    private void LeftMouseDown(SKPoint p, int clicks)
    {
        if (InBars(p))
        {
            // A click on a button presses it; the toolbar background ignores clicks.
            if (ButtonAt(p) is { } button)
                drag = new Pressing(button);
            return;
        }
        if (picker is { } open)
        {
            // A left click while the ring is open picks the highlighted color.
            if (open.Hovered is { } i)
                Apply(Palette[i]);
            picker = null;
            return;
        }
        if (editor != null)
        {
            // The first click only finishes text editing.
            CommitText();
            return;
        }
        if (clicks == 2 && tool == null && selected is { } s && annotations[s].Tool == Tool.Text && annotations[s].Contains(p))
        {
            BeginText(annotations[s].Start, s);
            return;
        }
        resized = null;
        if (selection is not { } sel)
        {
            drag = new Selecting(p);
            return;
        }
        var handle = Array.FindIndex(Handles, h => SKPoint.Distance(HandlePoint(h.Dx, h.Dy, sel), p) <= 6);
        if (handle >= 0)
        {
            selected = null;
            drag = new Resizing(Handles[handle].Dx, Handles[handle].Dy, sel);
        }
        else if (sel.Contains(p))
        {
            if (tool is { } t)
            {
                // With a tool active, a click always starts a new object.
                StartDrawing(t, p);
            }
            else if ((selected is { } o && annotations[o].Bounds.Contains(p) ? o : Hit(p)) is { } i)
            {
                SelectObject(i);
                drag = new MovingObject(p, [.. annotations]);
            }
            else
            {
                selected = null;
                // Move the frame only while nothing is drawn; otherwise drawings would drift away from what they point at.
                if (annotations.Count == 0)
                    drag = new MovingSelection(p, sel);
            }
        }
        else if (annotations.Count == 0)
        {
            // Start a new selection only while nothing is drawn, so a stray click can't lose the drawing.
            selection = null;
            drag = new Selecting(p);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = PointOf(e);
        mouse = p;
        if (e.Button == MouseButtons.Left && drag != null)
            MouseDragged(p);
        else
            MouseMoved(p);
    }

    private void MouseDragged(SKPoint p)
    {
        switch (drag)
        {
            case Selecting(var start):
                selection = Rect(start, p);
                break;
            case MovingSelection(var start, var original):
            {
                var r = original;
                r.Offset(Snap(p.X - start.X), Snap(p.Y - start.Y));
                r.Location = new SKPoint(Math.Min(Math.Max(r.Left, 0), Area.Right - r.Width), Math.Min(Math.Max(r.Top, 0), Area.Bottom - r.Height));
                selection = r;
                break;
            }
            case Resizing(var dx, var dy, var r):
            {
                SKPoint a = new(r.Left, r.Top), b = new(r.Right, r.Bottom);
                if (dx < 0)
                    a.X = p.X;
                else if (dx > 0)
                    b.X = p.X;
                if (dy < 0)
                    a.Y = p.Y;
                else if (dy > 0)
                    b.Y = p.Y;
                selection = Rect(a, b);
                break;
            }
            case Drawing when current is { } a:
                if (a.Tool == Tool.Pencil)
                {
                    current = a with { Points = [.. a.Points, p] };
                }
                else
                {
                    var v = p - a.Start;
                    var d = ModifierKeys.HasFlag(Keys.Shift) ? a.Tool.Constrained(v) : v;
                    current = Refreshed(a with { Points = [a.Start, a.Start + d] });
                }
                break;
            case MovingObject(var start, var before) when selected is { } i:
                annotations[i] = Refreshed(before[i].Moved(p - start));
                break;
            default:
                return;
        }
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        var p = PointOf(e);
        if (e.Button == MouseButtons.Right)
        {
            RightMouseUp(p);
            return;
        }
        var finished = drag;
        drag = null;
        switch (finished)
        {
            case Pressing(var button) when ButtonAt(p) == button:
                button.Run();
                break;
            case Drawing:
                if (current is { IsValid: true } a)
                {
                    Record(() => annotations.Add(a));
                    if (a.Tool == Tool.Counter)
                        counter = Math.Min(counter + 1, 999);
                }
                current = null;
                break;
            case MovingObject(var start, var before) when p != start:
                Push(before);
                break;
            case Selecting or Resizing:
                if (selection is { } sel && (sel.Width < 2 || sel.Height < 2))
                    selection = null;
                break;
        }
        UpdateCursor();
        Invalidate();
    }

    private void MouseMoved(SKPoint p)
    {
        UpdateCursor();
        if (picker is { } open && SwatchAt(open.Center, p) is { } i)
            picker = open with { Hovered = i };
        UpdateTip(p);
        if (tool != null || picker != null)
            Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        mouse = null;
        HideTip();
        Invalidate();
    }

    /// Right click opens the color ring; with no tool active it first selects the object under the cursor so it can be recolored.
    private void RightMouseDown(SKPoint p)
    {
        if (editor != null)
            return;
        if (tool == null && Hit(p) is { } i)
            SelectObject(i);
        picker = (p, IndexOf(color));
    }

    /// Besides clicking a color, you can press the right button, drag to a color and release.
    private void RightMouseUp(SKPoint p)
    {
        if (picker is { } open && SwatchAt(open.Center, p) is { } i)
        {
            Apply(Palette[i]);
            picker = null;
        }
        Invalidate();
    }

    /// The mouse wheel changes the size of the current tool, the selected object or the text being typed, within 1...72;
    /// Shift + wheel with the numbering tool changes the next number.
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        // Touchpads send a stream of small deltas: accumulate them, one size step per wheel notch.
        wheel += e.Delta;
        var step = wheel / SystemInformation.MouseWheelScrollDelta;
        wheel -= step * SystemInformation.MouseWheelScrollDelta;
        if (step == 0)
            return;

        if (tool == Tool.Counter && ModifierKeys.HasFlag(Keys.Shift))
        {
            counter = Math.Clamp(counter + step, 1, 999);
        }
        else if (editor != null)
        {
            editorSize = Clamp(editorSize + step);
        }
        else if (selected is { } i)
        {
            if (resized != i)
            {
                Push([.. annotations]);
                resized = i;
            }
            annotations[i] = Refreshed(annotations[i] with { Size = Clamp(annotations[i].Size + step) });
            Sizes[annotations[i].Tool] = annotations[i].Size;
        }
        else if (tool is { } t)
        {
            Sizes[t] = Clamp(SizeOf(t) + step);
        }
        else
        {
            return;
        }
        sizeHintUntil = DateTime.Now.AddSeconds(0.8);
        HideSizeHintLater();
        Invalidate();
    }

    private async void HideSizeHintLater()
    {
        await Task.Delay(850);
        Invalidate();
    }

    // MARK: - Keyboard

    /// Arrows, Tab, Enter and Esc come to OnKeyDown instead of moving the focus.
    protected override bool IsInputKey(Keys keyData) => true;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        HideTip();
        // AltGr is reported as Ctrl+Alt and types characters, so it isn't a command.
        if (e.Control && !e.Alt)
            Command(e.KeyCode, e.Shift);
        else if (editor != null)
            EditText(e.KeyCode);
        else
            Key(e.KeyCode);
        Invalidate();
    }

    /// Ctrl shortcuts; they work while typing text too.
    private void Command(Keys key, bool shift)
    {
        switch (key)
        {
            case Keys.C:
                CopyImage();
                break;
            case Keys.S:
                SaveImage();
                break;
            case Keys.Z when shift:
            case Keys.Y:
                Redo();
                break;
            case Keys.Z:
                Undo();
                break;
            case Keys.Enter when editor != null:
                CommitText();
                break;
            case Keys.Enter:
                CopyImage();
                break;
            case Keys.V when editor != null:
                try
                {
                    Insert(Clipboard.GetText().ReplaceLineEndings("\n"));
                }
                catch (ExternalException)
                {
                    // Another app holds the clipboard, so there is nothing to paste this time.
                }
                break;
            case Keys.A when editor != null:
                editor.AllSelected = true;
                break;
        }
    }

    private void Key(Keys key)
    {
        switch (key)
        {
            case Keys.Escape:
                // Esc works in steps: close the color ring, drop the tool, deselect the object, and only then quit.
                if (picker != null)
                    picker = null;
                else if (tool is { } active)
                    SelectTool(active);
                else if (selected != null)
                    selected = null;
                else
                    onClose();
                break;
            case Keys.Enter:
                CopyImage();
                break;
            case Keys.Back or Keys.Delete:
                DeleteSelected();
                break;
            case Keys.V:
                SelectMoveMode();
                break;
            default:
                foreach (var t in Enum.GetValues<Tool>())
                {
                    if (t.Key == (char)key)
                        SelectTool(t);
                }
                break;
        }
    }

    // MARK: - Text

    /// Text is typed right on the screenshot: Enter adds a line; Esc, Ctrl+Enter or a click elsewhere finishes.
    private void BeginText(SKPoint p, int? index = null)
    {
        var source = index is { } i ? annotations[i] : null;
        editorSize = source?.Size ?? SizeOf(Tool.Text);
        editor = new TextEditor(p, source?.Text ?? "", source?.Color ?? color);
        editing = index;
        selected = null;
        Invalidate();
    }

    private void EditText(Keys key)
    {
        var e = editor!;
        switch (key)
        {
            case Keys.Escape:
                CommitText();
                break;
            case Keys.Enter:
                Insert("\n");
                break;
            case Keys.Back:
                Erase(e.Caret - 1);
                break;
            case Keys.Delete:
                Erase(e.Caret);
                break;
            case Keys.Left:
                MoveCaret(e.AllSelected ? 0 : e.Caret - 1);
                break;
            case Keys.Right:
                MoveCaret(e.AllSelected ? e.Text.Length : e.Caret + 1);
                break;
            case Keys.Home:
                MoveCaret(e.Caret == 0 ? 0 : e.Text.LastIndexOf('\n', e.Caret - 1) + 1);
                break;
            case Keys.End:
                MoveCaret(e.Text.IndexOf('\n', e.Caret) is var end and >= 0 ? end : e.Text.Length);
                break;
        }
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        if (editor == null || char.IsControl(e.KeyChar))
            return;
        Insert(e.KeyChar.ToString());
        Invalidate();
    }

    private void Insert(string text)
    {
        if (editor is not { } e)
            return;
        if (e.AllSelected)
            Erase(0);
        e.Text = e.Text.Insert(e.Caret, text);
        e.Caret += text.Length;
    }

    /// Removes the character at `index`, or all the text while it's selected.
    private void Erase(int index)
    {
        var e = editor!;
        if (e.AllSelected)
        {
            e.Text = "";
            e.Caret = 0;
            e.AllSelected = false;
        }
        else if (index >= 0 && index < e.Text.Length)
        {
            e.Text = e.Text.Remove(index, 1);
            e.Caret = index;
        }
    }

    private void MoveCaret(int index)
    {
        var e = editor!;
        e.Caret = Math.Clamp(index, 0, e.Text.Length);
        e.AllSelected = false;
    }

    private void CommitText()
    {
        if (editor is not { } e)
            return;
        editor = null;
        var a = new Annotation(Tool.Text, e.Color, editorSize, [e.Origin]) { Text = e.Text };
        if (editing is { } i)
        {
            editing = null;
            var old = annotations[i];
            if (!a.IsValid)
                Record(() => annotations.RemoveAt(i));
            else if (a.Text != old.Text || a.Size != old.Size || a.Color != old.Color)
                Record(() => annotations[i] = a);
        }
        else if (a.IsValid)
        {
            Record(() => annotations.Add(a));
            Sizes[Tool.Text] = editorSize;
        }
        Invalidate();
    }

    // MARK: - Actions

    /// Choosing the active tool again deselects it.
    private void SelectTool(Tool newTool)
    {
        CommitText();
        tool = tool == newTool ? null : newTool;
        selected = null;
        Invalidate();
    }

    /// Selecting an object drops the active tool and makes the object's size current.
    private void SelectObject(int i)
    {
        selected = i;
        tool = null;
        Sizes[annotations[i].Tool] = annotations[i].Size;
    }

    /// Selection mode: clicking an object selects it, dragging moves it.
    private void SelectMoveMode()
    {
        if (tool is { } t)
            SelectTool(t);
    }

    /// The color button opens the same ring as a right click, around the button and fully on screen.
    private void PickColor()
    {
        var button = ButtonRects().First(b => b.Button.Icon == "Color").Rect;
        var margin = RingRadius + 20;
        picker = (new SKPoint(Math.Min(Math.Max(button.MidX, margin), Area.Right - margin), Math.Min(Math.Max(button.MidY, margin), Area.Bottom - margin)),
                  IndexOf(color));
        Invalidate();
    }

    /// The new color applies to new objects, to the selected object (undoable) and to the text being typed.
    private void Apply(SKColor newColor)
    {
        color = newColor;
        if (editor != null)
            editor.Color = newColor;
        else if (selected is { } i && annotations[i].Color != newColor)
            Record(() => annotations[i] = annotations[i] with { Color = newColor });
        Invalidate();
    }

    private void Undo()
    {
        CommitText();
        if (undoStack.Count == 0)
            return;
        redoStack.Add(annotations);
        Restore(Pop(undoStack));
    }

    private void Redo()
    {
        CommitText();
        if (redoStack.Count == 0)
            return;
        undoStack.Add(annotations);
        Restore(Pop(redoStack));
    }

    /// After undo or redo, the next number is the highest remaining one + 1.
    private void Restore(List<Annotation> state)
    {
        annotations = state;
        selected = null;
        counter = annotations.Where(a => a.Tool == Tool.Counter).Select(a => a.Number).DefaultIfEmpty(0).Max() + 1;
        Invalidate();
    }

    /// Deleting a numbered circle shifts the following numbers down so the sequence has no gaps.
    private void DeleteSelected()
    {
        if (selected is not { } i)
            return;
        var removed = annotations[i];
        Record(() =>
        {
            annotations.RemoveAt(i);
            if (removed.Tool == Tool.Counter)
            {
                for (var j = 0; j < annotations.Count; j++)
                {
                    if (annotations[j].Tool == Tool.Counter && annotations[j].Number > removed.Number)
                        annotations[j] = annotations[j] with { Number = annotations[j].Number - 1 };
                }
                counter = Math.Max(counter - 1, 1);
            }
        });
        selected = null;
        Invalidate();
    }

    private void CopyImage()
    {
        if (RenderSelection() is not { } image)
            return;
        using (image)
        using (var png = image.Encode(SKEncodedImageFormat.Png, 100))
        using (var bitmap = new Bitmap(new MemoryStream(png.ToArray())))
        {
            try
            {
                Clipboard.SetImage(bitmap);
            }
            catch (ExternalException error)
            {
                // Another app holds the clipboard; the screenshot stays open, so it can be copied again.
                MessageBox.Show(this, error.Message, "EasyShot", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
        }
        onClose();
    }

    private void SaveImage()
    {
        if (RenderSelection() is not { } image)
            return;
        byte[] png;
        using (image)
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
            png = data.ToArray();
        onClose();
        SaveLater(png);
    }

    /// Shows the save dialog once the overlays have closed, otherwise it would end up behind them.
    private static async void SaveLater(byte[] png)
    {
        await Task.Yield();
        using var dialog = new SaveFileDialog { Filter = "PNG|*.png", FileName = $"Screenshot {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png" };
        if (dialog.ShowDialog() != DialogResult.OK)
            return;
        try
        {
            File.WriteAllBytes(dialog.FileName, png);
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "EasyShot", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// Final image: pixels of the selected area plus the drawn objects.
    private SKImage? RenderSelection()
    {
        CommitText();
        if (selection is not { } sel)
            return null;
        var pixels = SKRectI.Round(new SKRect(sel.Left * PixelScale, sel.Top * PixelScale, sel.Right * PixelScale, sel.Bottom * PixelScale));
        using var surface = SKSurface.Create(new SKImageInfo(pixels.Width, pixels.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var canvas = surface.Canvas;
        canvas.DrawImage(screenshot, pixels, SKRect.Create(pixels.Width, pixels.Height), Nearest);
        // Objects are in points; map them to the area's pixels.
        canvas.Scale(PixelScale);
        canvas.Translate(-sel.Left, -sel.Top);
        annotations.ForEach(a => a.Draw(canvas));
        return surface.Snapshot();
    }

    // MARK: - Helpers

    private void StartDrawing(Tool t, SKPoint p)
    {
        if (t == Tool.Text)
        {
            BeginText(p);
            return;
        }
        current = new Annotation(t, color, SizeOf(t), t == Tool.Pencil ? [p] : [p, p]) { Number = counter };
        drag = new Drawing();
    }

    private static int SizeOf(Tool t) => Sizes.GetValueOrDefault(t, t.DefaultSize);

    private static int? IndexOf(SKColor c) => Array.IndexOf(Palette, c) is var i and >= 0 ? i : null;

    private void Record(Action change)
    {
        Push([.. annotations]);
        change();
    }

    private void Push(List<Annotation> state)
    {
        undoStack.Add(state);
        if (undoStack.Count > UndoLimit)
            undoStack.RemoveAt(0);
        redoStack.Clear();
    }

    private static List<Annotation> Pop(List<List<Annotation>> stack)
    {
        var last = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        return last;
    }

    /// Topmost object under the point.
    private int? Hit(SKPoint p)
    {
        for (var i = annotations.Count - 1; i >= 0; i--)
        {
            if (annotations[i].Contains(p))
                return i;
        }
        return null;
    }

    /// The mosaic depends on where it sits on the screenshot, so recompute it on every geometry change.
    private Annotation Refreshed(Annotation a)
    {
        if (a.Tool != Tool.Pixelate)
            return a;
        var box = a.Box;
        return a with
        {
            Pixelated = Pixelate.Image(screenshot, new SKRect(box.Left * PixelScale, box.Top * PixelScale, box.Right * PixelScale, box.Bottom * PixelScale),
                                       (int)(a.Size * PixelScale)),
        };
    }

    /// In selection mode, the move cursor over an object shows that it can be dragged.
    private void UpdateCursor()
    {
        if (mouse is not { } m)
            return;
        Cursor = InBars(m) ? Cursors.Default
            : drag is MovingObject || (tool == null && picker == null && editor == null && Hit(m) != null) ? Cursors.SizeAll
            : Cursors.Cross;
    }

    private static int Clamp(int size) => Math.Clamp(size, MinSize, MaxSize);

    private SKPoint PointOf(MouseEventArgs e) =>
        new(Math.Min(Math.Max(e.X / PixelScale, 0), Area.Right), Math.Min(Math.Max(e.Y / PixelScale, 0), Area.Bottom));

    /// Rounds a length in points to whole pixels.
    private float Snap(float length) => MathF.Round(length * PixelScale) / PixelScale;

    /// Rectangle between two points, with its edges on whole pixels.
    private SKRect Rect(SKPoint a, SKPoint b)
    {
        var s = PixelScale;
        return new SKRect(MathF.Floor(Math.Min(a.X, b.X) * s) / s, MathF.Floor(Math.Min(a.Y, b.Y) * s) / s,
                          MathF.Ceiling(Math.Max(a.X, b.X) * s) / s, MathF.Ceiling(Math.Max(a.Y, b.Y) * s) / s);
    }

    private static SKPoint HandlePoint(int dx, int dy, SKRect r) =>
        new(dx < 0 ? r.Left : dx > 0 ? r.Right : r.MidX, dy < 0 ? r.Top : dy > 0 ? r.Bottom : r.MidY);

    // MARK: - Toolbars

    /// Toolbar frames: tools go to the right of the frame and actions below it; near screen edges the toolbars move inside.
    /// The toolbars are hidden while selecting or drawing so they don't get in the way, and while the color ring is open so they don't cover it.
    private (SKRect Tools, SKRect Actions)? Bars()
    {
        if (selection is not { } sel || picker != null || drag is Selecting or Drawing)
            return null;
        var area = Area;
        var toolsSize = new SKSize(ButtonSize + 2 * BarInset, toolsBar.Length * ButtonSize + 2 * BarInset);
        var actionsSize = new SKSize(actionsBar.Length * ButtonSize + 2 * BarInset, ButtonSize + 2 * BarInset);
        var x = sel.Right + 6;
        if (x + toolsSize.Width > area.Right)
            x = sel.Left - 6 - toolsSize.Width;
        if (x < 0)
            x = sel.Right - toolsSize.Width - 6;
        var y = Math.Min(Math.Max(sel.Bottom - toolsSize.Height, 0), area.Bottom - toolsSize.Height);
        var tools = SKRect.Create(new SKPoint(x, y), toolsSize);

        var actions = SKRect.Create(new SKPoint(Math.Max(sel.Right - actionsSize.Width, 0), sel.Bottom + 6), actionsSize);
        if (actions.Bottom > area.Bottom)
            actions.Location = new SKPoint(actions.Left, sel.Bottom - actionsSize.Height - 6);
        if (actions.IntersectsWith(tools))
            actions.Location = new SKPoint(tools.Left - actionsSize.Width - 6, actions.Top);
        return (tools, actions);
    }

    /// The buttons with their frames, while the toolbars are shown.
    private IEnumerable<(BarButton Button, SKRect Rect)> ButtonRects()
    {
        if (Bars() is not { } bars)
            yield break;
        for (var i = 0; i < toolsBar.Length; i++)
            yield return (toolsBar[i], SKRect.Create(bars.Tools.Left + BarInset, bars.Tools.Top + BarInset + i * ButtonSize, ButtonSize, ButtonSize));
        for (var i = 0; i < actionsBar.Length; i++)
            yield return (actionsBar[i], SKRect.Create(bars.Actions.Left + BarInset + i * ButtonSize, bars.Actions.Top + BarInset, ButtonSize, ButtonSize));
    }

    private bool InBars(SKPoint p) => Bars() is { } bars && (bars.Tools.Contains(p) || bars.Actions.Contains(p));

    private BarButton? ButtonAt(SKPoint p) => ButtonRects().FirstOrDefault(b => b.Rect.Contains(p)).Button;

    private void DrawBars(SKCanvas canvas)
    {
        if (Bars() is not { } bars)
            return;
        using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(247, 247, 247) };
        canvas.DrawRoundRect(bars.Tools, 6, 6, paint);
        canvas.DrawRoundRect(bars.Actions, 6, 6, paint);
        paint.Color = new SKColor(0, 0, 0, 20);
        foreach (var (button, rect) in ButtonRects())
        {
            if (button == hovered)
                canvas.DrawRoundRect(SKRect.Inflate(rect, -2, -2), 4, 4, paint);
            var icon = SKRect.Create(rect.MidX - 9, rect.MidY - 9, 18, 18);
            if (button.Icon == "Color")
                DrawSwatch(canvas, icon);
            else
                Icons.Draw(canvas, button.Icon, icon, button.Active?.Invoke() == true ? Accent : Gray);
        }
    }

    /// The current color on the color button.
    private static void DrawSwatch(SKCanvas canvas, SKRect rect)
    {
        var swatch = SKRect.Inflate(rect, -2, -2);
        using var paint = new SKPaint { IsAntialias = true, Color = color };
        canvas.DrawOval(swatch, paint);
        paint.IsStroke = true;
        paint.StrokeWidth = 1;
        paint.Color = new SKColor(128, 128, 128);
        canvas.DrawOval(swatch, paint);
    }

    /// A button's hint shows up once the cursor rests on it.
    private async void UpdateTip(SKPoint p)
    {
        var button = ButtonAt(p);
        if (button == hovered)
            return;
        HideTip();
        hovered = button;
        Invalidate();
        if (button == null)
            return;
        await Task.Delay(600);
        if (hovered == button && mouse is { } m && !IsDisposed && Visible)
            tip.Show(button.Tip, this, (int)(m.X * PixelScale), (int)((m.Y + 24) * PixelScale));
    }

    private void HideTip()
    {
        hovered = null;
        // Show makes the whole view a tooltip tool, and Hide alone would keep it, bringing the last hint back on any pause over the view.
        tip.SetToolTip(this, null);
        tip.Hide(this);
    }

    // MARK: - Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ColorsUsed, ColorsImportant;
    }

    [LibraryImport("gdi32.dll")]
    private static partial int StretchDIBits(nint hdc, int x, int y, int width, int height, int sourceX, int sourceY, int sourceWidth, int sourceHeight,
                                             nint bits, in BitmapInfoHeader header, uint usage, uint operation);
}
