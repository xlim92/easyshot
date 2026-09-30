using Windows.Win32.System.SystemServices;

namespace EasyShot;

/// Capture screen of one display: dimmed screenshot, area selection with a resizable frame, toolbars next to the frame
/// and drawing on top of the screenshot: tools, Shift constraints, mouse wheel sizing, color ring, object editing.
/// Everything, toolbars included, is drawn in points (1/96 inch) at the display's scale.
sealed unsafe class CaptureView : Window
{
    private abstract record Drag;
    private sealed record Selecting(Vector2 Start) : Drag;
    private sealed record MovingSelection(Vector2 Start, RectangleF Original) : Drag;
    private sealed record Resizing(int Dx, int Dy, RectangleF Original) : Drag;
    private sealed record Drawing : Drag;
    private sealed record MovingObject(Vector2 Start, List<Annotation> Before) : Drag;
    private sealed record Pressing(BarButton Button) : Drag;

    /// Toolbar button: an icon, or the current color when Icon is null; the icon is in the accent color while Active returns true.
    private sealed record BarButton(Icon? Icon, string Tip, Action Run, Func<bool>? Active = null);

    /// Text being typed right on the screenshot. Text taken for editing starts fully selected, so typing replaces it.
    private sealed class TextEditor(Vector2 origin, string text, uint color)
    {
        public readonly Vector2 Origin = origin;
        public string Text = text;
        public int Caret = text.Length;
        public bool AllSelected = text.Length > 0;
        public uint Color = color;
    }

    private static readonly (int Dx, int Dy)[] Handles = [(-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1)];
    private static readonly uint[] Palette = [0xFFFF3B30, 0xFFFF9500, 0xFFFFCC00, 0xFF34C759, 0xFF00C7BE, 0xFF007AFF, 0xFFAF52DE, 0xFFFF2D55, 0xFF000000, 0xFFFFFFFF];
    /// Palette swatches sit on a ring with a gap between neighbors.
    private static readonly float RingRadius = MathF.Max(44, Palette.Length * 34 / (2 * MathF.PI));
    private static readonly float[] FrameDash = [4, 4], ObjectDash = [5, 3];
    private const int MinSize = 1, MaxSize = 72, UndoLimit = 200;
    private const float ButtonSize = 30, BarInset = 3;
    private const nuint TipTimer = 1, HintTimer = 2;
    private const uint White = 0xFFFFFFFF, Black = 0xFF000000, Gray = 0xFF555555, BarColor = 0xFFF7F7F7, HoverColor = 0x14000000;
    /// Color and tool sizes are remembered between screenshots while the app is running.
    private static uint color = Palette[0];
    private static readonly Dictionary<Tool, int> Sizes = [];

    private readonly RECT bounds;
    private readonly Pixels screenshot, dimmed, frame;
    private readonly Action onClose;
    private readonly BarButton[] toolsBar, actionsBar;
    private readonly uint accent;
    /// Pixels per point on this display.
    private readonly float scale;

    private RectangleF? selection;
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
    private (Vector2 Center, int? Hovered)? picker;
    private Vector2? mouse;
    private bool tracking;
    private BarButton? hovered, tip;
    private int wheel;
    private bool sizeHint;

