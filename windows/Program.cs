namespace EasyShot;

static class Program
{
    [STAThread]
    static void Main()
    {
        // A second copy would add a second tray icon and couldn't register the same hotkey.
        using var mutex = new Mutex(true, "EasyShot", out var isFirst);
        if (!isFirst)
            return;
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}
