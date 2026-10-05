using Windows.Win32.UI.Controls.Dialogs;
using Windows.Win32.UI.Shell;

namespace EasyShot;

/// The hidden main window: tray icon and menu, hotkey, launch at login, capture flow.
sealed unsafe class App : Window
{
    private const uint TrayMessage = PInvoke.WM_APP + 1, SaveMessage = PInvoke.WM_APP + 2;
    private const nuint CaptureTimer = 1;
    private const int TakeCommand = 1, ShortcutCommand = 2, QuitCommand = 3;
    private readonly uint taskbarCreated = PInvoke.RegisterWindowMessage("TaskbarCreated");
    private readonly List<CaptureView> overlays = [];
    private Shortcut shortcut = Shortcut.Load();
    private HICON icon;
    private bool recording;
    private Pixels? toSave;

    public App()
    {
        Current = this;
        // A hidden top-level window rather than a message-only one: it gets broadcasts such as TaskbarCreated and WM_SETTINGCHANGE.
        Create("EasyShot", 0, 0, "EasyShot", 0, 0, 0, 0);
        UpdateTrayIcon(NOTIFY_ICON_MESSAGE.NIM_ADD);
        PInvoke.RegisterHotKey(Handle, 1, shortcut.Modifiers, shortcut.Key);
        AddToStartup();
    }

    public static App Current { get; private set; } = null!;

    /// Shows the save dialog once the overlays have closed, otherwise it would end up behind them.
    public void SaveLater(Pixels image)
    {
        toSave = image;
        PInvoke.PostMessage(Handle, SaveMessage, default, default);
    }