    public CaptureView(RECT bounds, Pixels screenshot, Action onClose)
    {
        this.bounds = bounds;
        this.screenshot = screenshot;
        this.onClose = onClose;
        // 45% black over the screenshot, baked in once.
        dimmed = new Pixels(screenshot.Width, screenshot.Height);
        for (var i = 0; i < screenshot.Width * screenshot.Height; i++)
        {
            var c = screenshot.Data[i];
            dimmed.Data[i] = 0xFF000000 | (c >> 16 & 0xFF) * 141 >> 8 << 16 | (c >> 8 & 0xFF) * 141 >> 8 << 8 | (c & 0xFF) * 141 >> 8;
        }
        frame = new Pixels(screenshot.Width, screenshot.Height);
        // The selection color of the system, like the accent color on macOS.
        var highlight = PInvoke.GetSysColor(SYS_COLOR_INDEX.COLOR_HIGHLIGHT);
        accent = 0xFF000000 | (highlight & 0xFF) << 16 | (highlight & 0xFF00) | highlight >> 16 & 0xFF;

        var tools = new List<BarButton> { new(Icon.Move, "Выбор и перемещение объектов (V, Esc)", SelectMoveMode, () => tool == null) };
        for (var t = Tool.Pencil; t <= Tool.Invert; t++)
        {
            var each = t;
            tools.Add(new BarButton((Icon)each, each.Title + (each.Key is { } key ? $" ({key})" : ""), () => SelectTool(each), () => tool == each));
        }
        tools.Add(new BarButton(null, "Цвет (или правая кнопка мыши)", PickColor));
        tools.Add(new BarButton(Icon.Undo, "Отменить (Ctrl+Z)", Undo));
        tools.Add(new BarButton(Icon.Redo, "Повторить (Ctrl+Shift+Z)", Redo));
        toolsBar = [.. tools];
        actionsBar =
        [
            new BarButton(Icon.Copy, "Копировать (Ctrl+C, Enter)", CopyImage),
            new BarButton(Icon.Save, "Сохранить (Ctrl+S)", SaveImage),
            new BarButton(Icon.Close, "Закрыть (Esc)", onClose),
        ];

        // Topmost above the taskbar; a tool window has no taskbar button.
        Create("EasyShotOverlay", WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW, WINDOW_STYLE.WS_POPUP, "",
               bounds.left, bounds.top, screenshot.Width, screenshot.Height);
        scale = PInvoke.GetDpiForWindow(Handle) / 96f;
    }

    public bool Contains(System.Drawing.Point p) => p.X >= bounds.left && p.X < bounds.right && p.Y >= bounds.top && p.Y < bounds.bottom;

    public void Show(bool activate)
    {
        PInvoke.ShowWindow(Handle, activate ? SHOW_WINDOW_CMD.SW_SHOW : SHOW_WINDOW_CMD.SW_SHOWNA);
        if (activate)
            PInvoke.SetForegroundWindow(Handle);
    }

    /// The view's bounds in points.
    private RectangleF Area => new(0, 0, frame.Width / scale, frame.Height / scale);

    protected override LRESULT OnMessage(uint message, WPARAM wParam, LPARAM lParam)
    {
        switch (message)
        {
            case PInvoke.WM_PAINT:
                Paint();
                return default;
            case PInvoke.WM_ERASEBKGND:
                // Painting covers every pixel.
                return new LRESULT(1);
            case PInvoke.WM_LBUTTONDOWN or PInvoke.WM_LBUTTONDBLCLK:
                PInvoke.SetCapture(Handle);
                MouseDown(PointOf(lParam), message == PInvoke.WM_LBUTTONDBLCLK ? 2 : 1);
                return default;
            case PInvoke.WM_LBUTTONUP:
                PInvoke.ReleaseCapture();
                MouseUp(PointOf(lParam));
                return default;
            case PInvoke.WM_RBUTTONDOWN:
                RightMouseDown(PointOf(lParam));
                return default;
            case PInvoke.WM_RBUTTONUP:
                RightMouseUp(PointOf(lParam));
                return default;
            case PInvoke.WM_MOUSEMOVE:
                MouseMove(PointOf(lParam), (wParam.Value & (nuint)MODIFIERKEYS_FLAGS.MK_LBUTTON) != 0);
                return default;
            case PInvoke.WM_MOUSELEAVE:
                tracking = false;
                mouse = null;
                HideTip();
                Invalidate();
                return default;
            case PInvoke.WM_MOUSEWHEEL:
                Wheel((short)(wParam.Value >> 16 & 0xFFFF));
                return default;
            case PInvoke.WM_KEYDOWN or PInvoke.WM_SYSKEYDOWN:
                if (KeyDown((uint)wParam.Value))
                    return default;
                // Keys left to Windows, such as Alt+F4.
                break;
            case PInvoke.WM_CHAR:
                TypeCharacter((char)wParam.Value);
                return default;
            case PInvoke.WM_SYSCHAR:
                // Alt with a letter picks a tool; without this, Windows would beep for a missing menu.
                return default;
            case PInvoke.WM_SETCURSOR when (lParam.Value & 0xFFFF) == PInvoke.HTCLIENT:
                PInvoke.SetCursor(Cursor());
                return new LRESULT(1);
            case PInvoke.WM_TIMER:
                PInvoke.KillTimer(Handle, wParam.Value);
                if (wParam.Value == TipTimer)
                    tip = hovered;
                else
                    sizeHint = false;
                Invalidate();
                return default;
            case PInvoke.WM_CLOSE:
                // Alt+F4 or a close request from another app closes all the overlays, like Esc.
                onClose();
                return default;
            case PInvoke.WM_DPICHANGED:
                // The overlay stays on its display; the suggested size doesn't apply.
                return default;
            case PInvoke.WM_NCDESTROY:
                screenshot.Dispose();
                dimmed.Dispose();
                frame.Dispose();
                break;
        }
        return Default(message, wParam, lParam);
    }

