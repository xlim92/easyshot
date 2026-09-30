namespace EasyShot;

/// Full-screen overlay window; each display gets its own, and a selection can start on any of them.
sealed class OverlayForm : Form
{
    private readonly Action onClose;

    public OverlayForm(ScreenShot shot, Action onClose)
    {
        this.onClose = onClose;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = shot.Screen.Bounds;
        ShowInTaskbar = false;
        // Above the taskbar.
        TopMost = true;
        var view = new CaptureView(shot.Image, onClose) { Dock = DockStyle.Fill };
        Controls.Add(view);
        ActiveControl = view;
    }

    /// Alt+F4 or a close request from another app closes all the overlays, like Esc.
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason is CloseReason.UserClosing or CloseReason.None)
        {
            e.Cancel = true;
            onClose();
        }
        base.OnFormClosing(e);
    }
}