    protected override LRESULT OnMessage(uint message, WPARAM wParam, LPARAM lParam)
    {
        switch (message)
        {
            case PInvoke.WM_HOTKEY:
                TakeScreenshot();
                return default;
            case TrayMessage when (uint)lParam.Value == PInvoke.WM_LBUTTONUP:
                TakeScreenshotSoon();
                return default;
            case TrayMessage when (uint)lParam.Value == PInvoke.WM_RBUTTONUP:
                ShowMenu();
                return default;
            case PInvoke.WM_TIMER when wParam.Value == CaptureTimer:
                PInvoke.KillTimer(Handle, CaptureTimer);
                TakeScreenshot();
                return default;
            case PInvoke.WM_SETTINGCHANGE:
                // The taskbar may have switched between light and dark.
                UpdateTrayIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY);
                return default;
            case SaveMessage:
                Save();
                return default;
        }
        if (message == taskbarCreated)
        {
            // Explorer has restarted with an empty tray.
            UpdateTrayIcon(NOTIFY_ICON_MESSAGE.NIM_ADD);
            return default;
        }
        return Default(message, wParam, lParam);
    }

    private void TakeScreenshot()
    {
        if (overlays.Count > 0)
            return;
        foreach (var (bounds, image) in Capture.AllScreens())
            overlays.Add(new CaptureView(bounds, image, CloseOverlays));
        PInvoke.GetCursorPos(out var cursor);
        var active = overlays.Find(overlay => overlay.Contains(cursor)) ?? overlays[0];
        foreach (var overlay in overlays)
            overlay.Show(overlay == active);
    }

    /// The tray menu or the flyout with hidden icons is still on screen right after the click:
    /// let it disappear first, so it doesn't get into the screenshot.
    private void TakeScreenshotSoon() => PInvoke.SetTimer(Handle, CaptureTimer, 200, null);

    private void CloseOverlays()
    {
        foreach (var overlay in overlays)
            overlay.Close();
        overlays.Clear();
    }

    private void ShowMenu()
    {
        var menu = PInvoke.CreatePopupMenu();
        AddItem(menu, TakeCommand, $"Take Screenshot\t{shortcut.Description}");
        // A second dialog would register the hotkey in the middle of recording.
        AddItem(menu, ShortcutCommand, "Screenshot Shortcut…", recording ? MENU_ITEM_FLAGS.MF_GRAYED : 0);
        PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, default(PCWSTR));
        AddItem(menu, QuitCommand, "Quit EasyShot");
        PInvoke.GetCursorPos(out var cursor);
        // The menu closes on a click elsewhere only while its window is in the foreground.
        PInvoke.SetForegroundWindow(Handle);
        var command = PInvoke.TrackPopupMenuEx(menu, (uint)(TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON),
                                               cursor.X, cursor.Y, Handle, null).Value;
        PInvoke.PostMessage(Handle, PInvoke.WM_NULL, default, default);
        PInvoke.DestroyMenu(menu);
        switch (command)
        {
            case TakeCommand:
                TakeScreenshotSoon();
                break;
            case ShortcutCommand:
                ChangeShortcut();
                break;
            case QuitCommand:
                var data = TrayData();
                PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, &data);
                PInvoke.PostQuitMessage(0);
                break;
        }
    }

    private static void AddItem(HMENU menu, int command, string text, MENU_ITEM_FLAGS flags = 0)
    {
        fixed (char* s = text)
            PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING | flags, (nuint)command, s);
    }

    /// Records a new shortcut: the next key press, with or without modifiers, becomes the hotkey.
    private void ChangeShortcut()
    {
        // Unregister the current hotkey while recording, otherwise pressing it would fire it instead of being recorded.
        PInvoke.UnregisterHotKey(Handle, 1);
        recording = true;
        var chosen = ShortcutDialog.Run(shortcut);
        recording = false;
        if (chosen is { } newShortcut && PInvoke.RegisterHotKey(Handle, 1, newShortcut.Modifiers, newShortcut.Key))
        {
            shortcut = newShortcut;
            shortcut.Save();
        }
        else
        {
            PInvoke.RegisterHotKey(Handle, 1, shortcut.Modifiers, shortcut.Key);
            if (chosen is not { } taken)
                return;
            var title = $"{taken.Description} can’t be used";
            // Windows 11 keeps PrtScn on its own for Snipping Tool; combinations with it are taken only by other apps.
            if (taken != new Shortcut((uint)VIRTUAL_KEY.VK_SNAPSHOT))
            {
                PInvoke.MessageBox(Handle, $"This key combination is already taken by Windows or another app. The shortcut stays {shortcut.Description}.",
                                   title, MESSAGEBOX_STYLE.MB_ICONWARNING);
            }
            else if (PInvoke.MessageBox(Handle, "Windows keeps PrtScn for Snipping Tool. Turn off the Print screen key in the keyboard settings of Accessibility, " +
                                                "then set the shortcut again.\n\nOpen these settings?",
                                        title, MESSAGEBOX_STYLE.MB_YESNO | MESSAGEBOX_STYLE.MB_ICONWARNING) == MESSAGEBOX_RESULT.IDYES)
            {
                PInvoke.ShellExecute(HWND.Null, "open", "ms-settings:easeofaccess-keyboard", null, null, SHOW_WINDOW_CMD.SW_SHOWNORMAL);
            }
        }
    }

    private void Save()
    {
        using var image = toSave!;
        toSave = null;
        var file = new char[32768];
        $"Screenshot {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png".CopyTo(file);
        fixed (char* path = file, filter = "PNG\0*.png\0", extension = "png")
        {
            var dialog = new OPENFILENAMEW
            {
                lStructSize = (uint)sizeof(OPENFILENAMEW),
                hwndOwner = Handle,
                lpstrFilter = filter,
                lpstrFile = path,
                nMaxFile = (uint)file.Length,
                lpstrDefExt = extension,
                Flags = OPEN_FILENAME_FLAGS.OFN_OVERWRITEPROMPT | OPEN_FILENAME_FLAGS.OFN_PATHMUSTEXIST | OPEN_FILENAME_FLAGS.OFN_EXPLORER,
            };
            if (PInvoke.GetSaveFileName(&dialog) && !Canvas.SavePng(image, new string(path)))
                PInvoke.MessageBox(Handle, $"Не удалось сохранить {new string(path)}", "EasyShot", MESSAGEBOX_STYLE.MB_ICONERROR);
        }
    }

    /// The app glyph, white on a dark taskbar and black on a light one.
    private void UpdateTrayIcon(NOTIFY_ICON_MESSAGE action)
    {
        var old = icon;
        var light = Registry.ReadNumber(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme") == 1;
        icon = Glyph.TrayIcon(PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSMICON), light ? 0xFF000000 : 0xFFFFFFFF);
        var data = TrayData();
        data.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP;
        data.uCallbackMessage = TrayMessage;
        data.hIcon = icon;
        "EasyShot".CopyTo(data.szTip.AsSpan());
        PInvoke.Shell_NotifyIcon(action, &data);
        if (!old.IsNull)
            PInvoke.DestroyIcon(old);
    }

    private NOTIFYICONDATAW TrayData() => new() { cbSize = (uint)sizeof(NOTIFYICONDATAW), hWnd = Handle, uID = 1 };

    /// Launch at login: the app adds itself to the user's startup apps. Windows keeps the switch from Task Manager
    /// separately, so a copy the user turned off there stays off.
    private static void AddToStartup()
    {
        const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        var command = $"\"{Environment.ProcessPath}\"";
        if (Registry.ReadText(run, "EasyShot") != command)
            Registry.Write(run, "EasyShot", command);
    }
}