    // MARK: - Drawing

    private void Paint()
    {
        using (var canvas = new Canvas(frame))
        {
            canvas.Scale(scale, scale);
            Draw(canvas);
        }
        var header = frame.Header;
        var hdc = PInvoke.BeginPaint(Handle, out var paint);
        PInvoke.StretchDIBits(hdc, 0, 0, frame.Width, frame.Height, 0, 0, frame.Width, frame.Height, frame.Data, &header, DIB_USAGE.DIB_RGB_COLORS, ROP_CODE.SRCCOPY);
        PInvoke.EndPaint(Handle, in paint);
    }

    private void Draw(Canvas canvas)
    {
        canvas.Copy(dimmed, Area);
        if (selection is not { } sel)
            return;

        var state = canvas.Save();
        canvas.ClipRect(sel);
        canvas.Copy(screenshot, sel);
        for (var i = 0; i < annotations.Count; i++)
        {
            if (i != editing)
                annotations[i].Draw(canvas);
        }
        current?.Draw(canvas);
        canvas.Restore(state);

        DrawFrame(canvas, sel);
        if (selected is { } s)
        {
            // Selected object: an accent-colored dashed frame over a white underlay.
            var box = RectangleF.Inflate(annotations[s].Bounds, 3, 3);
            canvas.StrokeRoundRect(box, 3, White, 2);
            canvas.StrokeRoundRect(box, 3, accent, 2, ObjectDash);
        }
        DrawMousePreview(canvas);
        DrawSizeHint(canvas);
        DrawPicker(canvas);
        DrawBars(canvas);
        DrawEditor(canvas);
        DrawTip(canvas);
    }

    private void DrawFrame(Canvas canvas, RectangleF sel)
    {
        // A whole number of pixels wide, so the frame stays crisp at any display scale.
        var line = MathF.Max(1, MathF.Round(scale)) / scale;
        var border = RectangleF.Inflate(sel, line / 2, line / 2);
        canvas.StrokeRect(border, Black, line);
        canvas.StrokeRect(border, White, line, FrameDash);
        foreach (var (dx, dy) in Handles)
        {
            var p = HandlePoint(dx, dy, sel);
            var handle = new RectangleF(p.X - 3, p.Y - 3, 6, 6);
            canvas.FillRect(handle, White);
            canvas.StrokeRect(handle, Black, line);
        }

        var font = Font.Get(11);
        var label = $"{MathF.Round(sel.Width * scale)} × {MathF.Round(sel.Height * scale)}";
        var size = new Vector2(font.Width(label), font.LineHeight);
        var box = new RectangleF(sel.Left, sel.Top - size.Y - 8, size.X + 8, size.Y + 4);
        if (box.Top < 0)
            box = new RectangleF(sel.Left + 4, sel.Top + 4, box.Width, box.Height);
        canvas.FillRoundRect(box, 3, 0xB3000000);
        canvas.Text(label, new Vector2(box.Left + 4, box.Top + 2), font, White);
    }

    /// Brush preview under the cursor: tool color and width; for numbering, a circle with the next number.
    private void DrawMousePreview(Canvas canvas)
    {
        if (tool is not { } t || mouse is not { } m || drag != null || picker != null || editor != null || selection?.Contains(m.X, m.Y) != true)
            return;
        float size = SizeOf(t);
        var diameter = t switch
        {
            Tool.Text or Tool.Pixelate or Tool.Invert or Tool.FilledRect => 0,
            Tool.Counter => 2 * size,
            _ => MathF.Max(size, 3),
        };
        if (diameter <= 0)
            return;
        canvas.FillEllipse(Annotation.Circle(m, diameter / 2), t is Tool.Marker or Tool.Counter ? 0x66000000 | (color & 0xFFFFFF) : color);
        if (t == Tool.Counter)
        {
            var font = Font.Get(size, bold: true);
            var label = $"{counter}";
            canvas.Text(label, m - new Vector2(font.Width(label), font.LineHeight) / 2, font, White);
        }
    }

