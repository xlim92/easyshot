using System.Diagnostics;
using Microsoft.Win32;
using SkiaSharp;

namespace EasyShot;

/// Tray icon and menu, hotkey setup, launch at login, capture flow.
sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon tray = new() { Text = "EasyShot" };
    private readonly ToolStripMenuItem screenshotItem = new("Take Screenshot");
    private readonly ToolStripMenuItem shortcutItem = new("Screenshot Shortcut…");
    private readonly HotKey hotKey;
    private Keys shortcut = Shortcut.Load();
    private List<OverlayForm> overlays = [];

    public TrayApp()
    {
        screenshotItem.Click += (_, _) => TakeScreenshotFromTray();
        shortcutItem.Click += (_, _) => ChangeShortcut();
        var menu = new ContextMenuStrip();
        menu.Items.AddRange([screenshotItem, shortcutItem, new ToolStripSeparator()]);
        menu.Items.Add("Quit EasyShot", null, (_, _) =>
        {
            tray.Visible = false;
            ExitThread();
        });
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                TakeScreenshotFromTray();
        };
        UpdateIcon();
        SystemEvents.UserPreferenceChanged += (_, _) => UpdateIcon();
        tray.Visible = true;

        hotKey = new HotKey(TakeScreenshot);
        hotKey.Register(shortcut);
        ShowShortcutInMenu();
        AddToStartup();
    }

    /// Records a new shortcut: the next key press with Ctrl or Alt (or an F key) becomes the hotkey.
    private void ChangeShortcut()
    {
        // Unregister the current hotkey while recording, otherwise pressing it would fire it instead of being recorded.
        hotKey.Unregister();
        // The tray menu stays usable while the dialog is open; a second dialog would register the hotkey in the middle of recording.
        shortcutItem.Enabled = false;
        Keys? chosen = null;
        using var dialog = new Form
        {
            Text = "Screenshot Shortcut",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            StartPosition = FormStartPosition.CenterScreen,
            TopMost = true,
            KeyPreview = true,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var reset = new Button { Text = $"Reset to {Shortcut.Describe(Shortcut.Standard)}", AutoSize = true, DialogResult = DialogResult.OK };
        reset.Click += (_, _) => chosen = Shortcut.Standard;
        var buttons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange([cancel, reset]);
        var layout = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Padding = new Padding(12) };
        layout.Controls.AddRange([
            new Label
            {
                AutoSize = true,
                Text = $"Press a new key combination with Ctrl or Alt, or an F key.\n\nCurrent shortcut: {Shortcut.Describe(shortcut)}",
            },
            buttons,
        ]);
        dialog.Controls.Add(layout);
        dialog.CancelButton = cancel;
        dialog.KeyDown += (_, e) =>
        {
            if (Shortcut.From(e.KeyData) is not { } keys)
                return;
            chosen = keys;
            e.SuppressKeyPress = true;
            dialog.DialogResult = DialogResult.OK;
        };

        var result = dialog.ShowDialog();
        shortcutItem.Enabled = true;
        if (result == DialogResult.OK && chosen is { } newShortcut && hotKey.Register(newShortcut))
        {
            shortcut = newShortcut;
            Shortcut.Save(shortcut);
            ShowShortcutInMenu();
        }
        else
        {
            hotKey.Register(shortcut);
            if (chosen is { } taken)
                MessageBox.Show($"This key combination is already taken by Windows or another app. The shortcut stays {Shortcut.Describe(shortcut)}.",
                                $"{Shortcut.Describe(taken)} can’t be used", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShowShortcutInMenu() => screenshotItem.ShortcutKeyDisplayString = Shortcut.Describe(shortcut);

    private void TakeScreenshot()
    {
        if (overlays.Count > 0)
            return;
        try
        {
            overlays = [.. Capture.AllScreens().Select(shot => new OverlayForm(shot, CloseOverlays))];
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "Не удалось сделать снимок экрана", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        overlays.ForEach(overlay => overlay.Show());
        (overlays.Find(overlay => overlay.Bounds.Contains(Cursor.Position)) ?? overlays[0]).Activate();
    }

    /// The tray menu or the flyout with hidden icons is still on screen right after the click:
    /// let it disappear first, so it doesn't get into the screenshot.
    private async void TakeScreenshotFromTray()
    {
        await Task.Delay(200);
        TakeScreenshot();
    }

    private async void CloseOverlays()
    {
        var closed = overlays;
        overlays = [];
        closed.ForEach(overlay => overlay.Hide());
        // Release the windows only after the event handler that closed them has returned.
        await Task.Yield();
        closed.ForEach(overlay => overlay.Dispose());
    }

    /// The app glyph, white on a dark taskbar and black on a light one.
    private void UpdateIcon()
    {
        var light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is 1;
        var size = SystemInformation.SmallIconSize.Width;
        using var bitmap = new SKBitmap(size, size);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear();
            Glyph.Draw(canvas, SKRect.Inflate(SKRect.Create(size, size), -size / 16f, -size / 16f), light ? SKColors.Black : SKColors.White);
        }
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        // An .ico file holding this one PNG image.
        using var ico = new MemoryStream();
        ico.Write([0, 0, 1, 0, 1, 0, (byte)size, (byte)size, 0, 0, 1, 0, 32, 0]);
        ico.Write(BitConverter.GetBytes((int)png.Size));
        ico.Write(BitConverter.GetBytes(22));
        png.SaveTo(ico);
        ico.Position = 0;
        var old = tray.Icon;
        tray.Icon = new Icon(ico);
        old?.Dispose();
    }

    /// Launch at login: the app adds itself to the user's startup apps. Windows keeps the switch from Task Manager
    /// separately, so a copy the user turned off there stays off.
    private static void AddToStartup()
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            var command = $"\"{Environment.ProcessPath}\"";
            if (run.GetValue("EasyShot") as string != command)
                run.SetValue("EasyShot", command);
        }
        catch (Exception error)
        {
            Trace.WriteLine($"EasyShot: не удалось добавить в автозагрузку: {error}");
        }
    }
}