    /// After scrolling the wheel, the current size is briefly shown next to the cursor.
    private void DrawSizeHint(Canvas canvas)
    {
        if (!sizeHint || mouse is not { } m)
            return;
        int? value = editor != null ? editorSize : selected is { } i ? annotations[i].Size : tool is { } t ? SizeOf(t) : null;
        if (value == null)
            return;
        var font = Font.Get(13, bold: true);
        var label = $"{value}";
        var size = new Vector2(font.Width(label), font.LineHeight);
        var badge = new RectangleF(m.X + 16, m.Y + 16, size.X + 14, size.Y + 6);
        canvas.FillRoundRect(badge, badge.Height / 2, 0xBF262626);
        canvas.Text(label, new Vector2(badge.X + 7, badge.Y + 3), font, White);
    }

    /// Color ring around the point where it was opened; the highlighted color is enlarged and outlined in white.
    private void DrawPicker(Canvas canvas)
    {
        if (picker is not { } open)
            return;
        for (var i = 0; i < Palette.Length; i++)
        {
            var isHovered = i == open.Hovered;
            var swatch = RectangleF.Inflate(SwatchRect(i, open.Center), isHovered ? 3 : 0, isHovered ? 3 : 0);
            canvas.FillEllipse(swatch, Palette[i]);
            canvas.StrokeEllipse(swatch, isHovered ? White : 0x80000000, isHovered ? 3 : 1);
        }
    }

    /// Frame of swatch i, going clockwise from the top of the ring.
    private static RectangleF SwatchRect(int i, Vector2 center)
    {
        var angle = 2 * MathF.PI * i / Palette.Length - MathF.PI / 2;
        return Annotation.Circle(center + RingRadius * new Vector2(MathF.Cos(angle), MathF.Sin(angle)), 13);
    }

    private static int? SwatchAt(Vector2 center, Vector2 p)
    {
        for (var i = 0; i < Palette.Length; i++)
        {
            if (SwatchRect(i, center).Contains(p.X, p.Y))
                return i;
        }
        return null;
    }

    /// The text being typed with its caret, or with a highlight while all of it is selected.
    private void DrawEditor(Canvas canvas)
    {
        if (editor is not { } e)
            return;
        var font = Font.Get(editorSize);
        var lines = e.Text.Split('\n');
        if (e.AllSelected)
        {
            for (var i = 0; i < lines.Length; i++)
                canvas.FillRect(new RectangleF(e.Origin.X + 4, e.Origin.Y + 4 + i * font.LineHeight, font.Width(lines[i]), font.LineHeight), 0xFFB3D7FF);
        }
        new Annotation(Tool.Text, e.Color, editorSize, [e.Origin]) { Text = e.Text }.Draw(canvas);
        if (!e.AllSelected)
        {
            var before = e.Text[..e.Caret];
            var x = e.Origin.X + 4 + font.Width(before[(before.LastIndexOf('\n') + 1)..]);
            var y = e.Origin.Y + 4 + before.AsSpan().Count('\n') * font.LineHeight;
            canvas.Line(new Vector2(x, y), new Vector2(x, y + font.LineHeight), e.Color, 1);
        }
    }

    /// A button's hint, shown once the cursor rests on the button.
    private void DrawTip(Canvas canvas)
    {
        if (tip is not { } button || mouse is not { } m)
            return;
        var font = Font.Get(12);
        var size = new Vector2(font.Width(button.Tip) + 12, font.LineHeight + 6);
        var area = Area;
        var box = new RectangleF(MathF.Min(m.X + 12, area.Right - size.X), MathF.Min(m.Y + 22, area.Bottom - size.Y), size.X, size.Y);
        canvas.FillRoundRect(box, 4, White);
        canvas.StrokeRoundRect(box, 4, 0xFFA0A0A0, 1);
        canvas.Text(button.Tip, new Vector2(box.X + 6, box.Y + 3), font, 0xFF1A1A1A);
    }

    // MARK: - Mouse

    private void MouseDown(Vector2 p, int clicks)
    {
        mouse = p;
        HideTip();
        LeftMouseDown(p, clicks);
        Invalidate();
    }

    private void LeftMouseDown(Vector2 p, int clicks)
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
        var handle = Array.FindIndex(Handles, h => Vector2.Distance(HandlePoint(h.Dx, h.Dy, sel), p) <= 6);
        if (handle >= 0)
        {
            selected = null;
            drag = new Resizing(Handles[handle].Dx, Handles[handle].Dy, sel);
        }
        else if (sel.Contains(p.X, p.Y))
        {
            if (tool is { } t)
            {
                // With a tool active, a click always starts a new object.
                StartDrawing(t, p);
            }
            else if ((selected is { } o && annotations[o].Bounds.Contains(p.X, p.Y) ? o : Hit(p)) is { } i)
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

    private void MouseMove(Vector2 p, bool leftButton)
    {
        mouse = p;
        if (!tracking)
        {
            // Ask for WM_MOUSELEAVE when the cursor leaves the display.
            var track = new TRACKMOUSEEVENT { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = TRACKMOUSEEVENT_FLAGS.TME_LEAVE, hwndTrack = Handle };
            tracking = PInvoke.TrackMouseEvent(&track);
        }
        if (leftButton && drag != null)
            MouseDragged(p);
        else
            MouseMoved(p);
    }

    private void MouseDragged(Vector2 p)
    {
        switch (drag)
        {
            case Selecting(var start):
                selection = Rect(start, p);
                break;
            case MovingSelection(var start, var original):
            {
                var area = Area;
                var x = MathF.Min(MathF.Max(original.X + Snap(p.X - start.X), 0), area.Right - original.Width);
                var y = MathF.Min(MathF.Max(original.Y + Snap(p.Y - start.Y), 0), area.Bottom - original.Height);
                selection = new RectangleF(x, y, original.Width, original.Height);
                break;
            }
            case Resizing(var dx, var dy, var r):
            {
                Vector2 a = new(r.Left, r.Top), b = new(r.Right, r.Bottom);
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
                    var d = Pressed(VIRTUAL_KEY.VK_SHIFT) ? a.Tool.Constrained(v) : v;
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

    private void MouseUp(Vector2 p)
    {
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
        Invalidate();
    }

    private void MouseMoved(Vector2 p)
    {
        if (picker is { } open && SwatchAt(open.Center, p) is { } i)
            picker = open with { Hovered = i };
        UpdateTip(p);
        if (tool != null || picker != null || tip != null)
            Invalidate();
    }

    /// Right click opens the color ring; with no tool active it first selects the object under the cursor so it can be recolored.
    private void RightMouseDown(Vector2 p)
    {
        HideTip();
        if (editor != null)
            return;
        if (tool == null && Hit(p) is { } i)
            SelectObject(i);
        picker = (p, IndexOf(color));
        Invalidate();
    }

    /// Besides clicking a color, you can press the right button, drag to a color and release.
    private void RightMouseUp(Vector2 p)
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
    private void Wheel(int delta)
    {
        // Touchpads send a stream of small deltas: accumulate them, one size step per wheel notch.
        const int notch = 120;
        wheel += delta;
        var step = wheel / notch;
        wheel -= step * notch;
        if (step == 0)
            return;

        if (tool == Tool.Counter && Pressed(VIRTUAL_KEY.VK_SHIFT))
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
        sizeHint = true;
        PInvoke.SetTimer(Handle, HintTimer, 800, null);
        Invalidate();
    }

    // MARK: - Keyboard

    /// Returns false for keys left to Windows, such as Alt+F4.
    private bool KeyDown(uint key)
    {
        HideTip();
        bool control = Pressed(VIRTUAL_KEY.VK_CONTROL), alt = Pressed(VIRTUAL_KEY.VK_MENU);
        // AltGr is reported as Ctrl+Alt and types characters, so it isn't a command.
        var handled = control && !alt ? Command(key, Pressed(VIRTUAL_KEY.VK_SHIFT)) : editor != null ? EditText(key) : Key(key);
        Invalidate();
        return handled;
    }

    /// Ctrl shortcuts; they work while typing text too.
    private bool Command(uint key, bool shift)
    {
        switch (key)
        {
            case 'C':
                CopyImage();
                break;
            case 'S':
                SaveImage();
                break;
            case 'Z' when shift:
            case 'Y':
                Redo();
                break;
            case 'Z':
                Undo();
                break;
            case (uint)VIRTUAL_KEY.VK_RETURN when editor != null:
                CommitText();
                break;
            case (uint)VIRTUAL_KEY.VK_RETURN:
                CopyImage();
                break;
            case 'V' when editor != null:
                // Nothing to paste while another app holds the clipboard.
                if (Clipboard.GetText(App.Current.Handle) is { } text)
                    Insert(text.ReplaceLineEndings("\n"));
                break;
            case 'A' when editor != null:
                editor.AllSelected = true;
                break;
            default:
                return false;
        }
        return true;
    }

    private bool Key(uint key)
    {
        switch (key)
        {
            case (uint)VIRTUAL_KEY.VK_ESCAPE:
                // Esc works in steps: close the color ring, drop the tool, deselect the object, and only then quit.
                if (picker != null)
                    picker = null;
                else if (tool is { } active)
                    SelectTool(active);
                else if (selected != null)
                    selected = null;
                else
                    onClose();
                return true;
            case (uint)VIRTUAL_KEY.VK_RETURN:
                CopyImage();
                return true;
            case (uint)VIRTUAL_KEY.VK_BACK or (uint)VIRTUAL_KEY.VK_DELETE:
                DeleteSelected();
                return true;
            case 'V':
                SelectMoveMode();
                return true;
        }
        for (var t = Tool.Pencil; t <= Tool.Invert; t++)
        {
            if (t.Key == key)
            {
                SelectTool(t);
                return true;
            }
        }
        return false;
    }

    // MARK: - Text

    /// Text is typed right on the screenshot: Enter adds a line; Esc, Ctrl+Enter or a click elsewhere finishes.
    private void BeginText(Vector2 p, int? index = null)
    {
        var source = index is { } i ? annotations[i] : null;
        editorSize = source?.Size ?? SizeOf(Tool.Text);
        editor = new TextEditor(p, source?.Text ?? "", source?.Color ?? color);
        editing = index;
        selected = null;
        Invalidate();
    }

    private bool EditText(uint key)
    {
        var e = editor!;
        switch (key)
        {
            case (uint)VIRTUAL_KEY.VK_ESCAPE:
                CommitText();
                break;
            case (uint)VIRTUAL_KEY.VK_RETURN:
                Insert("\n");
                break;
            case (uint)VIRTUAL_KEY.VK_BACK:
                Erase(e.Caret - 1);
                break;
            case (uint)VIRTUAL_KEY.VK_DELETE:
                Erase(e.Caret);
                break;
            case (uint)VIRTUAL_KEY.VK_LEFT:
                MoveCaret(e.AllSelected ? 0 : e.Caret - 1);
                break;
            case (uint)VIRTUAL_KEY.VK_RIGHT:
                MoveCaret(e.AllSelected ? e.Text.Length : e.Caret + 1);
                break;
            case (uint)VIRTUAL_KEY.VK_HOME:
                MoveCaret(e.Caret == 0 ? 0 : e.Text.LastIndexOf('\n', e.Caret - 1) + 1);
                break;
            case (uint)VIRTUAL_KEY.VK_END:
                MoveCaret(e.Text.IndexOf('\n', e.Caret) is var end and >= 0 ? end : e.Text.Length);
                break;
            default:
                // Character keys come as WM_CHAR.
                return false;
        }
        return true;
    }

    private void TypeCharacter(char c)
    {
        if (editor == null || char.IsControl(c))
            return;
        Insert($"{c}");
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
        var button = default(RectangleF);
        foreach (var (b, rect) in ButtonRects())
        {
            if (b.Icon == null)
                button = rect;
        }
        var area = Area;
        var margin = RingRadius + 20;
        var center = new Vector2(MathF.Min(MathF.Max(button.X + button.Width / 2, margin), area.Right - margin),
                                 MathF.Min(MathF.Max(button.Y + button.Height / 2, margin), area.Bottom - margin));
        picker = (center, IndexOf(color));
        Invalidate();
    }

    /// The new color applies to new objects, to the selected object (undoable) and to the text being typed.
    private void Apply(uint newColor)
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
        counter = 1;
        foreach (var a in annotations)
        {
            if (a.Tool == Tool.Counter)
                counter = Math.Max(counter, a.Number + 1);
        }
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
        {
            if (!Clipboard.SetImage(image, App.Current.Handle))
            {
                // The screenshot stays open, so it can be copied again.
                PInvoke.MessageBox(Handle, "Буфер обмена занят другим приложением.", "EasyShot", MESSAGEBOX_STYLE.MB_ICONERROR);
                return;
            }
        }
        onClose();
    }

    private void SaveImage()
    {
        if (RenderSelection() is not { } image)
            return;
        onClose();
        App.Current.SaveLater(image);
    }

    /// Final image: pixels of the selected area plus the drawn objects.
    private Pixels? RenderSelection()
    {
        CommitText();
        if (selection is not { } sel)
            return null;
        int x0 = (int)MathF.Round(sel.Left * scale), y0 = (int)MathF.Round(sel.Top * scale);
        var image = new Pixels((int)MathF.Round(sel.Right * scale) - x0, (int)MathF.Round(sel.Bottom * scale) - y0);
        using (var canvas = new Canvas(image))
        {
            // Objects are in points; map them to the area's pixels.
            canvas.Translate(-x0, -y0);
            canvas.Scale(scale, scale);
            canvas.Copy(screenshot, sel, x0, y0);
            foreach (var a in annotations)
                a.Draw(canvas);
        }
        return image;
    }

    // MARK: - Helpers

    private void StartDrawing(Tool t, Vector2 p)
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

    private static int? IndexOf(uint c) => Array.IndexOf(Palette, c) is var i and >= 0 ? i : null;

    private static int Clamp(int size) => Math.Clamp(size, MinSize, MaxSize);

    private static bool Pressed(VIRTUAL_KEY key) => PInvoke.GetKeyState((int)key) < 0;

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
    private int? Hit(Vector2 p)
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
            Pixelated = Pixelate.Image(screenshot, new RectangleF(box.X * scale, box.Y * scale, box.Width * scale, box.Height * scale), (int)(a.Size * scale)),
        };
    }

    /// Arrow over the toolbars; in selection mode, the move cursor over an object shows that it can be dragged.
    private HCURSOR Cursor()
    {
        var name = mouse is { } m && InBars(m) ? PInvoke.IDC_ARROW
            : drag is MovingObject || (mouse is { } p && tool == null && picker == null && editor == null && Hit(p) != null) ? PInvoke.IDC_SIZEALL
            : PInvoke.IDC_CROSS;
        return PInvoke.LoadCursor(HINSTANCE.Null, name);
    }

    private Vector2 PointOf(LPARAM lParam)
    {
        var area = Area;
        float x = (short)(lParam.Value & 0xFFFF) / scale, y = (short)(lParam.Value >> 16 & 0xFFFF) / scale;
        return new Vector2(Math.Clamp(x, 0, area.Right), Math.Clamp(y, 0, area.Bottom));
    }

    /// Rounds a length in points to whole pixels.
    private float Snap(float length) => MathF.Round(length * scale) / scale;

    /// Rectangle between two points, with its edges on whole pixels.
    private RectangleF Rect(Vector2 a, Vector2 b) =>
        RectangleF.FromLTRB(MathF.Floor(MathF.Min(a.X, b.X) * scale) / scale, MathF.Floor(MathF.Min(a.Y, b.Y) * scale) / scale,
                            MathF.Ceiling(MathF.Max(a.X, b.X) * scale) / scale, MathF.Ceiling(MathF.Max(a.Y, b.Y) * scale) / scale);

    private static Vector2 HandlePoint(int dx, int dy, RectangleF r) =>
        new(dx < 0 ? r.Left : dx > 0 ? r.Right : r.X + r.Width / 2, dy < 0 ? r.Top : dy > 0 ? r.Bottom : r.Y + r.Height / 2);

    // MARK: - Toolbars

    /// Toolbar frames: tools go to the right of the frame and actions below it; near screen edges the toolbars move inside.
    /// The toolbars are hidden while selecting or drawing so they don't get in the way, and while the color ring is open so they don't cover it.
    private (RectangleF Tools, RectangleF Actions)? Bars()
    {
        if (selection is not { } sel || picker != null || drag is Selecting or Drawing)
            return null;
        var area = Area;
        float toolsWidth = ButtonSize + 2 * BarInset, toolsHeight = toolsBar.Length * ButtonSize + 2 * BarInset;
        float actionsWidth = actionsBar.Length * ButtonSize + 2 * BarInset, actionsHeight = ButtonSize + 2 * BarInset;
        var x = sel.Right + 6;
        if (x + toolsWidth > area.Right)
            x = sel.Left - 6 - toolsWidth;
        if (x < 0)
            x = sel.Right - toolsWidth - 6;
        var y = MathF.Min(MathF.Max(sel.Bottom - toolsHeight, 0), area.Bottom - toolsHeight);
        var tools = new RectangleF(x, y, toolsWidth, toolsHeight);

        var actions = new RectangleF(MathF.Max(sel.Right - actionsWidth, 0), sel.Bottom + 6, actionsWidth, actionsHeight);
        if (actions.Bottom > area.Bottom)
            actions.Y = sel.Bottom - actionsHeight - 6;
        if (actions.IntersectsWith(tools))
            actions.X = tools.Left - actionsWidth - 6;
        return (tools, actions);
    }

    /// The buttons with their frames, while the toolbars are shown.
    private IEnumerable<(BarButton Button, RectangleF Rect)> ButtonRects()
    {
        if (Bars() is not { } bars)
            yield break;
        for (var i = 0; i < toolsBar.Length; i++)
            yield return (toolsBar[i], new RectangleF(bars.Tools.Left + BarInset, bars.Tools.Top + BarInset + i * ButtonSize, ButtonSize, ButtonSize));
        for (var i = 0; i < actionsBar.Length; i++)
            yield return (actionsBar[i], new RectangleF(bars.Actions.Left + BarInset + i * ButtonSize, bars.Actions.Top + BarInset, ButtonSize, ButtonSize));
    }

    private bool InBars(Vector2 p) => Bars() is { } bars && (bars.Tools.Contains(p.X, p.Y) || bars.Actions.Contains(p.X, p.Y));

    private BarButton? ButtonAt(Vector2 p)
    {
        foreach (var (button, rect) in ButtonRects())
        {
            if (rect.Contains(p.X, p.Y))
                return button;
        }
        return null;
    }

    private void DrawBars(Canvas canvas)
    {
        if (Bars() is not { } bars)
            return;
        canvas.FillRoundRect(bars.Tools, 6, BarColor);
        canvas.FillRoundRect(bars.Actions, 6, BarColor);
        foreach (var (button, rect) in ButtonRects())
        {
            var background = BarColor;
            if (button == hovered)
            {
                canvas.FillRoundRect(RectangleF.Inflate(rect, -2, -2), 4, HoverColor);
                // The same gray as the highlight over the toolbar, for the digit cut out of the numbering icon.
                background = 0xFFE4E4E4;
            }
            var icon = new RectangleF(rect.X + rect.Width / 2 - 9, rect.Y + rect.Height / 2 - 9, 18, 18);
            if (button.Icon is { } i)
                Icons.Draw(canvas, i, icon, button.Active?.Invoke() == true ? accent : Gray, background);
            else
                DrawSwatch(canvas, icon);
        }
    }

    /// The current color on the color button.
    private static void DrawSwatch(Canvas canvas, RectangleF rect)
    {
        var swatch = RectangleF.Inflate(rect, -2, -2);
        canvas.FillEllipse(swatch, color);
        canvas.StrokeEllipse(swatch, 0xFF808080, 1);
    }

    /// A button's hint appears once the cursor has rested on it for a moment.
    private void UpdateTip(Vector2 p)
    {
        var button = ButtonAt(p);
        if (button == hovered)
            return;
        HideTip();
        hovered = button;
        if (button != null)
            PInvoke.SetTimer(Handle, TipTimer, 600, null);
        Invalidate();
    }

    private void HideTip()
    {
        hovered = tip = null;
        PInvoke.KillTimer(Handle, TipTimer);
    }
}
